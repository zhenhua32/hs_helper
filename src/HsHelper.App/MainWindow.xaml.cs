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
using HsHelper.Core;
using HsHelper.Ingestion;
using Microsoft.Win32;

namespace HsHelper.App;

public partial class MainWindow : Window
{
    private readonly CardCatalog _catalog;
    private readonly string _dataRoot;
    private PowerLogStateEngine _engine = new();
    private PowerLogTailer _tailer = new();
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

    public MainWindow()
    {
        InitializeComponent();
        _dataRoot = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
        _catalog = new CardCatalog(Path.Combine(_dataRoot, "cards.db"));
        _timer.Tick += async (_, _) => await ReadLiveAsync();
        Closed += (_, _) => { _timer.Stop(); _source.Cancel(); _analysis?.Cancel(); _http.Dispose(); _cardHttp.Dispose(); };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await _catalog.InitializeAsync();
        await RefreshCatalogAsync();
        Diagnose();
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
    private void PlayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || StateText is null) return;
        _engine.SetLocalPlayerId(SelectedPlayer);
        RenderSnapshot();
    }
    private async void ReplayClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "炉石日志|*.log|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        BeginSource();
        var generation = _sourceGeneration;
        var token = _source.Token;
        var engine = new PowerLogStateEngine(SelectedPlayer);
        await Task.Run(async () =>
        {
            await foreach (var line in SharedLogReplay.ReadLinesAsync(dialog.FileName, token)) engine.ProcessLine(line);
        }, token);
        if (generation != _sourceGeneration) return;
        engine.SetLocalPlayerId(SelectedPlayer);
        _engine = engine;
        LogPathBox.Text = dialog.FileName;
        SessionBuildBox.Text = ClientBuildMetadata.ReadPowerLogBuild(dialog.FileName) ?? "";
        RenderSnapshot();
        StatusText.Text = "离线回放完成。请选择已核对的我方玩家ID，空白属性表示未知。";
    });
    private async void WatchClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(LogPathBox.Text)) { Diagnose(); if (string.IsNullOrWhiteSpace(LogPathBox.Text)) return; }
        if (_reading) throw new InvalidOperationException("日志正在读取，请稍后重试。");
        BeginSource();
        _tailer.Dispose(); _tailer = new();
        _engine = new(SelectedPlayer);
        _liveRoot = LogPathBox.Text;
        _timer.Start();
        await ReadLiveAsync();
    });
    private void BeginSource()
    {
        _timer.Stop(); _sourceGeneration++;
        _source.Cancel(); _source.Dispose(); _source = new();
        InvalidateAdvice();
    }
    private void StopClicked(object sender, RoutedEventArgs e) { BeginSource(); StatusText.Text = "已停止监听，显示最后一次快照。"; }
    private void LogPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_timer?.IsEnabled == true) { BeginSource(); StatusText.Text = "日志路径已改变，请重新开始监听。"; }
    }
    private async Task ReadLiveAsync()
    {
        if (_reading) return;
        _reading = true;
        var generation = _sourceGeneration;
        var token = _source.Token;
        var engine = _engine;
        var tailer = _tailer;
        try
        {
            var batch = await tailer.ReadAvailableAsync(_liveRoot, token);
            if (generation != _sourceGeneration) return;
            if (batch.Path is null) { StatusText.Text = "等待 Power.log 写入…"; return; }
            if (batch.ResetReason != LogResetReason.None) engine.ResetSession();
            if (batch.ResetReason != LogResetReason.None) SessionBuildBox.Text = ClientBuildMetadata.ReadPowerLogBuild(batch.Path) ?? "";
            foreach (var line in batch.Lines) engine.ProcessLine(line);
            if (batch.Lines.Count > 0 || batch.ResetReason != LogResetReason.None) RenderSnapshot();
            StatusText.Text = $"正在监听 · 已读取 {batch.ByteOffset:N0} 字节 · {batch.Path}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _sourceGeneration) { _timer.Stop(); StatusText.Text = "日志读取停止：" + ex.Message; } }
        finally { _reading = false; }
    }
    private void RenderSnapshot()
    {
        var state = _engine.Snapshot();
        if (state.MatchId != _renderedMatchId || state.StateVersion != _renderedVersion)
        {
            InvalidateAdvice();
            _renderedMatchId = state.MatchId;
            _renderedVersion = state.StateVersion;
        }
        EntityGrid.ItemsSource = state.Entities.Values.OrderBy(x => x.ControllerId).ThenBy(x => x.Zone).ThenBy(x => x.ZonePosition).ToArray();
        StateText.Text = $"回合 {state.Turn?.ToString() ?? "未知"} · 阶段 {state.Phase} · 局面版本 {state.StateVersion} · 行动方 {state.ActivePlayerId?.ToString() ?? "未知"}\n" +
            $"完整：{state.Quality.IsComplete} · 稳定：{state.Quality.IsStable} · 我方视角：{state.Quality.HasLocalPerspective}\n" + string.Join("；", state.Quality.Warnings);
    }
    private void InvalidateAdvice()
    {
        _revision++;
        _analysis?.Cancel();
        if (AdviceBox is not null) AdviceBox.Text = "局面或视角已更新。已有建议已失效，可重新请求分析。";
    }
    private void BuildChanged(object sender, TextChangedEventArgs e) => InvalidateAdvice();
    private async void ExportClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new SaveFileDialog { Filter = "局面JSON|*.json", FileName = "snapshot.json" };
        if (dialog.ShowDialog() == true) await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(_engine.Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
    });

    private async Task RefreshCatalogAsync()
    {
        _manifest = await _catalog.GetActiveManifestAsync();
        CatalogText.Text = _manifest is null ? "尚未加载卡库" : $"build {_manifest.Build} · {_manifest.CardCount:N0} 张";
        await SearchCardsAsync();
    }
    private async Task SearchCardsAsync()
    {
        var query = SearchBox.Text;
        var hunterOnly = HunterOnlyBox.IsChecked == true;
        var cards = await _catalog.SearchAsync(new CardSearchQuery(Text: query, CardClass: hunterOnly ? "HUNTER" : null, Collectible: true, Limit: 200));
        if (hunterOnly)
        {
            var neutral = await _catalog.SearchAsync(new CardSearchQuery(Text: query, CardClass: "NEUTRAL", Collectible: true, Limit: 200));
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
    private async void DecodeClicked(object sender, RoutedEventArgs e) => await Run(() =>
    {
        _deck = DeckCode.Decode(DeckBox.Text);
        InvalidateAdvice();
        DeckResultBox.Text = $"模式：{_deck.Format}（2为标准）\n主牌库：{_deck.Cards.Sum(c => c.Count)}张；边栏：{_deck.Sideboards.Count}组\n当前仅解析牌码，尚未确认职业和标准卡池合法性。\n\n" +
            JsonSerializer.Serialize(_deck, new JsonSerializerOptions { WriteIndented = true });
        return Task.CompletedTask;
    });
    private async void SaveDeckClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var deck = DeckCode.Decode(DeckBox.Text);
        var dialog = new SaveFileDialog { Filter = "套牌JSON|*.json", FileName = "hunter-deck.json" };
        if (dialog.ShowDialog() == true) await File.WriteAllTextAsync(dialog.FileName,
            JsonSerializer.Serialize(new { Version = Guid.NewGuid(), SavedAt = DateTimeOffset.UtcNow, CardBuild = _manifest?.Build, Deck = deck, Code = DeckCode.Encode(deck) }, new JsonSerializerOptions { WriteIndented = true }));
    });

    private async void AnalyzeClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var snapshot = _engine.Snapshot();
        if (!snapshot.Quality.IsStable || !snapshot.Quality.HasLocalPerspective || snapshot.Entities.Count == 0)
            throw new InvalidOperationException("请先加载局面、确认我方玩家ID，并等待结算完成。");
        if (!Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out var endpoint)) throw new ArgumentException("请填写完整模型接口地址。");
        _analysis?.Cancel();
        using var cancellation = new CancellationTokenSource();
        var revision = _revision;
        var requestedBuild = SessionBuildBox.Text.Trim();
        var pinnedManifest = _manifest;
        var importedDeck = _deck;
        var task = ((ComboBoxItem)TaskBox.SelectedItem).Content.ToString()!;
        var json = JsonSerializer.SerializeToNode(new
        {
            snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, snapshot.Turn,
            snapshot.ActivePlayerId, snapshot.LocalPlayerId, snapshot.Phase, snapshot.Quality,
            Entities = snapshot.Entities.Values.Where(e => e.Kind != EntityKind.Card || e.Zone is "HAND" or "PLAY" or "SECRET").ToArray(),
            PlayedCards = snapshot.Entities.Values.Where(e => e.Zone == "GRAVEYARD" && e.CardId != null)
                .Select(e => new { e.Id, e.CardId, e.ControllerId }).ToArray(),
            DeckCounts = snapshot.Entities.Values.Where(e => e.Zone == "DECK").GroupBy(e => e.ControllerId)
                .Select(g => new { Player = g.Key, Count = g.Count() }).ToArray()
        }, SnapshotJson)!.AsObject();
        json["card_data_build"] = requestedBuild;
        json["rule_coverage"] = "仅策略分析，完整牌效模拟未实现；卡库build由玩家手动核对";
        if (importedDeck is not null)
        {
            json["imported_deck"] = JsonSerializer.SerializeToNode(importedDeck);
            json["deck_confirmation"] = "用户导入的套牌背景，尚未自动匹配当前对局，不据此断言剩余牌库。";
            if (pinnedManifest is not null && requestedBuild == pinnedManifest.Build)
            {
                var deckCards = await _catalog.GetByDbfIdsAsync(importedDeck.Cards.Select(c => c.DbfId), pinnedManifest.Build,
                    cancellationToken: cancellation.Token);
                json["imported_deck_cards"] = JsonSerializer.SerializeToNode(deckCards.Select(c => new { c.Id, c.DbfId, c.Name, c.Text, c.Cost }));
            }
        }
        var context = new AdviceContext(snapshot.MatchId, snapshot.StateVersion, task, json);
        _analysis = cancellation;
        AnalyzeButton.IsEnabled = false; AdviceBox.Text = "正在分析固定局面…";
        bool IsCurrent() => revision == _revision && SessionBuildBox.Text.Trim() == requestedBuild && ReferenceEquals(importedDeck, _deck);
        try
        {
            var advice = await new AdviceService(_http).AnalyzeAsync(new ModelSettings(endpoint, ModelBox.Text.Trim()), KeyBox.Password,
                context, async (name, args, token) =>
                {
                    if (name == "get_snapshot") return json.DeepClone();
                    if (pinnedManifest is null || requestedBuild != pinnedManifest.Build)
                        return new JsonObject { ["error"] = "未加载与局面build匹配的卡库，无法提供已核对的牌效。" };
                    var ids = args["card_ids"]?.AsArray().Select(x => x!.GetValue<string>()).Take(30).ToArray() ?? [];
                    var cards = await _catalog.GetByIdsAsync(ids, pinnedManifest.Build, cancellationToken: token);
                    return JsonSerializer.SerializeToNode(new { build = pinnedManifest.Build,
                        cards = cards.Select(c => new { c.Id, c.Name, c.Text, c.Cost, c.Type, c.Entourage }) })!;
                }, IsCurrent, cancellation.Token);
            if (IsCurrent()) AdviceBox.Text = $"策略建议 · 局面 v{advice.StateVersion}\n\n{advice.Summary}\n\n依据\n" +
                string.Join("\n", advice.Reasons) + $"\n\n备选\n{advice.Alternative ?? "无"}\n\n风险与未知\n" + string.Join("\n", advice.Risks);
        }
        catch (OperationCanceledException) { AdviceBox.Text = "分析已取消、超时或局面已变化，旧结果已丢弃。"; }
        finally { AnalyzeButton.IsEnabled = true; if (ReferenceEquals(_analysis, cancellation)) _analysis = null; }
    });
    private void CancelAdviceClicked(object sender, RoutedEventArgs e) => _analysis?.Cancel();
    private async Task Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
