using System.Text.Json.Nodes;

namespace HsHelper.Agent;

public sealed record AdviceAction(string Kind, int? SourceEntityId, int? TargetEntityId = null);

public enum AdviceValidationStatus { Verified, Conditional, Unsupported, Invalid }

public sealed record ActionValidationResult(AdviceValidationStatus Status, bool IsLegal,
    bool VerifiedLethal = false, IReadOnlyList<string>? Risks = null);

/// <summary>Only explicitly registered local tools can reach the execution callback.</summary>
public sealed record AgentToolDefinition(string Name, string Description, JsonObject Parameters,
    Func<JsonObject, bool> ValidateArguments);

public sealed record ModelUsage(string MatchId, long StateVersion, string Model, int Round,
    long? InputTokens, long? OutputTokens, long? TotalTokens, long ElapsedMilliseconds, bool Success)
{
    public bool UsageReported => TotalTokens.HasValue;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public long ChargedTokens { get; init; }
    public bool ConservativeCharge { get; init; }
}

public sealed class AgentCapabilities
{
    public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = [];
    public Func<IReadOnlyList<AdviceAction>, CancellationToken, Task<ActionValidationResult>>? ValidateActions { get; init; }
    public Action<ModelUsage>? OnUsage { get; init; }
    public AnalysisBudget? Budget { get; init; }
}
