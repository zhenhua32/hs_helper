using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HsHelper.Core;
using HsHelper.Storage;

namespace HsHelper.App;

public sealed record HistoryPageRequest(long Revision, SessionFilter Scope, SessionFilter Query, int PageNumber);

/// <summary>Guards delayed list results and freezes the first page's time boundary while browsing.</summary>
public sealed class HistoryPagingState
{
    private long _revision;
    public SessionFilter? Scope { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int ItemCount { get; private set; }
    public bool HasMore { get; private set; }
    public bool IsLoading { get; private set; }
    public DateTimeOffset? BrowsingUntil { get; private set; }

    public void Reset(SessionFilter scope)
    {
        _revision++; Scope = scope; PageNumber = 1; ItemCount = 0; HasMore = false; IsLoading = false; BrowsingUntil = null;
    }
    public HistoryPageRequest Begin(SessionFilter scope, int pageNumber, DateTimeOffset now, bool refresh = false)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (Scope != scope || refresh) { Reset(scope); pageNumber = 1; }
        BrowsingUntil ??= scope.Until is { } until && until < now ? until : now;
        IsLoading = true;
        return new(++_revision, scope, scope with { Until = BrowsingUntil }, pageNumber);
    }
    public bool IsCurrent(HistoryPageRequest request) => request.Revision == _revision && request.Scope == Scope;
    public bool TryApply(HistoryPageRequest request, SessionPage page)
    {
        if (!IsCurrent(request) || page.PageNumber != request.PageNumber) return false;
        PageNumber = page.PageNumber; ItemCount = page.Sessions.Count; HasMore = page.HasMore; IsLoading = false;
        return true;
    }
    public bool TryFail(HistoryPageRequest request)
    {
        if (!IsCurrent(request)) return false;
        IsLoading = false; return true;
    }
}

public partial class MainWindow
{
    private SessionStore _store = null!;
    private DeckVersion? _selectedDeckVersion;
    private DeckVersion? _sourceDeckVersion;
    private HsHelper.Cards.DeckDefinition? _sourceDeckDefinition;
    private bool _sourceBindingEstablished;
    private LogRecordingPipeline? _capture;
    private HistoricalContext? _historyContext;
    private long _sourceLine;
    private bool _allowClose;
    private bool _closing;
    private bool _loadingDecks;
    private bool _loadingSource;
    private long _historyRevision;
    private bool _historyLoading;
    private Task? _replayTask;
    private readonly HistoryPagingState _historyPaging = new();
    private CancellationTokenSource? _historyListCancellation;

    private sealed record DeckItem(DeckVersion Value)
    { public string Label => $"{Value.Name} · {Value.CreatedAt.ToLocalTime():MM-dd HH:mm} · {Value.Id[..6]}"; }
    private sealed record SessionItem(GameSession Value, string Deck)
    {
        public string Time => Value.StartedAt.ToLocalTime().ToString("MM-dd HH:mm");
        public string Opponent => Value.OpponentClass ?? "未知";
        public string Outcome => Value.Outcome switch { "Won" => "胜", "Lost" or "Conceded" => "负", "Tied" => "平", _ => "未结束/未知" };
    }
    private sealed record DecisionItem(DecisionPoint Value)
    { public string Label => $"回合 {Value.Turn?.ToString() ?? "?"} · {Value.Phase} · #{Value.EventSequence}"; }

    private GameSnapshot DisplaySnapshot => _historyContext?.Snapshot ?? _liveFrame ?? _engine.Snapshot();
    private HsHelper.Cards.DeckDefinition? AnalysisDeck => _historyContext is not null ? _historyContext.Deck?.Definition
        : _sourceBindingEstablished ? _sourceDeckDefinition : _deck;
    private void PinSourceDeck()
    {
        _sourceDeckVersion = _selectedDeckVersion;
        _sourceDeckDefinition = _selectedDeckVersion?.Definition ?? _deck;
        _sourceBindingEstablished = true;
    }

