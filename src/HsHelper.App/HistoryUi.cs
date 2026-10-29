using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Core;
using HsHelper.Storage;

namespace HsHelper.App;

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
        _loadingDecks = true;
        try
        {
            var items = (await _store.ListDeckVersionsAsync()).Select(d => new DeckItem(d)).ToArray();
            SavedDeckBox.ItemsSource = items;
            SavedDeckBox.SelectedItem = items.FirstOrDefault(d => d.Value.Id == (selectId ?? _selectedDeckVersion?.Id));
        }
        finally { _loadingDecks = false; }
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
        if (_store is not null) await RefreshHistoryAsync();
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _closing = true;
        try { await StopRecordingAsync(); _allowClose = true; Close(); }
        catch (Exception error) { _closing = false; StatusText.Text = "记录尚未安全保存，请重试关闭：" + error.Message; }
    }

    private async Task RefreshHistoryAsync()
    {
        var decks = (await _store.ListDeckVersionsAsync()).ToDictionary(d => d.Id);
        var filter = new SessionFilter(CardBuild: Null(StatsBuildBox.Text), DeckVersionId: Null(StatsDeckBox.Text),
            OpponentClass: Null(StatsOpponentBox.Text), WentFirst: StatsFirstBox.SelectedIndex switch { 1 => true, 2 => false, _ => null },
            Format: "STANDARD", PlayerClass: "HUNTER", Region: "CN", GameType: RankedOnlyBox.IsChecked == true ? "GT_RANKED" : null);
        var sessions = await _store.ListSessionsAsync(filter);
        SessionsGrid.ItemsSource = sessions.Select(s => new SessionItem(s,
            s.DeckVersionId is { } id && decks.TryGetValue(id, out var deck) ? $"{deck.Name} · {id[..6]}" : "未绑定套牌")).ToArray();
        var stats = await _store.GetStatsAsync(filter);
        StatsText.Text = $"{(filter.GameType is null ? "标准猎人" : "标准猎人天梯")} · {stats.Games} 局 · {stats.Wins}胜 {stats.Losses}负 {stats.Ties}平 · 胜率 {(stats.WinRate is { } rate ? rate.ToString("P1") : "样本不足")} · 未完成/未知 {stats.Unknown} 局";
        static string? Null(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
    private async void RefreshHistoryClicked(object sender, RoutedEventArgs e) => await Run(RefreshHistoryAsync);
    private async void SessionSelected(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (SessionsGrid.SelectedItem is not SessionItem item) return;
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
        var revision = ++_historyRevision;
        _historyLoading = true;
        _historyContext = null; InvalidateAdvice();
        RenderSnapshot();
        HistorySnapshotText.Text = "正在读取所选历史局面…";
        try
        {
            var selectedId = item.Value.Id;
            var context = await _store.GetHistoricalContextAtAsync(item.Value.SessionId, item.Value.EventSequence);
            if (revision != _historyRevision) return;
            var exact = await _store.GetDecisionSnapshotAsync(selectedId);
            if (revision != _historyRevision || context is null || exact is null || (DecisionsList.SelectedItem as DecisionItem)?.Value.Id != selectedId) return;
            var names = await _catalog.GetByIdsAsync(exact.Entities.Values.Select(x => x.CardId).OfType<string>().Distinct().Take(500), context.CardBuild);
            if (revision != _historyRevision || (DecisionsList.SelectedItem as DecisionItem)?.Value.Id != selectedId) return;
            _historyContext = context with { Snapshot = exact };
            SessionBuildBox.Text = context.CardBuild == "unknown" ? "" : context.CardBuild;
            EventsGrid.ItemsSource = context.RecentEvents;
            var lookup = names.ToDictionary(c => c.Id, c => c.Name);
            string Name(EntitySnapshot entity) => entity.CardId is { } card ? lookup.GetValueOrDefault(card, card) : "身份未知";
            HistorySnapshotText.Text = $"回合 {exact.Turn} · {exact.Phase} · 行动方 {exact.ActivePlayerId}\n" +
                $"我方视角 {exact.LocalPlayerId} · 完整 {exact.Quality.IsComplete} · 稳定 {exact.Quality.IsStable}\n" +
                "手牌：\n" + string.Join("\n", exact.Hand.Select(c => $"({c.Cost?.ToString() ?? "?"}) {Name(c)}")) +
                "\n\n场面：\n" + string.Join("\n", exact.Board.Select(c => $"玩家{c.ControllerId} · {Name(c)} {c.Attack?.ToString() ?? "?"}/{c.RemainingHealth?.ToString() ?? "?"}")) +
                "\n\n" + string.Join("；", exact.Quality.Warnings);
            RenderSnapshot();
            HistoryStatusText.Text = $"正在查看历史 #{exact.EventSequence}，AI 只能读取该序号及以前的信息。";
        }
        finally { if (revision == _historyRevision) _historyLoading = false; }
    });
    private void ReturnLiveClicked(object sender, RoutedEventArgs e)
    {
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
