using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Storage;

namespace HsHelper.App;

public sealed record KnowledgePageRequest(long Revision, CoachKnowledgeFilter Scope, DateTimeOffset AsOf, int PageNumber);

public sealed class KnowledgePagingState
{
    private long _revision;
    public CoachKnowledgeFilter? Scope { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int ItemCount { get; private set; }
    public bool HasMore { get; private set; }
    public bool IsLoading { get; private set; }
    public DateTimeOffset? AsOf { get; private set; }
    public void Reset(CoachKnowledgeFilter scope)
    { _revision++; Scope = scope; PageNumber = 1; ItemCount = 0; HasMore = false; IsLoading = false; AsOf = null; }
    public void Cancel() { _revision++; IsLoading = false; }
    public KnowledgePageRequest Begin(CoachKnowledgeFilter scope, int pageNumber, DateTimeOffset now, bool refresh = false)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (Scope != scope || refresh) { Reset(scope); pageNumber = 1; }
        AsOf ??= now; IsLoading = true;
        return new(++_revision, scope, AsOf.Value, pageNumber);
    }
    public bool IsCurrent(KnowledgePageRequest request) => request.Revision == _revision && request.Scope == Scope;
    public bool TryApply(KnowledgePageRequest request, CoachKnowledgePage page)
    {
        if (!IsCurrent(request) || page.PageNumber != request.PageNumber || page.AsOf != request.AsOf) return false;
        PageNumber = page.PageNumber; ItemCount = page.Entries.Count; HasMore = page.HasMore; IsLoading = false; return true;
    }
    public bool TryFail(KnowledgePageRequest request)
    { if (!IsCurrent(request)) return false; IsLoading = false; return true; }
}

public partial class MainWindow
{
    private CoachKnowledgeStore _knowledge = null!;
    private AdviceArchiveStore _adviceArchive = null!;
    private CoachKnowledgeVersion? _editingKnowledge;
    private long _knowledgeRevision;
    private bool _knowledgeSaving;
    private bool _changingKnowledgeScope;
    private readonly KnowledgePagingState _knowledgePaging = new();
    private CancellationTokenSource? _knowledgeListCancellation;
    private bool _updatingKnowledgeEditor;
    private bool _changingKnowledgeSelection;
    private string? _knowledgeEditorDeckId;
    private KnowledgeEditorValue? _knowledgeBaseline;
    private (string MatchId, long EventSequence)? _newNoteAnchor;
    private DateTimeOffset? _historyObservedAt;

    private sealed record KnowledgeItem(CoachKnowledgeVersion Value)
    {
        public string Label => $"{(Value.Kind == CoachKnowledgeKind.DeckPlan ? "计划" : "笔记")} · {Value.Title} · v{Value.Version}";
    }
    private sealed record KnowledgeEditorValue(int Kind, string Title, string Content, string Opponent, int First);
    private KnowledgeEditorValue KnowledgeEditorValues() => new(KnowledgeKindBox.SelectedIndex, KnowledgeTitleBox.Text,
        KnowledgeContentBox.Text, PlanOpponentBox.Text, PlanFirstBox.SelectedIndex);
    private bool KnowledgeHasChanges => _knowledgeBaseline is { } baseline && baseline != KnowledgeEditorValues() || _newNoteAnchor is not null;

