using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private static bool TryEquipWeapon(Runtime state, PlannedAction action, Entity source, Profile profile)
    {
        if (profile.Type != "WEAPON") return false;
        if (!state.RequireAuditedBuild()) return true;
        if (action.TargetEntityId is not null) { state.Invalid("装备武器不指定额外目标。"); return true; }
        state.EquipWeapon(source, profile);
        return true;
    }

    private sealed partial class Runtime
    {
        public bool WeaponResolutionBoundaryReached { get; private set; }
        private readonly HashSet<int> _equippedWeaponIds = [];
        private readonly HashSet<int> _removedWeaponIds = [];
        private readonly Dictionary<int, int> _weaponlessAttack = [];

        public void MarkWeaponBoundary()
        {
            WeaponResolutionBoundaryReached = true;
            Conditional("武器损毁或替换存在未展开的亡语、监听或攻击次数来源；投影停在该边界，等待实际结算。", "WEAPON_RESOLUTION_PENDING");
        }

        private IEnumerable<Entity> PossibleEquippedWeapons() => Entities.Values.Where(entity => !_removedWeaponIds.Contains(entity.Id) &&
            (entity.Zone == "PLAY" || !_equippedWeaponIds.Contains(entity.Id) && _snapshot.Entities.TryGetValue(entity.Id, out var zoneRaw) &&
                new[] { "ZONE", "49" }.Any(key => zoneRaw.Tags.GetValueOrDefault(key) is "PLAY" or "1")) &&
            (entity.Type == "WEAPON" || Profile(entity)?.Type == "WEAPON" || _snapshot.Entities.TryGetValue(entity.Id, out var raw) &&
                new[] { "CARDTYPE", "202" }.Any(key => raw.Tags.GetValueOrDefault(key) is "WEAPON" or "7")));

        public bool ConfirmAttackWeapon(Entity hero, out Entity? weapon)
        {
            weapon = null;
            var possible = PossibleEquippedWeapons().ToArray();
            foreach (var candidate in possible)
                if (!_snapshot.Entities.TryGetValue(candidate.Id, out var raw) ||
                    !RawNumber(raw, "CONTROLLER", "50", true, out var owner) || owner is not (1 or 2) || candidate.Controller != owner ||
                    !ObservedCardType(candidate, "WEAPON") || !_equippedWeaponIds.Contains(candidate.Id) && !ObservedPlayIdentity(candidate))
                { Unsupported("AMBIGUOUS_WEAPON_IDENTITY", "场上武器候选的归属、区域或类型缺失/冲突，不能据此认定英雄没有武器。"); return false; }
            var candidates = possible.Where(entity => entity.Controller == hero.Controller).Take(2).ToArray();
            if (candidates.Length > 1) { Unsupported("AMBIGUOUS_WEAPON", "同一控制者有多个场上武器候选，不能取第一把。"); return false; }
            if (candidates.Length == 0) return true;
            weapon = candidates[0];
            if (!ConfirmWeapon(weapon) || !ObservedCharacter(hero)) return false;
            if (!SupportedWeaponAttackProfile(weapon))
            { Unsupported("UNSUPPORTED_WEAPON_EFFECT", "武器的持续或攻击触发文字未覆盖，不能只按印刷攻击或耐久验证战斗。"); return false; }
            if (!_weaponlessAttack.ContainsKey(hero.Id))
            {
                if (hero.Attack == 0 && weapon.Attack > 0)
                { Unsupported("UNKNOWN_WEAPON_ATTACK_CONTRIBUTION", "英雄攻击力已被压至零，无法反推出武器以外的攻击贡献。"); return false; }
                _weaponlessAttack[hero.Id] = checked(hero.Attack!.Value - weapon.Attack!.Value);
            }
            return true;
        }

        private bool ObservedCharacter(Entity entity)
        {
            if (!_snapshot.Entities.TryGetValue(entity.Id, out var raw) ||
                !RawNumber(raw, "HEALTH", "45", true, out _) || !RawNumber(raw, "DAMAGE", "44", true, out _) ||
                !RawNumber(raw, "ATK", "47", true, out _) || entity.Type == "HERO" && !RawNumber(raw, "ARMOR", "292", true, out _) ||
                !RawCombatKeywords(raw) || !RawNumber(raw, "NUM_ATTACKS_THIS_TURN", "297", false, out _) ||
                !ObservedCardType(entity, entity.Type) || !ObservedPlayIdentity(entity))
            { Unsupported("UNKNOWN_WEAPON_HERO_STATE", "武器战斗角色的当前攻击、生命、伤害、护甲或身份缺失/冲突，不能用默认零验证。"); return false; }
            return true;
        }

        public bool ConfirmWeaponTarget(Entity target) => ObservedCharacter(target);

        private bool ConfirmWeapon(Entity weapon)
        {
            if (weapon.Type != "WEAPON" || weapon.Controller is not (1 or 2) ||
                !_snapshot.Entities.TryGetValue(weapon.Id, out var raw) || !ObservedCardType(weapon, "WEAPON") ||
                !_equippedWeaponIds.Contains(weapon.Id) && !ObservedPlayIdentity(weapon) ||
                !RawNumber(raw, "ATK", "47", true, out _) || weapon.Attack is null or < 0 ||
                weapon.WeaponDurability is not { IsKnown: true, Mode: WeaponDurabilityMode.ModernHealthDamage } || weapon.Health is null or <= 0)
            { Unsupported("UNKNOWN_WEAPON", "当前武器身份、攻击力或现代剩余耐久缺失、冲突或已耗尽，未执行攻击或替换。"); return false; }
            return ConfirmWeaponKeywords(weapon, raw);
        }

        private bool ConfirmWeaponKeywords(Entity weapon, EntitySnapshot raw)
        {
            foreach (var pair in new[] { ("WINDFURY", "189"), ("POISONOUS", "363"), ("LIFESTEAL", "685"), ("IMMUNE_WHILE_ATTACKING", "373"), ("SILENCED", "188"), ("IMMUNE", "240") })
                if (!RawNumber(raw, pair.Item1, pair.Item2, false, out var flag) || flag is not (null or 0 or 1))
                { Unsupported("CONFLICTING_WEAPON_KEYWORD", "武器关键词存在非法值或命名/数字别名反证，未执行攻击。"); return false; }
            foreach (var tag in new[] { "CANT_BE_DESTROYED", "CANT_DAMAGE", "CANT_LOSE_DURABILITY", "DURABILITY_LOSS_DISABLED" })
                if (raw.Tags.TryGetValue(tag, out var value) && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flag) || flag is not (0 or 1)))
                { Unsupported("UNKNOWN_WEAPON_DURABILITY_MODIFIER", "武器耐久修正的显式观测非法，不能按未启用处理。"); return false; }
            if (Has(weapon, "IMMUNE") || new[] { "CANT_BE_DESTROYED", "CANT_DAMAGE", "CANT_LOSE_DURABILITY", "DURABILITY_LOSS_DISABLED" }.Any(tag => weapon.Int(tag) is > 0))
            { Unsupported("UNSUPPORTED_WEAPON_DURABILITY_MODIFIER", "武器存在未审计的耐久损耗或损毁修正。"); return false; }
            return true;
        }

        public void SpendWeaponDurability(Entity hero, Entity weapon)
        {
            var maximum = weapon.MaximumHealth!.Value; var remaining = weapon.Health!.Value - 1;
            SetWeaponResource(weapon, maximum, maximum - remaining);
            Coverage.Add("weapon-modern-health-damage-spend-one");
            if (remaining > 0) return;
            hero.Attack = Math.Max(0, _weaponlessAttack.GetValueOrDefault(hero.Id, hero.Attack!.Value - weapon.Attack!.Value));
            RemoveWeapon(hero, weapon);
            if (!WeaponResolutionBoundaryReached)
            {
                var limit = (CombatHas(hero, "MEGA_WINDFURY") ? 4 : CombatHas(hero, "WINDFURY") ? 2 : 1) + (hero.Int("EXTRA_ATTACKS_THIS_TURN") ?? 0);
                if (hero.Attacks >= limit) hero.Flags["EXHAUSTED"] = 1;
            }
            Coverage.Add("weapon-destruction-removes-weapon-attack-not-independent-hero-attack");
        }

        private void RemoveWeapon(Entity hero, Entity weapon)
        {
            var ambiguousAttackCount = WeaponHas(weapon, "WINDFURY") &&
                (Has(hero, "WINDFURY") || hero.Flags.ContainsKey("MAX_ATTACKS_THIS_TURN"));
            Remove(weapon);
            if (ambiguousAttackCount || _unsupported.Overlaps(["UNKNOWN_BOARD_CARD", "UNKNOWN_TRIGGER", "UNKNOWN_AURA",
                "UNMAPPED_BOARD_TEXT", "UNMAPPED_ATTACHED_ENCHANTMENT", "CUSTOM_ASSEMBLED_HAND"])) MarkWeaponBoundary();
        }

        private void SetWeaponResource(Entity weapon, int maximum, int damage)
        {
            weapon.MaximumHealth = maximum; weapon.Health = Math.Max(0, maximum - damage);
            weapon.Flags["HEALTH"] = maximum; weapon.Flags["DAMAGE"] = damage;
            if (weapon.Flags.ContainsKey("45")) weapon.Flags["45"] = maximum;
            if (weapon.Flags.ContainsKey("44")) weapon.Flags["44"] = damage;
            weapon.WeaponDurability = new(_snapshot.ClientBuild, weapon.Id, WeaponDurabilityMode.ModernHealthDamage,
                maximum, damage, weapon.WeaponDurability?.LegacyDurability, weapon.Health, weapon.WeaponDurability?.Unknowns ?? []);
        }

        public void EquipWeapon(Entity source, Profile profile)
        {
            if (!SupportedWeaponAttackProfile(source) || profile.Mechanics.Overlaps(["BATTLECRY", "OVERLOAD"]))
            { Unsupported("UNSUPPORTED_WEAPON_EQUIP_EFFECT", "该武器的战吼、过载或装备/持续文字尚未覆盖，未支付或装备。"); return; }
            if (!_snapshot.Entities.TryGetValue(source.Id, out var raw) || !RawNumber(raw, "HEALTH", "45", true, out var maximum) || maximum is null or <= 0 ||
                !RawNumber(raw, "DAMAGE", "44", false, out var handDamage) || handDamage is > 0 ||
                !RawNumber(raw, "COST", "48", true, out var cost) || source.Cost != cost ||
                !RawNumber(raw, "ATK", "47", true, out var attack) || attack is null || source.Attack != attack || !ObservedCardType(source, "WEAPON"))
            { Unsupported("UNKNOWN_HAND_WEAPON_STATE", "手牌武器的当前攻击、最大耐久或损伤证据不足，未使用印刷值或修复反证。"); return; }
            if (!ConfirmWeaponKeywords(source, raw)) return;
            var hero = Hero(source.Controller);
            if (hero is null || !ObservedCharacter(hero) || !ConfirmAttackWeapon(hero, out var previous)) return;
            var offset = _weaponlessAttack.GetValueOrDefault(hero.Id, hero.Attack!.Value);
            var nextAttack = Math.Max(0L, (long)offset + attack.Value);
            if (nextAttack > int.MaxValue) { Unsupported("EVALUATION_LIMIT", "装备后的攻击数值超出计算范围。"); return; }
            if (!Pay(source) || OpponentDefeatedByPayment) return;
            if (previous is not null)
            {
                hero.Attack = Math.Max(0, offset); RemoveWeapon(hero, previous);
                if (WeaponResolutionBoundaryReached) return;
            }
            _weaponlessAttack[hero.Id] = offset;
            source.Zone = "PLAY"; source.Alive = true; source.Type = "WEAPON";
            SetWeaponResource(source, maximum.Value, 0); _equippedWeaponIds.Add(source.Id);
            hero.Attack = (int)nextAttack;
            if (source.Flags.ContainsKey("49")) source.Flags["49"] = 1;
            source.Flags["ZONE"] = 1;
            Coverage.Add("simple-weapon-equip-observed-current-stats-fresh-damage-zero");
        }

        public bool WeaponHas(Entity weapon, string keyword) => Has(weapon, keyword) ||
            keyword == "IMMUNE_WHILE_ATTACKING" && !Silenced(weapon) && NativeAttackingImmunityWeapon(weapon);

        public bool SupportedWeaponAttackProfile(Entity weapon)
        {
            if (NativeCardDefinitions.IsRegistered(weapon.CardId) &&
                !NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, weapon.CardId, _cards.GetValueOrDefault(weapon.CardId!))) return false;
            if (weapon.CardId is not { } id || !_cards.TryGetValue(id, out var card) || card.Type != "WEAPON") return false;
            var profile = Profile(weapon); if (profile is null) return false;
            try
            {
                using var doc = JsonDocument.Parse(card.RawJson); var raw = doc.RootElement;
                if (raw.GetProperty("id").GetString() != id || raw.GetProperty("type").GetString() != "WEAPON" ||
                    raw.TryGetProperty("dbfId", out var dbf) && dbf.GetInt32() != card.DbfId ||
                    (raw.TryGetProperty("text", out var text) ? text.GetString() : "") != card.Text) return false;
                if (SimpleProfile(profile)) return true;
                if (NativeAttackingImmunityWeapon(weapon)) return true;
                if (_snapshot.ClientBuild == AuditedBuild && id is "EX1_567" or "CORE_EX1_567" &&
                    card.DbfId == (id == "EX1_567" ? 352 : 69630) && card.Text == "<b>风怒，过载：</b>（2）" &&
                    profile.Mechanics.SetEquals(["OVERLOAD", "WINDFURY"])) return true;
                return profile.Mechanics.Contains("DEATHRATTLE") && profile.Mechanics.All(value => value == "DEATHRATTLE" || StaticKeywords.Contains(value)) &&
                    card.Text.StartsWith("<b>亡语：</b>", StringComparison.Ordinal);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
        }

        private bool NativeAttackingImmunityWeapon(Entity weapon) => _snapshot.ClientBuild == AuditedBuild &&
            weapon.CardId is { } id && _cards.TryGetValue(id, out var card) && card.Type == "WEAPON" &&
            NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, id, card) &&
            card.DbfId == (id switch { "DS1_188" => 311, "CORE_LOOT_222" => 86543, "LOOT_222" => 45537, _ => -1 }) &&
            card.Text == "你的英雄在攻击时<b>免疫</b>。";

        private static bool RawNumber(EntitySnapshot entity, string name, string alias, bool required, out int? number)
        {
            number = null; var values = new[] { name, alias }.Where(entity.Tags.ContainsKey).Select(key => entity.Tags[key]).ToArray();
            if (values.Length == 0) return !required;
            foreach (var value in values)
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || number.HasValue && number.Value != parsed) return false;
                number = parsed;
            }
            return true;
        }

        private static bool RawCombatKeywords(EntitySnapshot entity)
        {
            foreach (var pair in new[] { ("WINDFURY", "189"), ("DIVINE_SHIELD", "194"), ("IMMUNE", "240"),
                ("IMMUNE_WHILE_ATTACKING", "373"), ("LIFESTEAL", "685"), ("POISONOUS", "363"), ("CANT_BE_HEALED", "239"),
                ("SILENCED", "188"), ("FROZEN", "260"), ("CANT_ATTACK", "227"), ("EXHAUSTED", "43"),
                ("TAUNT", "190"), ("STEALTH", "191"), ("DORMANT", "1518"), ("UNTOUCHABLE", "448") })
                if (!RawNumber(entity, pair.Item1, pair.Item2, false, out var value) || value is not (null or 0 or 1)) return false;
            return true;
        }
    }
}
