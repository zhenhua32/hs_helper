namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        public bool RequireNativeDefinition(Entity entity)
        {
            if (HasCustomAssembledText(entity))
            {
                if (AssembledDefinition(entity) is not null) { Coverage.Add("fixed-assembled-script-definition:" + entity.CardId); return true; }
                Unsupported("UNAUDITED_ASSEMBLED_DEFINITION", "拼装实体未通过独立完整定义审核，不能回退原生牌效。"); return false;
            }
            if (!NativeCardDefinitions.IsRegistered(entity.CardId)) return true;
            if (!RequireAuditedBuild()) return false;
            if (entity.CardId == "GAME_005" && !AuditedCoinSource(entity)) return false;
            if (_snapshot.Entities.TryGetValue(entity.Id, out var observed) && observed.Id == entity.Id &&
                (observed.CardId == entity.CardId || _heroPowerChanges.Any(change => change.EntityId == entity.Id && change.CurrentCardId == entity.CardId)) &&
                entity.Type == NativeCardDefinitions.ExpectedType(entity.CardId) &&
                NativeCardDefinitions.IsAuditedEntity(_snapshot.ClientBuild, observed, _cards, entity.CardId))
            { Coverage.Add("fixed-native-definition:" + entity.CardId); return true; }
            Unsupported("UNAUDITED_NATIVE_DEFINITION", "参与本次结算的原生来源定义、实体类型或固定版本不一致，未按原卡ID执行效果。");
            return false;
        }

        public bool InspectNativeDefinitions(PlannedAction action)
        {
            if (action.Kind is ActionKind.PlayCard or ActionKind.HeroPower && Entities.TryGetValue(action.SourceEntityId, out var selected) &&
                !RequireNativeDefinition(selected)) return false;
            foreach (var entity in Entities.Values)
            {
                if (!NativeCardDefinitions.IsRegistered(entity.CardId) || HasCustomAssembledText(entity)) continue;
                var expected = NativeCardDefinitions.ExpectedType(entity.CardId);
                var active = entity.Zone == "PLAY" && (expected != "HERO_POWER" || entity.Type != "HERO_POWER") &&
                    (entity.Alive || expected == "ENCHANTMENT");
                var secret = entity.Zone == "SECRET" && entity.Controller == Local;
                var attached = expected == "ENCHANTMENT" && entity.Zone != "GRAVEYARD" && entity.Int("ATTACHED") is > 0;
                if ((active || secret || attached) && !RequireNativeDefinition(entity)) return false;
            }
            return true;
        }
    }
}
