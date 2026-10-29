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
            var candidates = await knowledge.ListAsOfAsync(Binding.AsOf, new CoachKnowledgeFilter(
                DeckVersionId: Binding.DeckVersionId, OpponentClass: Binding.OpponentClass,
                WentFirst: Binding.WentFirst, Limit: 200), token);
            var eligible = candidates.Where(e => e.MatchId != Binding.MatchId || e.EventSequence is null ||
                (Binding.EventSequence is { } sequence && e.EventSequence <= sequence)).ToArray();
            var entries = eligible.Take(20).ToArray();
            return Node(new
            {
                match_id = Binding.MatchId, state_version = Binding.StateVersion, as_of = Binding.AsOf,
                deck_version = Binding.DeckVersionId, opponent_class = Binding.OpponentClass, went_first = Binding.WentFirst,
                source = "用户保存的策略计划与笔记；它们是参考意见，不能证明当前对手持有某张牌。",
                entries_truncated = eligible.Length > 20 || candidates.Count >= 200,
                entries = entries.Select(e => new { e.EntryId, e.Version, e.RecordedAt, e.Source, e.Kind, e.Title, e.EventSequence,
                    content = e.Content.Length > 1600 ? e.Content[..1600] : e.Content,
                    content_truncated = e.Content.Length > 1600, e.OpponentClass, e.WentFirst, e.MatchId }),
                missing = entries.Length == 0 ? "本次查询范围内没有可用于该时点的计划或笔记。" : null
            });
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
        new AgentToolDefinition("get_matchup_plan", "获取固定套牌与分析时间之前的用户计划和对局笔记；不包含以后修改的内容。",
            Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""), args => args.Count == 0),
        new AgentToolDefinition("get_personal_stats", "查询固定版本、套牌及时间之前已结束的个人天梯样本，始终排除当前局和未来结果。",
            Schema("""{"type":"object","properties":{"scope":{"type":"string","enum":["current_matchup","all_opponents"]},"went_first":{"type":"string","enum":["current","any"]}},"required":["scope","went_first"],"additionalProperties":false}"""),
            args => args.Count == 2 && args["scope"]?.ToString() is "current_matchup" or "all_opponents" && args["went_first"]?.ToString() is "current" or "any")
    });
}
