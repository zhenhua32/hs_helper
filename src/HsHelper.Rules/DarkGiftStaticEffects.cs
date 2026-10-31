using System.Globalization;
using System.Security.Cryptography;
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
        private sealed record StaticGift(int DbfId, int ButtonDbfId, string Text, string Keyword, string KeywordAlias, string RawHash, string Coverage);
        private static readonly IReadOnlyDictionary<string, StaticGift> StaticGifts = new Dictionary<string, StaticGift>(StringComparer.Ordinal)
        {
            ["EDR_100t6e"] = new(113809, 113802, "<b>冲锋</b>", "CHARGE", "197",
                "D2B2DB02AC482B8A1DB7A3EC3705D4011360B343553657F0269197D095978328", "dark-gift-observed-charge:EDR_100t6e"),
            ["EDR_100te"] = new(113803, 113796, "+3攻击力和<b>吸血</b>。", "LIFESTEAL", "685",
                "A02523FE54B70350112A57C8158544704F023134B63DBBD71D325958E981B9C7", "dark-gift-observed-lifesteal:EDR_100te")
        };
        // Existing ID-selected native minion handlers only. Pinning their source data here
        // does not add any new body effect; other targets still need the simple-profile path.
        private static readonly IReadOnlyDictionary<string, string> GiftNativeTargets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EDR_849"] = "6A4B51F2B5E9D180ED742E19AAA769BBC3C68DB2686D18E3414D9A4C76DF48B3",
            ["CORE_UNG_912"] = "6F9E73E03FC966673390D527CD369C9A018BB0593050FDAEEF543EFE8A3E5C4D",
            ["EDR_852"] = "C33ECC8684CCA8C3DFE62C6792CDEC54CB01B2EB5D70FA2773D0C509079A7943",
            ["EDR_800"] = "EFD951C8FC68AD2A1CFDA6672DA3C33BDCFF2BBF6B5376F6FE45E124BEECADDF",
            ["EDR_227"] = "AF62F68FF8753F225D1E2370134BE205A997628B876CE8622ECEC26283905243",
            ["EDR_226"] = "BE4CC19E8E010C5E02FDFB3F595000103BCE186DC3FE62BEB1F3EFF611224458",
            ["FIR_921"] = "670EC9479EFEEBF2A3B24C14715E6576523AF8933775593511BFE9828D5C1487",
            ["EDR_480"] = "317E05FBEFDB1ADC4D227C96DD20FF0A3B9BC3BEC3E3EFB748568BDA80D409B3",
            ["DINO_421"] = "05D24C37EFCFC6FA7D62AAC50868BE4F2C8C08541250A31E72CB83763579CB0C",
            ["FIR_953"] = "309266F75C03168FAE80A003A44454229D457D245345BD27096F1D0C4B1776B7",
            ["EDR_489"] = "2508997755BE0F7C992A2F3FB037B7F576D43F31FF1B62CA1B02EDBBAC839E1C",
            ["BE_044"] = "A4FDAD092108D4BB0A637EB8E087DA5CB207A908FEA1BDC38A2854490B710D98",
            ["JAIL_831"] = "8C5D426A4AC272DF3EF01CC67BBD08B70B87C533324DFC9453C0158306AE905A",
            ["EDR_888"] = "E823E5C7C0EB6E75E45F223A5F8FA4BB74229AEFA2B411A6C903840AE7E4AE30",
            ["CORE_EDR_004_2026"] = "6C120E2ADDE795669B18F10A8C0D2F65DCA751CF864B6F055DF9088019D6B66F",
            ["CORE_EDR_004"] = "0F8FF8A1D86AC6C41DB4DB6E2D895AECFF49A9E24528936D5A671D29D708A5E8",
            ["CORE_CS2_189"] = "8B88F3B0530DAA425A623581BF7F9079CC7A282CAD280B766BA656823E9A365E",
            ["CS2_189"] = "E1F5397D021DC46912200F9B862B11C7DBA9163325347D824383169596C6B0B7",
            ["CORE_TTN_866"] = "E7DC2F40977573AB05F1D17971515EEB3D1BD1E4DB7FBD026FC865CC3BF63E35"
        };

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
            if (!GiftOptionalInt(original, "TAG_SCRIPT_DATA_NUM_6", "2921", gift.ButtonDbfId) ||
                !GiftOptionalInt(original, "IS_NIGHTMARE_BONUS", "3567", 1) || !StaticGiftMetadata(enchantment.CardId!, gift) ||
                enchantment.Flags.Any(flag => flag.Value != 0 &&
                    (flag.Key is "BATTLECRY" or "DEATHRATTLE" or "TRIGGER_VISUAL" or "AURA" or "REBORN" or "SECRET" ||
                     StaticKeywords.Contains(flag.Key) && flag.Key != gift.Keyword)) ||
                !Entities.TryGetValue(targetId.Value, out var target) || !target.Alive || target.Type != "MINION" ||
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
                !GiftOptionalArmor(observed) || AssembledCardText.RequiresCustomText(observed.Tags) ||
                Silenced(target) || Has(target, "DORMANT") ||
                !GiftTargetMetadata(target) ||
                !GiftInt(observed, "ATK", "47", out var attack) || attack is null or < 0 ||
                !GiftInt(observed, "HEALTH", "45", out var health) || health is null or <= 0 ||
                !GiftInt(observed, "DAMAGE", "44", out var damage) || damage is null or < 0 || damage > health ||
                observed.Zone == "HAND" && (damage != 0 || !GiftInt(observed, "COST", "48", out var cost) || cost is null or < 0) ||
                target.Attack is null or < 0 || target.MaximumHealth is null or <= 0 || target.Health is null or <= 0 ||
                target.Int(gift.Keyword) != 1 || !GiftInt(observed, gift.Keyword, gift.KeywordAlias, out var keyword) || keyword != 1)
                return false;
            // A lone numeric flag is not converted into a grant. The generic combat path must
            // already have an observed named flag; aliases only corroborate or contradict it.
            if (!observed.Tags.ContainsKey(gift.Keyword)) return false;
            var competing = Entities.Values.Count(other => other.Alive && other.Zone != "GRAVEYARD" && IsStaticDarkGiftCandidate(other) &&
                _snapshot.Entities.TryGetValue(other.Id, out var raw) && GiftInt(raw, "ATTACHED", "40", out var attached) && attached == target.Id);
            if (competing != 1) return false;
            if (gift.Keyword == "LIFESTEAL")
            {
                var hero = Hero(target.Controller);
                if (hero is null || !_snapshot.Entities.TryGetValue(hero.Id, out var rawHero) ||
                    !GiftInt(rawHero, "HEALTH", "45", out var max) || max is null or <= 0 ||
                    !GiftInt(rawHero, "DAMAGE", "44", out var wounds) || wounds is null or < 0 || wounds > max) return false;
            }
            coverage = gift.Coverage;
            return true;
        }

        private bool IsObservedStaticDarkGiftNumericTag(Entity entity, string tag)
        {
            if (IsStaticDarkGiftCandidate(entity))
                return tag is "40" or "49" or "50" or "53" or "188" or "202" or "1518" or "2921" or "3567" && TryObservedStaticDarkGift(entity, out _);
            if (tag is not ("44" or "45" or "47" or "48" or "49" or "50" or "53" or "188" or "197" or "202" or "260" or "292" or "685" or "1518")) return false;
            return Entities.Values.Any(enchantment => IsStaticDarkGiftCandidate(enchantment) &&
                _snapshot.Entities.TryGetValue(enchantment.Id, out var raw) && GiftInt(raw, "ATTACHED", "40", out var attached) &&
                attached == entity.Id && TryObservedStaticDarkGift(enchantment, out _));
        }

        private bool StaticGiftMetadata(string id, StaticGift gift)
        {
            if (!_cards.TryGetValue(id, out var card) || card.Id != id || card.DbfId != gift.DbfId || card.Type != "ENCHANTMENT" ||
                card.Text != gift.Text || card.RawJson is null || card.RawJson.Length > 65536) return false;
            try
            {
                using var document = JsonDocument.Parse(card.RawJson);
                var raw = document.RootElement;
                if (!GiftRecordMatchesRaw(card, raw) || raw.GetProperty("id").GetString() != id ||
                    raw.GetProperty("dbfId").GetInt32() != gift.DbfId || raw.GetProperty("type").GetString() != "ENCHANTMENT" ||
                    raw.GetProperty("text").GetString() != gift.Text || raw.GetProperty("name").GetString() != card.Name ||
                    raw.GetProperty("cardClass").GetString() != card.CardClass || raw.GetProperty("set").GetString() != card.Set) return false;
                return GiftMetadataHash(raw) == gift.RawHash;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { return false; }
        }

        private bool GiftTargetMetadata(Entity target)
        {
            if (target.CardId is null || !_cards.TryGetValue(target.CardId, out var card) || card.Type != "MINION" ||
                card.Id != target.CardId || card.RawJson is null || card.RawJson.Length > 65536) return false;
            try
            {
                using var document = JsonDocument.Parse(card.RawJson);
                if (!GiftRecordMatchesRaw(card, document.RootElement)) return false;
                if (GiftNativeTargets.TryGetValue(card.Id, out var hash)) return GiftMetadataHash(document.RootElement) == hash;
                return Profile(target) is { } profile && SimpleProfile(profile);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { return false; }
        }

        private static bool GiftRecordMatchesRaw(CardRecord card, JsonElement raw)
        {
            if (raw.ValueKind != JsonValueKind.Object || card.DbfId is null or <= 0 ||
                raw.GetProperty("id").GetString() != card.Id || raw.GetProperty("dbfId").GetInt32() != card.DbfId ||
                raw.GetProperty("type").GetString() != card.Type) return false;
            string Text(string key) => raw.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
            var name = Text("name");
            var cost = raw.TryGetProperty("cost", out var costValue) ? costValue.GetInt32() : (int?)null;
            var collectible = raw.TryGetProperty("collectible", out var collect) && collect.GetBoolean();
            return (name.Length == 0 ? card.Id : name) == card.Name && Text("text") == card.Text && Text("cardClass") == card.CardClass &&
                Text("set") == card.Set && cost == card.Cost && collectible == card.Collectible &&
                SameList("classes", card.Classes) && SameList("entourage", card.Entourage);
            bool SameList(string key, IReadOnlyList<string>? values) => values is not null &&
                (raw.TryGetProperty(key, out var array) ? array.EnumerateArray().Select(value => value.GetString()!).SequenceEqual(values, StringComparer.Ordinal) : values.Count == 0);
        }
        private static string GiftMetadataHash(JsonElement raw)
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) WriteCanonicalGift(raw, writer);
            return Convert.ToHexString(SHA256.HashData(output.ToArray()));
        }

        private static void WriteCanonicalGift(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonicalGift(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonicalGift(item, writer); writer.WriteEndArray(); }
            else value.WriteTo(writer);
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
