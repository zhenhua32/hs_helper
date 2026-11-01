using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed record BattlecryStep(bool IsComplete, string Status, string Reason, int[] Affected)
    {
        public static BattlecryStep Done(params int[] ids) => new(true, "Completed", "ActivationResolved", ids);
        public static BattlecryStep Fizzle(string reason, params int[] ids) => new(true, "Fizzled", reason, ids);
        public static BattlecryStep Pending(string reason, params int[] ids) => new(false, "AwaitingObservation", reason, ids);
    }

    private sealed class BattlecryRun(BattlecryMultiplicityOutcome multiplicity, string cardId, int actionOrdinal)
    {
        public BattlecryMultiplicityOutcome Multiplicity { get; } = multiplicity;
        public string CardId { get; } = cardId;
        public int ActionOrdinal { get; } = actionOrdinal;
        public List<BattlecryActivationOutcome> Steps { get; } = [];
        public string StopReason { get; set; } = "SourceNotEntered";
        public int Completed => Steps.Count(step => step.IsComplete);
        public BattlecryExecutionOutcome Build() => new(Multiplicity.MatchId, Multiplicity.StateVersion, Multiplicity.EventSequence,
            Multiplicity.CardBuild ?? "unknown", Multiplicity.SourceEntityId, CardId, ActionOrdinal, Multiplicity.IsResolved,
            Multiplicity.IsEnabled, Multiplicity.Activations, Steps.Count, Completed,
            Multiplicity.Activations is { } planned ? planned - Completed : null,
            Multiplicity.IsResolved && (Completed == Multiplicity.Activations || StopReason is "GameEnded" or "PaymentEndedGame"),
            Multiplicity.EnchantmentEntityIds, Steps, StopReason);
    }

    private sealed partial class Runtime
    {
        private readonly List<BattlecryRun> _battlecryRuns = [];
        private readonly Dictionary<int, int> _battlecryAttachmentTargets = [];
        private readonly HashSet<int> _inactiveBattlecryAttachments = [];
        private readonly Dictionary<int, KeyValuePair<string,int>[]> _battlecryBehavior = [];
        public bool BattlecryBoundaryReached { get; private set; }

        public BattlecryRun BeginBattlecry(Entity source)
        {
            var plan = BattlecryMultiplicityRules.Evaluate(_snapshot, source.Id, _cards);
            NormalizeAssembledFlags(source, _snapshot.Entities[source.Id]);
            _battlecryBehavior[source.Id] = source.Flags.Where(pair => pair.Key is "BATTLECRY" or "DEATHRATTLE" or "TRIGGER_VISUAL" or "AURA" or "217" or "218" or "32" or "362").ToArray();
            if (!Silenced(source) && (Has(source,"AURA") || Has(source,"TRIGGER_VISUAL")))
                Conditional("战吼来源另有当前光环或监听观察，不能当作只有已审核的战吼子脚本。", "UNKNOWN_BATTLECRY_SOURCE_TRIGGER");
            var run = new BattlecryRun(plan, source.CardId!, Applied + 1);
            _battlecryRuns.Add(run);
            foreach (var coverage in plan.RuleCoverage) Coverage.Add(coverage);
            return run;
        }

        public bool ValidateArcherBattlecryFlags(Entity source)
        {
            if (!_snapshot.Entities.TryGetValue(source.Id,out var observed) || !RawCombatKeywords(observed) ||
                !GiftBooleanTags.All(pair => RawNumber(observed,pair.Name,pair.Alias,false,out var flag) && flag is null or 0 or 1))
            { Unsupported("CONFLICTING_BATTLECRY_COMBAT_KEYWORD", "战吼伤害来源的当前关键词格式或命名/数字别名冲突，未支付或结算伤害。"); return false; }
            return true;
        }

        public void RestoreBattlecryBehavior(Entity source)
        {
            if (_battlecryBehavior.TryGetValue(source.Id,out var flags))
                foreach(var flag in flags)source.Flags[flag.Key]=flag.Value;
        }

        public bool RequireKnownBattlecry(BattlecryRun run)
        {
            if (run.Multiplicity.IsResolved) return true;
            run.StopReason = "UnknownMultiplicity";
            Unsupported("UNRESOLVED_BATTLECRY_MULTIPLICITY", "当前战吼次数或抑制证据未通过审核，未按普通一次执行。");
            return false;
        }

        public void PendingUnknownBattlecry(BattlecryRun run)
        {
            run.StopReason = "UnknownMultiplicity";
            BattlecryBoundaryReached = true;
            Conditional("战吼次数仍未知；单次直接预览不是全部战吼完成，后续动作等待新局面。", "BATTLECRY_REQUIRES_OBSERVATION");
        }

        public void ExecuteBattlecry(BattlecryRun run, Func<int, BattlecryStep> activate, bool allowUnknownImbueParameters = false)
        {
            if (OpponentDefeatedByPayment) { run.StopReason = "PaymentEndedGame"; return; }
            if (!run.Multiplicity.IsResolved) { PendingUnknownBattlecry(run); return; }
            var count = run.Multiplicity.Activations!.Value;
            if (count == 0) { run.StopReason = "Suppressed"; return; }
            for (var index = 1; index <= count; index++)
            {
                if (OwnHero?.Health <= 0 || EnemyHero?.Health <= 0) { run.StopReason = "GameEnded"; return; }
                var assumptionsBefore = _assumptions.Count;
                BattlecryStep step;
                try { step = activate(index); }
                catch (OverflowException)
                {
                    run.Steps.Add(new(index, false, "Interrupted", "EvaluationLimit", [])); run.StopReason = "EvaluationLimit"; throw;
                }
                if (Stopped) step = new(false, "Interrupted", "ActivationInterrupted", step.Affected);
                else if (step.IsComplete && ((_assumptions.Count > assumptionsBefore &&
                    !(allowUnknownImbueParameters && _unsupported.All(kind => kind is "UNKNOWN_IMBUE_POWER" or "UNKNOWN_IMBUE_LEVEL" or
                        "UNKNOWN_IMBUE_READINESS" or "UNSUPPORTED_IMBUE_CLASS" or "MISSING_IMBUE_CARD_DATA"))) ||
                    HasUnresolvedNativeResolution || WeaponResolutionBoundaryReached))
                    step = BattlecryStep.Pending("RelatedResolutionRequiresObservation", step.Affected);
                run.Steps.Add(new(index, step.IsComplete, step.Status, step.Reason, step.Affected));
                if (!step.IsComplete)
                {
                    run.StopReason = step.Reason; BattlecryBoundaryReached = true;
                    if (!Stopped) Conditional("战吼只结算到已列前缀，余下各轮不能当作已完成。", "BATTLECRY_REQUIRES_OBSERVATION");
                    return;
                }
                if (OwnHero?.Health <= 0 || EnemyHero?.Health <= 0) { run.StopReason = "GameEnded"; return; }
            }
            run.StopReason = "AllActivationsResolved";
        }

        private bool IsAuditedBattlecryAttachment(Entity entity)
        {
            if (entity.CardId != BattlecryMultiplicityRules.EnchantmentCardId) return false;
            if (_inactiveBattlecryAttachments.Contains(entity.Id) && _battlecryAttachmentTargets.TryGetValue(entity.Id, out var ended) &&
                Entities.TryGetValue(ended, out var removed) && !removed.Alive && removed.Zone == "GRAVEYARD") return true;
            if (!BattlecryMultiplicityRules.TryAuditAttachment(_snapshot, entity.Id, _cards, out var targetId) ||
                !Entities.TryGetValue(targetId, out var target) || !target.Alive || target.Zone is not ("HAND" or "PLAY") ||
                !_snapshot.Entities.TryGetValue(targetId, out var before) || target.CardId != before.CardId || target.Controller != before.ControllerId) return false;
            _battlecryAttachmentTargets[entity.Id] = targetId;
            return true;
        }

        private bool IsBattlecryEvidenceNumericTag(Entity entity, string tag)
        {
            if (IsAuditedBattlecryAttachment(entity))
                return tag is "40" or "49" or "50" or "53" or "202" or "188" or "1518" or "330" or "331" or "2921" or "3567" or "1179" or "4571";
            if (tag is not ("218" or "188")) return false;
            return BattlecryMultiplicityRules.Evaluate(_snapshot, entity.Id, _cards).IsResolved;
        }

        private void CompleteBattlecryAttachmentsAfterDeath(Entity target)
        {
            foreach (var pair in _battlecryAttachmentTargets.Where(pair => pair.Value == target.Id)) _inactiveBattlecryAttachments.Add(pair.Key);
        }

        public bool SearchableBattlecryEnabler(int id) => Entities.TryGetValue(id, out var source) && source.CardId is "EDR_852" or "EDR_800" &&
            BattlecryMultiplicityRules.Evaluate(_snapshot, id, _cards) is { IsResolved: true, IsEnabled: true, Activations: 2 };
    }
}
