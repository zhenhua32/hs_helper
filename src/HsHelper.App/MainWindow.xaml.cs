using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Ingestion;
using Microsoft.Win32;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow : Window
{
    private readonly CardCatalog _catalog;
    private readonly string _dataRoot;
    private PowerLogStateEngine _engine = new();
    private PowerLogTailer _tailer = new(64 * 1024);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient _cardHttp = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly JsonSerializerOptions SnapshotJson = new() { Converters = { new JsonStringEnumConverter() } };
    private CardManifest? _manifest;
    private bool _reading;
    private bool _catalogBusy;
    private long _revision;
    private string? _renderedMatchId;
    private long _renderedVersion = -1;
    private long _sourceGeneration;
    private string _liveRoot = "";
    private CancellationTokenSource _source = new();
    private CancellationTokenSource? _analysis;
    private DeckDefinition? _deck;
    private readonly AnalysisBudget _analysisBudget = new();
    private readonly HashSet<Task> _analysisTasks = [];

    public MainWindow()
    {
        InitializeComponent();
        _dataRoot = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
        _catalog = new CardCatalog(Path.Combine(_dataRoot, "cards.db"));
        _modelStore = new ProtectedModelConfigurationStore(Path.Combine(_dataRoot, "model-config.json"), new WindowsSecretProtector());
        Closing += WindowClosing;
        InitializeOverlay();
        _timer.Tick += async (_, _) => await ReadLiveAsync();
        Closed += (_, _) => { _timer.Stop(); _source.Cancel(); _analysis?.Cancel(); _connectionCheck?.Cancel(); _http.Dispose(); _cardHttp.Dispose(); };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await _catalog.InitializeAsync();
        await RefreshCatalogAsync();
        Diagnose();
        await InitializeHistoryAsync();
        await InitializeTracesAsync();
        await LoadModelConfigurationAsync();
    });

    private void Diagnose()
    {
        var report = HearthstoneDiagnostics.Inspect(string.IsNullOrWhiteSpace(LogPathBox.Text) ? null : LogPathBox.Text);
        if (string.IsNullOrWhiteSpace(LogPathBox.Text)) LogPathBox.Text = report.LogSearchRoot ?? "";
        if (string.IsNullOrWhiteSpace(SessionBuildBox.Text) && report.ObservedLogBuild is { } build) SessionBuildBox.Text = build;
        StatusText.Text = string.Join("  ", report.Messages.DefaultIfEmpty("已找到游戏日志，可选择开始监听。"));
    }
    private async void DiagnoseClicked(object sender, RoutedEventArgs e) => await Run(() => { Diagnose(); return Task.CompletedTask; });
    private async void PreviewConfigClicked(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var plan = LogConfiguration.CreatePlan(HearthstoneDiagnostics.DefaultConfigurationPath);
        var preview = new Window { Title = "日志配置预览（尚未写入）", Owner = this, Width = 650, Height = 500,
            Content = new TextBox { Text = $"路径：{plan.Path}\n{plan.RestartNotice}\n\n建议内容：\n{plan.ProposedText}",
                IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) } };
        preview.Show();
        return Task.CompletedTask;
    });

    private int? SelectedPlayer => PlayerBox?.SelectedIndex is 1 or 2 ? PlayerBox.SelectedIndex : null;
    private async void PlayerChanged(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (_engine is null || StateText is null) return;
        if (_timer.IsEnabled) await StopRecordingAsync();
        _engine.SetLocalPlayerId(SelectedPlayer);
        _liveFrame = null; _trackingFrames = null;
        RenderSnapshot();
    });
    private async void ReplayClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_loadingSource) throw new InvalidOperationException("回放正在进行，可先停止后再选择其他日志。");
        var dialog = new OpenFileDialog { Filter = "炉石日志|*.log|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        BeginSource();
        await FinishCaptureAsync();
        _historyContext = null;
        PinSourceDeck();
        var generation = _sourceGeneration;
        var token = _source.Token;
        _loadingSource = true; PlayerBox.IsEnabled = false; SavedDeckBox.IsEnabled = false;
        var build = ClientBuildMetadata.ReadPowerLogBuild(dialog.FileName);
        var engine = NewEngine(SourceIdentity(dialog.FileName));
        DeckTrackingEnricher? tracking = null;
        GameSnapshot lastFrame = engine.Snapshot();
        var capture = NewCapture(build);
        _capture = capture;
        try
        {
            tracking = await CreateTrackingFramesAsync(build);
            token.ThrowIfCancellationRequested();
            var producer = Task.Run(async () =>
            {
                long lineNumber = 0;
                await foreach (var line in SharedLogReplay.ReadLinesAsync(dialog.FileName, token))
                {
                    lineNumber++;
                    if (engine.ProcessLine(line))
                    {
                        lastFrame = tracking.Observe(engine.Snapshot());
                        capture.Observe(lastFrame, lineNumber);
                    }
                    if (capture.PendingCount >= 512) await capture.FlushAsync(token);
                }
                await capture.CompleteAsync(token);
            }, token);
            _replayTask = producer;
            try { await producer; }
            finally { if (ReferenceEquals(_replayTask, producer)) _replayTask = null; }
        }
        finally
        {
            try
            {
                await capture.CompleteAsync();
                if (ReferenceEquals(_capture, capture)) _capture = null;
            }
            finally { _loadingSource = false; PlayerBox.IsEnabled = true; SavedDeckBox.IsEnabled = true; }
        }
        if (generation != _sourceGeneration) return;
        _engine = engine;
        _liveFrame = lastFrame; _trackingFrames = tracking;
        LogPathBox.Text = dialog.FileName;
        SessionBuildBox.Text = build ?? "";
        RenderSnapshot();
        await RefreshHistoryAsync();
        StatusText.Text = $"回放完成，已保存 {capture.RecordedMatchCount} 局，视角未确认 {capture.UnconfirmedMatchCount} 局。空白属性表示未知。";
    });
    private async void WatchClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_loadingSource) throw new InvalidOperationException("回放正在进行，可先停止后再开始监听。");
        if (string.IsNullOrWhiteSpace(LogPathBox.Text)) { Diagnose(); if (string.IsNullOrWhiteSpace(LogPathBox.Text)) return; }
        if (_reading) throw new InvalidOperationException("日志正在读取，请稍后重试。");
        BeginSource();
        await FinishCaptureAsync();
        _historyContext = null;
        PinSourceDeck();
        _tailer.Dispose(); _tailer = new(64 * 1024);
        _engine = NewEngine();
        _liveFrame = null; _trackingFrames = null;
        _sourceLine = 0;
        _liveRoot = LogPathBox.Text;
        _timer.Start();
        await ReadLiveAsync();
    });
    private void BeginSource()
    {
        _timer.Stop(); _sourceGeneration++;
        _historyRevision++;
        _historyLoading = false;
        _source.Cancel(); _source.Dispose(); _source = new();
        InvalidateAdvice();
    }
    private async void StopClicked(object sender, RoutedEventArgs e) => await Run(async () => { await StopRecordingAsync(); StatusText.Text = "已停止监听并保存记录，显示最后一次快照。"; });
    private async void LogPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_timer?.IsEnabled == true) await Run(async () => { await StopRecordingAsync(); StatusText.Text = "日志路径已改变，记录已保存，请重新开始监听。"; });
    }
    private async Task ReadLiveAsync()
    {
        if (_reading) return;
        _reading = true;
        var generation = _sourceGeneration;
        var token = _source.Token;
        var engine = _engine;
        var tailer = _tailer;
        var catchUp = false;
        try
        {
            var batch = await tailer.ReadAvailableAsync(_liveRoot, token);
            if (generation != _sourceGeneration) return;
            if (batch.Path is null) { StatusText.Text = "等待 Power.log 写入…"; return; }
            catchUp = batch.HasMoreData;
            if (batch.ResetReason != LogResetReason.None)
            {
                await FinishCaptureAsync();
                if (generation != _sourceGeneration) return;
                var build = ClientBuildMetadata.ReadPowerLogBuild(batch.Path);
                var identity = SourceIdentity(batch.Path);
                if (batch.ResetReason is LogResetReason.Truncated or LogResetReason.Replaced) identity += "-" + Guid.NewGuid().ToString("N")[..8];
                engine = _engine = NewEngine(identity);
                _capture = NewCapture(build);
                var tracking = await CreateTrackingFramesAsync(build);
                if (generation != _sourceGeneration) return;
                _trackingFrames = tracking;
                _liveFrame = null;
                _sourceLine = 0;
                if (_historyContext is null) SessionBuildBox.Text = build ?? "";
            }
            var capture = _capture;
            foreach (var line in batch.Lines)
            {
                _sourceLine++;
                if (engine.ProcessLine(line))
                {
                    var frame = engine.Snapshot();
                    _liveFrame = _trackingFrames?.Observe(frame) ?? frame;
                    capture?.Observe(_liveFrame, _sourceLine);
                }
            }
            if (_historyContext is null && _liveFrame is { } current &&
                (current.MatchId != _renderedMatchId || current.StateVersion != _renderedVersion)) InvalidateAdvice();
            if (capture is not null) await Task.Run(() => capture.FlushAsync(token), token);
            if (generation != _sourceGeneration) return;
            if (batch.Lines.Count > 0 || batch.ResetReason != LogResetReason.None) RenderSnapshot();
            if (batch.Lines.Count > 0) ScheduleAutomaticAdvice(batch.HasMoreData);
            StatusText.Text = $"正在监听 · 已读取 {batch.ByteOffset:N0} 字节 · {batch.Path}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _sourceGeneration) { _timer.Stop(); StatusText.Text = "日志读取停止：" + ex.Message; } }
        finally
        {
            _reading = false;
            if (catchUp && generation == _sourceGeneration && _timer.IsEnabled)
                _ = Dispatcher.BeginInvoke(new Action(async () => await ReadLiveAsync()), DispatcherPriority.Background);
        }
    }
    private void RenderSnapshot()
    {
        var state = DisplaySnapshot;
        if (state.MatchId != _renderedMatchId || state.StateVersion != _renderedVersion)
        {
            InvalidateAdvice();
            _renderedMatchId = state.MatchId;
            _renderedVersion = state.StateVersion;
        }
        EntityGrid.ItemsSource = state.Entities.Values.OrderBy(x => x.ControllerId).ThenBy(x => x.Zone).ThenBy(x => x.ZonePosition).ToArray();
        StateText.Text = (_historyContext is null ? "当前日志 · " : "历史局面 · ") + $"回合 {state.Turn?.ToString() ?? "未知"} · 阶段 {state.Phase} · 局面版本 {state.StateVersion} · 行动方 {state.ActivePlayerId?.ToString() ?? "未知"}\n" +
            $"完整：{state.Quality.IsComplete} · 稳定：{state.Quality.IsStable} · 我方视角：{state.Quality.HasLocalPerspective}\n" + string.Join("；", state.Quality.Warnings);
        _ = Run(UpdateOverlayAsync);
    }
    private void InvalidateAdvice()
    {
        _revision++;
        _analysis?.Cancel();
        _overlayAdvice = "局面已更新，旧建议已失效。";
        if (AdviceBox is not null) AdviceBox.Text = "局面或视角已更新。已有建议已失效，可重新请求分析。";
    }
    private void BuildChanged(object sender, TextChangedEventArgs e) => InvalidateAdvice();
    private void AdviceTaskChanged(object sender, SelectionChangedEventArgs e) => InvalidateAdvice();
    private async void ExportClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new SaveFileDialog { Filter = "局面JSON|*.json", FileName = "snapshot.json" };
        if (dialog.ShowDialog() == true) await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(DisplaySnapshot, new JsonSerializerOptions { WriteIndented = true }));
    });

    private async Task RefreshCatalogAsync()
    {
        _manifest = await _catalog.GetActiveManifestAsync();
        CatalogText.Text = _manifest is null ? "尚未加载卡库" : $"build {_manifest.Build} · {_manifest.CardCount:N0} 张";
        await RefreshPoolStatusAsync();
        await SearchCardsAsync();
    }
    private async Task SearchCardsAsync()
    {
        var query = SearchBox.Text;
        var hunterOnly = HunterOnlyBox.IsChecked == true;
        var keyword = KeywordBox.Text.Trim();
        var poolOnly = StandardPoolBox.IsChecked == true;
        var cards = await _catalog.SearchAsync(new CardSearchQuery(Text: query, CardClass: hunterOnly ? "HUNTER" : null, Collectible: true, Limit: 200,
            Keyword: keyword, StandardPoolOnly: poolOnly));
        if (hunterOnly)
        {
            var neutral = await _catalog.SearchAsync(new CardSearchQuery(Text: query, CardClass: "NEUTRAL", Collectible: true, Limit: 200,
                Keyword: keyword, StandardPoolOnly: poolOnly));
            cards = cards.Concat(neutral.Where(c => c.Classes.Count == 0 || c.Classes.Contains("HUNTER"))).DistinctBy(c => c.Id).OrderBy(c => c.Cost).ThenBy(c => c.Name).ToArray();
        }
        CardGrid.ItemsSource = cards;
    }
    private async void SearchClicked(object sender, RoutedEventArgs e) => await Run(SearchCardsAsync);
    private async void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await Run(SearchCardsAsync); }
    private async void ImportCardsClicked(object sender, RoutedEventArgs e) => await CatalogOperation(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "卡牌JSON|*.json" };
        if (dialog.ShowDialog() != true) return;
        using var input = File.OpenRead(dialog.FileName);
        var build = ImportBuildBox.Text.Trim();
        await Task.Run(() => _catalog.ImportAsync(input, build));
        await RefreshCatalogAsync(); StatusText.Text = "卡库已导入并切换活动版本。";
    });
    private async void SyncCardsClicked(object sender, RoutedEventArgs e) => await CatalogOperation(async () =>
    {
        StatusText.Text = "正在下载并校验 zhCN 卡库，已有完整版本继续保留…";
        await Task.Run(() => _catalog.SyncAsync(_cardHttp));
        await RefreshCatalogAsync(); StatusText.Text = "卡库同步完成；请另行核对国服客户端build与标准可用性。";
    });
    private async Task CatalogOperation(Func<Task> work)
    {
        if (_catalogBusy) { StatusText.Text = "卡库任务正在进行。"; return; }
        _catalogBusy = true;
        try { await Run(work); } finally { _catalogBusy = false; }
    }
    private async void DecodeClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var parsed = DeckCode.Decode(DeckBox.Text);
        if (_capture is not null || _timer.IsEnabled) await StopRecordingAsync();
        _selectedDeckVersion = null;
        _loadingDecks = true;
        try { SavedDeckBox.SelectedItem = null; }
        finally { _loadingDecks = false; }
        _deck = parsed;
        InvalidateAdvice();
        await DescribeDeckAsync(_deck);
    });
    private async void SaveDeckClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var deck = DeckCode.Decode(DeckBox.Text);
        var dialog = new SaveFileDialog { Filter = "套牌JSON|*.json", FileName = "hunter-deck.json" };
        if (dialog.ShowDialog() == true) await File.WriteAllTextAsync(dialog.FileName,
            JsonSerializer.Serialize(new { Version = Guid.NewGuid(), SavedAt = DateTimeOffset.UtcNow, CardBuild = _manifest?.Build, Deck = deck, Code = DeckCode.Encode(deck) }, new JsonSerializerOptions { WriteIndented = true }));
    });

    private async void AnalyzeClicked(object sender, RoutedEventArgs e) => await Run(AnalyzeCurrentAsync);
    private Task AnalyzeCurrentAsync()
    {
        var operation = AnalyzeCoreAsync();
        _analysisTasks.Add(operation);
        return FinishAsync();
        async Task FinishAsync()
        {
            try { await operation; }
            finally { _analysisTasks.Remove(operation); }
        }
    }
    private async Task AnalyzeCoreAsync()
    {
        if (_closing || _traces is null) throw new InvalidOperationException("应用正在初始化或关闭，请稍后再分析。");
        if (_historyLoading) throw new InvalidOperationException("历史局面正在读取，请等待读取完成后分析。");
        var modelConfiguration = ConfigurationFromFields();
        var configurationRevision = _configurationRevision;
        var history = _historyContext;
        var snapshot = history?.Snapshot ?? _liveFrame ?? _engine.Snapshot();
        var importedDeck = AnalysisDeck;
        var requestedBuild = history?.CardBuild ?? SessionBuildBox.Text.Trim();
        var historyRevision = _historyRevision;
        var revision = _revision;
        if (snapshot.ClientBuild is { } observedBuild && observedBuild != requestedBuild)
            throw new InvalidOperationException($"局面报告 build {observedBuild}，请使用对应卡库，不能用其他版本牌效分析。");
        if (snapshot.LocalClass is { } localClass && localClass != "HUNTER")
            throw new InvalidOperationException("当前选中视角不是猎人，请核对我方身份后再使用猎人教练。");
        if (!snapshot.Quality.IsStable || !snapshot.Quality.HasLocalPerspective || snapshot.Entities.Count == 0)
            throw new InvalidOperationException("请先加载局面、确认我方玩家ID，并等待结算完成。");
        if (!Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out var endpoint)) throw new ArgumentException("请填写完整模型接口地址。");
        _analysis?.Cancel();
        using var cancellation = new CancellationTokenSource();
        bool IsCurrent() => revision == _revision && configurationRevision == _configurationRevision && historyRevision == _historyRevision && ReferenceEquals(history, _historyContext)
            && (history?.CardBuild ?? SessionBuildBox.Text.Trim()) == requestedBuild && ReferenceEquals(importedDeck, AnalysisDeck)
            && DisplaySnapshot.MatchId == snapshot.MatchId && DisplaySnapshot.StateVersion == snapshot.StateVersion
            && ReferenceEquals(_analysis, cancellation);
        _analysis = cancellation;
        AnalyzeButton.IsEnabled = false;
        try
        {
            var pinnedManifest = string.IsNullOrEmpty(requestedBuild) || requestedBuild == "unknown" ? null : await _catalog.GetManifestAsync(requestedBuild);
            if (!IsCurrent()) throw new OperationCanceledException("所选局面已经改变。");
            var task = ((ComboBoxItem)TaskBox.SelectedItem).Content.ToString()!;
            if (task == "回合建议") task = snapshot.Phase switch { GamePhase.Mulligan => "留牌建议", GamePhase.Choice => "选择建议", _ => task };
            var json = JsonSerializer.SerializeToNode(new
            {
                snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, snapshot.Turn,
                snapshot.ActivePlayerId, snapshot.LocalPlayerId, snapshot.Phase, snapshot.Quality, snapshot.Choice, snapshot.DeckTracking,
                RecentEvents = history is null ? (object)snapshot.RecentEvents : history.RecentEvents,
                Entities = snapshot.Entities.Values.Where(e => e.Kind != EntityKind.Card || e.Zone is "HAND" or "PLAY" or "SECRET").ToArray(),
                snapshot.PublicPlays,
                DeckCounts = snapshot.Entities.Values.Where(e => e.Zone == "DECK").GroupBy(e => e.ControllerId)
                    .Select(g => new { Player = g.Key, Count = g.Count() }).ToArray()
            }, SnapshotJson)!.AsObject();
            json["card_data_build"] = requestedBuild;
            json["rule_coverage"] = "基础战斗及部分猎人牌效；具体覆盖和限制以evaluate_line/find_lethal返回为准。未覆盖效果不能视为已经计算。";
            if (importedDeck is not null)
            {
                json["imported_deck"] = JsonSerializer.SerializeToNode(importedDeck);
                json["deck_confirmation"] = history?.Deck is not null || _sourceDeckVersion is not null
                    ? "用户为该局确认并固定的套牌版本；实际剩余牌库以DeckTracking的证据和质量标记为准。"
                    : "用户导入的套牌背景，尚未确认当前对局绑定，不据此断言剩余牌库。";
                if (pinnedManifest is not null && requestedBuild == pinnedManifest.Build)
                {
                    var deckNumbers = importedDeck.Cards.Select(c => c.DbfId)
                        .Concat(importedDeck.Sideboards.SelectMany(s => s.Cards.Select(c => c.DbfId))).Distinct();
                    var deckCards = await _catalog.GetByDbfIdsAsync(deckNumbers, pinnedManifest.Build,
                        cancellationToken: cancellation.Token);
                    json["imported_deck_cards"] = JsonSerializer.SerializeToNode(deckCards.Select(c => new { c.Id, c.DbfId, c.Name, c.Text, c.Cost }));
                }
            }
            if (!IsCurrent()) throw new OperationCanceledException("所选局面已经改变。");
            var context = new AdviceContext(snapshot.MatchId, snapshot.StateVersion, task, json);
            AdviceBox.Text = "正在分析固定局面…";
            await SeedBudgetAsync(snapshot.MatchId);
            if (!IsCurrent()) throw new OperationCanceledException("所选局面已经改变。");
            var coach = new SnapshotCoach(snapshot, json, _catalog, requestedBuild,
                (name, elapsed, success) => RememberTool(snapshot.MatchId, snapshot.StateVersion, name, elapsed, success));
            var advice = await new AdviceService(_http).AnalyzeAsync(modelConfiguration.Settings, modelConfiguration.ApiKey,
                context, coach.ExecuteAsync, IsCurrent, cancellation.Token,
                coach.Capabilities(_analysisBudget, usage => RememberUsage(usage, requestedBuild, task)));
            if (IsCurrent())
            {
                AdviceBox.Text = $"{(advice.Verification == "verified" ? "动作已通过本地规则验证" : "策略建议")} · 局面 v{advice.StateVersion}\n\n{advice.Summary}\n\n依据\n" +
                    string.Join("\n", advice.Reasons) + $"\n\n备选\n{advice.Alternative ?? "无"}\n\n风险与未知\n" + string.Join("\n", advice.Risks) +
                    (advice.Actions.Count == 0 ? "" : "\n\n已验证动作\n" + string.Join("\n", advice.Actions.Select(a => $"{a.Kind} · 实体 {a.SourceEntityId} → {a.TargetEntityId?.ToString() ?? "无目标"}"))) +
                    (advice.VerifiedLethal ? "\n本地规则确认此序列可斩杀。" : "");
                _overlayAdvice = advice.Summary + (advice.Risks.Length > 0 ? "\n风险：" + advice.Risks[0] : "");
                await UpdateOverlayAsync();
            }
        }
        catch (OperationCanceledException) { if (IsCurrent()) AdviceBox.Text = "分析已取消、超时或局面已变化，旧结果已丢弃。"; }
        finally
        {
            try { await FlushTracesAsync(); }
            catch (Exception error) { StatusText.Text = "模型用量尚未安全保存，下次请求前将重试：" + error.Message; }
            finally
            {
                if (IsCurrent()) DisplayBudget(snapshot.MatchId);
                if (ReferenceEquals(_analysis, cancellation)) { AnalyzeButton.IsEnabled = true; _analysis = null; ScheduleAutomaticAdvice(false); }
            }
        }
    }
    private void CancelAdviceClicked(object sender, RoutedEventArgs e) => _analysis?.Cancel();
    private async Task Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
