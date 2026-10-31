using System.Collections.Frozen;
using System.Globalization;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>Isolates an already checked ordinary combat from its unresolved split damage, without exporting private entities.</summary>
public sealed record MagmaAttackPreview(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, int TargetEntityId, LineVerification Verification, bool? AttackIsLegal, int AppliedActions,
    MagmaSplitOutcome? Split, IReadOnlyList<string> Errors, IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> UnsupportedMechanics, IReadOnlyList<string> RuleCoverage)
{
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
}

public sealed partial class LineEvaluator
{
    public MagmaAttackPreview EvaluateMagmaSplit(GameSnapshot snapshot, int sourceEntityId, int targetEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        cancellationToken.ThrowIfCancellationRequested();
        if (ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not { } local ||
            !snapshot.Entities.TryGetValue(sourceEntityId, out var source) || source?.Tags is null || source.Kind != EntityKind.Card ||
            source.CardId != "FIR_953" || source.Zone != "PLAY" || source.ControllerId != local ||
            !CurrentMinion(source) || !MagmaSplitRules.IsAuditedSource(cards.GetValueOrDefault("FIR_953")) ||
            !snapshot.Entities.TryGetValue(targetEntityId, out var target) || target?.Tags is null || target.Kind != EntityKind.Card ||
            target.Zone != "PLAY" || target.ControllerId is not (1 or 2) || target.ControllerId == local ||
            !CurrentMinion(target))
            return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, sourceEntityId, targetEntityId,
                LineVerification.Unsupported, null, 0, null, [], ["需要已确认的我方场上原生熔岩猎犬与对方场上随从；不公开隐藏或未知来源。"], [], []);
        var combat = EvaluateLineCore(snapshot, [new(ActionKind.Attack, sourceEntityId, targetEntityId)], cards,
            cancellationToken, previewMagmaOnly: true);
        return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, sourceEntityId, targetEntityId,
            combat.Verification, combat.IsLegal, combat.AppliedActions, combat.Result.MagmaSplitPreviews.SingleOrDefault(),
            combat.Errors, combat.Assumptions, combat.UnsupportedMechanics, combat.RuleCoverage);

        static bool CurrentMinion(EntitySnapshot entity) =>
            (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
            (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named is "MINION" or "4") &&
            (!entity.Tags.TryGetValue("202", out var alias) || alias is "MINION" or "4");
    }

    private sealed partial class Runtime
    {
        private readonly bool _previewMagmaOnly;
        private readonly List<MagmaSplitOutcome> _magmaSplitPreviews = [];
        public bool MagmaSplitBoundaryReached { get; private set; }

        private MagmaSplitOutcome PreviewMagmaSplit(Entity source, Entity target)
        {
            // Freeze only public board characters and the two just-used public combatants.
            // Private HAND/DECK/SECRET identities never enter this consequence helper.
            if (_snapshot.Entities.Any(pair => pair.Value is null || pair.Value.Tags is null || pair.Key != pair.Value.Id))
                return MagmaSplitRules.Evaluate(_snapshot, source.Id, target.Id, _cards);
            var after = Entities.Values.Where(entity => entity.Id == source.Id || entity.Id == target.Id ||
                    (entity.Zone == "PLAY" || _snapshot.Entities[entity.Id].Zone == "PLAY") && entity.Type is "HERO" or "MINION")
                .OrderBy(entity => entity.Id).Select(FreezeForMagma).ToArray();
            var frame = new MagmaSplitState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence,
                source.Id, target.Id, Array.AsReadOnly(after)) { PriorResolutionPending = _assumptions.Count > 0 };
            return MagmaSplitRules.Evaluate(_snapshot, source.Id, target.Id, _cards, frame);
        }

        private EntitySnapshot FreezeForMagma(Entity entity)
        {
            var frozen = FreezeForPlayTriggers(entity); var original = _snapshot.Entities[entity.Id]; var tags = frozen.Tags.ToDictionary();
            foreach (var (name, alias, value) in new[] { ("ARMOR", "292", entity.Armor), ("DIVINE_SHIELD", "194", Has(entity, "DIVINE_SHIELD") ? 1 : 0) })
            {
                var named = original.Tags.GetValueOrDefault(name); var numeric = original.Tags.GetValueOrDefault(alias);
                if (named is null && numeric is null) continue;
                if (named is not null && !int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                    numeric is not null && !int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                    named is not null && numeric is not null && Number(named) != Number(numeric)) continue;
                tags[name] = value.ToString(CultureInfo.InvariantCulture);
                if (numeric is not null) tags[alias] = tags[name];
            }
            return frozen with { Tags = tags.ToFrozenDictionary(StringComparer.Ordinal) };
        }

        public void RecordMagmaSplit(Entity source, Entity target, MagmaSplitOutcome? preview = null)
        {
            preview ??= PreviewMagmaSplit(source, target);
            if (!_previewMagmaOnly && preview.TriggerActive == false && !preview.RequiresObservation) return;
            _magmaSplitPreviews.Add(preview);
            Coverage.UnionWith(preview.RuleCoverage);
            if (preview.TriggerActive == false && !preview.RequiresObservation) return;
            MagmaSplitBoundaryReached = true;
            Conditional("熔岩猎犬分配伤害只列独立条件分支；当前实体投影停在基础交换之后，未选定实际命中，后续动作等待新局面。",
                "FIR_953_RANDOM_AFTER_ATTACK");
        }
    }
}
