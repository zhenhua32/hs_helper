using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace HsHelper.Rules;

public enum ActionKind { Attack, PlayCard, HeroPower, EndTurn }
public enum LineVerification { Verified, Conditional, Unsupported, Invalid }
public sealed record PlannedAction(ActionKind Kind, int SourceEntityId, int? TargetEntityId = null);
public sealed record SimulatedEntity(int EntityId, string? CardId, string Type, int? ControllerId,
    string Zone, int? Attack, int? MaximumHealth, int? RemainingHealth, int Armor, int? Cost,
    int AttacksUsed, bool IsAlive, FrozenDictionary<string, int> Flags)
{
    public HsHelper.Core.WeaponDurabilityResolution? WeaponDurability { get; init; }
}
public sealed record HeroPowerChange(int ControllerId, int EntityId, string? PreviousCardId, string? CurrentCardId,
    int? PreviousLevel, int? CurrentLevel, bool AvailabilityKnown, string Reason);
public sealed record OpponentHealthPayment(int CardEntityId, string CardId, int PayingHeroEntityId,
    int Cost, int HealthBefore, int HealthAfter);
public sealed record ForcedAttackResult(int TriggerEntityId, int SourceEntityId, int TargetEntityId,
    int DamageToTarget, int DamageToSource, bool SourceSurvived, bool TargetSurvived);
public sealed record PendingForcedAttack(int TriggerEntityId, int TargetEntityId,
    IReadOnlyList<int> CandidateSourceEntityIds, string Reason);
public sealed record AssembledEffectTarget(int EntityId, int AttackBefore, int AttackAfter,
    int MaximumHealthBefore, int MaximumHealthAfter, int RemainingHealthBefore, int RemainingHealthAfter);
public sealed record AssembledEffectOutcome(string MatchId, long StateVersion, long EventSequence,
    string CardBuild, int SourceEntityId, string BaseCardId, int FirstHalfDbfId, int SecondHalfDbfId,
    string TriggerCardId, string EffectCardId, string ScriptKey, bool? IsTriggerActive, int? ExecutedCount,
    IReadOnlyList<AssembledEffectTarget> Targets, string Scope)
{
    private IReadOnlyList<AssembledEffectTarget> _targets = Array.AsReadOnly(Targets.ToArray());
    public IReadOnlyList<AssembledEffectTarget> Targets { get => _targets; init => _targets = Array.AsReadOnly(value.ToArray()); }
}
public sealed record SimulatedState(int AvailableMana, IReadOnlyList<SimulatedEntity> Entities)
{
    private IReadOnlyList<BattlecryExecutionOutcome>? _battlecryExecutions;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BattlecryExecutionOutcome>? BattlecryExecutions
    { get => _battlecryExecutions; init => _battlecryExecutions = value is null ? null : Array.AsReadOnly(value.ToArray()); }
    private IReadOnlyList<AssembledEffectOutcome>? _assembledEffects;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AssembledEffectOutcome>? AssembledEffects
    { get => _assembledEffects; init => _assembledEffects = value is null ? null : Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<HeroPowerChange> HeroPowerChanges { get; init; } = [];
    public bool? NextCardCostsOpponentHealth { get; init; }
    /// <summary>Resolution stopped at lethal payment; the submitted card retains its last observed state.</summary>
    public bool OpponentDefeatedByPayment { get; init; }
    public IReadOnlyList<OpponentHealthPayment> OpponentHealthPayments { get; init; } = [];
    /// <summary>Stops at the own end-turn boundary; the opponent's start, draw and actions are not simulated.</summary>
    public bool TurnEnded { get; init; }
    public bool EndTurnTriggersResolved { get; init; }
    public IReadOnlyList<ForcedAttackResult> ForcedAttacks { get; init; } = [];
    public IReadOnlyList<PendingForcedAttack> PendingForcedAttacks { get; init; } = [];
    public IReadOnlyList<TerrorEndTurnOutcome> TerrorEndTurnPreviews { get; init; } = [];
    /// <summary>These fields retain their last observed value rather than a purported post-trigger value.</summary>
    public IReadOnlyList<string> UnresolvedEndTurnFields { get; init; } = [];
    public int? ImbueCountBefore { get; init; }
    public int? ImbueCountAfter { get; init; }
    public bool DrawBoundaryReached { get; init; }
    /// <summary>Entity/resource projection stops before the unresolved draw; direct effects are isolated below.</summary>
    public IReadOnlyList<DrawResolutionOutcome> DrawPreviews { get; init; } = [];
    public bool GenerationBoundaryReached { get; init; }
    /// <summary>Direct counts only; generated cards have no simulated identity or usable entity.</summary>
    public IReadOnlyList<GenerationResolutionOutcome> GenerationPreviews { get; init; } = [];
    public bool WolfBlessingBoundaryReached { get; init; }
    public IReadOnlyList<WolfBlessingResolutionOutcome> WolfBlessingPreviews { get; init; } = [];
    public bool QuestRewardBoundaryReached { get; init; }
    public IReadOnlyList<QuestProgressOutcome> QuestProgressPreviews { get; init; } = [];
    public bool RaptorBoundaryReached { get; init; }
    public IReadOnlyList<DreamboundRaptorOutcome> RaptorPreviews { get; init; } = [];
    public bool DiscoveryBoundaryReached { get; init; }
    public IReadOnlyList<DiscoveryPlayOutcome> DiscoveryPreviews { get; init; } = [];
    public bool MapFollowupBoundaryReached { get; init; }
    public IReadOnlyList<MapPlayTriggerOutcome> MapPlayPreviews { get; init; } = [];
    public bool MagmaSplitBoundaryReached { get; init; }
    public IReadOnlyList<MagmaSplitOutcome> MagmaSplitPreviews { get; init; } = [];
    public bool WeaponResolutionBoundaryReached { get; init; }
}
public sealed record LineEvaluation(string MatchId, long StateVersion, LineVerification Verification,
    bool? IsLegal, bool IsLethal, bool PotentialLethal, int AppliedActions,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> UnsupportedMechanics, IReadOnlyList<string> RuleCoverage,
    SimulatedState Result)
{
    public string? CardBuild { get; init; }
}
public sealed record LethalSearchResult(bool VerifiedLethalFound, IReadOnlyList<PlannedAction> Actions,
    LineEvaluation? Evaluation, int ExploredNodes, bool IsExhaustive, string Coverage);
public sealed record ChoiceValidationResult(string MatchId, long StateVersion, int? ChoiceId,
    LineVerification Verification, bool IsLegal, IReadOnlyList<int> SelectedEntityIds,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool SelectionOnly { get; init; } = true;
    public bool ResolvesEffects { get; init; } = false;
}
