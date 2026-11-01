using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private static void PlayAssembled(Runtime state, PlannedAction action, Entity source)
    {
        var definition = state.AssembledDefinition(source);
        if (definition is null) { state.Unsupported("CUSTOM_ASSEMBLED_SOURCE", "拼装牌的半段、完整定义或当前实体身份未通过脚本审核，未按原卡ID执行。"); return; }
        state.PlayAssembledHandBuff(action, source, definition);
    }

    private sealed partial class Runtime
    {
        private readonly Dictionary<int, AssembledScriptDefinition?> _assembledDefinitions = [];
        private readonly Dictionary<int, Profile> _assembledProfiles = [];
        private readonly List<AssembledEffectOutcome> _assembledEffects = [];
        private void NormalizeAssembledFlags(Entity entity, EntitySnapshot observed)
        {
            foreach (var pair in GiftBooleanTags)
                if (!entity.Flags.ContainsKey(pair.Name) && RawNumber(observed, pair.Name, pair.Alias, false, out var value) && value is 0 or 1)
                    entity.Flags[pair.Name] = value.Value;
        }
        private bool ValidAssembledFlags(Entity entity) => _snapshot.Entities.TryGetValue(entity.Id, out var observed) &&
            GiftBooleanTags.All(pair => RawNumber(observed, pair.Name, pair.Alias, false, out var value) && value is null or 0 or 1);

        public AssembledScriptDefinition? AssembledDefinition(Entity entity)
        {
            if (!HasCustomAssembledText(entity) || !_snapshot.Entities.TryGetValue(entity.Id, out var observed) ||
                observed.Kind != EntityKind.Card || observed.Id != entity.Id || observed.CardId != entity.CardId ||
                observed.ControllerId is not (1 or 2) || !NativeCardDefinitions.ObservedType(observed, "MINION") ||
                !ObservedPlayIdentity(new Entity { Id = entity.Id, Controller = entity.Controller, Zone = observed.Zone ?? "" })) return null;
            if (!_assembledDefinitions.TryGetValue(entity.Id, out var definition))
                _assembledDefinitions[entity.Id] = definition = AssembledScriptAudit.Resolve(_snapshot.ClientBuild, observed.CardId, observed.Tags, _cards);
            return definition;
        }
        public bool HasCustomAssembledTextById(int id) => Entities.TryGetValue(id, out var entity) && HasCustomAssembledText(entity);
        private bool IsAssembledKnownNumericTag(Entity entity, string tag) => AssembledDefinition(entity) is not null &&
            tag is "4572" or "4573" or "202" or "49" or "50" or "53" or "47" or "45" or "44" or "48" or
                "218" or "188" or "197" or "791" or "190" or "194" or "189" or "685" or "363" or "191" or "1211" or "260" or "448";

        private Profile? AssembledProfile(Entity entity)
        {
            if (AssembledDefinition(entity) is not { } definition || !_cards.TryGetValue(definition.BaseCardId, out var card)) return null;
            if (_assembledProfiles.TryGetValue(entity.Id, out var existing)) return existing;
            using var raw = System.Text.Json.JsonDocument.Parse(card.RawJson);
            var data = raw.RootElement; var races = new HashSet<string>(StringComparer.Ordinal);
            var nativeRace = data.TryGetProperty("race", out var race) ? race.GetString() : null;
            if (nativeRace is not null) races.Add(nativeRace);
            if (data.TryGetProperty("races", out var list)) foreach (var value in list.EnumerateArray()) races.Add(value.GetString()!);
            // Abilities are replaced. Only the proven recipe and actual current keyword flags remain;
            // base/donor printed cost, stats, keywords and native handlers do not describe this script.
            int? Printed(string key) => data.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : null;
            return _assembledProfiles[entity.Id] = new("MINION", Printed("attack"), Printed("health"),
                ["BATTLECRY"], definition.EffectiveText, races, 0, nativeRace);
        }

        private bool ScriptBoolean(EntitySnapshot observed, string name, string alias, bool fallback, out bool value)
        {
            value = fallback;
            if (!RawNumber(observed, name, alias, false, out var raw) || raw is not (null or 0 or 1)) return false;
            if (raw.HasValue) value = raw == 1;
            return true;
        }

        public void PlayAssembledHandBuff(PlannedAction action, Entity source, AssembledScriptDefinition definition)
        {
            if (action.TargetEntityId is not null) { Invalid("已审核拼装手牌增益不接受指定目标。"); return; }
            if (source.Type != "MINION" || !KnownHandType(source) || !ObservedPlayIdentity(source))
            { Unsupported("ASSEMBLED_SOURCE_IDENTITY", "拼装成品当前类别、区域或归属不一致，未支付。"); return; }
            var observed = _snapshot.Entities[source.Id];
            if (!RawNumber(observed, "COST", "48", true, out _) || source.Cost is null or < 0 ||
                !KnownScriptStats(source, fresh: true) || !RawCombatKeywords(observed) ||
                !ScriptBoolean(observed, "BATTLECRY", "218", true, out var battlecry) ||
                !ScriptBoolean(observed, "SILENCED", "188", false, out var silence) ||
                !ScriptBoolean(observed, "CANT_PLAY", "", false, out _))
            { Unsupported("ASSEMBLED_CURRENT_OBSERVATIONS", "拼装来源当前费用、属性、战吼或沉默证据缺失/冲突，不补印刷值。"); return; }
            if (Has(source, "CANT_PLAY")) { Invalid("该拼装手牌当前不能使用。"); return; }
            if (Has(source, "DORMANT") || Has(source, "UNTOUCHABLE"))
            { Unsupported("ASSEMBLED_ENTRY_STATE", "拼装来源当前休眠或不可交互的入场状态未覆盖。"); return; }
            var active = battlecry && !silence;
            var cry = BeginBattlecry(source);
            if (!cry.Multiplicity.IsResolved)
            {
                RecordAssembled(source, definition, cry.Multiplicity.IsEnabled, null, [], "战吼次数仍未审计，不按一次或任意重复次数计算，也未支付或入场。");
                RequireKnownBattlecry(cry); return;
            }
            var hand = Entities.Values.Where(entity => entity.Zone == "HAND" && entity.Controller == Local).ToArray();
            if (active && Entities.Values.Any(entity => entity.Zone == "HAND" && entity.Controller is not (1 or 2)))
            { Unsupported("ASSEMBLED_HAND_OWNERSHIP", "存在归属未确认的手牌实体，不能确定完整目标集合。"); return; }
            if (_snapshot.Entities.TryGetValue(Player!.Id, out var rawPlayer) &&
                (!RawNumber(rawPlayer, "NUM_CARDS_IN_HAND", "", false, out var count) ||
                    count.HasValue && count != _snapshot.Entities.Values.Count(entity => entity.ControllerId == Local && entity.Zone == "HAND")))
            { Unsupported("ASSEMBLED_HAND_CARD_COUNT", "当前手牌计数或别名与原始实体集合不一致，不能把未识别目标当作空手牌。"); return; }
            var targets = new List<Entity>();
            if (active)
                foreach (var card in hand.Where(entity => entity.Id != source.Id))
                {
                    if (card.CardId is null || !_cards.ContainsKey(card.CardId) || !ObservedPlayIdentity(card) || !KnownHandType(card))
                    { Unsupported("ASSEMBLED_HAND_TARGET_TYPE", "手牌集合仍有身份/类别冲突，不能确认全部随从目标。"); return; }
                    if (card.Type != "MINION") continue;
                    if (!KnownScriptStats(card, fresh: false))
                    { Unsupported("ASSEMBLED_HAND_TARGET_STATS", "手牌随从当前属性缺失或冲突，不能用印刷值补齐增益结果。"); return; }
                    if (!HasCustomAssembledText(card) && !RequireNativeDefinition(card)) return;
                    targets.Add(card);
                }
            // Preflight all arithmetic before paying or changing any entity.
            var changes = targets.OrderBy(entity => entity.Id).Select(target =>
                new AssembledEffectTarget(target.Id, target.Attack!.Value, checked(target.Attack.Value + cry.Multiplicity.Activations!.Value),
                    target.MaximumHealth!.Value, checked(target.MaximumHealth.Value + cry.Multiplicity.Activations.Value), target.Health!.Value, checked(target.Health.Value + cry.Multiplicity.Activations.Value)));
            var frozenChanges = changes.ToArray();
            var observedBehavior = source.Flags.Where(pair => pair.Key is "BATTLECRY" or "DEATHRATTLE" or "TRIGGER_VISUAL" or "AURA" or "217" or "218" or "32" or "362").ToArray();
            var profile = AssembledProfile(source)!;
            if (!CanAffordCard(source) || !Summon(source, profile, observedStatsOnly: true)) return;
            if (OpponentDefeatedByPayment) { ExecuteBattlecry(cry, _ => BattlecryStep.Done()); return; }
            foreach (var pair in observedBehavior) source.Flags[pair.Key] = pair.Value;
            ExecuteBattlecry(cry, _ =>
            {
                var currentTargets = targets.Where(target => target.Zone == "HAND" && target.Controller == Local).OrderBy(target => target.Id).ToArray();
                foreach (var target in currentTargets)
                { target.Attack = checked(target.Attack!.Value + 1); target.MaximumHealth = checked(target.MaximumHealth!.Value + 1); target.Health = checked(target.Health!.Value + 1); }
                return BattlecryStep.Done(currentTargets.Select(target => target.Id).ToArray());
            });
            var completeCount = cry.Steps.All(step => step.IsComplete) && !BattlecryBoundaryReached ? (int?)cry.Completed : null;
            var actualChanges = cry.Completed == 0 ? [] : frozenChanges.Select(change => change with
            { AttackAfter=Entities[change.EntityId].Attack!.Value, MaximumHealthAfter=Entities[change.EntityId].MaximumHealth!.Value, RemainingHealthAfter=Entities[change.EntityId].Health!.Value }).ToArray();
            RecordAssembled(source, definition, active, completeCount, actualChanges,
                active ? "已审核成品的战吼；支付和本体离手一次，各已完成轮仅增益当时仍在手牌的随从，保持当前费用及损伤。逐轮次数见BattlecryExecutions；未完成前缀不是全部结算。" : "当前明确战吼抑制；只支付并入场，不执行增益。保留独立的使用后触发。");
            Coverage.Add("assembled-script:" + definition.ScriptKey);
            ResolveDeaths();
        }

        private bool KnownScriptStats(Entity entity, bool fresh)
        {
            if (!_snapshot.Entities.TryGetValue(entity.Id, out var original) ||
                !RawNumber(original, "ATK", "47", true, out _) || !RawNumber(original, "HEALTH", "45", true, out _) ||
                !RawNumber(original, "DAMAGE", "44", true, out var damage) || fresh && damage != 0 ||
                entity.Attack is null or < 0 || entity.MaximumHealth is null or <= 0 || entity.Health is null or <= 0 || entity.Health > entity.MaximumHealth) return false;
            return true;
        }
        private bool KnownHandType(Entity entity) => entity.Type is "MINION" or "SPELL" or "WEAPON" or "HERO" or "LOCATION" &&
            _snapshot.Entities.TryGetValue(entity.Id, out var original) &&
            entity.CardId is not null && _cards.TryGetValue(entity.CardId, out var definition) && definition.Type == entity.Type &&
            (original.Tags.ContainsKey("CARDTYPE") || original.Tags.ContainsKey("202")) && ObservedCardType(entity, entity.Type);
        private void RecordAssembled(Entity source, AssembledScriptDefinition definition, bool? active, int? count,
            IReadOnlyList<AssembledEffectTarget> targets, string scope) => _assembledEffects.Add(new(_snapshot.MatchId, _snapshot.StateVersion,
                _snapshot.EventSequence, _snapshot.ClientBuild!, source.Id, definition.BaseCardId, definition.FirstHalfDbfId, definition.SecondHalfDbfId,
                definition.TriggerCardId, definition.EffectCardId, definition.ScriptKey, active, count, targets, scope));
        public bool SearchableAssembledBody(int id) => Entities.TryGetValue(id, out var source) && AssembledDefinition(source) is not null;
    }
}
