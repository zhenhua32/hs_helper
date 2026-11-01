using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public static partial class DrawResolutionRules
{
    public const string DeathrattleEnchantmentCardId = "EDR_100t4e";
    private static readonly (string Name, string Alias)[] DeathModifiers =
    [
        ("EXTRA_MINION_DEATHRATTLES_BASE", "371"), ("EXTRA_DEATHRATTLES_BASE", "882"),
        ("EXTRA_DEATHRATTLES_ADDITIONAL", "1131"), ("CANT_TRIGGER_DEATHRATTLE", "1831")
    ];
    private static readonly HashSet<string> PassiveKeywords = ["TAUNT", "DIVINE_SHIELD", "CHARGE", "RUSH", "WINDFURY", "MEGA_WINDFURY", "LIFESTEAL", "POISONOUS", "STEALTH", "ELUSIVE", "CANT_ATTACK", "IMMUNE", "SPELLPOWER"];

    internal static bool TryDeathrattleSource(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, out EntitySnapshot source,
        out EntitySnapshot? enchantment, out string reason)
    {
        source = null!; enchantment = null;
        reason = "需要当前我方场上随从和唯一有效的心中的恶魔附魔；未按同名、手牌赠福按钮或过去的死亡记录模拟。";
        if (snapshot.ClientBuild != NativeCardDefinitions.Build || snapshot.LocalPlayerId is not (1 or 2) ||
            !snapshot.Entities.TryGetValue(sourceEntityId, out source!) || source is not { Kind: EntityKind.Card, Tags: not null } ||
            source.Id != sourceEntityId || sourceEntityId <= 0 || source.ControllerId != snapshot.LocalPlayerId || source.Zone != "PLAY" ||
            !EnumEvidence(source, "ZONE", "49", "PLAY", "1") || !EnumEvidence(source, "CARDTYPE", "202", "MINION", "4") ||
            !IntEvidence(source, "CONTROLLER", "50", out var owner) || owner != snapshot.LocalPlayerId ||
            !Optional(source, "ENTITY_ID", "53", sourceEntityId) ||
            !Bool(source, "DEATHRATTLE", "217", false, out var dr) || !dr ||
            !Bool(source, "SILENCED", "188", false, out var silence) || silence ||
            !Bool(source, "DORMANT", "1518", false, out var dormant) || dormant ||
            !Bool(source, "REBORN", "1085", false, out var reborn) || reborn ||
            !IntEvidence(source, "HEALTH", "45", out var health) || health is null or <= 0 ||
            !IntEvidence(source, "DAMAGE", "44", out var wounds) || wounds is null or < 0 || wounds >= health ||
            AssembledCardText.RequiresCustomText(source.Tags) || !BodyDefinition(snapshot, source, cards, selected: true)) return false;

        var candidates = snapshot.Entities.Values.Where(entity => entity is { Kind: EntityKind.Card, Tags: not null } &&
            entity.CardId == DeathrattleEnchantmentCardId && entity.Zone != "GRAVEYARD" &&
            (entity.GetIntTag("ATTACHED") == sourceEntityId || entity.GetIntTag("40") == sourceEntityId)).ToArray();
        if (candidates.Length != 1) return false;
        enchantment = candidates[0];
        if (enchantment.Id <= 0 || !snapshot.Entities.TryGetValue(enchantment.Id, out var keyed) || !ReferenceEquals(keyed, enchantment) ||
            enchantment.Zone != "PLAY" || enchantment.ControllerId != snapshot.LocalPlayerId ||
            !EnumEvidence(enchantment, "ZONE", "49", "PLAY", "1") || !EnumEvidence(enchantment, "CARDTYPE", "202", "ENCHANTMENT", "6") ||
            !IntEvidence(enchantment, "CONTROLLER", "50", out var enchantOwner) || enchantOwner != owner ||
            !IntEvidence(enchantment, "ATTACHED", "40", out var attached) || attached != sourceEntityId ||
            !Optional(enchantment, "ENTITY_ID", "53", enchantment.Id) || !Optional(enchantment, "SILENCED", "188", 0) ||
            !Optional(enchantment, "DORMANT", "1518", 0) || !Optional(enchantment, "HEALTH", "45", 1) ||
            !Optional(enchantment, "TAG_SCRIPT_DATA_NUM_6", "2921", 0) || !Optional(enchantment, "IS_NIGHTMARE_BONUS", "3567", 1) ||
            !Visual(enchantment, "ENCHANTMENT_BIRTH_VISUAL", "330") || !Visual(enchantment, "ENCHANTMENT_IDLE_VISUAL", "331") ||
            !NativeCardDefinitions.IsAuditedEntity(snapshot.ClientBuild, enchantment, cards) ||
            AssembledCardText.RequiresCustomText(enchantment.Tags)) return false;
        foreach (var (name, alias) in DeathModifiers)
            if (!Optional(enchantment, name, alias, 0)) return false;
        foreach (var (name, alias) in new[] { ("BATTLECRY", "218"), ("DEATHRATTLE", "217"), ("TRIGGER_VISUAL", "32"), ("AURA", "362"), ("REBORN", "1085") })
            if (!Optional(enchantment, name, alias, 0)) return false;
        foreach (var tag in enchantment.Tags.Where(pair => pair.Key.StartsWith("TAG_SCRIPT_DATA_NUM_", StringComparison.Ordinal)))
            if (!int.TryParse(tag.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 0) return false;
        reason = "";
        return true;
    }

    private static bool DeathrattleDrawContext(GameSnapshot snapshot, int bodyId, int enchantmentId,
        IReadOnlyDictionary<string, CardRecord> cards, out string reason)
    {
        reason = "当前有未覆盖、重复、抑制或顺序未明的死亡/抽牌/入手/受伤触发，不能给出完整的一次直接抽牌预览。";
        if (NativeCardDefinitions.HasInvalidContext(snapshot, cards, bodyId)) return false;
        var passiveAttachments = LineEvaluator.AuditedStaticAttachmentsForDeathDraw(snapshot, cards);
        foreach (var entity in snapshot.Entities.Values)
        {
            if (entity.Tags is null) return false;
            if (entity.Zone == "SECRET") return false;
            if (entity.Id == enchantmentId) continue;
            var attached = entity.GetIntTag("ATTACHED") is > 0 || entity.GetIntTag("40") is > 0;
            if (entity.Zone != "GRAVEYARD" && attached &&
                (entity.Tags.GetValueOrDefault("CARDTYPE") is "ENCHANTMENT" or "6" || entity.Tags.GetValueOrDefault("202") is "ENCHANTMENT" or "6" ||
                 NativeCardDefinitions.ExpectedType(entity.CardId) == "ENCHANTMENT") && !passiveAttachments.Contains(entity.Id)) return false;
            if (entity.Kind != EntityKind.Player && entity.Zone != "PLAY") continue;
            foreach (var (name, alias) in DeathModifiers)
                if (!Optional(entity, name, alias, 0)) return false;
            foreach (var (name, alias) in new[] { ("TRIGGER_VISUAL", "32"), ("AURA", "362") })
                if (!Bool(entity, name, alias, false, out var active) || active && !KnownUnrelatedTrigger(snapshot, entity, cards)) return false;
            if (entity.Id == bodyId) continue;
            if (entity.Kind == EntityKind.Card &&
                (entity.Tags.GetValueOrDefault("CARDTYPE") is "MINION" or "4" || entity.Tags.GetValueOrDefault("202") is "MINION" or "4") &&
                !BodyDefinition(snapshot, entity, cards, selected: false)) return false;
        }
        reason = "";
        return true;
    }

    private static bool KnownUnrelatedTrigger(GameSnapshot snapshot, EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards) =>
        entity.CardId is "EDR_849" or "CORE_TTN_866" or "EDR_480" &&
        NativeCardDefinitions.IsAuditedEntity(snapshot.ClientBuild, entity, cards);

    private static bool BodyDefinition(GameSnapshot snapshot, EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards, bool selected)
    {
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out var card) || card is null || card.Type != "MINION" ||
            card.Id != id || card.RawJson is not { Length: > 0 and <= 65536 } ||
            NativeCardDefinitions.IsRegistered(id) && !NativeCardDefinitions.IsAuditedEntity(snapshot.ClientBuild, entity, cards)) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            if (!NativeCardDefinitions.RecordMatchesRaw(card, raw)) return false;
            var mechanics = raw.TryGetProperty("mechanics", out var list)
                ? list.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            if (selected && (mechanics.Contains("DEATHRATTLE") || mechanics.Contains("REBORN"))) return false;
            if (mechanics.Contains("AURA") || mechanics.Contains("TRIGGER_VISUAL"))
                return KnownUnrelatedTrigger(snapshot, entity, cards);
            if (NativeCardDefinitions.IsRegistered(id) && NativeCardDefinitions.IsImplementedBody(id)) return true;
            return mechanics.All(PassiveKeywords.Contains) && string.IsNullOrWhiteSpace(Regex.Replace(card.Text,
                "<[^>]+>|\\$|法术伤害\\s*\\+\\d+|Spell Damage\\s*\\+\\d+|[\\s。；，,.!！:：]|嘲讽|圣盾|冲锋|突袭|风怒|吸血|剧毒|潜行|扰魔|Taunt|Divine Shield|Charge|Rush|Windfury|Lifesteal|Poisonous|Stealth|Elusive", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or RegexMatchTimeoutException)
        { return false; }
    }

    private static bool Bool(EntitySnapshot entity, string name, string alias, bool absent, out bool value)
    {
        value = absent;
        if (!entity.Tags.ContainsKey(name) && !entity.Tags.ContainsKey(alias)) return true;
        if (!IntEvidence(entity, name, alias, out var number) || number is not (0 or 1)) return false;
        value = number == 1; return true;
    }
    private static bool? StrictDrawFlag(EntitySnapshot entity, string name, string alias) =>
        Bool(entity, name, alias, false, out var value) ? value : null;
    private static bool Optional(EntitySnapshot entity, string name, string alias, int expected) =>
        !entity.Tags.ContainsKey(name) && (alias.Length == 0 || !entity.Tags.ContainsKey(alias)) ||
        IntEvidence(entity, name, alias, out var value) && value == expected;
    private static bool Visual(EntitySnapshot entity, string name, string alias) =>
        !entity.Tags.ContainsKey(name) && !entity.Tags.ContainsKey(alias) || IntEvidence(entity, name, alias, out var value) && value is 0 or 1;
    private static bool IntEvidence(EntitySnapshot entity, string name, string alias, out int? number)
    {
        number = null;
        foreach (var key in new[] { name, alias }.Where(key => key.Length > 0))
            if (entity.Tags.TryGetValue(key, out var raw))
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || number.HasValue && number != value) return false;
                number = value;
            }
        return number.HasValue;
    }
    private static bool EnumEvidence(EntitySnapshot entity, string name, string alias, string text, string numeric)
    {
        var found = false;
        foreach (var key in new[] { name, alias })
            if (entity.Tags.TryGetValue(key, out var value)) { found = true; if (value != text && value != numeric) return false; }
        return found;
    }
}
