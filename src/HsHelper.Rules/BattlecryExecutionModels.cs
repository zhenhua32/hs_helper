namespace HsHelper.Rules;

/// <summary>One started activation of a hypothetical play, never an event observed in the actual game.</summary>
public sealed record BattlecryActivationOutcome(int ActivationOrdinal, bool IsComplete, string Status,
    string StopReason, IReadOnlyList<int> AffectedEntityIds)
{
    private IReadOnlyList<int> _affected = Array.AsReadOnly(AffectedEntityIds.ToArray());
    public IReadOnlyList<int> AffectedEntityIds { get => _affected; init => _affected = Array.AsReadOnly(value.ToArray()); }
}

/// <summary>
/// Battlecry schedule for one submitted play. Ordinals are one-based. Remaining includes an
/// activation that started but still requires observation; it does not promise future execution.
/// Payment, entry and independent after-play triggers belong to the single play, not each activation.
/// </summary>
public sealed record BattlecryExecutionOutcome(string MatchId, long StateVersion, long EventSequence,
    string CardBuild, int SourceEntityId, string SourceCardId, int ActionOrdinal, bool IsMultiplicityResolved,
    bool? IsEnabled, int? PlannedActivations, int StartedActivations, int CompletedActivations,
    int? RemainingActivations, bool IsComplete, IReadOnlyList<int> EnchantmentEntityIds,
    IReadOnlyList<BattlecryActivationOutcome> Activations, string StopReason)
{
    private IReadOnlyList<int> _enchantments = Array.AsReadOnly(EnchantmentEntityIds.ToArray());
    private IReadOnlyList<BattlecryActivationOutcome> _activations = Array.AsReadOnly(Activations.ToArray());
    public IReadOnlyList<int> EnchantmentEntityIds { get => _enchantments; init => _enchantments = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<BattlecryActivationOutcome> Activations { get => _activations; init => _activations = Array.AsReadOnly(value.ToArray()); }
}
