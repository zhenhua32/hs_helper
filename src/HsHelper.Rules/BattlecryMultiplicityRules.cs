using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>A bound count, not a play, a payment or evidence that any activation has completed.</summary>
public sealed record BattlecryMultiplicityOutcome(string MatchId, long StateVersion, long EventSequence,
    string? CardBuild, int SourceEntityId, bool IsResolved, bool? IsEnabled, int? Activations,
    IReadOnlyList<int> EnchantmentEntityIds, IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    private IReadOnlyList<int> _enchantments = Array.AsReadOnly(EnchantmentEntityIds.ToArray());
    private IReadOnlyList<string> _unknowns = Array.AsReadOnly(Unknowns.ToArray());
    private IReadOnlyList<string> _coverage = Array.AsReadOnly(RuleCoverage.ToArray());
    public IReadOnlyList<int> EnchantmentEntityIds { get => _enchantments; init => _enchantments = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<string> Unknowns { get => _unknowns; init => _unknowns = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<string> RuleCoverage { get => _coverage; init => _coverage = Array.AsReadOnly(value.ToArray()); }
}

/// <summary>
/// Build 255790: ordinary/disabled Battlecry and one observed Rude Awakening attachment only.
/// This does not infer a general formula from EXTRA_*_BASE/ADDITIONAL or authorize double Deathrattles.
/// The full t7e definition is pinned to the same cards.json/CardDefs sources as native rules.
/// </summary>
public static class BattlecryMultiplicityRules
{
    public const string Build = "255790";
    public const string EnchantmentCardId = "EDR_100t7e";
    private const string Fingerprint = "5188D5BAD475D99676D84F41E6E25D3F1B089C671DE1CE07A12AD29CD3DF86D5";
    private static readonly (string Name, string Alias)[] Counters =
        [("EXTRA_BATTLECRIES_BASE", "411"), ("EXTRA_MINION_BATTLECRIES_BASE", "1112"), ("EXTRA_BATTLECRIES_ADDITIONAL", "1126")];
    private static readonly (string Name, string Alias)[] EnchantmentFlags =
        [("BATTLECRY", "218"), ("DEATHRATTLE", "217"), ("TRIGGER_VISUAL", "32"), ("AURA", "362"),
         ("REBORN", "1085"), ("CHARGE", "197"), ("RUSH", "791"), ("TAUNT", "190"), ("DIVINE_SHIELD", "194"),
         ("LIFESTEAL", "685"), ("POISONOUS", "363"), ("WINDFURY", "189"), ("MEGA_WINDFURY", "1207"),
         ("STEALTH", "191"), ("ELUSIVE", "1211"), ("IMMUNE", "240"), ("SILENCED", "188"), ("DORMANT", "1518")];

    public static bool IsAuditedEnchantment(string? build, CardRecord? card)
    {
        if (build != Build || card is not { Id: EnchantmentCardId, DbfId: 113883, Type: "ENCHANTMENT", RawJson.Length: > 0 and <= 65536 }) return false;
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson);
            return NativeCardDefinitions.RecordMatchesRaw(card, raw.RootElement) && NativeCardDefinitions.CanonicalHash(raw.RootElement) == Fingerprint;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { return false; }
    }

    public static BattlecryMultiplicityOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        snapshot = AttachmentVisibility.Project(snapshot);
        var unknowns = new List<string>(); var coverage = new List<string>(); var ids = new List<int>();
        bool? enabled = null; int? count = null;
        if (snapshot.ClientBuild != Build || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not { } local || !snapshot.Entities.TryGetValue(sourceEntityId, out var source) ||
            !SourceIdentity(snapshot, source, local, allowPinnedNativeType: true) || source.CardId is null || !cards.TryGetValue(source.CardId, out var card) || card is null || card.Type != "MINION")
        { unknowns.Add("战吼次数缺少固定版本、可靠视角或当前可见随从身份，未推定普通一次。"); return Result(); }
        if (NativeCardDefinitions.IsRegistered(source.CardId) && !NativeCardDefinitions.IsAudited(snapshot.ClientBuild, source.CardId, card))
        { unknowns.Add("原生战吼主体完整定义不一致，未按卡牌ID计算次数。"); return Result(); }
        bool printed;
        if (AssembledCardText.RequiresCustomText(source.Tags))
        {
            if (AssembledScriptAudit.Resolve(snapshot.ClientBuild, source.CardId, source.Tags, cards) is null)
            { unknowns.Add("拼装主体未通过有效脚本审核，未恢复原卡战吼。"); return Result(); }
            printed = true;
        }
        else if (!PrintedBattlecry(card, out printed))
        { unknowns.Add("随从当前基础定义与战吼机制不能一致核对。"); return Result(); }
        if (!Boolean(source, "BATTLECRY", "218", printed, out var battlecry) || !Boolean(source, "SILENCED", "188", false, out var silenced))
        { unknowns.Add("战吼或沉默的原始布尔值/别名冲突，未选择有利值。"); return Result(); }
        if (battlecry && !printed)
        { unknowns.Add("当前额外战吼没有已审核的子脚本，未从基础空效果推定结算。"); return Result(); }
        enabled = battlecry && !silenced;
        // Expiry is safe to ignore only when both zone observations agree; a graveyard
        // name must not hide raw tag49=PLAY and silently restore ordinary single execution.
        foreach (var entity in snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && !Enum(entity, "ZONE", "49", "GRAVEYARD", "4") &&
            entity.CardId is EnchantmentCardId or "EDR_100t7e1" && RefersTo(entity, sourceEntityId)))
        {
            if (!TryAuditAttachment(snapshot, entity.Id, cards, out var attached) || attached != sourceEntityId)
            { unknowns.Add("已观察重复战吼来源的定义、挂载或生命周期未通过审核。"); continue; }
            ids.Add(entity.Id);
        }
        if (ids.Count > 1) unknowns.Add("多个重复来源的叠加与顺序尚未审计，不自动相加或相乘。");
        // Explicit suppression completes with zero; it does not remove unrelated unknown effects
        // from the full action evaluator's separate interaction audit.
        if (enabled == false)
        {
            if (unknowns.Count == 0) { count = 0; coverage.Add("battlecry-explicitly-disabled-or-absent"); }
            return Result();
        }
        foreach (var entity in snapshot.Entities.Values.Where(entity => RelevantCounterEntity(entity, source)))
        {
            if (Counters.Any(pair => !Number(entity, pair.Name, pair.Alias, out var value) || value is not (null or 0)) ||
                entity.Tags.Any(pair => pair.Key.Contains("BATTLECR", StringComparison.Ordinal) && pair.Key != "BATTLECRY" &&
                    !Counters.Any(counter => pair.Key == counter.Name) && (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 0)))
                unknowns.Add("存在未审计的全局、基础或追加战吼修正；字段值不是可直接使用的总次数。");
        }
        if (unknowns.Count == 0)
        {
            count = ids.Count == 1 ? 2 : 1;
            coverage.Add(ids.Count == 1 ? "observed-single-rude-awakening-battlecry-twice" : "ordinary-battlecry-once-no-extra-marker-required");
        }
        return Result();

        BattlecryMultiplicityOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            snapshot.ClientBuild, sourceEntityId, count.HasValue && unknowns.Count == 0, enabled, count,
            Array.AsReadOnly(ids.Order().ToArray()), Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(coverage.ToArray()));
    }

    public static bool TryAuditAttachment(GameSnapshot snapshot, int enchantmentEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, out int targetEntityId)
    {
        targetEntityId = 0;
        if (snapshot.ClientBuild != Build || ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not { } local ||
            !snapshot.Entities.TryGetValue(enchantmentEntityId, out var entity) || entity.Kind != EntityKind.Card || entity.Id != enchantmentEntityId ||
            entity.CardId != EnchantmentCardId || entity.Tags is null || !cards.TryGetValue(EnchantmentCardId, out var definition) ||
            !IsAuditedEnchantment(snapshot.ClientBuild, definition) || AssembledCardText.RequiresCustomText(entity.Tags) ||
            !Enum(entity, "CARDTYPE", "202", "ENCHANTMENT", "6") || !Enum(entity, "ZONE", "49", "PLAY", "1") ||
            !Number(entity, "CONTROLLER", "50", out var owner) || owner is not (1 or 2) || entity.ControllerId != owner ||
            !Number(entity, "ATTACHED", "40", out var attached) || attached is null or <= 0 ||
            !Optional(entity, "ENTITY_ID", "53", entity.Id) || !Optional(entity, "TAG_SCRIPT_DATA_NUM_6", "2921", 113882) ||
            !Optional(entity, "IS_NIGHTMARE_BONUS", "3567", 1) || !ValidEnchantmentFlags(entity) ||
            !snapshot.Entities.TryGetValue(attached.Value, out var target) || !SourceIdentity(snapshot, target, local) || target.ControllerId != owner ||
            target.RemainingHealth is not > 0 || !Number(target, "DORMANT", "1518", out var dormant) || dormant is not (null or 0)) return false;
        if (target.CardId is null || !cards.TryGetValue(target.CardId, out var body) || body is null || body.Type != "MINION" ||
            NativeCardDefinitions.IsRegistered(body.Id) && !NativeCardDefinitions.IsAudited(snapshot.ClientBuild, body.Id, body) ||
            AssembledCardText.RequiresCustomText(target.Tags) && AssembledScriptAudit.Resolve(snapshot.ClientBuild, target.CardId, target.Tags, cards) is null) return false;
        targetEntityId = attached.Value; return true;
    }

    private static bool ValidEnchantmentFlags(EntitySnapshot entity) =>
        EnchantmentFlags.All(pair => Optional(entity, pair.Name, pair.Alias, 0)) && Optional(entity, "SPELLPOWER", "192", 0) &&
        Number(entity, "ENCHANTMENT_BIRTH_VISUAL", "330", out var birth) && birth is null or 0 or 1 &&
        Number(entity, "ENCHANTMENT_IDLE_VISUAL", "331", out var idle) && idle is null or 0 or 1 &&
        // These two unnamed source tags are fingerprint corroboration only, never count formulas.
        Optional(entity, "1179", "1179", 1) && Optional(entity, "4571", "4571", 1) &&
        entity.Tags.Where(pair => pair.Key.StartsWith("TAG_SCRIPT_DATA_NUM_", StringComparison.Ordinal) && pair.Key != "TAG_SCRIPT_DATA_NUM_6")
            .All(pair => int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value == 0);

    private static bool SourceIdentity(GameSnapshot snapshot, EntitySnapshot source, int local, bool allowPinnedNativeType = false) =>
        source.Kind == EntityKind.Card && source.Id > 0 && source.CardId is not null && source.Tags is not null &&
        snapshot.Entities.TryGetValue(source.Id, out var actual) && ReferenceEquals(actual, source) && source.ControllerId is 1 or 2 &&
        source.Zone is "HAND" or "PLAY" && (source.Zone == "PLAY" || source.ControllerId == local) &&
        (Enum(source, "CARDTYPE", "202", "MINION", "4") || allowPinnedNativeType &&
            !source.Tags.ContainsKey("CARDTYPE") && !source.Tags.ContainsKey("202") &&
            !AssembledCardText.RequiresCustomText(source.Tags) && NativeCardDefinitions.ExpectedType(source.CardId) == "MINION") &&
        Enum(source, "ZONE", "49", source.Zone, source.Zone == "HAND" ? "3" : "1") &&
        Number(source, "CONTROLLER", "50", out var owner) && owner == source.ControllerId && Optional(source, "ENTITY_ID", "53", source.Id);
    private static bool RelevantCounterEntity(EntitySnapshot entity, EntitySnapshot source) => entity.Id == source.Id ||
        entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == source.ControllerId ||
        entity.Kind == EntityKind.Game || entity.Zone == "PLAY" && entity.ControllerId == source.ControllerId;
    private static bool RefersTo(EntitySnapshot entity, int id) =>
        entity.GetIntTag("ATTACHED") == id || entity.GetIntTag("40") == id;
    private static bool PrintedBattlecry(CardRecord card, out bool printed)
    {
        printed = false;
        if (card.RawJson is not { Length: > 0 and <= 65536 }) return false;
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson);
            if (!NativeCardDefinitions.RecordMatchesRaw(card, raw.RootElement)) return false;
            if (!raw.RootElement.TryGetProperty("mechanics", out var list)) return true;
            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return false;
            printed = list.EnumerateArray().Any(value => value.GetString() == "BATTLECRY"); return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) { return false; }
    }
    private static bool Boolean(EntitySnapshot entity, string name, string alias, bool fallback, out bool value)
    { value = fallback; if (!Number(entity, name, alias, out var raw) || raw is not (null or 0 or 1)) return false; if (raw.HasValue) value = raw == 1; return true; }
    private static bool Optional(EntitySnapshot entity, string name, string alias, int expected) => Number(entity, name, alias, out var value) && (value is null || value == expected);
    private static bool Number(EntitySnapshot entity, string name, string alias, out int? value)
    {
        value = null; var named = entity.Tags.TryGetValue(name, out var first); var numbered = entity.Tags.TryGetValue(alias, out var second);
        if (!named && !numbered) return true;
        if (named && !int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
        if (numbered && !int.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
        var a = named ? int.Parse(first!, CultureInfo.InvariantCulture) : (int?)null;
        var b = numbered ? int.Parse(second!, CultureInfo.InvariantCulture) : (int?)null;
        if (a.HasValue && b.HasValue && a != b) return false; value = a ?? b; return true;
    }
    private static bool Enum(EntitySnapshot entity, string name, string alias, string expected, string numeric)
    {
        var named = entity.Tags.TryGetValue(name, out var first); var numbered = entity.Tags.TryGetValue(alias, out var second);
        return (named || numbered) && (!named || first == expected || first == numeric) && (!numbered || second == expected || second == numeric);
    }
}
