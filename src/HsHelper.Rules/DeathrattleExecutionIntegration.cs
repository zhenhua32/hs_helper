using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed class DeathrattleRun(DeathrattleMultiplicityOutcome plan, Entity body, string kind,
        string? effectCardId, int action, int batch, int[] batchIds)
    {
        public DeathrattleMultiplicityOutcome Plan { get; } = plan;
        public Entity Body { get; } = body;
        public string Kind { get; } = kind;
        public List<DeathrattleActivationOutcome> Steps { get; } = [];
        public bool OrderIndependent { get; set; }
        public string StopReason { get; set; } = "NotStarted";
        public DeathrattleExecutionOutcome Build() => new(Plan.MatchId, Plan.StateVersion, Plan.EventSequence,
            Plan.CardBuild ?? "unknown", action, batch, batchIds, OrderIndependent, Body.Id, Body.CardId, Kind,
            Plan.EffectSourceEntityId, effectCardId, Plan.EffectEnchantmentEntityId, Plan.IsAcquiredEffect,
            Plan.IsResolved, Plan.IsEnabled, Plan.Activations, Steps.Count, Steps.Count(step => step.IsComplete),
            Plan.Activations is { } count ? count - Steps.Count(step => step.IsComplete) : null,
            Plan.IsResolved && (Steps.Count(step => step.IsComplete) == Plan.Activations || StopReason == "GameEnded"),
            Plan.MultiplierSourceEntityIds, Plan.EnchantmentEntityIds, Steps, StopReason);
    }

    private sealed partial class Runtime
    {
        private readonly List<DeathrattleRun> _deathrattleRuns = [];
        private readonly Dictionary<int, int> _deathrattleAttachmentTargets = [];
        private readonly HashSet<int> _inactiveDeathrattleAttachments = [];
        private int _deathBatchOrdinal;
        public bool DeathrattleBoundaryReached { get; private set; }

        // Attachment identity and its current stat contribution are observed, not reapplied. The
        // immutable original frame provides the definition/topology; runtime keeps its lineage.
        private bool IsAuditedDeathrattleAttachment(Entity entity)
        {
            if (entity.CardId != DeathrattleMultiplicityRules.EnchantmentCardId) return false;
            if (_inactiveDeathrattleAttachments.Contains(entity.Id)) return true;
            if (!DeathrattleMultiplicityRules.TryAuditAttachment(_snapshot, entity.Id, _cards, out var id) ||
                !Entities.TryGetValue(id, out var body) || !body.Alive || body.Zone is not ("HAND" or "PLAY") ||
                !_snapshot.Entities.TryGetValue(id, out var before) || body.CardId != before.CardId || body.Controller != before.ControllerId) return false;
            _deathrattleAttachmentTargets[entity.Id] = id; return true;
        }

        private bool IsAuditedDeathrattleBody(Entity entity) => !HasCustomAssembledText(entity) &&
            entity.CardId == DeathrattleMultiplicityRules.SelfDoublerCardId &&
            DeathrattleMultiplicityRules.IsAuditedSelfDoubler(_snapshot.ClientBuild, _cards.GetValueOrDefault(entity.CardId));

        private bool UnknownDeathListener(Entity entity)
        {
            if (!_snapshot.Entities.TryGetValue(entity.Id, out var observed)) return true;
            foreach (var (name, alias) in new[] { ("AURA", "362"), ("TRIGGER_VISUAL", "32"), ("REBORN", "1085") })
                if (!RawNumber(observed, name, alias, false, out var value) || value is not (null or 0 or 1) || value == 1)
                    return true;
            return false;
        }

        private bool IsDeathrattleEvidenceNumericTag(Entity entity, string tag)
        {
            if (IsAuditedDeathrattleAttachment(entity))
                return tag is "40" or "49" or "50" or "53" or "202" or "188" or "1518" or "330" or "331" or "2921" or "313";
            if (IsAuditedDeathrattleBody(entity) && tag == "882" && entity.Int("882") == 1) return true;
            var bodyPlan = entity.Type == "MINION" ? DeathrattleMultiplicityRules.Evaluate(_snapshot, entity.Id, _cards) : null;
            if (bodyPlan is { IsResolved: true } && tag is "49" or "50" or "53" or "202") return true;
            var statName = tag switch { "47" => "ATK", "45" => "HEALTH", "44" => "DAMAGE", "48" => "COST", _ => null };
            if (statName is not null &&
                _snapshot.Entities.TryGetValue(entity.Id, out var observed) && RawNumber(observed, statName, tag, true, out _) &&
                (bodyPlan is { IsResolved: true } || entity.Zone is "HAND" or "DECK" &&
                    Entities.Values.Any(source => source.CardId == "DINO_421" && !HasCustomAssembledText(source) &&
                        source.Controller == entity.Controller && DeathrattleMultiplicityRules.Evaluate(_snapshot, source.Id, _cards).IsResolved))) return true;
            return tag is "217" or "188" or "1831" && bodyPlan is { IsResolved: true };
        }

        private bool ScopedDeathrattleModifier(Entity entity, string key) =>
            key == "EXTRA_DEATHRATTLES_BASE" && IsAuditedDeathrattleBody(entity) && entity.Int(key) == 1 ||
            key == "CANT_TRIGGER_DEATHRATTLE" && DeathrattleMultiplicityRules.Evaluate(_snapshot, entity.Id, _cards).IsResolved;

        private void PreserveDeathrattleBehavior(Entity entity)
        {
            if (!_snapshot.Entities.TryGetValue(entity.Id, out var before)) return;
            foreach (var (name, alias) in new[] { ("DEATHRATTLE", "217"), ("SILENCED", "188"),
                ("CANT_TRIGGER_DEATHRATTLE", "1831"), ("REBORN", "1085") })
                if (RawNumber(before, name, alias, false, out var value) && value is 0 or 1)
                    entity.Flags[name] = value.Value;
        }

        private DeathrattleRun NewDeathrattleRun(Entity body, int? effect, string kind, int batch, int[] ids)
        {
            var plan = DeathrattleMultiplicityRules.Evaluate(_snapshot, body.Id, _cards, effect);
            // Native absence says nothing about the number/identity of an observed extra script.
            // A proved source-wide gate can still disable it without inventing its provenance.
            if (kind == "Unknown" && plan.IsResolved && !SourceDeathrattleSuppressed(body))
                plan = plan with { IsResolved = false, IsEnabled = null, Activations = null,
                    Unknowns = plan.Unknowns.Append("当前获得的亡语脚本来源仍未知；原生脚本为零不能取消它。").ToArray() };
            // Runtime can only suppress the original script, never restore a printed effect.
            if (plan.IsResolved && (Silenced(body) || body.Int("CANT_TRIGGER_DEATHRATTLE") == 1 || body.Int("DEATHRATTLE") == 0))
                plan = plan with { IsEnabled = false, Activations = 0 };
            var run = new DeathrattleRun(plan, body, kind, effect is { } id ? Entities.GetValueOrDefault(id)?.CardId : body.CardId,
                Applied + 1, batch, ids);
            _deathrattleRuns.Add(run);
            foreach (var item in plan.RuleCoverage) Coverage.Add(item);
            return run;
        }

        private bool SourceDeathrattleSuppressed(Entity body) => _snapshot.Entities.TryGetValue(body.Id, out var original) &&
            RawNumber(original,"SILENCED","188",false,out var silence) && silence is null or 0 or 1 &&
            RawNumber(original,"CANT_TRIGGER_DEATHRATTLE","1831",false,out var blocked) && blocked is null or 0 or 1 &&
            RawNumber(original,"DEATHRATTLE","217",false,out var dr) && dr is null or 0 or 1 &&
            (silence == 1 || blocked == 1 || dr == 0);

        public bool ValidatePassiveMinionEntry(Entity body)
        {
            if (!_snapshot.Entities.TryGetValue(body.Id, out var original) ||
                !RawNumber(original,"SILENCED","188",false,out var silence) || silence is not (null or 0 or 1))
            { Unsupported("UNRESOLVED_MINION_ENTRY", "入场来源当前沉默格式或别名未知，未支付或入场。"); return false; }
            foreach (var (name,alias) in new[] { ("BATTLECRY","218"), ("AURA","362"), ("TRIGGER_VISUAL","32") })
                if (!RawNumber(original,name,alias,false,out var value) || value is not (null or 0 or 1) || silence != 1 && value == 1)
                { Unsupported("UNRESOLVED_MINION_ENTRY", "普通随从有未覆盖的当前战吼、光环或监听，未支付或按基础文字入场。"); return false; }
            return true;
        }

        public bool AuditedAcquiredOnlyBody(Entity body) => IsAuditedDeathrattleBody(body);

        private void ResolveDeathBatch(Entity[] bodies)
        {
            if (bodies.Length == 0) return;
            var ordinal = ++_deathBatchOrdinal;
            var ids = bodies.Select(body => body.Id).Order().ToArray();
            var runs = new List<DeathrattleRun>();
            var uncertain = false;
            // Freeze all scripts and their multipliers before removing any member of the batch.
            foreach (var body in bodies)
            {
                if (body.Type != "MINION" || body.Zone != "PLAY")
                { uncertain |= Has(body, "DEATHRATTLE") || Has(body, "REBORN"); continue; }
                var native = !HasCustomAssembledText(body) && body.CardId is "EDR_227" or "DINO_421";
                var grants = _snapshot.Entities.Values.Where(entity => entity.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId &&
                    (entity.GetIntTag("ATTACHED") == body.Id || entity.GetIntTag("40") == body.Id) &&
                    !(entity.Zone == "GRAVEYARD" && entity.Tags.GetValueOrDefault("49") is null or "4" or "GRAVEYARD"))
                    .ToArray();
                if (native) runs.Add(NewDeathrattleRun(body, null, body.CardId == "EDR_227" ? "NativeImbue" : "NativeHandDeckBuff", ordinal, ids));
                foreach (var grant in grants) runs.Add(NewDeathrattleRun(body, grant.Id, "ObservedEnchantmentDraw", ordinal, ids));
                if (!native && grants.Length == 0 && Has(body, "DEATHRATTLE"))
                    runs.Add(NewDeathrattleRun(body, null, "Unknown", ordinal, ids));
                uncertain |= Has(body, "REBORN") || body.Int("1085") is > 0 ||
                    (native || grants.Length > 0) && UnknownDeathListener(body);
            }
            // Removal is one operation per entity, and precedes every death script. In particular
            // a simultaneously dead aura never participates in a later script's damage/state.
            foreach (var body in bodies)
            {
                body.Alive = false; body.Zone = "GRAVEYARD";
                CompleteBattlecryAttachmentsAfterDeath(body);
                CompleteObservedStaticGiftsAfterDeath(body);
                foreach (var pair in _deathrattleAttachmentTargets.Where(pair => pair.Value == body.Id)) _inactiveDeathrattleAttachments.Add(pair.Key);
                if (body.Type == "WEAPON")
                { _removedWeaponIds.Add(body.Id); if (Has(body, "DEATHRATTLE") || Has(body, "REBORN")) MarkWeaponBoundary(); }
            }
            var active = runs.Where(run => run.Plan.Activations != 0).ToArray();
            if (active.Length > 1)
                foreach (var run in active.Where(run => run.Kind == "NativeHandDeckBuff"))
                    CheckDinoCollection(run.Body, auditTargets: true);
            // Native Imbue and fixed hand/deck additions commute. Draw/unknown scripts do not:
            // choosing an ID order could change drawn stats, fatigue or subsequent listeners.
            var independent = !uncertain && active.All(run => run.Plan.IsResolved && run.Kind is "NativeImbue" or "NativeHandDeckBuff") &&
                (active.Length <= 1 || _assumptions.Count == 0);
            var singleDraw = !uncertain && active.Length == 1 && active[0].Kind == "ObservedEnchantmentDraw";
            foreach (var run in runs) run.OrderIndependent = independent || singleDraw;
            foreach (var run in runs.Where(run => run.Plan.Activations == 0)) run.StopReason = "Suppressed";
            if (OwnHero?.Health <= 0 || EnemyHero?.Health <= 0)
            { foreach (var run in active) run.StopReason = "GameEnded"; return; }
            if (uncertain || !independent && !singleDraw)
            {
                foreach (var run in active) run.StopReason = uncertain ? "DeathOrRebornRequiresObservation" : "DeathBatchOrderRequiresObservation";
                foreach (var run in active.Where(run => run.Kind == "ObservedEnchantmentDraw")) RecordDeathrattleDraw(run.Body);
                if (uncertain || active.Length > 0) PendingDeathrattle("DEATHRATTLE_OR_REBORN");
                return;
            }
            try
            {
                foreach (var group in active.Where(run => run.Kind == "NativeHandDeckBuff").GroupBy(run => run.Body.Controller))
                    PreflightDinoArithmetic(group.First().Body, group.Sum(run => run.Plan.Activations!.Value));
            }
            catch (OverflowException)
            { foreach (var run in active) run.StopReason = "EvaluationLimit"; throw; }
            foreach (var run in active)
            {
                if (DeathrattleBoundaryReached) { run.StopReason = "PriorResolutionRequiresObservation"; continue; }
                ExecuteDeathrattle(run);
            }
        }

        private void PendingDeathrattle(string kind)
        {
            DeathrattleBoundaryReached = true;
            Conditional("死亡实体已移除；亡语、复生或同批顺序只计算到已列前缀，余下结算等待新局面。", kind);
        }

        private void ExecuteDeathrattle(DeathrattleRun run)
        {
            if (!run.Plan.IsResolved) { run.StopReason = "UnknownMultiplicity"; PendingDeathrattle("UNRESOLVED_DEATHRATTLE_MULTIPLICITY"); return; }
            for (var index = 1; index <= run.Plan.Activations; index++)
            {
                if (OwnHero?.Health <= 0 || EnemyHero?.Health <= 0) { run.StopReason = "GameEnded"; return; }
                var powerChangesBefore = _heroPowerChanges.Count;
                var statsBefore = Entities.Values.Where(entity => entity.Controller == run.Body.Controller && entity.Zone is "HAND" or "DECK")
                    .ToDictionary(entity => entity.Id, entity => (entity.Attack, entity.MaximumHealth, entity.Health));
                if (run.Kind == "ObservedEnchantmentDraw")
                {
                    RecordDeathrattleDraw(run.Body);
                    run.Steps.Add(new(index, false, "AwaitingObservation", "DrawRequiresObservation", []));
                    run.StopReason = "DrawRequiresObservation"; DeathrattleBoundaryReached = true; return;
                }
                try
                {
                    TryResolveBeastHunterDeath(run.Body);
                }
                catch (OverflowException)
                { run.Steps.Add(new(index, false, "Interrupted", "EvaluationLimit", [])); run.StopReason = "EvaluationLimit"; throw; }
                var affected = run.Kind == "NativeImbue" ? _heroPowerChanges.Skip(powerChangesBefore).Select(change => change.EntityId).Distinct().Order().ToArray() :
                    statsBefore.Where(pair => Entities.TryGetValue(pair.Key, out var entity) && pair.Value != (entity.Attack, entity.MaximumHealth, entity.Health))
                        .Select(pair => pair.Key).Order().ToArray();
                var onlyImbueParameters = run.Kind == "NativeImbue" && _unsupported.All(kind => kind is "UNKNOWN_IMBUE_POWER" or "UNKNOWN_IMBUE_LEVEL" or
                    "UNKNOWN_IMBUE_READINESS" or "UNSUPPORTED_IMBUE_CLASS" or "MISSING_IMBUE_CARD_DATA");
                var complete = !Stopped && (_assumptions.Count == 0 || onlyImbueParameters) &&
                    !DrawBoundaryReached && !GenerationBoundaryReached && !RaptorBoundaryReached;
                var reason = complete ? "ActivationResolved" : "RelatedResolutionRequiresObservation";
                run.Steps.Add(new(index, complete, complete ? "Completed" : Stopped ? "Interrupted" : "AwaitingObservation", reason, affected));
                if (!complete) { run.StopReason = reason; PendingDeathrattle("DEATHRATTLE_REQUIRES_OBSERVATION"); return; }
            }
            run.StopReason = "AllActivationsResolved";
        }
    }
}
