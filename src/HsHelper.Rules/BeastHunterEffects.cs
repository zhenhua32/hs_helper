using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    // Text/stat provenance: fixed build 255790 cards.json and CardDefs.base.xml.
    // Imbue semantics: https://hearthstone.blizzard.com/en-us/expansions-adventures/into-the-emerald-dream
    private static readonly HashSet<string> AuditedHunterBodies = ["EDR_852", "EDR_800", "EDR_227", "EDR_226", "FIR_921", "EDR_480", "DINO_421"];

    private static bool TryPlayBeastHunter(Runtime state, PlannedAction action, Entity source, Profile profile)
    {
        if (source.CardId is not { } id || !AuditedHunterBodies.Contains(id)) return false;
        if (!state.RequireAuditedBuild()) return true;
        if (profile.Type != "MINION") { state.Unsupported("CARD_DATA_MISMATCH", "固定版本牌效与卡牌类型不一致。"); return true; }
        if (action.TargetEntityId is not null) { state.Invalid("该随从的入场不需要指定目标。"); return true; }
        var battlecryActive = state.Has(source, "BATTLECRY");
        if (!state.Summon(source, profile)) return true;
        if (battlecryActive && id is "EDR_852" or "EDR_800" or "EDR_226")
        {
            if (id == "EDR_226") state.Conditional("入场与灌注已计算；抽取野兽的身份、实体与牌库顺序未知，抽牌尚未结算，不能据此安排抽到的牌。", "UNRESOLVED_BEAST_DRAW");
            state.ImbueHunter(source.Controller, "battlecry:" + id);
        }
        if (battlecryActive && id == "FIR_921")
            state.Conditional("花木护侍检查本局累计灌注次数，当前技能参数不能完整证明历史次数；条件抽牌、疲劳与抽牌触发尚未展开，结果仅包含入场。", "UNKNOWN_IMBUE_DRAW_THRESHOLD");
        state.Coverage.Add("audited-minion-summon:" + id);
        return true;
    }

    private static void UseWolfBlessing(Runtime state, Entity source, PlannedAction action)
    {
        if (!state.RequireAuditedBuild()) return;
        if (action.TargetEntityId is not null) { state.Invalid("巨狼的祝福随机选择手牌，不能指定目标。"); return; }
        var level = state.WolfLevel(source);
        if (level is null) { state.Unsupported("UNKNOWN_WOLF_PARAMETERS", "巨狼祝福的当前攻击力和减费参数未观察到一致可靠值，不能把印刷初值当当前强度。"); return; }
        var hand = state.Entities.Values.Where(e => e.Alive && e.Controller == source.Controller && e.Zone == "HAND").ToArray();
        if (hand.Any(e => state.Profile(e) is null))
        { state.Unsupported("UNKNOWN_WOLF_CANDIDATES", "存在身份或类型未知的手牌，不能确认巨狼祝福的随机候选集合。"); return; }
        var beasts = hand.Where(state.IsBeast).ToArray();
        if (beasts.Length == 0) { state.Unsupported("NO_WOLF_CANDIDATE", "没有可确认的野兽手牌；空放该英雄技能的合法性尚未审计。"); return; }
        if (beasts.Length == 1 && (beasts[0].Attack is null or < 0 || beasts[0].Cost is null or < 0))
        { state.Unsupported("UNKNOWN_WOLF_TARGET_STATS", "唯一候选的当前攻击力或费用未知，不能用印刷值代替手牌增益后的属性。"); return; }
        if (!state.Pay(source)) return;
        source.Flags["RULES_USED"] = 1; source.Flags["EXHAUSTED"] = 1;
        if (beasts.Length > 1)
        { state.Conditional("巨狼祝福存在多个野兽候选，未选择或枚举随机命中结果；已记录费用但未修改任一手牌。", "RANDOM_WOLF_TARGET"); return; }
        beasts[0].Attack = checked(beasts[0].Attack!.Value + level.Value);
        beasts[0].Cost = Math.Max(0, beasts[0].Cost!.Value - level.Value);
        state.Coverage.Add("EDR_850p-single-observed-beast-buff-and-discount");
    }

    private sealed partial class Runtime
    {
        private readonly List<HeroPowerChange> _heroPowerChanges = [];

        private bool IsObservedStatEnchantment(Entity enchantment)
        {
            if (_snapshot.ClientBuild != AuditedBuild || enchantment.Type != "ENCHANTMENT" || enchantment.CardId is null || Profile(enchantment) is null ||
                enchantment.Int("ATTACHED") is not { } id || !Entities.TryGetValue(id, out var target) || target.Type != "MINION" ||
                target.Zone is not ("HAND" or "DECK" or "PLAY")) return false;
            // These pinned definitions only modify stats/cost. Current observed values already include them.
            return enchantment.CardId switch
            {
                "EDR_850pe1" => target.Attack is not null,
                "EDR_850pe5" => target.Cost is not null,
                "DINO_421e" => target.Attack is not null && target.MaximumHealth is not null,
                _ => false
            };
        }

        public bool Summon(Entity source, Profile profile)
        {
            if (Entities.Values.Count(e => e.Alive && e.Type == "MINION" && e.Zone == "PLAY" && e.Controller == source.Controller) >= 7)
            { Invalid("我方随从区已满。"); return false; }
            if (profile.Attack is null || profile.Health is null) { Unsupported("UNKNOWN_BASE_STATS", "入场随从的基础属性缺失。"); return false; }
            if (!Pay(source)) return false;
            source.Zone = "PLAY"; source.Attack ??= profile.Attack; source.MaximumHealth ??= profile.Health; source.Health = source.MaximumHealth;
            source.Type = "MINION"; source.Attacks = 0;
            source.Flags = source.Flags.Where(flag => StaticKeywords.Contains(flag.Key) || flag.Key == "SILENCED").ToDictionary(StringComparer.Ordinal);
            source.Flags["NUM_TURNS_IN_PLAY"] = 0;
            source.Flags["EXHAUSTED"] = Has(source, "CHARGE") || Has(source, "RUSH") ? 0 : 1;
            return true;
        }

        private Entity? Power(int? controller)
        {
            var powers = Entities.Values.Where(e => e.Alive && e.Type == "HERO_POWER" && e.Controller == controller && e.Zone == "PLAY").Take(2).ToArray();
            return powers.Length == 1 ? powers[0] : null;
        }
        public int? WolfLevel(Entity power)
        {
            var attack = power.Int("TAG_SCRIPT_DATA_NUM_1") ?? power.Int("2");
            var discount = power.Int("TAG_SCRIPT_DATA_NUM_2") ?? power.Int("3");
            // Both text numbers increase together. Unequal or missing observations are not a known level.
            return attack is >= 1 && discount == attack ? attack : null;
        }
        public void ImbueHunter(int? controller, string reason)
        {
            var heroClass = controller == Local ? _snapshot.LocalClass : _snapshot.OpponentClass;
            if (controller is null || heroClass != "HUNTER")
            { Conditional("本次灌注所属英雄职业未知或不是已覆盖的猎人，未模拟其英雄技能变化。", "UNSUPPORTED_IMBUE_CLASS"); return; }
            var power = Power(controller);
            if (power is null) { Conditional("当前英雄技能实体未唯一识别，灌注后的技能保持未知。", "UNKNOWN_IMBUE_POWER"); return; }
            if (!_cards.ContainsKey("EDR_850p"))
            { Conditional("缺少固定版本关联牌 EDR_850p，未展开灌注后的技能。", "MISSING_IMBUE_CARD_DATA"); return; }
            var previous = power.CardId;
            var oldLevel = previous == "EDR_850p" ? WolfLevel(power) : null;
            int? newLevel = oldLevel is { } level ? checked(level + 1) : null;
            var availabilityKnown = previous == "EDR_850p" && power.Int("RULES_HERO_POWER_READINESS_UNKNOWN") is not > 0;
            if (newLevel is { } next)
            { power.Flags["TAG_SCRIPT_DATA_NUM_1"] = next; power.Flags["TAG_SCRIPT_DATA_NUM_2"] = next; power.Flags.Remove("2"); power.Flags.Remove("3"); }
            else
            {
                power.Flags.Remove("TAG_SCRIPT_DATA_NUM_1"); power.Flags.Remove("TAG_SCRIPT_DATA_NUM_2"); power.Flags.Remove("2"); power.Flags.Remove("3");
                Conditional("灌注会将猎人技能变为或强化巨狼祝福，但缺少可靠累计强度，不假设当前为首次灌注。", "UNKNOWN_IMBUE_LEVEL");
            }
            if (!availabilityKnown)
            { power.Flags["RULES_HERO_POWER_READINESS_UNKNOWN"] = 1; Conditional("技能替换后的可用状态需要客户端确认，不据此追加一次技能使用。", "UNKNOWN_IMBUE_READINESS"); }
            power.CardId = "EDR_850p";
            _heroPowerChanges.Add(new(controller.Value, power.Id, previous, power.CardId, oldLevel, newLevel, availabilityKnown, reason));
            Coverage.Add("hunter-imbue-observed-level-transition");
        }

        private bool TryResolveBeastHunterDeath(Entity entity)
        {
            if (_snapshot.ClientBuild != AuditedBuild || Silenced(entity) || !Has(entity, "DEATHRATTLE")) return false;
            if (entity.CardId == "EDR_227")
            {
                ImbueHunter(entity.Controller, "deathrattle:EDR_227"); Coverage.Add("EDR_227-deathrattle-imbue"); return true;
            }
            if (entity.CardId != "DINO_421") return false;
            foreach (var card in Entities.Values.Where(e => e.Alive && e.Controller == entity.Controller && e.Zone is "HAND" or "DECK"))
            {
                var profile = Profile(card);
                if (profile is null) { Conditional("震地雷龙的隐藏牌库或手牌身份未知，未对未知实体虚构随从增益。", "UNKNOWN_DINO_BUFF_TARGET"); continue; }
                if (profile.Type != "MINION") continue;
                if (card.Attack is null || card.MaximumHealth is null)
                { Conditional("震地雷龙目标的当前属性未知，未用印刷值覆盖既有增益。", "UNKNOWN_DINO_BUFF_STATS"); continue; }
                card.Attack = checked(card.Attack.Value + 3); card.MaximumHealth = checked(card.MaximumHealth.Value + 3);
                if (card.Health is { } health) card.Health = checked(health + 3);
            }
            Coverage.Add("DINO_421-observed-hand-and-deck-minions-plus-3-3"); return true;
        }
    }
}
