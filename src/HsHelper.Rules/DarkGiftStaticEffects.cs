using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        // Exact public build-255790 records, not a name/prefix or generic Nightmare-bonus allowlist.
        // cards.json SHA256 7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299;
        // CardDefs.base.xml SHA256 EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8.
        // CardDefs: CARDTYPE=6, 2921/TAG_SCRIPT_DATA_NUM_6 points at the gift button DBF,
        // 3567/IS_NIGHTMARE_BONUS=1. Neither field grants a current target keyword by itself.
        private sealed record GiftKeyword(string Name, string Alias, bool Consumable = false);
        private sealed record StaticGift(int? ButtonDbfId, bool SupportsVisual, string Coverage, GiftKeyword[] Keywords);
        private static readonly IReadOnlyDictionary<string, StaticGift> StaticGifts = new Dictionary<string, StaticGift>(StringComparer.Ordinal)
        {
            ["EDR_100t6e"] = new(113802, true, "dark-gift-observed-charge:EDR_100t6e", [new("CHARGE", "197")]),
            ["EDR_100te"] = new(113796, true, "dark-gift-observed-lifesteal:EDR_100te", [new("LIFESTEAL", "685")]),
            ["EDR_100t1e"] = new(113797, true, "dark-gift-observed-elusive:EDR_100t1e", [new("ELUSIVE", "1211")]),
            ["EDR_100t2e"] = new(113798, true, "dark-gift-observed-short-claws:EDR_100t2e", []),
            ["EDR_100t3e"] = new(113799, true, "dark-gift-observed-taunt:EDR_100t3e", [new("TAUNT", "190")]),
            ["EDR_100t8e1"] = new(113884, true, "dark-gift-observed-sweet-dreams:EDR_100t8e1", []),
            ["EDR_100t13e"] = new(120225, true, "dark-gift-observed-shield-windfury:EDR_100t13e", [new("DIVINE_SHIELD", "194", true), new("WINDFURY", "189")]),
            // These exact annotations describe settled values. They do not establish how the
            // shuffle/copy was produced, and do not schedule another shuffle or create an entity.
            ["EDR_100t8e"] = new(null, true, "dark-gift-observed-shuffled-stats:EDR_100t8e", []),
            ["EDR_100t5e2"] = new(null, false, "dark-gift-observed-copy-stats:EDR_100t5e2", []),
            ["EDR_100t5e5"] = new(null, false, "dark-gift-observed-copy-stats:EDR_100t5e5", [])
        };
        private static readonly (string Name, string Alias)[] GiftBooleanTags =
        [
            ("BATTLECRY", "218"), ("DEATHRATTLE", "217"), ("TRIGGER_VISUAL", "32"), ("AURA", "362"), ("REBORN", "1085"), ("SECRET", "219"),
            ("TAUNT", "190"), ("DIVINE_SHIELD", "194"), ("CHARGE", "197"), ("RUSH", "791"), ("WINDFURY", "189"), ("MEGA_WINDFURY", "1207"),
            ("LIFESTEAL", "685"), ("POISONOUS", "363"), ("STEALTH", "191"), ("ELUSIVE", "1211"), ("CANT_ATTACK", "227"), ("IMMUNE", "240"),
            ("SILENCED", "188"), ("DORMANT", "1518"), ("FROZEN", "260"), ("UNTOUCHABLE", "448"),
            ("CANT_BE_TARGETED_BY_SPELLS", "311"), ("CANT_BE_TARGETED_BY_HERO_POWERS", "332"),
            ("CANT_ATTACK_HEROES", ""), ("CANNOT_ATTACK_HEROES", ""), ("CANT_ATTACK_MINIONS", "")
        ];
        private readonly Dictionary<int, int> _auditedStaticGiftTargets = [];
        private readonly HashSet<int> _inactiveStaticGifts = [];

        private static bool IsStaticDarkGiftCandidate(Entity entity) => entity.CardId is not null && StaticGifts.ContainsKey(entity.CardId);

        private bool TryObservedStaticDarkGift(Entity enchantment, out string coverage)
        {
            coverage = "";
            if (_snapshot.ClientBuild != AuditedBuild || !IsStaticDarkGiftCandidate(enchantment) || !enchantment.Alive ||
                enchantment.Type != "ENCHANTMENT" || enchantment.Zone != "PLAY" || enchantment.Controller is not (1 or 2) ||
                !_snapshot.Entities.TryGetValue(enchantment.Id, out var original) || original is not { Kind: EntityKind.Card, Tags: not null } ||
                original.Id != enchantment.Id || original.CardId != enchantment.CardId || original.ControllerId != enchantment.Controller ||
                !GiftEnum(original, "CARDTYPE", "202", "ENCHANTMENT", "6") || !GiftEnum(original, "ZONE", "49", "PLAY", "1") ||
                !GiftInt(original, "CONTROLLER", "50", out var owner) || owner != enchantment.Controller ||
                !GiftInt(original, "ATTACHED", "40", out var targetId) || targetId is not > 0 ||
                !GiftOptionalInt(original, "ENTITY_ID", "53", enchantment.Id) || !GiftOptionalInt(original, "SILENCED", "188", 0) ||
                !GiftOptionalInt(original, "DORMANT", "1518", 0) || AssembledCardText.RequiresCustomText(original.Tags)) return false;
            var gift = StaticGifts[enchantment.CardId!];
            if (!GiftOptionalInt(original, "TAG_SCRIPT_DATA_NUM_6", "2921", gift.ButtonDbfId ?? 0) ||
                !GiftOptionalInt(original, "IS_NIGHTMARE_BONUS", "3567", gift.ButtonDbfId.HasValue ? 1 : 0) ||
                !StaticGiftMetadata(enchantment.CardId!, gift) || !GiftSourceFlags(original, gift)) return false;
            if (_inactiveStaticGifts.Contains(enchantment.Id) && _auditedStaticGiftTargets.GetValueOrDefault(enchantment.Id) == targetId &&
                Entities.TryGetValue(targetId.Value, out var removed) && !removed.Alive && removed.Zone == "GRAVEYARD")
            { coverage = gift.Coverage; return true; }
            if (!Entities.TryGetValue(targetId.Value, out var target) || !target.Alive || target.Type != "MINION" ||
                target.Controller != enchantment.Controller || target.Zone is not ("HAND" or "PLAY") ||
                target.Zone == "HAND" && target.Controller != Local ||
                !_snapshot.Entities.TryGetValue(target.Id, out var observed) || observed is not { Kind: EntityKind.Card, Tags: not null } ||
                observed.Id != target.Id || observed.CardId != target.CardId || observed.ControllerId != target.Controller ||
                observed.Zone is not ("HAND" or "PLAY") || observed.Zone == "HAND" && observed.ControllerId != Local ||
                !GiftEnum(observed, "CARDTYPE", "202", "MINION", "4") ||
                !GiftEnum(observed, "ZONE", "49", observed.Zone, observed.Zone == "HAND" ? "3" : "1") ||
                !GiftInt(observed, "CONTROLLER", "50", out var targetOwner) || targetOwner != target.Controller ||
                !GiftOptionalInt(observed, "ENTITY_ID", "53", target.Id) || !GiftOptionalInt(observed, "SILENCED", "188", 0) ||
                !GiftOptionalInt(observed, "DORMANT", "1518", 0) || !GiftOptionalInt(observed, "FROZEN", "260", 0) ||
                !GiftOptionalArmor(observed) || !GiftTargetFlags(observed) || AssembledCardText.RequiresCustomText(observed.Tags) ||
                Silenced(target) || Has(target, "DORMANT") ||
                !GiftTargetMetadata(target) ||
                !GiftInt(observed, "ATK", "47", out var attack) || attack is null or < 0 ||
                !GiftInt(observed, "HEALTH", "45", out var health) || health is null or <= 0 ||
                !GiftInt(observed, "DAMAGE", "44", out var damage) || damage is null or < 0 || damage > health ||
                observed.Zone == "HAND" && (damage != 0 || !GiftInt(observed, "COST", "48", out var cost) || cost is null or < 0) ||
                target.Attack is null or < 0 || target.MaximumHealth is null or <= 0 || target.Health is null or <= 0)
                return false;
            // A lone numeric flag is not converted into a grant. The generic combat path must
            // already have an observed named flag; aliases only corroborate or contradict it.
            foreach (var keyword in gift.Keywords)
            {
                if (!observed.Tags.ContainsKey(keyword.Name) || !GiftInt(observed, keyword.Name, keyword.Alias, out var initial) ||
                    initial is not (0 or 1) || target.Int(keyword.Name) is not { } current || current is not (0 or 1)) return false;
                if (keyword.Consumable ? current > initial : initial != 1 || current != 1) return false;
            }
            // Each exact source is audited separately against the same observed aggregate.
            // Multiple static sources do not reapply stats, multiply keywords, or establish order.
            if (gift.Keywords.Any(keyword => keyword.Name == "LIFESTEAL"))
            {
                var hero = Hero(target.Controller);
                if (hero is null || !_snapshot.Entities.TryGetValue(hero.Id, out var rawHero) ||
                    !GiftInt(rawHero, "HEALTH", "45", out var max) || max is null or <= 0 ||
                    !GiftInt(rawHero, "DAMAGE", "44", out var wounds) || wounds is null or < 0 || wounds > max) return false;
            }
            _auditedStaticGiftTargets[enchantment.Id] = target.Id;
            coverage = gift.Coverage;
            return true;
        }
        public void CompleteObservedStaticGiftsAfterDeath(Entity target)
        {
            foreach (var pair in _auditedStaticGiftTargets.Where(pair => pair.Value == target.Id))
                _inactiveStaticGifts.Add(pair.Key);
        }

        private bool IsObservedStaticDarkGiftNumericTag(Entity entity, string tag)
        {
            if (IsStaticDarkGiftCandidate(entity))
                return (tag is "40" or "49" or "50" or "53" or "188" or "202" or "330" or "331" or "1518" or "2921" or "3567" ||
                    GiftBooleanTags.Any(pair => pair.Alias == tag)) && TryObservedStaticDarkGift(entity, out _);
            if (tag is not ("44" or "45" or "47" or "48" or "49" or "50" or "53" or "188" or "189" or "190" or "194" or "197" or "202" or "260" or "292" or "685" or "1211" or "1518")) return false;
            var boolean = GiftBooleanTags.FirstOrDefault(pair => pair.Alias == tag);
            if (boolean.Name is not null && (!_snapshot.Entities.TryGetValue(entity.Id, out var rawTarget) || !rawTarget.Tags.ContainsKey(boolean.Name))) return false;
            return Entities.Values.Any(enchantment => IsStaticDarkGiftCandidate(enchantment) &&
                _snapshot.Entities.TryGetValue(enchantment.Id, out var raw) && GiftInt(raw, "ATTACHED", "40", out var attached) &&
                attached == entity.Id && TryObservedStaticDarkGift(enchantment, out _));
        }
        private static bool GiftSourceFlags(EntitySnapshot source, StaticGift gift)
        {
            foreach (var pair in GiftBooleanTags)
            {
                if (!source.Tags.ContainsKey(pair.Name) && !source.Tags.ContainsKey(pair.Alias)) continue;
                if (!GiftInt(source, pair.Name, pair.Alias, out var value) || value is not (0 or 1) ||
                    value == 1 && !gift.Keywords.Any(keyword => keyword.Name == pair.Name)) return false;
            }
            if (!GiftOptionalInt(source, "SPELLPOWER", "192", 0)) return false;
            var visual = gift.SupportsVisual;
            if (!GiftOptionalVisual(source, "ENCHANTMENT_BIRTH_VISUAL", "330", visual) ||
                !GiftOptionalVisual(source, "ENCHANTMENT_IDLE_VISUAL", "331", visual)) return false;
            foreach (var tag in source.Tags.Where(tag => tag.Key.StartsWith("TAG_SCRIPT_DATA_NUM_", StringComparison.Ordinal) && tag.Key != "TAG_SCRIPT_DATA_NUM_6"))
                if (!int.TryParse(tag.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 0) return false;
            return true;
        }
        private static bool GiftTargetFlags(EntitySnapshot target)
        {
            foreach (var pair in GiftBooleanTags)
                if ((target.Tags.ContainsKey(pair.Name) || target.Tags.ContainsKey(pair.Alias)) &&
                    (!GiftInt(target, pair.Name, pair.Alias, out var value) || value is not (0 or 1))) return false;
            return true;
        }

        private bool StaticGiftMetadata(string id, StaticGift gift) =>
            _cards.TryGetValue(id, out var card) && NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, id, card);

        private bool GiftTargetMetadata(Entity target)
        {
            if (target.CardId is null || !_cards.TryGetValue(target.CardId, out var card) || card.Type != "MINION" ||
                card.Id != target.CardId || card.RawJson is null || card.RawJson.Length > 65536) return false;
            try
            {
                using var document = JsonDocument.Parse(card.RawJson);
                if (!NativeCardDefinitions.RecordMatchesRaw(card, document.RootElement)) return false;
                if (NativeCardDefinitions.IsRegistered(card.Id)) return NativeCardDefinitions.IsImplementedBody(card.Id) && NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, card.Id, card);
                return Profile(target) is { } profile && SimpleProfile(profile);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { return false; }
        }

        private static bool GiftInt(EntitySnapshot entity, string name, string alias, out int? value)
        {
            value = null;
            if (entity.Tags is null) return false;
            foreach (var key in new[] { name, alias })
                if (entity.Tags.TryGetValue(key, out var raw))
                {
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || value.HasValue && number != value) return false;
                    value = number;
                }
            return value.HasValue;
        }
        private static bool GiftOptionalInt(EntitySnapshot entity, string name, string alias, int expected) =>
            !entity.Tags.ContainsKey(name) && !entity.Tags.ContainsKey(alias) || GiftInt(entity, name, alias, out var value) && value == expected;
        private static bool GiftOptionalVisual(EntitySnapshot entity, string name, string alias, bool supportsVisual) =>
            !entity.Tags.ContainsKey(name) && !entity.Tags.ContainsKey(alias) ||
            GiftInt(entity, name, alias, out var value) && value is 0 or 1 && (value == 0 || supportsVisual);
        private static bool GiftOptionalArmor(EntitySnapshot entity) =>
            !entity.Tags.ContainsKey("ARMOR") && !entity.Tags.ContainsKey("292") ||
            entity.Tags.ContainsKey("ARMOR") && GiftInt(entity, "ARMOR", "292", out var armor) && armor is >= 0;
        private static bool GiftEnum(EntitySnapshot entity, string name, string alias, string text, string number)
        {
            var found = false;
            foreach (var key in new[] { name, alias })
                if (entity.Tags.TryGetValue(key, out var value)) { found = true; if (value != text && value != number) return false; }
            return found;
        }
    }
}
