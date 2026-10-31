using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    // Text/stat provenance: fixed build 255790 cards.json and CardDefs.base.xml.
    // Imbue semantics: https://hearthstone.blizzard.com/en-us/expansions-adventures/into-the-emerald-dream
    private static readonly HashSet<string> AuditedHunterBodies = ["EDR_849", "CORE_UNG_912", "EDR_852", "EDR_800", "EDR_227", "EDR_226", "FIR_921", "EDR_480", "DINO_421", "FIR_953", "EDR_489"];
    private static readonly HashSet<string> SearchableRushBodies = ["EDR_227", "EDR_480", "FIR_953"];

    private static bool TryPlayBeastHunter(Runtime state, PlannedAction action, Entity source, Profile profile)
    {
        if (source.CardId is not { } id || !AuditedHunterBodies.Contains(id)) return false;
        if (!state.RequireAuditedBuild()) return true;
        if (profile.Type != "MINION") { state.Unsupported("CARD_DATA_MISMATCH", "固定版本牌效与卡牌类型不一致。"); return true; }
        if (action.TargetEntityId is not null) { state.Invalid("该随从的入场不需要指定目标。"); return true; }
        var battlecryActive = state.Has(source, "BATTLECRY");
        if (!state.Summon(source, profile)) return true;
        if (state.OpponentDefeatedByPayment) return true;
        if (battlecryActive && id == "EDR_489") state.ArmOpponentHealthPayment();
        if (id == "EDR_226")
        {
            var draw = state.RecordNativeDraw(source);
            if (!draw.ResolvesEffects || draw.RequestedDraws.Maximum != 0)
                state.DeferImbueAfterDraw();
        }
        else if (battlecryActive && id is "EDR_852" or "EDR_800") state.ImbueHunter(source.Controller, "battlecry:" + id);
        if (id == "FIR_921")
            state.RecordNativeDraw(source);
        // The direct resolver retains raw alias conflicts even when the runtime flag is zero.
        if (id == "CORE_UNG_912")
            state.RecordNativeGeneration(source);
        state.Coverage.Add("audited-minion-summon:" + id);
        return true;
    }

    private static void UseWolfBlessing(Runtime state, Entity source, PlannedAction action)
    {
        if (!state.RequireAuditedBuild()) return;
        if (action.TargetEntityId is not null) { state.Invalid("巨狼的祝福随机选择手牌，不能指定目标。"); return; }
        var level = state.WolfLevel(source);
        if (level is null) { state.Unsupported("UNKNOWN_WOLF_PARAMETERS", "巨狼祝福的当前攻击力和减费参数未观察到一致可靠值，不能把印刷初值当当前强度。"); return; }
        var preview = state.PreviewWolfBlessing(source, level.Value);
        if (!preview.ResolvesEffects || !preview.IsCandidateSetComplete)
        { state.Unsupported("UNKNOWN_WOLF_CANDIDATES", "手牌身份、当前类型或技能参数不足以确认完整随机候选集合，详情见直接分支预览。"); return; }
        if (preview.Branches.Count == 0) { state.Unsupported("NO_WOLF_CANDIDATE", "没有可确认的野兽手牌；空放该英雄技能的合法性尚未审计。"); return; }
        if (preview.Branches.Count == 1 && (preview.Branches[0].ResultingAttack is null || preview.Branches[0].ResultingCost is null))
        { state.Unsupported("UNKNOWN_WOLF_TARGET_STATS", "唯一候选的当前攻击力或费用未知，不能用印刷值代替手牌增益后的属性。"); return; }
        if (!state.Pay(source)) return;
        source.Flags["RULES_USED"] = 1; source.Flags["EXHAUSTED"] = 1;
        state.RecordWolfBlessing(preview);
        if (preview.Branches.Count > 1) return;
        var branch = preview.Branches[0]; var beast = state.Entities[branch.TargetEntityId];
        beast.Attack = branch.ResultingAttack; beast.Cost = branch.ResultingCost;
        state.Coverage.Add("EDR_850p-single-observed-beast-buff-and-discount");
    }

    private sealed partial class Runtime
    {
        private readonly List<HeroPowerChange> _heroPowerChanges = [];

        public void ResolveMagmaHoundAfterAttack(Entity source, Entity target)
        {
            if (source.CardId != "FIR_953" || target.Type != "MINION") return;
            var preview = PreviewMagmaSplit(source, target);
            if (_previewMagmaOnly || !preview.SingleHeroStateAudited)
            {
                RecordMagmaSplit(source, target, preview);
                if (_snapshot.ClientBuild != AuditedBuild)
                    Conditional("当前版本的熔岩猎犬攻击后触发尚未审计，未展开分配伤害。", "UNAUDITED_MAGMA_HOUND_BUILD");
                return;
            }
            var enemies = Entities.Values.Where(e => e.Alive && e.Zone == "PLAY" && e.Controller != source.Controller &&
                e.Type is "HERO" or "MINION" && !Has(e, "DORMANT") && !Has(e, "UNTOUCHABLE")).ToArray();
            if (enemies.Length != 1 || enemies[0].Id != EnemyHero?.Id)
            { RecordMagmaSplit(source, target, preview); return; }
            var hero = enemies[0];
            if (source.Attack is null or < 0 || hero.Health is null || hero.Armor < 0)
            { Conditional("熔岩猎犬攻击力或敌方英雄当前生命/护甲未知，未展开分配伤害。", "UNKNOWN_MAGMA_HOUND_DAMAGE"); return; }

            var shots = source.Attack.Value;
            // Split damage is a sequence of one-damage packets. A shield blocks only one packet;
            // the sequence stops when the enemy hero dies, including for Lifesteal accounting.
            if (shots > 0 && !Has(hero, "IMMUNE") && Has(hero, "DIVINE_SHIELD"))
            { Damage(source, hero, 1, false); shots--; }
            if (shots > 0 && !Has(hero, "IMMUNE") && hero.Health > 0)
            {
                var perShot = ModifyBeastDamage(source, 1);
                var shotsToLethal = ((long)hero.Health.Value + hero.Armor + perShot - 1) / perShot;
                shots = (int)Math.Min(shots, shotsToLethal);
                var dealt = Damage(source, hero, shots, false);
                HealLifesteal(source, dealt);
            }
            ResolveDeaths();
            Coverage.Add("FIR_953-single-enemy-split-damage");
        }

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
            if (OpponentDefeatedByPayment) return true;
            if (source.MaximumHealth.HasValue) _summonedWithObservedHealth.Add(source.Id);
            source.Zone = "PLAY"; source.Attack ??= profile.Attack; source.MaximumHealth ??= profile.Health; source.Health = source.MaximumHealth;
            source.Type = "MINION"; source.Attacks = 0;
            // Entry resets readiness/counts, not observed restrictions used by Attack. Clearing
            // these would make a manual or searched play->attack line bypass its original state.
            source.Flags = source.Flags.Where(flag => StaticKeywords.Contains(flag.Key) ||
                flag.Key is "SILENCED" or "FROZEN" or "CANT_ATTACK_HEROES" or "CANNOT_ATTACK_HEROES" or
                    "CANT_ATTACK_MINIONS" or "DORMANT" or "UNTOUCHABLE").ToDictionary(StringComparer.Ordinal);
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
            if (_snapshot.Entities.TryGetValue(power.Id, out var observed))
                foreach (var pair in new[] { ("TAG_SCRIPT_DATA_NUM_1", "2"), ("TAG_SCRIPT_DATA_NUM_2", "3") })
                {
                    var named = observed.Tags.GetValueOrDefault(pair.Item1); var numeric = observed.Tags.GetValueOrDefault(pair.Item2);
                    if (named is not null && !int.TryParse(named, out _) || numeric is not null && !int.TryParse(numeric, out _) ||
                        named is not null && numeric is not null && int.Parse(named) != int.Parse(numeric)) return null;
                }
            var attack = power.Int("TAG_SCRIPT_DATA_NUM_1") ?? power.Int("2");
            var discount = power.Int("TAG_SCRIPT_DATA_NUM_2") ?? power.Int("3");
            // Both text numbers increase together. Unequal or missing observations are not a known level.
            return attack is >= 1 && discount == attack &&
                (!power.Flags.ContainsKey("2") || power.Int("2") == attack) &&
                (!power.Flags.ContainsKey("3") || power.Int("3") == discount) ? attack : null;
        }
        public void ImbueHunter(int? controller, string reason)
        {
            AdvanceImbueEvidence(controller);
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
