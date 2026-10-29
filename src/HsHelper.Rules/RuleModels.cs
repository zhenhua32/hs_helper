using System.Collections.Frozen;

namespace HsHelper.Rules;

public enum ActionKind { Attack, PlayCard, HeroPower }
public enum LineVerification { Verified, Conditional, Unsupported, Invalid }
public sealed record PlannedAction(ActionKind Kind, int SourceEntityId, int? TargetEntityId = null);
public sealed record SimulatedEntity(int EntityId, string? CardId, string Type, int? ControllerId,
    string Zone, int? Attack, int? MaximumHealth, int? RemainingHealth, int Armor, int? Cost,
    int AttacksUsed, bool IsAlive, FrozenDictionary<string, int> Flags);
public sealed record SimulatedState(int AvailableMana, IReadOnlyList<SimulatedEntity> Entities);
public sealed record LineEvaluation(string MatchId, long StateVersion, LineVerification Verification,
    bool? IsLegal, bool IsLethal, bool PotentialLethal, int AppliedActions,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> UnsupportedMechanics, IReadOnlyList<string> RuleCoverage,
    SimulatedState Result);
public sealed record LethalSearchResult(bool VerifiedLethalFound, IReadOnlyList<PlannedAction> Actions,
    LineEvaluation? Evaluation, int ExploredNodes, bool IsExhaustive, string Coverage);
