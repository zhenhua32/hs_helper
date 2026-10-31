using System.Windows;
using System.Windows.Controls;
using HsHelper.Storage;

namespace HsHelper.App;

public sealed record KnowledgeNavigationRequest(long Revision, CoachKnowledgeVersion Note, long KnowledgeRevision,
    long HistoryRevision, long SourceGeneration, string SourceMatchId);
public sealed class KnowledgeNavigationState
{
    private long _revision;
    public KnowledgeNavigationRequest Begin(CoachKnowledgeVersion note, long knowledgeRevision, long historyRevision, long sourceGeneration, string sourceMatchId)
        => new(++_revision, note, knowledgeRevision, historyRevision, sourceGeneration, sourceMatchId);
    public void Cancel() => _revision++;
    public bool IsCurrent(KnowledgeNavigationRequest request, CoachKnowledgeVersion? note, long knowledgeRevision, long historyRevision, long sourceGeneration, string sourceMatchId)
        => request.Revision == _revision && note?.EntryId == request.Note.EntryId && note.Version == request.Note.Version &&
            note.MatchId == request.Note.MatchId && note.EventSequence == request.Note.EventSequence && knowledgeRevision == request.KnowledgeRevision &&
            historyRevision == request.HistoryRevision && sourceGeneration == request.SourceGeneration && sourceMatchId == request.SourceMatchId;
}

public partial class MainWindow
{
    private readonly KnowledgeNavigationState _knowledgeNavigation = new();
    private CancellationTokenSource? _knowledgeNavigationCancellation;
    private long? _knowledgeNavigationHistoryRevision;
    private bool _knowledgeNavigationLoading;
    private sealed record KnowledgeDecisionOption(DecisionPoint Point)
    { public string Label => $"v{Point.StateVersion} · #{Point.EventSequence} · {Point.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fffffff} · {(Point.IsStable ? "稳定" : "未稳定")}/{(Point.IsComplete ? "完整" : "不完整")}"; }