    private async Task InitializeHistoryAsync()
    {
        _store = new SessionStore(Path.Combine(_dataRoot, "sessions.db"));
        await _store.InitializeAsync();
        await LoadDeckLibraryAsync();
        var sample = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            .Select(root => Path.Combine(root, "samples", "decks", "beast-hunter.txt")).FirstOrDefault(File.Exists);
        if (sample is not null && string.IsNullOrWhiteSpace(DeckBox.Text))
        {
            DeckBox.Text = await File.ReadAllTextAsync(sample);
            var existing = await _store.ListDeckVersionsAsync();
            var code = HsHelper.Cards.DeckCode.Encode(HsHelper.Cards.DeckCode.Decode(DeckBox.Text));
            var seed = existing.FirstOrDefault(d => d.Code == code);
            if (seed is null && existing.Count == 0)
                seed = await _store.SaveDeckVersionAsync("野兽快", HsHelper.Cards.DeckCode.Decode(code), _manifest?.Build ?? "unknown");
            if (seed is not null)
            {
                _selectedDeckVersion = seed; _deck = seed.Definition;
                await LoadDeckLibraryAsync(seed.Id);
                await DescribeDeckAsync(seed.Definition);
            }
        }
        await RefreshHistoryAsync();
    }

    private async Task LoadDeckLibraryAsync(string? selectId = null)
    {
        var previousPlanId = (PlanDeckBox.SelectedItem as DeckItem)?.Value.Id;
        _loadingDecks = true;
        try
        {
            var items = (await _store.ListDeckVersionsAsync()).Select(d => new DeckItem(d)).ToArray();
            SavedDeckBox.ItemsSource = items;
            SavedDeckBox.SelectedItem = items.FirstOrDefault(d => d.Value.Id == (selectId ?? _selectedDeckVersion?.Id));
            var planId = previousPlanId ?? selectId ?? _selectedDeckVersion?.Id;
            PlanDeckBox.ItemsSource = items;
            PlanDeckBox.SelectedItem = items.FirstOrDefault(d => d.Value.Id == planId);
        }
        finally { _loadingDecks = false; }
        if (_knowledge is not null && previousPlanId != (PlanDeckBox.SelectedItem as DeckItem)?.Value.Id)
        { ClearKnowledgeEditor(); await RefreshKnowledgeAsync(); }
    }

