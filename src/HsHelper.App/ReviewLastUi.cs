using HsHelper.Core;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private sealed record LastObservedEventRow(long EventSequence, string Kind, string Summary);

    private async Task ReviewLastObservedAsync()
    {
        if (_closing) return;
        if (_loadingSource || _reading) throw new InvalidOperationException("日志正在读取，请稍后再固定局面。");
        var live = _liveFrame;
        var frame = live ?? _engine.Snapshot();
        if (frame.Entities.Count == 0) throw new InvalidOperationException("尚无可复盘的局面。");
        var frameEvidence = SnapshotCodec.Serialize(frame);
        CancelKnowledgeNavigation();
        var revision = ++_historyRevision; var generation = _sourceGeneration;
        var token = _source.Token;
        var sourceEstablished = _sourceBindingEstablished;
        var sourceDeck = sourceEstablished ? _sourceDeckVersion : null;
        var engineFrame = _engine.Snapshot();
        var matchesLiveSource = live is not null && frame.LocalPlayerId is 1 or 2 && engineFrame.MatchId == frame.MatchId && engineFrame.StateVersion == frame.StateVersion &&
            engineFrame.EventSequence == frame.EventSequence && engineFrame.LocalPlayerId == frame.LocalPlayerId;
        var observedAt = live is not null ? _lastStateObservedAt : null;
        bool Current()
        {
            var current = _liveFrame ?? _engine.Snapshot();
            return !_closing && !token.IsCancellationRequested && revision == _historyRevision && generation == _sourceGeneration &&
                current.MatchId == frame.MatchId && current.StateVersion == frame.StateVersion && current.EventSequence == frame.EventSequence &&
                current.LocalPlayerId == frame.LocalPlayerId && sourceEstablished == _sourceBindingEstablished &&
                (!sourceEstablished || ReferenceEquals(sourceDeck, _sourceDeckVersion)) && SnapshotCodec.Serialize(current) == frameEvidence;
        }
        _historyLoading = true; _historyContext = null; _historyObservedAt = null;
        InvalidateAdvice(); _localTools?.Invalidate("正在固定最后观察局面，请等待读取完成后重新绑定。");
        const string loadingText = "正在核对最后观察局面的精确保存版本…";
        EventsGrid.ItemsSource = null; HistorySnapshotText.Text = loadingText;
        RenderSnapshot();
        var published = false;
        try
        {
            var points = _store is null ? [] : await _store.ListDecisionPointsAsync(frame.MatchId, includeUnstable: true, token);
            if (!Current()) return;
            var matches = points.Where(point => point.SessionId == frame.MatchId && point.StateVersion == frame.StateVersion && point.EventSequence == frame.EventSequence).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("同一局面存在多个精确保存点，不能自动选择版本。");
            if (matches.Length == 1)
            {
                var data = await ReadHistoricalDecisionAsync(matches[0], token);
                if (!Current()) return;
                if (SnapshotCodec.Serialize(data.Context.Snapshot) != frameEvidence)
                    throw new InvalidOperationException("精确保存点的内容或视角与最后观察局面不一致，未使用其他内容代替。");
                PublishHistoricalDecision(data);
                StatusText.Text = "已打开最后观察局面的精确保存版本；仅供复盘，不会自动分析或操作游戏。";
            }
            else
            {
                // Saved session metadata is an original binding, not a neighbouring frame or timeline.
                var session = _store is null ? null : await _store.GetSessionAsync(frame.MatchId, token);
                if (!Current()) return;
                var samePerspective = session is null || session.LocalPlayerId is (1 or 2) && session.LocalPlayerId == frame.LocalPlayerId;
                var memoryDeck = samePerspective && session?.DeckVersionId is { } originalId && _store is { } sessionStore ? await sessionStore.GetDeckVersionAsync(originalId, token)
                    : session is null && matchesLiveSource ? sourceDeck : null;
                if (!Current()) return;
                var deckId = session is not null ? samePerspective ? session.DeckVersionId : null : memoryDeck?.Id;
                var reportedBuild = session is not null ? session.CardBuild : frame.ClientBuild;
                var build = string.IsNullOrWhiteSpace(reportedBuild) ? "unknown" : reportedBuild;
                var gaps = new List<string> { "未找到该状态版本的精确保存点；只展示此内存快照，不拼接前后存档时间线" };
                if (!samePerspective) gaps.Add("此帧视角与原会话记录不一致或未确认；原套牌归属保持未知");
                if (session is not null && sourceDeck is not null && session.DeckVersionId != sourceDeck.Id)
                    gaps.Add("当前来源套牌与原会话绑定不一致；仅保留原会话来源，不使用当前来源套牌参考");
                if (session is null && memoryDeck is not null) gaps.Add("原会话尚未保存；套牌参考来自与此帧对应的当前日志来源绑定");
                if (deckId is null) gaps.Add("未确认此帧的原套牌，不借用当前选择、编辑草稿或其他来源绑定");
                else if (memoryDeck is null) gaps.Add("原会话套牌版本缺失，未借用其他保存版本");
                if (build == "unknown") gaps.Add("来源未提供可确认的卡库 build，卡库保持未知");
                if (frame.ClientBuild is { } clientBuild && clientBuild != build)
                    gaps.Add("快照客户端 build 与原会话卡库绑定不一致，相关分析仍须拒绝版本混用");
                var names = new Dictionary<string, string>();
                var manifest = build == "unknown" ? null : await _catalog.GetManifestAsync(build, cancellationToken: token);
                if (!Current()) return;
                if (manifest is not null)
                {
                    if (!Current()) return;
                    names = (await _catalog.GetByIdsBatchedAsync(AttachmentVisibility.Project(frame).Entities.Values.Select(entity => entity.CardId).OfType<string>()
                        .Concat(PublicHandMemoryPresentation.CardIds(frame, build)).Distinct(), build, cancellationToken: token))
                        .ToDictionary(card => card.Id, card => card.Name);
                }
                if (!Current()) return;
                if (manifest is null && build != "unknown") gaps.Add("对应版本卡库未加载，卡名与规则资料保持未知");
                if (observedAt is null) gaps.Add("缺少此快照的明确观察时间；历史知识截止未知，不能请求依赖历史截止的模型分析");
                _historyContext = new(frame.MatchId, build, deckId, memoryDeck, frame, []);
                _historyObservedAt = observedAt;
                SessionBuildBox.Text = build == "unknown" ? "" : build;
                EventsGrid.ItemsSource = frame.RecentEvents.Select(entry => new LastObservedEventRow(entry.Sequence, entry.Kind, "内存快照近期事件：" + entry.Summary)).ToArray();
                var time = observedAt is { } at ? $"观察于 {at.ToLocalTime():yyyy-MM-dd HH:mm:ss.fffffff}" : "观察时间未知";
                HistorySnapshotText.Text = DescribeHistoryFrame(frame, names, $"未精确保存的最后内存局面 · 对局 {frame.MatchId} · #{frame.EventSequence} · v{frame.StateVersion} · {time}", gaps);
                HistoryStatusText.Text = "正在查看最后内存局面；未找到精确保存点。" + string.Join("；", gaps);
                RenderSnapshot();
                StatusText.Text = "已固定最后内存局面供查看；未找到精确保存版本，缺失来源和时间保持未知。";
            }
            published = true;
            TaskBox.SelectedIndex = 2;
            MainTabs.SelectedIndex = MainTabs.Items.Count - 1;
        }
        catch (Exception) when (!Current()) { }
        catch (OperationCanceledException)
        {
            HistorySnapshotText.Text = "最后观察局面读取已取消，未发布保存版本。";
        }
        catch (Exception error)
        {
            HistorySnapshotText.Text = "最后观察局面读取失败，未发布其他保存版本。";
            StatusText.Text = error is InvalidOperationException ? error.Message : "最后观察局面读取失败，请重试；未使用其他状态版本代替。";
        }
        finally
        {
            if (revision == _historyRevision)
            {
                _historyLoading = false;
                if (!_closing)
                {
                    if (!published && !Current() && HistorySnapshotText.Text == loadingText) HistorySnapshotText.Text = "";
                    RefreshAccessState();
                }
            }
        }
    }
}
