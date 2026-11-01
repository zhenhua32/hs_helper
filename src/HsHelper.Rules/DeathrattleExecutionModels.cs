namespace HsHelper.Rules;

/// <summary>One started hypothetical script activation; no additional death or observed game event is implied.</summary>
public sealed record DeathrattleActivationOutcome(int ActivationOrdinal, bool IsComplete, string Status,
    string StopReason, IReadOnlyList<int> AffectedEntityIds)
{
    private IReadOnlyList<int> _affected = Array.AsReadOnly(AffectedEntityIds.ToArray());
    public IReadOnlyList<int> AffectedEntityIds { get => _affected; init => _affected = Array.AsReadOnly(value.ToArray()); }
}

/// <summary>
/// One script on one actually dying simulated body. All ordinals are one-based. DeathBatchEntityIds
/// is a set, never a claimed death/trigger order. The effect source and multiplier sources are distinct.
/// Remaining includes the current incomplete activation and does not promise future execution.
/// </summary>
public sealed record DeathrattleExecutionOutcome(string MatchId, long StateVersion, long EventSequence,
    string CardBuild, int ActionOrdinal, int DeathBatchOrdinal, IReadOnlyList<int> DeathBatchEntityIds,
    bool IsBatchOrderIndependent, int SourceEntityId, string? SourceCardId, string EffectKind,
    int EffectSourceEntityId, string? EffectSourceCardId, int? EffectEnchantmentEntityId, bool IsAcquiredEffect,
    bool IsMultiplicityResolved, bool? IsEnabled, int? PlannedActivations, int StartedActivations,
    int CompletedActivations, int? RemainingActivations, bool IsComplete,
    IReadOnlyList<int> MultiplierSourceEntityIds, IReadOnlyList<int> EnchantmentEntityIds,
    IReadOnlyList<DeathrattleActivationOutcome> Activations, string StopReason)
{
    private IReadOnlyList<int> _batch = Array.AsReadOnly(DeathBatchEntityIds.ToArray());
    private IReadOnlyList<int> _multipliers = Array.AsReadOnly(MultiplierSourceEntityIds.ToArray());
    private IReadOnlyList<int> _enchantments = Array.AsReadOnly(EnchantmentEntityIds.ToArray());
    private IReadOnlyList<DeathrattleActivationOutcome> _activations = Array.AsReadOnly(Activations.ToArray());
    public IReadOnlyList<int> DeathBatchEntityIds { get => _batch; init => _batch = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<int> MultiplierSourceEntityIds { get => _multipliers; init => _multipliers = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<int> EnchantmentEntityIds { get => _enchantments; init => _enchantments = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<DeathrattleActivationOutcome> Activations { get => _activations; init => _activations = Array.AsReadOnly(value.ToArray()); }
}