    private async void SaveDeckToLibraryClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var deck = HsHelper.Cards.DeckCode.Decode(DeckBox.Text);
        if (_capture is not null || _timer.IsEnabled) await StopRecordingAsync();
        var version = await _store.SaveDeckVersionAsync(DeckNameBox.Text, deck, _manifest?.Build ?? "unknown",
            _selectedDeckVersion?.Name == DeckNameBox.Text.Trim() ? _selectedDeckVersion.Id : null);
        _selectedDeckVersion = version;
        _deck = version.Definition;
        await LoadDeckLibraryAsync(version.Id);
        InvalidateAdvice();
        StatusText.Text = "套牌版本已保存，下一次开始监听或导入时固定绑定该版本。";
        await DescribeDeckAsync(deck, version.CardBuild);
    });

    private async void SavedDeckChanged(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (_loadingDecks || _store is null) return;
        if (_timer.IsEnabled) await StopRecordingAsync();
        _selectedDeckVersion = (SavedDeckBox.SelectedItem as DeckItem)?.Value;
        if (_selectedDeckVersion is { } version)
        {
            _deck = version.Definition; DeckBox.Text = version.Code; DeckNameBox.Text = version.Name;
            await DescribeDeckAsync(version.Definition, version.CardBuild);
        }
        InvalidateAdvice();
    });

    private async Task DescribeDeckAsync(HsHelper.Cards.DeckDefinition deck, string? build = null)
    {
        build ??= _manifest?.Build;
        if (build is null || build == "unknown") { DeckResultBox.Text = "套牌已解析，加载对应卡库后可显示名称和费用曲线。"; return; }
        var description = await _catalog.DescribeDeckAsync(deck, build);
        var text = new StringBuilder($"{description.HeroClass ?? "未知职业"} · 主牌 {deck.Cards.Sum(c => c.Count)} 张 · 构筑检查 {description.Legality}\n");
        foreach (var card in description.Cards.OrderBy(c => c.Cost).ThenBy(c => c.Name)) text.AppendLine($"{card.Count}x ({card.Cost?.ToString() ?? "?"}) {card.Name}");
        foreach (var sideboard in description.Sideboards)
        {
            text.AppendLine($"\n{sideboard.OwnerName} 的边栏：");
            foreach (var card in sideboard.Cards) text.AppendLine($"  {card.Count}x ({card.Cost?.ToString() ?? "?"}) {card.Name}");
        }
        text.AppendLine("\n费用曲线：" + string.Join("  ", description.ManaCurve.Select(b => $"{b.Cost}费 {b.Count}张")));
        foreach (var issue in description.Issues) text.AppendLine(issue);
        DeckResultBox.Text = text.ToString();
    }

    private PowerLogStateEngine NewEngine(string? identity = null) => new(SelectedPlayer, identity, autoDetectPerspective: PlayerBox.SelectedIndex == 0);
    private string SourceIdentity(string path)
        => HsHelper.Ingestion.LogSourceIdentity.Create(path, PlayerBox.SelectedIndex, _sourceDeckVersion?.Id);
    private LogRecordingPipeline NewCapture(string? build) => new(_store, _sourceDeckVersion?.Id,
        string.IsNullOrWhiteSpace(build) ? "unknown" : build);

    private async Task FinishCaptureAsync()
    {
        var capture = _capture;
        if (capture is not null) await Task.Run(() => capture.CompleteAsync());
        if (ReferenceEquals(_capture, capture)) _capture = null;
    }
    private async Task StopRecordingAsync()
    {
        BeginSource();
        var analyses = _analysisTasks.ToArray();
        try { await Task.WhenAll(analyses); }
        catch (Exception) { /* Each request keeps usage in its own finally; saving below is still mandatory. */ }
        var replay = _replayTask;
        if (replay is not null)
        {
            try { await replay; }
            catch (OperationCanceledException) { }
        }
        await FinishCaptureAsync();
        if (_traces is not null) await FlushTracesAsync();
        await FlushAdviceArchivesAsync();
        await WaitForConfigurationIoAsync();
        if (_closing && _logSetupWindow is not null) await _logSetupWindow.WaitForPendingWriteAsync();
        if (_store is not null) await RefreshHistoryAsync();
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (!TryLeaveKnowledgeEditor()) return;
        _closing = true;
        try
        {
            RefreshKnowledgeControls();
            RefreshDeckExportControls();
            CancelKnowledgeListRead();
            CancelKnowledgeNavigation();
            var historyRead = _historyListCancellation; _historyListCancellation = null;
            if (_historyPaging.IsLoading) HistoryPageText.Text = "记录读取已取消，可刷新重试。";
            _historyPaging.Reset(_historyPaging.Scope ?? new());
            try { historyRead?.Cancel(); } finally { historyRead?.Dispose(); }
            RefreshHistoryPageButtons();
            var pendingDeckExport = WaitForDeckExportAsync();
            // Even a fully synchronous save must leave this Closing callback before calling Close again.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            await pendingDeckExport;
            await StopRecordingAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            _allowClose = false; _closing = false;
            RefreshKnowledgeControls();
            RefreshDeckExportControls();
            RefreshHistoryPageButtons();
            StatusText.Text = "记录尚未安全保存，请重试关闭：" + error.Message;
        }
    }

    private Task RefreshHistoryAsync() => LoadHistoryPageAsync(1, refresh: true);

    private SessionFilter HistoryFilter() => new(CardBuild: HistoryFilterText(StatsBuildBox.Text), DeckVersionId: HistoryFilterText(StatsDeckBox.Text),
        OpponentClass: HistoryFilterText(StatsOpponentBox.Text)?.ToUpperInvariant(),
        WentFirst: StatsFirstBox.SelectedIndex switch { 1 => true, 2 => false, _ => null },
        Format: "STANDARD", PlayerClass: "HUNTER", Region: "CN", GameType: RankedOnlyBox.IsChecked == true ? "GT_RANKED" : null);
    private static string? HistoryFilterText(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void ClearHistoryPageSelection()
    {
        _historyRevision++; _historyLoading = false;
        SessionsGrid.ItemsSource = null; DecisionsList.ItemsSource = null;
        if (_historyContext is null)
        { _historyObservedAt = null; EventsGrid.ItemsSource = null; HistorySnapshotText.Text = ""; }
        // A completed historical frame stays explicitly pinned while the list changes.
        // A new session selection still clears that frame before loading another session's decisions.
        if (_analysis is not null) InvalidateAdvice();
    }

    private async Task LoadHistoryPageAsync(int pageNumber, bool refresh = false)
    {
        if (_store is null || _closing) return;
        var request = _historyPaging.Begin(HistoryFilter(), pageNumber, DateTimeOffset.UtcNow, refresh);
        _historyListCancellation?.Cancel(); _historyListCancellation?.Dispose();
        _historyListCancellation = new CancellationTokenSource(); var token = _historyListCancellation.Token;
        HistoryPageText.Text = $"正在读取第 {request.PageNumber} 页…";
        StatsText.Text = "正在读取匹配范围的战绩…";
        RefreshHistoryPageButtons();
        try
        {
            var data = await Task.Run(async () =>
            {
                var decks = await _store.ListDeckVersionsAsync(token);
                var page = await _store.ListSessionPageAsync(request.Query, request.PageNumber, 100, token);
                var stats = await _store.GetStatsAsync(request.Query, token);
                return (Decks: decks.ToDictionary(deck => deck.Id), Page: page, Stats: stats);
            }, token);
            if (_closing || !_historyPaging.TryApply(request, data.Page)) return;
            ClearHistoryPageSelection();
            SessionsGrid.ItemsSource = data.Page.Sessions.Select(session => new SessionItem(session,
                session.DeckVersionId is { } id && data.Decks.TryGetValue(id, out var deck) ? $"{deck.Name} · {id[..6]}" : "未绑定套牌")).ToArray();
            var stats = data.Stats;
            StatsText.Text = $"{(request.Scope.GameType is null ? "标准猎人" : "标准猎人天梯")} · {stats.Games} 局 · {stats.Wins}胜 {stats.Losses}负 {stats.Ties}平 · 胜率 {(stats.WinRate is { } rate ? rate.ToString("P1") : "样本不足")} · 未完成/未知 {stats.Unknown} 局";
            HistoryPageText.Text = $"第 {data.Page.PageNumber} 页 · 本页 {data.Page.Sessions.Count} 条 · " +
                (data.Page.HasMore ? "后面还有记录" : "已到末页") + "（刷新可包含新记录）";
            HistoryStatusText.Text = _historyContext is { } pinned
                ? $"记录列表已更新；仍固定复盘 {pinned.SessionId} · #{pinned.Snapshot.EventSequence}。选择新对局再切换局面。"
                : "选择一局，再选择决策点查看当时信息。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (_closing || !_historyPaging.TryFail(request)) return;
            HistoryPageText.Text = "记录读取失败，请刷新重试。";
            StatsText.Text = "战绩读取失败，请刷新重试。";
            throw;
        }
        finally { if (!_closing && _historyPaging.IsCurrent(request)) RefreshHistoryPageButtons(); }
    }
    private void RefreshHistoryPageButtons()
    {
        var ready = _store is not null && !_historyPaging.IsLoading && !_closing;
        SessionsGrid.IsEnabled = ready;
        SessionPrevPageButton.IsEnabled = ready && _historyPaging.PageNumber > 1;
        SessionNextPageButton.IsEnabled = ready && _historyPaging.HasMore && _historyPaging.PageNumber < int.MaxValue;
    }
    private async void HistoryFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_store is null || _closing) return;
        _historyPaging.Reset(HistoryFilter());
        ClearHistoryPageSelection();
        await Run(RefreshHistoryAsync);
    }
    private async void HistoryPrevPageClicked(object sender, RoutedEventArgs e)
    {
        if (!_historyPaging.IsLoading && _historyPaging.PageNumber > 1)
            await Run(() => LoadHistoryPageAsync(_historyPaging.PageNumber - 1));
    }
    private async void HistoryNextPageClicked(object sender, RoutedEventArgs e)
    {
        if (!_historyPaging.IsLoading && _historyPaging.HasMore && _historyPaging.PageNumber < int.MaxValue)
            await Run(() => LoadHistoryPageAsync(_historyPaging.PageNumber + 1));
    }
    private async void RefreshHistoryClicked(object sender, RoutedEventArgs e) => await Run(RefreshHistoryAsync);
    private async void SessionSelected(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (SessionsGrid.SelectedItem is not SessionItem item) return;
        CancelKnowledgeNavigation();
        var revision = ++_historyRevision;
        _historyLoading = true;
        _historyContext = null; InvalidateAdvice();
        RenderSnapshot();
        DecisionsList.ItemsSource = null; EventsGrid.ItemsSource = null; HistorySnapshotText.Text = "正在读取决策点…";
        try
        {
            var points = await _store.ListDecisionPointsAsync(item.Value.Id, includeUnstable: true);
            if (revision != _historyRevision || (SessionsGrid.SelectedItem as SessionItem)?.Value.Id != item.Value.Id) return;
            DecisionsList.ItemsSource = points.Select(d => new DecisionItem(d)).ToArray();
            HistoryStatusText.Text = $"{item.Deck} · build {item.Value.CardBuild} · 选择一个决策点查看当时信息。";
        }
        finally { if (revision == _historyRevision) _historyLoading = false; }
    });
    private async void DecisionSelected(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (DecisionsList.SelectedItem is not DecisionItem item) return;
        CancelKnowledgeNavigation();
        var revision = ++_historyRevision;
        _historyLoading = true;
        _historyContext = null; InvalidateAdvice();
        RenderSnapshot();
        HistorySnapshotText.Text = "正在读取所选历史局面…";
        try
        {
            var selectedId = item.Value.Id;
            var data = await ReadHistoricalDecisionAsync(item.Value, CancellationToken.None);
            if (revision != _historyRevision || (DecisionsList.SelectedItem as DecisionItem)?.Value.Id != selectedId) return;
            PublishHistoricalDecision(data);
        }
        finally { if (revision == _historyRevision) { _historyLoading = false; RefreshAccessState(); } }
    });
    private sealed record HistoricalDecisionRead(DecisionPoint Point, HistoricalContext Context,
        IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> Gaps);
    private async Task<HistoricalDecisionRead> ReadHistoricalDecisionAsync(DecisionPoint point, CancellationToken token)
    {
        if (await _store.GetSessionAsync(point.SessionId, token) is null) throw new InvalidOperationException("原对局记录已清理或不存在；笔记仍保留。");
        var selected = await _store.GetHistoricalDecisionAsync(point.Id, cancellationToken: token)
            ?? throw new InvalidOperationException("所选精确快照或原对局绑定已不存在，不能用较早快照代替。");
        if (selected.Point != point) throw new InvalidOperationException("决策点记录已变更，请重新读取关联版本。");
        var context = selected.Context;
        var exact = context.Snapshot;
        if (exact.MatchId != point.SessionId || exact.EventSequence != point.EventSequence || exact.StateVersion != point.StateVersion)
            throw new InvalidOperationException("保存快照的对局、事件或状态版本与决策点不一致，无法打开。");
        var manifest = context.CardBuild == "unknown" ? null : await _catalog.GetManifestAsync(context.CardBuild, cancellationToken: token);
        var names = manifest is null ? new Dictionary<string, string>() :
            (await _catalog.GetByIdsAsync(exact.Entities.Values.Select(entity => entity.CardId).OfType<string>().Distinct().Take(500), context.CardBuild, cancellationToken: token))
            .ToDictionary(card => card.Id, card => card.Name);
        var gaps = new List<string>();
        if (selected.HasUnattributedEvents) gaps.Add("旧事件列表未记录同序号状态版本，无法归属的同序号事件已省略");
        if (context.DeckVersionId is null) gaps.Add("原对局未绑定套牌，未借用当前套牌");
        else if (context.Deck is null) gaps.Add($"原套牌版本 {context.DeckVersionId} 缺失");
        if (manifest is null) gaps.Add($"对应卡库 build {context.CardBuild} 缺失，名称及规则资料保持未知");
        if (exact.ClientBuild is { } observed && observed != context.CardBuild) gaps.Add($"快照 build {observed} 与原对局 build {context.CardBuild} 不一致，不能核验牌效");
        return new(point, context with { Snapshot = exact }, names, gaps.AsReadOnly());
    }
    private void PublishHistoricalDecision(HistoricalDecisionRead data)
    {
        var exact = data.Context.Snapshot;
        _historyContext = data.Context; _historyObservedAt = data.Point.RecordedAt;
        SessionBuildBox.Text = data.Context.CardBuild == "unknown" ? "" : data.Context.CardBuild;
        EventsGrid.ItemsSource = data.Context.RecentEvents;
        string Name(EntitySnapshot entity) => entity.CardId is { } card ? data.Names.GetValueOrDefault(card, card) : "身份未知";
        HistorySnapshotText.Text = $"对局 {data.Point.SessionId} · #{exact.EventSequence} · v{exact.StateVersion} · 录制于 {data.Point.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fffffff}\n" +
            $"回合 {exact.Turn} · {exact.Phase} · 行动方 {exact.ActivePlayerId}\n我方视角 {exact.LocalPlayerId} · 完整 {exact.Quality.IsComplete} · 稳定 {exact.Quality.IsStable}\n手牌：\n" +
            string.Join("\n", exact.Hand.Select(entity => $"({entity.Cost?.ToString() ?? "?"}) {Name(entity)}")) + "\n\n场面：\n" +
            string.Join("\n", exact.Board.Select(entity => $"玩家{entity.ControllerId} · {Name(entity)} {entity.Attack?.ToString() ?? "?"}/{entity.RemainingHealth?.ToString() ?? "?"}")) +
            "\n\n" + string.Join("；", exact.Quality.Warnings.Concat(data.Gaps));
        RenderSnapshot();
        HistoryStatusText.Text = $"正在查看原对局 {data.Point.SessionId} · 历史 #{exact.EventSequence} · v{exact.StateVersion}，知识截止为录制时刻 {data.Point.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fffffff}。" + string.Join("；", data.Gaps);
    }
    private void ReturnLiveClicked(object sender, RoutedEventArgs e)
    {
        CancelKnowledgeNavigation();
        _historyRevision++; _historyLoading = false; _historyContext = null; InvalidateAdvice();
        SessionBuildBox.Text = _engine.Snapshot().ClientBuild ?? "";
        RenderSnapshot();
        StatusText.Text = "已返回当前日志局面。";
    }
    private async void AnalyzeHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (_historyContext is null) { StatusText.Text = "请先选择历史决策点。"; return; }
        TaskBox.SelectedIndex = 2;
        MainTabs.SelectedIndex = MainTabs.Items.Count - 1;
        await Run(AnalyzeCurrentAsync);
    }
    private async void CleanupHistoryClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!int.TryParse(RetentionDaysBox.Text, out var days) || days is < 1 or > 3650) throw new ArgumentException("保留天数应在 1 至 3650 之间。");
        if (MessageBox.Show(this, $"删除超过 {days} 天的已结束对局记录？套牌版本将保留。", "清理历史记录", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var result = await _store.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-days));
        _historyRevision++; _historyLoading = false; _historyContext = null; InvalidateAdvice();
        await RefreshHistoryAsync();
        StatusText.Text = $"已清理 {result.SessionsDeleted} 局及其关联记录。";
    });
}
