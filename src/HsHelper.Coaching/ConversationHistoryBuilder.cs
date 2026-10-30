using HsHelper.Agent;
using HsHelper.Storage;

namespace HsHelper.Coaching;

/// <summary>Builds a bounded archive conversation independently of the personal-library knowledge cutoff.</summary>
public static class ConversationHistoryBuilder
{
    public static async Task<AdviceContext> AttachAsync(AdviceContext context, AdviceArchiveStore archive,
        string? deckVersionId, DateTimeOffset asOf, int maxTurns = AdviceConversationLimits.MaxTurns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(archive);
        if (context.EventSequence is null || context.KnowledgeAsOf is null || string.IsNullOrWhiteSpace(context.CardBuild))
            throw new ArgumentException("连续追问需要带有卡库、事件序号和知识截止时间的固定上下文。");
        if (context.KnowledgeAsOf > asOf) throw new ArgumentException("知识截止时间不能晚于对话查询时间。");
        var history = await archive.GetConversationHistoryAsync(new(context.MatchId, deckVersionId,
            context.CardBuild, context.StateVersion, context.EventSequence.Value, asOf, maxTurns, context.KnowledgeAsOf), cancellationToken);
        var turns = history.Records.Select(record => new AdviceConversationTurn(record.Id, record.Advice.MatchId,
            record.Advice.StateVersion, record.Advice.EventSequence, record.Advice.CardBuild, record.Advice.DeckVersionId,
            record.RecordedAt, record.Advice.UserQuestion, record.Advice.Summary)
        {
            Model = record.Advice.Model, AnalysisRunId = record.Advice.AnalysisRunId,
            RequestIds = record.Advice.RequestIds ?? Array.Empty<string>(), KnowledgeAsOf = record.Advice.KnowledgeAsOf
        }).ToArray();
        return context with { DeckVersionId = deckVersionId, AsOf = asOf,
            Conversation = Array.AsReadOnly(turns), ConversationTruncated = history.IsTruncated };
    }
}
