using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private CoachKnowledgeStore _knowledge = null!;
    private AdviceArchiveStore _adviceArchive = null!;
    private CoachKnowledgeVersion? _editingKnowledge;
    private long _knowledgeRevision;
    private bool _knowledgeSaving;
    private bool _changingKnowledgeScope;
    private (string MatchId, long EventSequence)? _newNoteAnchor;
    private DateTimeOffset? _historyObservedAt;

    private sealed record KnowledgeItem(CoachKnowledgeVersion Value)
    {
        public string Label => $"{(Value.Kind == CoachKnowledgeKind.DeckPlan ? "计划" : "笔记")} · {Value.Title} · v{Value.Version}";
    }

    private async Task InitializeKnowledgeAsync()
    {
        _knowledge = new CoachKnowledgeStore(Path.Combine(_dataRoot, "knowledge.db"));
        _adviceArchive = new AdviceArchiveStore(Path.Combine(_dataRoot, "advice-archive.db"));
        await _knowledge.InitializeAsync(); await _adviceArchive.InitializeAsync();
        await RefreshKnowledgeAsync();
    }
    private async void PlanDeckChanged(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (_loadingDecks || _changingKnowledgeScope || _knowledge is null) return;
        _knowledgeRevision++; ClearKnowledgeEditor();
        await RefreshKnowledgeAsync();
    });
    private async Task RefreshKnowledgeAsync(string? selectId = null)
    {
        if (_knowledge is null) return;
        var revision = ++_knowledgeRevision;
        var deck = (PlanDeckBox.SelectedItem as DeckItem)?.Value;
        var items = deck is null ? [] : (await _knowledge.ListAsync(new CoachKnowledgeFilter(DeckVersionId: deck.Id)))
            .Select(k => new KnowledgeItem(k)).ToArray();
        if (revision != _knowledgeRevision) return;
        KnowledgeList.ItemsSource = items;
        KnowledgeList.SelectedItem = items.FirstOrDefault(i => i.Value.EntryId == selectId);
        KnowledgeStatusText.Text = deck is null ? "先保存并选择一个套牌版本。" : $"{deck.Name} · {deck.Id} · {items.Length} 条计划和笔记";
    }
    private async void RefreshKnowledgeClicked(object sender, RoutedEventArgs e) => await Run(() => RefreshKnowledgeAsync());
    private void NewKnowledgeClicked(object sender, RoutedEventArgs e) => ClearKnowledgeEditor();
    private void ClearKnowledgeEditor()
    {
        _knowledgeRevision++;
        _editingKnowledge = null; _newNoteAnchor = null;
        if (KnowledgeList is null) return;
        KnowledgeList.SelectedItem = null; KnowledgeKindBox.IsEnabled = true;
        KnowledgeTitleBox.Text = ""; KnowledgeContentBox.Text = "";
        PlanOpponentBox.Text = ""; PlanFirstBox.SelectedIndex = 0;
        KnowledgeVersionText.Text = "新条目。保存时记录版本，历史分析只能查询当时已保存的内容。";
    }
    private void KnowledgeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (KnowledgeList.SelectedItem is not KnowledgeItem item) return;
        var value = _editingKnowledge = item.Value;
        _knowledgeRevision++;
        _newNoteAnchor = null;
        KnowledgeKindBox.SelectedIndex = value.Kind == CoachKnowledgeKind.DeckPlan ? 0 : 1;
        KnowledgeKindBox.IsEnabled = false;
        KnowledgeTitleBox.Text = value.Title; KnowledgeContentBox.Text = value.Content;
        PlanOpponentBox.Text = value.OpponentClass ?? "";
        PlanFirstBox.SelectedIndex = value.WentFirst switch { true => 1, false => 2, _ => 0 };
        KnowledgeVersionText.Text = $"{value.Source} · v{value.Version} · {value.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
            (value.MatchId is null ? "" : $" · 对局 {value.MatchId} · 事件 {value.EventSequence?.ToString() ?? "未指定"}");
    }
    private async void SaveKnowledgeClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_knowledgeSaving) return;
        var deck = (PlanDeckBox.SelectedItem as DeckItem)?.Value ?? throw new InvalidOperationException("请先选择套牌版本。");
        var editing = _editingKnowledge;
        var revision = _knowledgeRevision;
        var kind = KnowledgeKindBox.SelectedIndex == 0 ? CoachKnowledgeKind.DeckPlan : CoachKnowledgeKind.MatchNote;
        var anchor = kind == CoachKnowledgeKind.MatchNote ? _newNoteAnchor : null;
        var draft = new CoachKnowledgeDraft(deck.Id, kind, KnowledgeTitleBox.Text, KnowledgeContentBox.Text,
            string.IsNullOrWhiteSpace(PlanOpponentBox.Text) ? null : PlanOpponentBox.Text.Trim().ToUpperInvariant(),
            PlanFirstBox.SelectedIndex switch { 1 => true, 2 => false, _ => null }, editing?.MatchId ?? anchor?.MatchId)
        { EventSequence = editing?.EventSequence ?? anchor?.EventSequence };
        _knowledgeSaving = true; SaveKnowledgeButton.IsEnabled = false;
        KnowledgeEditor.IsEnabled = false; PlanDeckBox.IsEnabled = false; KnowledgeList.IsEnabled = false;
        try
        {
            var saved = await _knowledge.SaveAsync(draft, editing?.EntryId, editing?.Version);
            if (revision == _knowledgeRevision) await RefreshKnowledgeAsync(saved.EntryId);
            StatusText.Text = "计划或笔记已保存为新版本，旧内容保留供历史查询。";
        }
        finally
        {
            _knowledgeSaving = false; SaveKnowledgeButton.IsEnabled = true;
            KnowledgeEditor.IsEnabled = true; PlanDeckBox.IsEnabled = true; KnowledgeList.IsEnabled = true;
        }
    });
    private async void DeleteKnowledgeClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_knowledgeSaving || _editingKnowledge is not { } entry) return;
        var revision = _knowledgeRevision;
        _knowledgeSaving = true; KnowledgeEditor.IsEnabled = false; PlanDeckBox.IsEnabled = false; KnowledgeList.IsEnabled = false;
        try
        {
            await _knowledge.DeleteAsync(entry.EntryId, entry.Version);
            if (revision == _knowledgeRevision) { ClearKnowledgeEditor(); await RefreshKnowledgeAsync(); }
            StatusText.Text = "条目已删除，历史版本仍保留。";
        }
        finally { _knowledgeSaving = false; KnowledgeEditor.IsEnabled = true; PlanDeckBox.IsEnabled = true; KnowledgeList.IsEnabled = true; }
    });
    private async void MarkDecisionClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_knowledgeSaving) { StatusText.Text = "计划或笔记正在保存，请稍后标记。"; return; }
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
        KnowledgeKindBox.SelectedIndex = 1;
        _newNoteAnchor = (state.MatchId, state.EventSequence);
        KnowledgeTitleBox.Text = $"关键回合 {state.Turn?.ToString() ?? "?"} · #{state.EventSequence}";
        KnowledgeContentBox.Text = "记录这个决策点的想法、备选和复盘问题：\n";
        PlanOpponentBox.Text = state.OpponentClass ?? "";
        PlanFirstBox.SelectedIndex = state.FirstPlayerId is null || state.LocalPlayerId is null ? 0 : state.FirstPlayerId == state.LocalPlayerId ? 1 : 2;
        KnowledgeVersionText.Text = $"关联对局 {state.MatchId}，事件 {state.EventSequence}；编辑后点击保存。";
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
