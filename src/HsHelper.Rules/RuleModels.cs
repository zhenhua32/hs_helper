using System.Collections.Frozen;

namespace HsHelper.Rules;

public enum ActionKind { Attack, PlayCard, HeroPower }
public enum LineVerification { Verified, Conditional, Unsupported, Invalid }
public sealed record PlannedAction(ActionKind Kind, int SourceEntityId, int? TargetEntityId = null);
public sealed record SimulatedEntity(int EntityId, string? CardId, string Type, int? ControllerId,
    string Zone, int? Attack, int? MaximumHealth, int? RemainingHealth, int Armor, int? Cost,
    int AttacksUsed, bool IsAlive, FrozenDictionary<string, int> Flags);
public sealed record HeroPowerChange(int ControllerId, int EntityId, string? PreviousCardId, string? CurrentCardId,
    int? PreviousLevel, int? CurrentLevel, bool AvailabilityKnown, string Reason);
public sealed record SimulatedState(int AvailableMana, IReadOnlyList<SimulatedEntity> Entities)
{
    public IReadOnlyList<HeroPowerChange> HeroPowerChanges { get; init; } = [];
}
public sealed record LineEvaluation(string MatchId, long StateVersion, LineVerification Verification,
    bool? IsLegal, bool IsLethal, bool PotentialLethal, int AppliedActions,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> UnsupportedMechanics, IReadOnlyList<string> RuleCoverage,
    SimulatedState Result);
public sealed record LethalSearchResult(bool VerifiedLethalFound, IReadOnlyList<PlannedAction> Actions,
    LineEvaluation? Evaluation, int ExploredNodes, bool IsExhaustive, string Coverage);
public sealed record ChoiceValidationResult(string MatchId, long StateVersion, int? ChoiceId,
    LineVerification Verification, bool IsLegal, IReadOnlyList<int> SelectedEntityIds,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool SelectionOnly { get; init; } = true;
    public bool ResolvesEffects { get; init; } = false;
}