    private async Task InitializeKnowledgeAsync()
    {
        _knowledge = new CoachKnowledgeStore(Path.Combine(_dataRoot, "knowledge.db"));
        _adviceArchive = new AdviceArchiveStore(Path.Combine(_dataRoot, "advice-archive.db"));
        await _knowledge.InitializeAsync(); await _adviceArchive.InitializeAsync();
        ClearKnowledgeEditor(discardChanges: true);
        await RefreshKnowledgeAsync();
    }
    private async void PlanDeckChanged(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (_loadingDecks || _changingKnowledgeScope || _knowledge is null || _closing) return;
        if (_knowledgeSaving || KnowledgeHasChanges)
        {
            _changingKnowledgeScope = true;
            try { PlanDeckBox.SelectedItem = PlanDeckBox.Items.OfType<DeckItem>().FirstOrDefault(item => item.Value.Id == _knowledgeEditorDeckId); }
            finally { _changingKnowledgeScope = false; }
            if (_knowledgeSaving) StatusText.Text = "计划或笔记正在保存，请稍后切换套牌。"; else WarnKnowledgeDraft();
            return;
        }
        ClearKnowledgeEditor();
        await RefreshKnowledgeAsync();
    });
    private Task RefreshKnowledgeAsync(string? selectId = null) => LoadKnowledgePageAsync(1, refresh: true);
    private CoachKnowledgeFilter KnowledgeFilter() => new(DeckVersionId: (PlanDeckBox.SelectedItem as DeckItem)?.Value.Id,
        Kind: KnowledgeTypeFilterBox.SelectedIndex switch { 1 => CoachKnowledgeKind.DeckPlan, 2 => CoachKnowledgeKind.MatchNote, _ => null })
        { TitleContains = KnowledgeTitleFilterBox.Text.Length == 0 ? null : KnowledgeTitleFilterBox.Text };
    private void CancelKnowledgeListRead()
    {
        _knowledgePaging.Cancel();
        var read = _knowledgeListCancellation; _knowledgeListCancellation = null;
        try { read?.Cancel(); } finally { read?.Dispose(); }
    }
    private async Task LoadKnowledgePageAsync(int pageNumber, bool refresh = false)
    {
        if (_knowledge is null || _closing) return;
        CancelKnowledgeListRead();
        var deck = (PlanDeckBox.SelectedItem as DeckItem)?.Value;
        var scope = KnowledgeFilter();
        if (_knowledgePaging.Scope != scope) SetKnowledgeList([]);
        var request = _knowledgePaging.Begin(scope, pageNumber, DateTimeOffset.UtcNow, refresh);
        var editorRevision = _knowledgeRevision;
        var cancellation = _knowledgeListCancellation = new CancellationTokenSource(); var token = cancellation.Token;
        KnowledgePageText.Text = $"正在读取第 {request.PageNumber} 页…";
        RefreshKnowledgeControls();
        try
        {
            var page = deck is null ? new CoachKnowledgePage(Array.AsReadOnly(Array.Empty<CoachKnowledgeVersion>()), 1, 100, false, request.AsOf)
                : await Task.Run(() => _knowledge.ListPageAsOfAsync(request.AsOf, request.Scope, request.PageNumber, 100, token), token);
            if (_closing || !_knowledgePaging.TryApply(request, page)) return;
            if (editorRevision == _knowledgeRevision && !KnowledgeHasChanges && _editingKnowledge is { } editing &&
                page.Entries.FirstOrDefault(entry => entry.EntryId == editing.EntryId) is { } latest && latest.Version > editing.Version)
                DisplayKnowledgeEntry(latest);
            SetKnowledgeList(page.Entries.Select(entry => new KnowledgeItem(entry)).ToArray());
            KnowledgeStatusText.Text = deck is null ? "先保存并选择一个套牌版本。" : $"{deck.Name} · {deck.Id} · 当前筛选的计划和笔记";
            KnowledgePageText.Text = $"第 {page.PageNumber} 页 · 本页 {page.Entries.Count} 条 · " + (page.HasMore ? "后面还有条目" : "已到末页") + "（刷新可包含新版本）";
            RefreshKnowledgeDraftStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (_closing || !_knowledgePaging.TryFail(request)) return;
            KnowledgePageText.Text = "计划和笔记读取失败，请刷新重试。"; throw;
        }
        finally
        {
            if (ReferenceEquals(_knowledgeListCancellation, cancellation)) { _knowledgeListCancellation = null; cancellation.Dispose(); }
            if (!_closing && _knowledgePaging.IsCurrent(request)) RefreshKnowledgeControls();
        }
    }
    private void SetKnowledgeList(KnowledgeItem[] items)
    {
        _changingKnowledgeSelection = true;
        try
        {
            KnowledgeList.ItemsSource = items;
            KnowledgeList.SelectedItem = _editingKnowledge is { } editing ? items.FirstOrDefault(item => item.Value.EntryId == editing.EntryId) : null;
        }
        finally { _changingKnowledgeSelection = false; }
    }
    private void RefreshKnowledgeControls()
    {
        if (KnowledgeList is null || KnowledgePrevPageButton is null) return;
        var ready = _knowledge is not null && !_closing && !_knowledgeSaving;
        KnowledgeList.IsEnabled = ready && !_knowledgePaging.IsLoading;
        KnowledgeEditor.IsEnabled = PlanDeckBox.IsEnabled = ready;
        KnowledgeTypeFilterBox.IsEnabled = KnowledgeTitleFilterBox.IsEnabled = KnowledgeRefreshButton.IsEnabled = ready;
        SaveKnowledgeButton.IsEnabled = ready;
        KnowledgePrevPageButton.IsEnabled = ready && !_knowledgePaging.IsLoading && _knowledgePaging.PageNumber > 1;
        KnowledgeNextPageButton.IsEnabled = ready && !_knowledgePaging.IsLoading && _knowledgePaging.HasMore && _knowledgePaging.PageNumber < int.MaxValue;
        DiscardKnowledgeChangesButton.IsEnabled = ready && KnowledgeHasChanges;
    }
    private void RefreshKnowledgeDraftStatus()
    {
        if (KnowledgeEditorStatusText is null) return;
        var outside = _editingKnowledge is { } editing && !KnowledgeList.Items.OfType<KnowledgeItem>().Any(item => item.Value.EntryId == editing.EntryId);
        var newer = _editingKnowledge is { } current && KnowledgeList.Items.OfType<KnowledgeItem>().Any(item => item.Value.EntryId == current.EntryId && item.Value.Version > current.Version);
        KnowledgeEditorStatusText.Text = KnowledgeHasChanges ? "有未保存修改。翻页和筛选会保留草稿；切换条目前请保存，或明确放弃未保存修改。"
            : outside ? "仍显示刚才选中的条目；它不在当前页或筛选中。编辑和删除仍绑定原条目。" : "";
        if (newer) KnowledgeEditorStatusText.Text += " 此条目已有较新版本；草稿尚未覆盖它。放弃未保存修改会重新读取最新内容。";
    }
    private void WarnKnowledgeDraft()
    { StatusText.Text = "有未保存修改，请先保存，或点击“放弃未保存修改”后再切换。"; RefreshKnowledgeDraftStatus(); }
    private bool TryLeaveKnowledgeEditor()
    {
        if (_knowledgeSaving) { StatusText.Text = "计划或笔记正在保存，窗口将保持打开；请保存完成后再关闭。"; return false; }
        if (!KnowledgeHasChanges) return true;
        MainTabs.SelectedItem = KnowledgeTab; WarnKnowledgeDraft();
        StatusText.Text = "计划或笔记有未保存修改，窗口将保持打开。请先保存，或点击“放弃未保存修改”后再关闭。";
        return false;
    }
    private void KnowledgeEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingKnowledgeEditor || KnowledgeContentBox is null || PlanFirstBox is null) return;
        _knowledgeRevision++; RefreshKnowledgeDraftStatus(); RefreshKnowledgeControls();
    }
    private async void KnowledgeFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_knowledge is null || _knowledgeSaving || _closing) return;
        await Run(() => LoadKnowledgePageAsync(1, refresh: true));
    }
    private async void KnowledgePrevPageClicked(object sender, RoutedEventArgs e)
    { if (!_knowledgeSaving && !_knowledgePaging.IsLoading && _knowledgePaging.PageNumber > 1) await Run(() => LoadKnowledgePageAsync(_knowledgePaging.PageNumber - 1)); }
    private async void KnowledgeNextPageClicked(object sender, RoutedEventArgs e)
    { if (!_knowledgeSaving && !_knowledgePaging.IsLoading && _knowledgePaging.HasMore && _knowledgePaging.PageNumber < int.MaxValue) await Run(() => LoadKnowledgePageAsync(_knowledgePaging.PageNumber + 1)); }
    private async void RefreshKnowledgeClicked(object sender, RoutedEventArgs e)
    { if (!_knowledgeSaving) await Run(() => RefreshKnowledgeAsync()); }
    private void NewKnowledgeClicked(object sender, RoutedEventArgs e)
    { if (!_knowledgeSaving && !_closing) ClearKnowledgeEditor(); }
    private async void DiscardKnowledgeChangesClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_knowledgeSaving || _closing) return;
        if (_editingKnowledge is not { } entry) { ClearKnowledgeEditor(discardChanges: true); return; }
        var revision = _knowledgeRevision;
        var latest = await _knowledge.GetAsOfAsync(entry.EntryId, DateTimeOffset.UtcNow);
        if (_closing || revision != _knowledgeRevision || _editingKnowledge?.EntryId != entry.EntryId) return;
        if (latest is null)
        {
            ClearKnowledgeEditor(discardChanges: true);
            StatusText.Text = "条目已在其他操作中删除，已清除编辑；历史版本仍保留。";
        }
        else DisplayKnowledgeEntry(latest);
        await RefreshKnowledgeAsync();
    });
    private bool ClearKnowledgeEditor(bool discardChanges = false)
    {
        if (!discardChanges && KnowledgeHasChanges) { WarnKnowledgeDraft(); return false; }
        _knowledgeRevision++;
        _editingKnowledge = null; _newNoteAnchor = null;
        _knowledgeEditorDeckId = (PlanDeckBox.SelectedItem as DeckItem)?.Value.Id;
        if (KnowledgeList is null) return true;
        _updatingKnowledgeEditor = true; _changingKnowledgeSelection = true;
        try
        {
            KnowledgeList.SelectedItem = null; KnowledgeKindBox.SelectedIndex = 0; KnowledgeKindBox.IsEnabled = true;
            KnowledgeTitleBox.Text = ""; KnowledgeContentBox.Text = "";
            PlanOpponentBox.Text = ""; PlanFirstBox.SelectedIndex = 0;
            _knowledgeBaseline = KnowledgeEditorValues();
        }
        finally { _updatingKnowledgeEditor = false; _changingKnowledgeSelection = false; }
        KnowledgeVersionText.Text = "新条目。保存时记录版本，历史分析只能查询当时已保存的内容。";
        RefreshKnowledgeDraftStatus(); RefreshKnowledgeControls(); return true;
    }
    private void KnowledgeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_changingKnowledgeSelection || _closing || KnowledgeList.SelectedItem is not KnowledgeItem item) return;
        if (_knowledgeSaving || KnowledgeHasChanges)
        {
            _changingKnowledgeSelection = true;
            try { KnowledgeList.SelectedItem = KnowledgeList.Items.OfType<KnowledgeItem>().FirstOrDefault(row => row.Value.EntryId == _editingKnowledge?.EntryId); }
            finally { _changingKnowledgeSelection = false; }
            WarnKnowledgeDraft(); return;
        }
        DisplayKnowledgeEntry(item.Value);
    }
    private void DisplayKnowledgeEntry(CoachKnowledgeVersion value)
    {
        _editingKnowledge = value; _knowledgeEditorDeckId = value.DeckVersionId;
        _knowledgeRevision++;
        _newNoteAnchor = null;
        _updatingKnowledgeEditor = true;
        try
        {
            KnowledgeKindBox.SelectedIndex = value.Kind == CoachKnowledgeKind.DeckPlan ? 0 : 1;
            KnowledgeKindBox.IsEnabled = false;
            KnowledgeTitleBox.Text = value.Title; KnowledgeContentBox.Text = value.Content;
            PlanOpponentBox.Text = value.OpponentClass ?? "";
            PlanFirstBox.SelectedIndex = value.WentFirst switch { true => 1, false => 2, _ => 0 };
            _knowledgeBaseline = KnowledgeEditorValues();
        }
        finally { _updatingKnowledgeEditor = false; }
        KnowledgeVersionText.Text = $"{value.Source} · v{value.Version} · {value.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
            (value.MatchId is null ? "" : $" · 对局 {value.MatchId} · 事件 {value.EventSequence?.ToString() ?? "未指定"}");
        RefreshKnowledgeDraftStatus(); RefreshKnowledgeControls();
    }
    private async void SaveKnowledgeClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_knowledgeSaving || _closing) return;
        var deck = (PlanDeckBox.SelectedItem as DeckItem)?.Value ?? throw new InvalidOperationException("请先选择套牌版本。");
        var editing = _editingKnowledge;
        var deckId = editing?.DeckVersionId ?? _knowledgeEditorDeckId ?? deck.Id;
        if (deckId != deck.Id) { WarnKnowledgeDraft(); return; }
        var revision = _knowledgeRevision;
        var kind = KnowledgeKindBox.SelectedIndex == 0 ? CoachKnowledgeKind.DeckPlan : CoachKnowledgeKind.MatchNote;
        var anchor = kind == CoachKnowledgeKind.MatchNote ? _newNoteAnchor : null;
        var draft = new CoachKnowledgeDraft(deckId, kind, KnowledgeTitleBox.Text, KnowledgeContentBox.Text,
            string.IsNullOrWhiteSpace(PlanOpponentBox.Text) ? null : PlanOpponentBox.Text.Trim().ToUpperInvariant(),
            PlanFirstBox.SelectedIndex switch { 1 => true, 2 => false, _ => null }, editing?.MatchId ?? anchor?.MatchId)
        { EventSequence = editing?.EventSequence ?? anchor?.EventSequence };
        CancelKnowledgeListRead(); _knowledgeSaving = true; RefreshKnowledgeControls();
        try
        {
            var saved = await _knowledge.SaveAsync(draft, editing?.EntryId, editing?.Version);
            if (_closing) return;
            if (revision == _knowledgeRevision) { DisplayKnowledgeEntry(saved); await RefreshKnowledgeAsync(saved.EntryId); }
            StatusText.Text = "计划或笔记已保存为新版本，旧内容保留供历史查询。";
        }
        finally
        {
            _knowledgeSaving = false; if (!_closing) RefreshKnowledgeControls();
        }
    });
    private async void DeleteKnowledgeClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_knowledgeSaving || _closing || _editingKnowledge is not { } entry) return;
        if (KnowledgeHasChanges) { WarnKnowledgeDraft(); return; }
        var revision = _knowledgeRevision;
        CancelKnowledgeListRead(); _knowledgeSaving = true; RefreshKnowledgeControls();
        try
        {
            await _knowledge.DeleteAsync(entry.EntryId, entry.Version);
            if (_closing) return;
            if (revision == _knowledgeRevision) { ClearKnowledgeEditor(discardChanges: true); await RefreshKnowledgeAsync(); }
            StatusText.Text = "条目已删除，历史版本仍保留。";
        }
        finally { _knowledgeSaving = false; if (!_closing) RefreshKnowledgeControls(); }
    });
    private async void MarkDecisionClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_closing) return;
        if (_knowledgeSaving) { StatusText.Text = "计划或笔记正在保存，请稍后标记。"; return; }
        if (KnowledgeHasChanges) { WarnKnowledgeDraft(); return; }
        var state = DisplaySnapshot;
        var deckId = _historyContext?.DeckVersionId ?? _sourceDeckVersion?.Id;
        if (deckId is null || !state.Quality.HasGameStart) { StatusText.Text = "请先选择已绑定套牌的实际局面。"; return; }
        _changingKnowledgeScope = true;
        try { PlanDeckBox.SelectedItem = PlanDeckBox.Items.OfType<DeckItem>().FirstOrDefault(d => d.Value.Id == deckId); }
        finally { _changingKnowledgeScope = false; }
        ClearKnowledgeEditor();
        var refresh = RefreshKnowledgeAsync(); var revision = _knowledgeRevision;
        await refresh;
        if (revision != _knowledgeRevision || (PlanDeckBox.SelectedItem as DeckItem)?.Value.Id != deckId) return;
        _updatingKnowledgeEditor = true;
        try
        {
            KnowledgeKindBox.SelectedIndex = 1;
            _newNoteAnchor = (state.MatchId, state.EventSequence);
            KnowledgeTitleBox.Text = $"关键回合 {state.Turn?.ToString() ?? "?"} · #{state.EventSequence}";
            KnowledgeContentBox.Text = "记录这个决策点的想法、备选和复盘问题：\n";
            PlanOpponentBox.Text = state.OpponentClass ?? "";
            PlanFirstBox.SelectedIndex = state.FirstPlayerId is null || state.LocalPlayerId is null ? 0 : state.FirstPlayerId == state.LocalPlayerId ? 1 : 2;
            _knowledgeRevision++;
        }
        finally { _updatingKnowledgeEditor = false; }
        KnowledgeVersionText.Text = $"关联对局 {state.MatchId}，事件 {state.EventSequence}；编辑后点击保存。";
        RefreshKnowledgeDraftStatus(); RefreshKnowledgeControls();
        MainTabs.SelectedItem = KnowledgeTab;
    });
    private async void ViewAdviceArchiveClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var match = (SessionsGrid.SelectedItem as SessionItem)?.Value.Id ?? DisplaySnapshot.MatchId;
        var records = await _adviceArchive.ListAsync(new AdviceArchiveFilter(MatchId: match));
        ShowAdviceArchive(records);
    });
    private void ShowAdviceArchive(IReadOnlyList<ArchivedAdviceRecord> records)
    {
        var list = new ListBox { Margin = new Thickness(12), DisplayMemberPath = "Label", ItemsSource = records.Select(r => new ArchiveItem(r)).ToArray() };
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ArchiveItem item) text.Text = RenderArchivedAdvice(item.Value);
        };
        var panel = new Grid(); panel.ColumnDefinitions.Add(new() { Width = new GridLength(260) }); panel.ColumnDefinitions.Add(new());
        Grid.SetColumn(text, 1); panel.Children.Add(list); panel.Children.Add(text);
        var window = new Window { Owner = this, Title = "本局建议记录（事后资料）", Width = 900, Height = 650, Content = panel };
        if (records.Count == 0) text.Text = "该局尚无已保存的建议。";
        window.Show(); list.SelectedIndex = records.Count == 0 ? -1 : 0;
    }
    private sealed record ArchiveItem(ArchivedAdviceRecord Value)
    {
        public string Label => $"{Value.RecordedAt.ToLocalTime():MM-dd HH:mm:ss} · v{Value.Advice.StateVersion} · {Value.Advice.Task}";
    }
    private static string RenderArchivedAdvice(ArchivedAdviceRecord value)
    {
        var a = value.Advice;
        var text = new StringBuilder($"{a.Task} · v{a.StateVersion} · #{a.EventSequence} · {a.Verification}\n{a.Summary}\n\n依据\n");
        text.Insert(0, $"模型：{a.Model ?? "旧记录未保存来源"} · 模板：{a.PromptTemplateVersion}\n分析：{a.AnalysisRunId ?? "未知"}\n\n");
        if (!string.IsNullOrWhiteSpace(a.UserQuestion)) text.Insert(0, $"当时提问：{a.UserQuestion}\n\n");
        foreach (var reason in a.Reasons) text.AppendLine(reason);
        text.AppendLine($"\n备选\n{a.Alternative ?? "无"}\n\n风险");
        foreach (var risk in a.Risks) text.AppendLine(risk);
        text.AppendLine("\n工具依据");
        foreach (var evidence in a.Evidence) text.AppendLine($"{evidence.ToolName} · {evidence.Summary}");
        text.AppendLine("\n这是当时生成的建议记录，不能继续当作当前可执行步骤。");
        return text.ToString();
    }
}
