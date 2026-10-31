using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HsHelper.Agent;
using HsHelper.Storage;

namespace HsHelper.Coaching;

public sealed record CoachLibraryBinding(string MatchId, long StateVersion, string CardBuild,
    string? DeckVersionId, DateTimeOffset AsOf, string? OpponentClass = null, bool? WentFirst = null,
    string Region = "CN", string Format = "STANDARD", string GameType = "GT_RANKED", long? EventSequence = null);

/// <summary>Read-only personal context, pinned to one analysis cutoff. Models cannot move that cutoff.</summary>
public sealed class CoachLibraryContext(SessionStore sessions, CoachKnowledgeStore knowledge, CoachLibraryBinding binding)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    public CoachLibraryBinding Binding { get; } = binding;

    public async Task<JsonNode> ExecuteAsync(string name, JsonObject args, CancellationToken token)
    {
        var definition = Definitions.FirstOrDefault(d => d.Name == name);
        if (definition is null || !definition.ValidateArguments(args)) throw new ArgumentException("无效的个人资料查询。");
        if (Binding.DeckVersionId is null) return new JsonObject
        {
            ["missing"] = "当前对局未绑定套牌版本，无法把计划或个人战绩关联到这副套牌。",
            ["as_of"] = Binding.AsOf.ToString("O")
        };
        if (name == "get_matchup_plan")
        {
            const int totalLimit = 20, initialQuota = 10, perKindLimit = 20;
            CoachKnowledgeFilter Filter(CoachKnowledgeKind kind) => new(
                DeckVersionId: Binding.DeckVersionId, Kind: kind, OpponentClass: Binding.OpponentClass,
                WentFirst: Binding.WentFirst)
            {
                EventCutoff = new(Binding.MatchId, Binding.EventSequence), RankByApplicability = true
            };
            var planTask = knowledge.ListPageAsOfAsync(Binding.AsOf, Filter(CoachKnowledgeKind.DeckPlan),
                pageNumber: 1, pageSize: perKindLimit, cancellationToken: token);
            var noteTask = knowledge.ListPageAsOfAsync(Binding.AsOf, Filter(CoachKnowledgeKind.MatchNote),
                pageNumber: 1, pageSize: perKindLimit, cancellationToken: token);
            await Task.WhenAll(planTask, noteTask);
            var plans = await planTask; var notes = await noteTask;
            var planCount = Math.Min(initialQuota, plans.Entries.Count);
            var noteCount = Math.Min(initialQuota, notes.Entries.Count);
            var remaining = totalLimit - planCount - noteCount;
            var extraPlans = Math.Min(remaining, plans.Entries.Count - planCount);
            planCount += extraPlans; remaining -= extraPlans;
            noteCount += Math.Min(remaining, notes.Entries.Count - noteCount);
            var entries = plans.Entries.Take(planCount).Concat(notes.Entries.Take(noteCount)).ToArray();
            var plansTruncated = plans.HasMore || plans.Entries.Count > planCount;
            var notesTruncated = notes.HasMore || notes.Entries.Count > noteCount;
            return Node(new
            {
                match_id = Binding.MatchId, state_version = Binding.StateVersion, as_of = Binding.AsOf,
                deck_version = Binding.DeckVersionId, opponent_class = Binding.OpponentClass, went_first = Binding.WentFirst,
                source = "用户保存的策略计划与笔记；它们是参考意见，不能证明当前对手持有某张牌。",
                entries_truncated = plansTruncated || notesTruncated,
                retrieval = new
                {
                    total_limit = totalLimit, initial_per_kind_quota = initialQuota, per_kind_candidate_limit = perKindLimit,
                    ranking = "每类优先匹配已知对手职业及先后手，再按保存时间倒序、条目标识排序。",
                    selection_policy = "计划和笔记各先保留10条；不足的名额先补剩余计划，再补笔记。各类内部保留适用性排序。",
                    count_policy = "at_least_matches仅为匹配数量下限；未读取全库总数。",
                    deck_plans = Counts(plans, planCount), match_notes = Counts(notes, noteCount)
                },
                entries = entries.Select(e => new { e.EntryId, e.Version, e.RecordedAt, e.Source, e.Kind, e.Title, e.EventSequence,
                    content = e.Content.Length > 1600 ? e.Content[..1600] : e.Content,
                    content_truncated = e.Content.Length > 1600, e.OpponentClass, e.WentFirst, e.MatchId }),
                missing = entries.Length == 0 ? "本次查询范围内没有可用于该时点的计划或笔记。" : null
            });
            static object Counts(CoachKnowledgePage page, int returned) => new
            {
                returned, at_least_matches = page.Entries.Count + (page.HasMore ? 1 : 0),
                truncated = page.HasMore || page.Entries.Count > returned
            };
        }
        var matchup = args["scope"]!.GetValue<string>() == "current_matchup";
        if (matchup && Binding.OpponentClass is null) return new JsonObject { ["missing"] = "对手职业未知，不能虚构该对局的职业统计。" };
        var first = args["went_first"]!.GetValue<string>() == "current" ? Binding.WentFirst : null;
        var filter = new SessionFilter(CardBuild: Binding.CardBuild, DeckVersionId: Binding.DeckVersionId,
            OpponentClass: matchup ? Binding.OpponentClass : null, WentFirst: first, Format: Binding.Format,
            PlayerClass: "HUNTER", Region: Binding.Region, GameType: Binding.GameType,
            EndedBefore: Binding.AsOf, ExcludeSessionId: Binding.MatchId);
        var stats = await Task.Run(() => sessions.GetStatsAsOfAsync(Binding.AsOf, filter, token), token);
        return Node(new { match_id = Binding.MatchId, state_version = Binding.StateVersion,
            as_of = Binding.AsOf, filters = filter, stats,
            interpretation = "只统计此前已经结束的记录，排除当前局；胜率是个人样本描述，不能当作当前局的获胜概率。" });
    }

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
    public static IReadOnlyList<AgentToolDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new AgentToolDefinition("get_matchup_plan", "按计划和笔记分别获取固定套牌、分析时间与当前事件截止之前的适用资料，各保留配额并优先精确职业及先后手；最多20条，截断数量明确为已知值或下限。不包含以后修改的内容，调用者不能指定范围或截止。",
            Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""), args => args.Count == 0),
        new AgentToolDefinition("get_personal_stats", "查询固定版本、套牌及时间之前已结束的个人天梯样本，始终排除当前局和未来结果。",
            Schema("""{"type":"object","properties":{"scope":{"type":"string","enum":["current_matchup","all_opponents"]},"went_first":{"type":"string","enum":["current","any"]}},"required":["scope","went_first"],"additionalProperties":false}"""),
            args => args.Count == 2 && args["scope"]?.ToString() is "current_matchup" or "all_opponents" && args["went_first"]?.ToString() is "current" or "any")
    });
}
