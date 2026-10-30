using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HsHelper.Agent;

/// <summary>An archived exchange is a previous opinion, never a source of current game facts.</summary>
public sealed record AdviceConversationTurn(string ArchiveId, string MatchId, long StateVersion, long EventSequence,
    string CardBuild, string? DeckVersionId, DateTimeOffset RecordedAt, string? UserQuestion, string Answer)
{
    public string? Model { get; init; }
    public string? AnalysisRunId { get; init; }
    public IReadOnlyList<string> RequestIds { get; init; } = [];
    public DateTimeOffset? KnowledgeAsOf { get; init; }
}

public static class AdviceConversationLimits
{
    public const int MaxTurns = 4;
    public const int MaxQuestionCharacters = 1000;
    public const int MaxAnswerCharacters = 2000;

    internal static AdviceContext FreezeAndValidate(AdviceContext context)
    {
        if (context.Conversation is null || context.Conversation.Count > MaxTurns)
            throw new ArgumentException("连续追问最多保留4轮已完成对话。");
        if (context.EventSequence < 0) throw new ArgumentException("分析事件序号无效。");
        if (context.KnowledgeAsOf.HasValue && context.AsOf.HasValue && context.KnowledgeAsOf > context.AsOf)
            throw new ArgumentException("知识截止时间不能晚于本次分析查询时间。");
        if (context.CardBuild is not null && !Identifier(context.CardBuild) ||
            context.DeckVersionId is not null && !Identifier(context.DeckVersionId))
            throw new ArgumentException("分析的卡库或套牌来源标识无效。");
        if (context.EventSequence.HasValue && context.Snapshot["event_sequence"] is { } eventNode &&
            (eventNode is not JsonValue eventValue || !Integer(eventValue, out var sequence) || sequence != context.EventSequence))
            throw new ArgumentException("对话事件边界与当前局面不一致。");
        if (context.CardBuild is not null && context.Snapshot["card_build"] is { } buildNode &&
            (buildNode is not JsonValue buildValue || !buildValue.TryGetValue<string>(out var build) || build != context.CardBuild))
            throw new ArgumentException("对话卡库边界与当前局面不一致。");
        if (context.Conversation.Count == 0) return context with { Conversation = [] };
        if (context.EventSequence is null || context.AsOf is null || context.KnowledgeAsOf is null ||
            context.KnowledgeAsOf > context.AsOf || string.IsNullOrWhiteSpace(context.CardBuild))
            throw new ArgumentException("连续追问必须固定卡库、事件序号、查询时刻和不晚于查询时刻的知识截止时间。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var turns = new List<AdviceConversationTurn>();
        DateTimeOffset? previous = null;
        foreach (var turn in context.Conversation)
        {
            if (turn is null || !Identifier(turn.ArchiveId) || !ids.Add(turn.ArchiveId) ||
                turn.MatchId != context.MatchId || turn.CardBuild != context.CardBuild || turn.DeckVersionId != context.DeckVersionId ||
                turn.StateVersion < 0 || turn.StateVersion > context.StateVersion || turn.EventSequence < 0 || turn.EventSequence > context.EventSequence ||
                turn.RecordedAt > context.AsOf || (previous.HasValue && turn.RecordedAt < previous) ||
                turn.KnowledgeAsOf is null || turn.KnowledgeAsOf > context.KnowledgeAsOf || turn.KnowledgeAsOf > turn.RecordedAt ||
                (turn.UserQuestion is not null && (turn.UserQuestion.Length > MaxQuestionCharacters || turn.UserQuestion.Contains('\0'))) ||
                string.IsNullOrWhiteSpace(turn.Answer) || turn.Answer.Length > MaxAnswerCharacters || turn.Answer.Contains('\0') ||
                turn.RequestIds is null || turn.RequestIds.Count > 5 || turn.RequestIds.Any(id => !Identifier(id)) ||
                turn.RequestIds.Distinct(StringComparer.Ordinal).Count() != turn.RequestIds.Count ||
                (turn.AnalysisRunId is not null && !Identifier(turn.AnalysisRunId)) ||
                (turn.Model is not null && (turn.Model.Length > 200 || turn.Model.Any(char.IsControl))))
                throw new ArgumentException("连续追问的来源、范围或文本无效，不能读取其他对局或未来回答。");
            previous = turn.RecordedAt;
            turns.Add(turn with { RequestIds = Array.AsReadOnly(turn.RequestIds.ToArray()) });
        }
        return context with { Conversation = turns.AsReadOnly() };
    }

    private static bool Identifier(string? value) => value is not null &&
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$", RegexOptions.CultureInvariant) && !value.Contains("://", StringComparison.Ordinal);
    private static bool Integer(JsonValue value, out long result)
    {
        if (value.TryGetValue<long>(out result)) return true;
        if (value.TryGetValue<int>(out var integer)) { result = integer; return true; }
        result = 0; return false;
    }
}