    private string NavigationSourceMatchId => (_liveFrame ?? _engine.Snapshot()).MatchId;
    private bool CanOpenKnowledgeAnchor => _editingKnowledge is { Kind: CoachKnowledgeKind.MatchNote, MatchId: not null, EventSequence: >= 0 } &&
        !KnowledgeHasChanges && !_knowledgeSaving && !_closing;
    private void RefreshKnowledgeNavigationControls()
    {
        if (OpenKnowledgeSnapshotButton is null) return;
        OpenKnowledgeSnapshotButton.IsEnabled = CanOpenKnowledgeAnchor && !_knowledgeNavigationLoading;
        KnowledgeSnapshotVersionBox.IsEnabled = CanOpenKnowledgeAnchor && !_knowledgeNavigationLoading;
    }
    private void CancelKnowledgeNavigation(string? reason = null)
    {
        _knowledgeNavigation.Cancel();
        var read = _knowledgeNavigationCancellation; _knowledgeNavigationCancellation = null;
        try { read?.Cancel(); } finally { read?.Dispose(); }
        if (_knowledgeNavigationHistoryRevision is { } owned && owned == _historyRevision)
        { _historyRevision++; _historyLoading = false; if (!_closing) RefreshAccessState(); }
        _knowledgeNavigationHistoryRevision = null; _knowledgeNavigationLoading = false;
        if (KnowledgeSnapshotVersionBox is not null)
        {
            KnowledgeSnapshotVersionBox.ItemsSource = null; KnowledgeSnapshotVersionBox.Visibility = Visibility.Collapsed;
            KnowledgeNavigationStatusText.Text = reason ?? "";
        }
        RefreshKnowledgeNavigationControls();
    }
    private bool KnowledgeNavigationIsCurrent(KnowledgeNavigationRequest request, CancellationToken token) => !_closing && !token.IsCancellationRequested &&
        !_knowledgeSaving && !KnowledgeHasChanges && _knowledgeNavigation.IsCurrent(request, _editingKnowledge, _knowledgeRevision, _historyRevision, _sourceGeneration, NavigationSourceMatchId);
    private async void OpenKnowledgeSnapshotClicked(object sender, RoutedEventArgs e)
    {
        if (!CanOpenKnowledgeAnchor || _editingKnowledge is not { } note)
        { if (KnowledgeHasChanges) WarnKnowledgeDraft(); return; }
        var selected = (KnowledgeSnapshotVersionBox.SelectedItem as KnowledgeDecisionOption)?.Point;
        if (KnowledgeSnapshotVersionBox.Items.Count > 1 && selected is null)
        { KnowledgeNavigationStatusText.Text = "同一事件有多个实际保存版本，请先明确选择一个版本。"; return; }
        CancelKnowledgeNavigation();
        var cancellation = _knowledgeNavigationCancellation = new CancellationTokenSource(); var token = cancellation.Token;
        var request = _knowledgeNavigation.Begin(note, _knowledgeRevision, _historyRevision, _sourceGeneration, NavigationSourceMatchId);
        _knowledgeNavigationLoading = true; RefreshKnowledgeNavigationControls();
        KnowledgeNavigationStatusText.Text = "正在按笔记原对局及精确事件查找保存局面…";
        try
        {
            if (selected is null)
            {
                var points = await Task.Run(async () =>
                {
                    if (await _store.GetSessionAsync(note.MatchId!, token) is null) throw new InvalidOperationException("原对局记录已清理或不存在；笔记仍保留。");
                    return (await _store.ListDecisionPointsAsync(note.MatchId!, includeUnstable: true, token))
                        .Where(point => point.EventSequence == note.EventSequence).ToArray();
                }, token);
                if (!KnowledgeNavigationIsCurrent(request, token)) return;
                if (points.Length == 0) { KnowledgeNavigationStatusText.Text = $"原对局没有事件 #{note.EventSequence} 的精确快照，不能用更早快照代替；笔记仍保留。"; return; }
                if (points.Length > 1)
                {
                    KnowledgeSnapshotVersionBox.ItemsSource = points.Select(point => new KnowledgeDecisionOption(point)).ToArray();
                    KnowledgeSnapshotVersionBox.SelectedIndex = -1; KnowledgeSnapshotVersionBox.Visibility = Visibility.Visible;
                    KnowledgeNavigationStatusText.Text = $"事件 #{note.EventSequence} 有 {points.Length} 个实际版本，笔记未保存状态版本。请选择后再打开。";
                    return;
                }
                selected = points[0];
            }
            if (!KnowledgeNavigationIsCurrent(request, token)) return;
            request = request with { HistoryRevision = ++_historyRevision };
            _knowledgeNavigationHistoryRevision = request.HistoryRevision; _historyLoading = true; InvalidateAdvice(); RefreshAccessState();
            var data = await ReadHistoricalDecisionAsync(selected, token);
            if (!KnowledgeNavigationIsCurrent(request, token)) return;
            SessionsGrid.SelectedItem = null; DecisionsList.ItemsSource = null;
            PublishHistoricalDecision(data);
            KnowledgeNavigationStatusText.Text = $"已打开原对局 {data.Point.SessionId} · #{data.Point.EventSequence} · v{data.Point.StateVersion}，录制于 {data.Point.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fffffff}。" +
                (data.Gaps.Count > 0 ? " " + string.Join("；", data.Gaps) : "") + " 笔记及草稿未改动，未自动分析。";
            MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>().FirstOrDefault(tab => Equals(tab.Header, "对局记录"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (KnowledgeNavigationIsCurrent(request, token)) KnowledgeNavigationStatusText.Text = error is InvalidOperationException ? error.Message : "关联局面读取失败，请重试；笔记仍保留。";
        }
        finally
        {
            if (ReferenceEquals(_knowledgeNavigationCancellation, cancellation))
            {
                if (!_closing && !KnowledgeNavigationIsCurrent(request, token))
                    KnowledgeNavigationStatusText.Text = "当前条目或局面已变更，旧的关联读取未发布；可重新打开。";
                _knowledgeNavigationCancellation = null; cancellation.Dispose(); _knowledgeNavigationLoading = false;
                if (_knowledgeNavigationHistoryRevision == _historyRevision) { _historyLoading = false; if (!_closing) RefreshAccessState(); }
                _knowledgeNavigationHistoryRevision = null; RefreshKnowledgeNavigationControls();
            }
        }
    }
}
