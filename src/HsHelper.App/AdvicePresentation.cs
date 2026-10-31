using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private sealed record PendingAdvice(AdviceRecordDraft Draft, string Credential,
        TaskCompletionSource<ArchivedAdviceRecord> Completion, string Id);
    private readonly ConcurrentQueue<PendingAdvice> _pendingAdvice = new();
    private readonly SemaphoreSlim _archiveGate = new(1, 1);

    private async Task<ArchivedAdviceRecord> ArchiveAdviceAsync(StrategyAdvice advice, GameSnapshot snapshot,
        AdviceContext context, string build, string? deckVersion, IReadOnlyList<AdviceToolEvidence> evidence, string credential)
    {
        var draft = new AdviceRecordDraft(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            string.IsNullOrWhiteSpace(build) ? "unknown" : build, context.TemplateVersion, advice.Summary)
        {
            DeckVersionId = deckVersion, Task = context.Task, UserQuestion = context.UserQuestion, Reasons = advice.Reasons, Alternative = advice.Alternative,
            Risks = advice.Risks, Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(a => new ArchivedAdviceAction(a.Kind, a.SourceEntityId, a.TargetEntityId)).ToArray(),
            Evidence = evidence, Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds,
            ConversationArchiveIds = advice.ConversationArchiveIds, KnowledgeAsOf = advice.KnowledgeAsOf
        };
        draft = AdviceArchiveStore.Prepare(draft, [credential]);
        var pending = new PendingAdvice(draft, credential, new(TaskCreationOptions.RunContinuationsAsynchronously), Guid.NewGuid().ToString("N"));
        _pendingAdvice.Enqueue(pending);
        await FlushAdviceArchivesAsync();
        return await pending.Completion.Task;
    }
    private async Task FlushAdviceArchivesAsync()
    {
        if (_adviceArchive is null) return;
        await _archiveGate.WaitAsync();
        try
        {
            while (_pendingAdvice.TryPeek(out var pending))
            {
                var saved = await _adviceArchive.SaveAsync(pending.Draft, pending.Id, [pending.Credential]);
                _pendingAdvice.TryDequeue(out _); pending.Completion.TrySetResult(saved);
            }
        }
        finally { _archiveGate.Release(); }
    }
    private void ObserveTool(List<AdviceToolEvidence> evidence, GameSnapshot snapshot, CoachToolObservation observation)
    {
        RememberTool(snapshot.MatchId, snapshot.StateVersion, observation.Name, observation.ElapsedMilliseconds,
            observation.Success, observation.ResultHash, observation);
        var summary = observation.Success ? "查询已完成。" : "工具未完成。";
        if (observation.Success && observation.ResultJson is { } result)
        {
            var json = JsonNode.Parse(result);
            string? Field(string key) => json?[key]?.ToString();
            summary = observation.Name switch
            {
                "get_snapshot" => $"固定局面 v{snapshot.StateVersion}，事件 #{snapshot.EventSequence}，阶段 {snapshot.Phase}。",
                "get_cards" => "固定版本卡牌：" + string.Join("、", (json?["cards"] as JsonArray ?? []).Select(c => c?["Id"]?.ToString() ?? c?["id"]?.ToString() ?? "未知")),
                "get_recent_events" => "仅查询固定序号以前的匿名近期事件。",
                "calculate_draw_odds" => $"精确条件 {Field("is_exact")}；概率 {Field("probability") ?? "未知"}；{Field("reason")}",
                "evaluate_line" => $"验证 {Field("Verification")}；合法 {Field("IsLegal")}；确定斩杀 {Field("IsLethal")}；未覆盖 {Field("UnsupportedMechanics")}",
                "find_lethal" => $"已验证斩杀 {Field("VerifiedLethalFound")}；搜索节点 {Field("ExploredNodes")}；{Field("Coverage")}",
                "validate_choice" => $"候选合法 {Field("IsLegal")}；仅验证当前选择，不模拟后续效果。",
                "evaluate_choice" => $"选择直接后果 {Field("Verification")}；来源 {Field("SourceCardId")}；不作为已经发生的局面或后续动作依据。",
                "evaluate_draw" => $"抽牌直接范围；来源 {Field("SourceCardId")}；不生成新手牌实体，不证明后续动作或斩杀。",
                "evaluate_generation" => $"随机生成数量范围；来源 {Field("SourceCardId")}；不选择随机身份或创建新实体。",
                "evaluate_wolf_blessing" => "巨狼祝福随机候选的直接增益分支；不指定实际命中或证明后续动作。",
                "evaluate_magma_split" => "熔岩猎犬攻击与逐包随机分配的条件分支；区间相关，截断不代表全部结果，不指定命中或证明斩杀。",
                "evaluate_explosive_trap" => "我方已安装爆炸陷阱的当前来袭条件预览；仅列直接触发伤害，不执行对手攻击或给出权威后续局面。",
                "evaluate_play_triggers" => "原生发现、拼装、回溯入口及使用后任务、迅猛龙触发预览；选择和随机效果等待新局面，不证明完整结算或斩杀。",
                "get_entity_effect" => "已查询固定局面可见实体文字；拼装半段不等于完整成品，也不作为原生动作或斩杀证明。",
                "get_matchup_plan" => "查询当时已有的用户计划/笔记版本：" + string.Join("、", (json?["entries"] as JsonArray ?? []).Select(e => $"{e?["EntryId"]} v{e?["Version"]}")),
                "get_personal_stats" => $"此前已结束个人样本 {json?["stats"]?["Games"]?.ToString() ?? "未知"} 局，排除当前局。",
                _ => summary
            };
        }
        if (summary.Length > 900) summary = summary[..900] + "…";
        evidence.Add(new(observation.Name, summary, observation.ResultHash));
    }
    private void DisplayArchivedAdvice(ArchivedAdviceRecord saved)
    {
        var advice = saved.Advice;
        var choiceOnly = advice.Actions.Count > 0 && advice.Actions.All(a => a.Kind == "choose");
        var label = advice.Verification == "verified" ? choiceOnly ? "选择范围已验证" : "动作已通过本地规则验证" : "策略建议";
        if (advice.Task.Equals(AdviceTaskKind.DeckPlan.ToString(), StringComparison.Ordinal)) label = "赛前套牌计划";
        else if (CurrentAccess.IsHistorical) label = "历史复盘 · " + label;
        var text = new StringBuilder($"{label} · 局面 v{advice.StateVersion}\n\n{advice.Summary}\n\n依据\n");
        if (!string.IsNullOrWhiteSpace(advice.UserQuestion)) text.Insert(0, $"本次提问：{advice.UserQuestion}\n\n");
        if (advice.ConversationArchiveIds is { Count: > 0 }) text.AppendLine($"承接 {advice.ConversationArchiveIds.Count} 条此前解释；当前局面和工具结果优先。\n");
        foreach (var reason in advice.Reasons) text.AppendLine(reason);
        text.AppendLine($"\n备选\n{advice.Alternative ?? "无"}\n\n风险与未知");
        foreach (var risk in advice.Risks) text.AppendLine(risk);
        if (advice.Actions.Count > 0)
        {
            text.AppendLine("\n已验证动作");
            foreach (var action in advice.Actions) text.AppendLine($"{action.Kind} · 实体 {action.SourceEntityId} → {action.TargetEntityId?.ToString() ?? "无目标"}");
        }
        if (advice.VerifiedLethal) text.AppendLine("本地规则确认此序列可斩杀。");
        text.AppendLine($"\n已保存建议记录 · {saved.RecordedAt.ToLocalTime():HH:mm:ss}");
        AdviceBox.Text = text.ToString();
        if (advice.MatchId == DisplaySnapshot.MatchId && advice.StateVersion == DisplaySnapshot.StateVersion)
            _overlayAdvice = advice.Summary + (advice.Risks.Count > 0 ? "\n风险：" + advice.Risks[0] : "");
    }
    private async void AnalyzeDeckPlanClicked(object sender, RoutedEventArgs e) => await Run(() => TrackAnalysisAsync(AnalyzeDeckPlanCoreAsync));
    private async Task AnalyzeDeckPlanCoreAsync()
    {
        if (_closing || _knowledge is null) throw new InvalidOperationException("应用尚未准备好。");
        var deck = (PlanDeckBox.SelectedItem as DeckItem)?.Value ?? throw new InvalidOperationException("请先选择已保存的套牌版本。");
        var config = ConfigurationFromFields();
        var configurationRevision = _configurationRevision;
        var revision = _revision;
        var userQuestion = string.IsNullOrWhiteSpace(QuestionBox.Text) ? null : QuestionBox.Text.Trim();
        var asOf = DateTimeOffset.UtcNow;
        var opponent = string.IsNullOrWhiteSpace(PlanOpponentBox.Text) ? null : PlanOpponentBox.Text.Trim().ToUpperInvariant();
        bool? first = PlanFirstBox.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        _analysis?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _analysis = cancellation; AnalyzeButton.IsEnabled = false;
        bool IsCurrent() => !_closing && configurationRevision == _configurationRevision && revision == _revision &&
            ReferenceEquals(_analysis, cancellation) && (PlanDeckBox.SelectedItem as DeckItem)?.Value.Id == deck.Id &&
            (string.IsNullOrWhiteSpace(PlanOpponentBox.Text) ? null : PlanOpponentBox.Text.Trim().ToUpperInvariant()) == opponent &&
            (PlanFirstBox.SelectedIndex switch { 1 => (bool?)true, 2 => false, _ => null }) == first;
        try
        {
            var build = deck.CardBuild == "unknown" ? _manifest?.Build ?? "unknown" : deck.CardBuild;
            var cards = build == "unknown" ? [] : await _catalog.GetByDbfIdsAsync(deck.Definition.Cards.Select(c => c.DbfId)
                .Concat(deck.Definition.Sideboards.SelectMany(s => s.Cards.Select(c => c.DbfId))), build, cancellationToken: cancellation.Token);
            var snapshot = new GameSnapshot("deck-" + deck.Id, 0, 0, null, null, null, GamePhase.Unknown,
                FrozenDictionary<int, EntitySnapshot>.Empty, new(false, false, true, false, 0, ["赛前套牌背景，不是实时局面。"]));
            var context = CompactAdviceContextBuilder.Create(snapshot, build, AdviceTaskKind.DeckPlan, deck.Definition, cards)
                with { UserQuestion = userQuestion, KnowledgeAsOf = asOf };
            context = await ConversationHistoryBuilder.AttachAsync(context, _adviceArchive, deck.Id, asOf,
                cancellationToken: cancellation.Token);
            context.Snapshot["planning_opponent_class"] = opponent;
            context.Snapshot["planning_went_first"] = first;
            var library = new CoachLibraryContext(_store, _knowledge, new(snapshot.MatchId, 0, build, deck.Id, asOf, opponent, first, EventSequence: 0));
            var evidence = new List<AdviceToolEvidence>();
            var coach = new SnapshotCoach(snapshot, context.Snapshot, _catalog, build, library: library,
                toolCompleted: observation => ObserveTool(evidence, snapshot, observation), taskKind: context.TaskKind);
            await SeedBudgetAsync(snapshot.MatchId);
            if (!IsCurrent()) throw new OperationCanceledException();
            MainTabs.SelectedIndex = MainTabs.Items.Count - 1;
            AdviceBox.Text = "正在分析套牌获胜方式、留牌方向和对阵计划…";
            var advice = await new AdviceService(_http).AnalyzeAsync(config.Settings, config.ApiKey, context, coach.ExecuteAsync,
                IsCurrent, cancellation.Token, coach.Capabilities(_analysisBudget, u => RememberUsage(u, build, context.Task, context.TemplateVersion)));
            var saved = await ArchiveAdviceAsync(advice, snapshot, context, build, deck.Id, evidence, config.ApiKey);
            if (IsCurrent()) DisplayArchivedAdvice(saved);
        }
        catch (OperationCanceledException) { if (IsCurrent()) AdviceBox.Text = "套牌分析已取消或配置已变化。"; }
        finally
        {
            try { await FlushTracesAsync(); }
            finally { if (ReferenceEquals(_analysis, cancellation)) { AnalyzeButton.IsEnabled = true; _analysis = null; } }
        }
    }
    private void ViewDeckAdviceArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (_closing || _adviceArchive is null) return;
        if ((PlanDeckBox.SelectedItem as DeckItem)?.Value is not { } deck) { StatusText.Text = "请先选择套牌版本。"; return; }
        OpenAdviceArchive(new("deck-" + deck.Id, $"套牌 {deck.Name} · 版本 {deck.Id}", AdviceArchiveScopeKind.DeckPlan, deck.Id));
    }
}
