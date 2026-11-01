using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private static bool TryPlayNativeDiscovery(Runtime state, PlannedAction action, Entity source, Profile profile)
    {
        if (source.CardId is not { } id || !DiscoveryPlayRules.SupportsCardId(id)) return false;
        if (!state.RequireAuditedBuild()) return true;
        if (action.TargetEntityId is not null) { state.Invalid("该发现、拼装或回溯来源不需要指定目标。"); return true; }
        var native = state.InspectNativeDiscovery(source);
        if (!native.ResolvesEffects) { state.Unsupported("UNCONFIRMED_DISCOVERY_SOURCE", "来源的固定定义、当前身份或原生文字未确认，未支付或使用。"); return true; }
        var cry = profile.Type == "MINION" ? state.BeginBattlecry(source) : null;
        if (profile.Type == "MINION")
        {
            if (!state.Summon(source, profile)) return true;
            if (state.OpponentDefeatedByPayment)
            {
                state.ExecuteBattlecry(cry!, _ => BattlecryStep.Done()); return true;
            }
        }
        else if (profile.Type == "SPELL")
        {
            if (!state.Pay(source) || state.OpponentDefeatedByPayment) return true;
            source.Zone = "GRAVEYARD";
        }
        else { state.Unsupported("DISCOVERY_SOURCE_TYPE", "发现来源不是已覆盖的随从或法术。"); return true; }
        if (cry is null) state.RecordNativeDiscovery(source, native);
        else if (!cry.Multiplicity.IsResolved || cry.Multiplicity.Activations == 0)
        {
            state.RecordNativeDiscovery(source, native);
            state.ExecuteBattlecry(cry, _ => BattlecryStep.Done());
        }
        else state.ExecuteBattlecry(cry, _ =>
        {
            var result = state.RecordNativeDiscovery(source, native);
            return result.ResolvesEffects && result.RequiresChoice == false
                ? BattlecryStep.Done() : BattlecryStep.Pending("DiscoveryRequiresObservation");
        });
        state.Coverage.Add("native-discovery-source-used:" + id);
        return true;
    }

    private sealed partial class Runtime
    {
        private readonly List<DiscoveryPlayOutcome> _discoveryPreviews = [];
        public bool DiscoveryBoundaryReached { get; private set; }

        private bool HasUnresolvedNativeResolution => BattlecryBoundaryReached || DrawBoundaryReached || GenerationBoundaryReached || WolfBlessingBoundaryReached ||
            DiscoveryBoundaryReached || MapFollowupBoundaryReached || MagmaSplitBoundaryReached || _unsupported.Overlaps(["DEATHRATTLE_OR_REBORN", "BATTLECRY_OR_DEATHRATTLE_MODIFIER",
                "UNKNOWN_MYTHICAL_TERROR_EXTRA_EFFECT", "UNKNOWN_TRIGGER", "UNKNOWN_AURA", "UNMAPPED_BOARD_TEXT",
                "UNMAPPED_ATTACHED_ENCHANTMENT", "OPPONENT_SECRET", "FRIENDLY_SECRET_OR_QUEST", "CUSTOM_ASSEMBLED_HAND", "ASSEMBLED_EXTRA_TRIGGER", "UNKNOWN_BATTLECRY_SOURCE_TRIGGER"]);

        public DiscoveryPlayOutcome InspectNativeDiscovery(Entity source) => DiscoveryPlayRules.Evaluate(_snapshot, source.Id, _cards);

        public DiscoveryPlayOutcome RecordNativeDiscovery(Entity source, DiscoveryPlayOutcome native)
        {
            var originalCount = _snapshot.Entities.Values.Count(entity => entity.ControllerId == Local && entity.Zone == "DECK");
            var currentCount = Entities.Values.Count(entity => entity.Controller == Local && entity.Zone == "DECK");
            var deckCount = !HasUnresolvedNativeResolution && originalCount == currentCount ? native.DeckCountBeforeEffect : null;
            var before = new DiscoveryPlayState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence, source.Id, deckCount, native.EffectActive)
                { SourceHasLeftHand = true, PriorResolutionPending = HasUnresolvedNativeResolution };
            var result = DiscoveryPlayRules.Evaluate(_snapshot, source.Id, _cards, before);
            _discoveryPreviews.Add(result);
            foreach (var coverage in result.RuleCoverage) Coverage.Add(coverage);
            if (!result.ResolvesEffects || result.RequiresChoice != false)
            {
                DiscoveryBoundaryReached = true;
                Conditional("本体已成功离手，原生发现、拼装或回溯需要新的选择局面；未创建候选、手牌或恢复旧时间线，后续动作先等待日志。", "DISCOVERY_REQUIRES_OBSERVATION");
            }
            else Coverage.Add("native-discovery-no-choice-under-confirmed-current-condition");
            return result;
        }
    }
}
