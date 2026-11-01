using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>A schedule for one native or actually attached granted script, not another death.</summary>
public sealed record DeathrattleMultiplicityOutcome(string MatchId, long StateVersion, long EventSequence,
    string? CardBuild, int SourceEntityId, int EffectSourceEntityId, int? EffectEnchantmentEntityId,
    bool IsAcquiredEffect, bool IsResolved, bool? IsEnabled, int? Activations,
    IReadOnlyList<int> MultiplierSourceEntityIds, IReadOnlyList<int> EnchantmentEntityIds,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    private IReadOnlyList<int> _sources = Array.AsReadOnly(MultiplierSourceEntityIds.ToArray());
    private IReadOnlyList<int> _enchantments = Array.AsReadOnly(EnchantmentEntityIds.ToArray());
    private IReadOnlyList<string> _unknowns = Array.AsReadOnly(Unknowns.ToArray());
    private IReadOnlyList<string> _coverage = Array.AsReadOnly(RuleCoverage.ToArray());
    public IReadOnlyList<int> MultiplierSourceEntityIds { get => _sources; init => _sources = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<int> EnchantmentEntityIds { get => _enchantments; init => _enchantments = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<string> Unknowns { get => _unknowns; init => _unknowns = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<string> RuleCoverage { get => _coverage; init => _coverage = Array.AsReadOnly(value.ToArray()); }
}

/// <summary>
/// Build255790: ordinary/disabled native or Inner Demons scripts, one observed EDR_531e,
/// or RLK_912's own acquired-only effect. No global/base/additional aggregation formula.
/// Counts are frozen before removal; this helper neither kills a body nor grants an effect.
/// </summary>
public static class DeathrattleMultiplicityRules
{
    public const string Build = "255790";
    public const string EnchantmentCardId = "EDR_531e";
    public const string SelfDoublerCardId = "RLK_912";
    public const string GrantedDrawEnchantmentCardId = "EDR_100t4e";
    private const string AttachmentFingerprint = "BCB708B6E9BD4D6A2CC8077EA2F099DB99BCD744BD033F6810B1F4EF84360C2F";
    private const string SelfFingerprint = "45690F39267472B1D7B346B2397DD5CDF2B2C1D2D89731E017917B148F40C62A";
    private static readonly (string Name, string Alias)[] Counters =
        [("EXTRA_MINION_DEATHRATTLES_BASE", "371"), ("EXTRA_DEATHRATTLES_BASE", "882"), ("EXTRA_DEATHRATTLES_ADDITIONAL", "1131")];
    private static readonly (string Name, string Alias)[] AttachmentFlags =
        [("BATTLECRY", "218"), ("DEATHRATTLE", "217"), ("TRIGGER_VISUAL", "32"), ("AURA", "362"),
         ("REBORN", "1085"), ("CHARGE", "197"), ("RUSH", "791"), ("TAUNT", "190"), ("DIVINE_SHIELD", "194"),
         ("LIFESTEAL", "685"), ("POISONOUS", "363"), ("WINDFURY", "189"), ("MEGA_WINDFURY", "1207"),
         ("STEALTH", "191"), ("ELUSIVE", "1211"), ("IMMUNE", "240"), ("SILENCED", "188"), ("DORMANT", "1518"),
         ("SPELLPOWER", "192"), ("CANT_TRIGGER_DEATHRATTLE", "1831")];
    private static readonly IReadOnlySet<string> AttachmentTags = AttachmentFlags.Concat(Counters)
        .SelectMany(pair => new[] { pair.Name, pair.Alias }).Concat(new[]
        { "CARDTYPE", "202", "ZONE", "49", "CONTROLLER", "50", "ATTACHED", "40", "ENTITY_ID", "53",
          "ENCHANTMENT_BIRTH_VISUAL", "330", "ENCHANTMENT_IDLE_VISUAL", "331", "IS_NIGHTMARE_BONUS", "3567",
          "HEALTH", "45", "TAG_SCRIPT_DATA_NUM_6", "2921", "CREATOR", "313" }).ToHashSet(StringComparer.Ordinal);

    public static bool IsAuditedEnchantment(string? build, CardRecord? card) =>
        Pinned(build, card, EnchantmentCardId, 120093, "ENCHANTMENT", AttachmentFingerprint);
    public static bool IsAuditedSelfDoubler(string? build, CardRecord? card) =>
        Pinned(build, card, SelfDoublerCardId, 84229, "MINION", SelfFingerprint);

    public static DeathrattleMultiplicityOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, int? effectEnchantmentEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var coverage = new List<string>(); var ids = new List<int>(); var sources = new List<int>();
        bool? enabled = null; int? count = null;
        if (snapshot.Entities.Any(pair => pair.Value is null || pair.Value.Tags is null || pair.Key != pair.Value.Id))
        { unknowns.Add("亡语实体集合的原始身份或标签缺失，未计算次数。"); return Result(); }
        snapshot = AttachmentVisibility.Project(snapshot);
        if (snapshot.ClientBuild != Build || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not { } local || !snapshot.Entities.TryGetValue(sourceEntityId, out var body) ||
            !BodyIdentity(snapshot, body, local, cards, allowPinnedType: true) || body.CardId is null || !cards.TryGetValue(body.CardId, out var card))
        { unknowns.Add("亡语次数缺少固定版本、可靠视角或当前随从身份，未补造一次结算。"); return Result(); }
        var self = IsAuditedSelfDoubler(snapshot.ClientBuild, card);
        if (body.CardId == SelfDoublerCardId && !self || !PrintedDeathrattle(snapshot, body, card, cards, out var printed))
        { unknowns.Add("亡语主体的完整定义或有效拼装文字未通过审核，未恢复基础脚本。"); return Result(); }
        if (!Boolean(body, "DEATHRATTLE", "217", printed, out var current) || !Boolean(body, "SILENCED", "188", false, out var silence) ||
            !Boolean(body, "CANT_TRIGGER_DEATHRATTLE", "1831", false, out var blocked))
        { unknowns.Add("亡语、沉默或本体抑制布尔值/原始别名存在冲突。"); return Result(); }
        if (effectEnchantmentEntityId is { } effect)
        {
            if (!TryAuditDrawAttachment(snapshot, effect, cards, out var target) || target != sourceEntityId ||
                !body.Tags.ContainsKey("DEATHRATTLE") && !body.Tags.ContainsKey("217"))
            { unknowns.Add("获得的亡语缺少真实完整抽牌附魔、当前启用观察或同主体挂载，未用调用者参数代替证据。"); return Result(); }
        }
        else if (!printed && current && !self)
        { unknowns.Add("当前额外亡语不属于可核对的原生脚本，未把获得的效果当作印刷亡语。"); return Result(); }
        enabled = !silence && !blocked && current && (effectEnchantmentEntityId.HasValue || printed);
        foreach (var entity in snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card &&
            entity.CardId == EnchantmentCardId && RefersTo(entity, sourceEntityId) && !Enum(entity, "ZONE", "49", "GRAVEYARD", "4")))
        {
            if (!TryAuditAttachment(snapshot, entity.Id, cards, out var target) || target != sourceEntityId)
            { unknowns.Add("已观察双亡语附魔的当前挂载、完整定义或动态字段未通过审核。"); continue; }
            ids.Add(entity.Id); sources.Add(entity.Id);
        }
        if (self && effectEnchantmentEntityId.HasValue) sources.Add(sourceEntityId);
        if (sources.Count > 1) unknowns.Add("多个亡语倍率来源的叠加或先后未审计，不推定两次或四次。");
        if (enabled == false)
        {
            if (unknowns.Count == 0) { count = 0; coverage.Add("deathrattle-scoped-script-disabled-or-absent"); }
            return Result();
        }
        foreach (var entity in snapshot.Entities.Values.Where(entity => Relevant(entity, body)))
        {
            foreach (var pair in Counters)
            {
                if (!Number(entity, pair.Name, pair.Alias, out var value)) { unknowns.Add("亡语倍数原始值或别名冲突，不能选择有利字段。"); continue; }
                var knownSelf = pair.Alias == "882" && entity.CardId == SelfDoublerCardId &&
                    cards.TryGetValue(SelfDoublerCardId, out var selfCard) && IsAuditedSelfDoubler(snapshot.ClientBuild, selfCard) &&
                    !AssembledCardText.RequiresCustomText(entity.Tags);
                if (knownSelf && value is null or 1) continue;
                if (value is not (null or 0) || knownSelf && value == 0)
                    unknowns.Add("存在未审计的基础、追加或全局亡语修正；原始值不是总次数。");
            }
            if (entity.Id != sourceEntityId && (!Number(entity, "CANT_TRIGGER_DEATHRATTLE", "1831", out var suppression) || suppression is not (null or 0)))
                unknowns.Add("其它载体上的亡语抑制没有已核对的全局作用域。");
            if (entity.Tags.Any(pair => pair.Key.Contains("DEATHRATT", StringComparison.Ordinal) &&
                pair.Key is not ("DEATHRATTLE" or "CANT_TRIGGER_DEATHRATTLE") && !Counters.Any(counter => counter.Name == pair.Key) &&
                (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 0)))
                unknowns.Add("存在未解释的亡语修正字段，未按普通次数继续。");
        }
        if (unknowns.Count == 0)
        {
            count = sources.Count == 1 ? 2 : 1;
            coverage.Add(self && effectEnchantmentEntityId.HasValue ? "RLK_912-audited-acquired-deathrattle-twice" :
                ids.Count == 1 ? "observed-single-EDR_531e-deathrattle-twice" : "ordinary-deathrattle-once-no-extra-marker-required");
        }
        return Result();

        DeathrattleMultiplicityOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            snapshot.ClientBuild, sourceEntityId, effectEnchantmentEntityId ?? sourceEntityId, effectEnchantmentEntityId,
            effectEnchantmentEntityId.HasValue, count.HasValue && unknowns.Count == 0, enabled, count,
            sources.Distinct().Order().ToArray(), ids.Order().ToArray(), unknowns.Distinct(StringComparer.Ordinal).ToArray(), coverage.ToArray());
    }

    public static bool TryAuditAttachment(GameSnapshot snapshot, int enchantmentEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, out int targetEntityId) =>
        Attachment(snapshot, enchantmentEntityId, cards, EnchantmentCardId, out targetEntityId);

    public static bool TryAuditDrawAttachment(GameSnapshot snapshot, int enchantmentEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, out int targetEntityId) =>
        Attachment(snapshot, enchantmentEntityId, cards, GrantedDrawEnchantmentCardId, out targetEntityId);

    private static bool Attachment(GameSnapshot snapshot, int id, IReadOnlyDictionary<string, CardRecord> cards, string expected, out int targetId)
    {
        targetId = 0;
        if (snapshot.ClientBuild != Build || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not { } local || id <= 0 ||
            !snapshot.Entities.TryGetValue(id, out var entity) || entity.Kind != EntityKind.Card || entity.Tags is null || entity.Id != id || entity.CardId != expected ||
            !cards.TryGetValue(expected, out var definition) ||
            !(expected == EnchantmentCardId ? IsAuditedEnchantment(snapshot.ClientBuild, definition) : NativeCardDefinitions.IsAudited(snapshot.ClientBuild, expected, definition)) ||
            !Enum(entity, "CARDTYPE", "202", "ENCHANTMENT", "6") || !Enum(entity, "ZONE", "49", "PLAY", "1") ||
            !Number(entity, "CONTROLLER", "50", out var owner) || owner is not (1 or 2) || owner != entity.ControllerId ||
            !Number(entity, "ATTACHED", "40", out var attached) || attached is null or <= 0 || !Optional(entity, "ENTITY_ID", "53", id) ||
            // CREATOR/313 is pinned HearthDb provenance only. It never locates/grants the effect.
            !Number(entity, "CREATOR", "313", out var creator) || creator is < 0 ||
            !AttachmentFlags.All(pair => Optional(entity, pair.Name, pair.Alias, 0)) ||
            !Counters.All(pair => Optional(entity, pair.Name, pair.Alias, 0)) ||
            !Visual(entity, "ENCHANTMENT_BIRTH_VISUAL", "330") || !Visual(entity, "ENCHANTMENT_IDLE_VISUAL", "331") ||
            !Optional(entity, "IS_NIGHTMARE_BONUS", "3567", expected == EnchantmentCardId ? 0 : 1) ||
            !Optional(entity, "HEALTH", "45", 1) || AssembledCardText.RequiresCustomText(entity.Tags) ||
            entity.Tags.Where(pair => pair.Key.StartsWith("TAG_SCRIPT_DATA_NUM_", StringComparison.Ordinal))
                .Any(pair => !int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 0) ||
            entity.Tags.Any(pair => !AttachmentTags.Contains(pair.Key) &&
                (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 0)) ||
            !Optional(entity, "TAG_SCRIPT_DATA_NUM_6", "2921", 0) ||
            !snapshot.Entities.TryGetValue(attached.Value, out var body) || !BodyIdentity(snapshot, body, local, cards) || body.ControllerId != owner ||
            !Number(body, "ATK", "47", out var attack) || attack is null or < 0 || !Number(body, "HEALTH", "45", out var health) || health is null or <= 0 ||
            !Number(body, "DAMAGE", "44", out var damage) || damage is null or < 0 || damage >= health ||
            !Number(body, "DORMANT", "1518", out var dormant) || dormant is not (null or 0)) return false;
        targetId = attached.Value; return true;
    }

    private static bool BodyIdentity(GameSnapshot snapshot, EntitySnapshot body, int local, IReadOnlyDictionary<string, CardRecord> cards, bool allowPinnedType = false) =>
        body.Kind == EntityKind.Card && body.Tags is not null && body.Id > 0 && snapshot.Entities.TryGetValue(body.Id, out var actual) && ReferenceEquals(body, actual) &&
        body.CardId is not null && cards.TryGetValue(body.CardId, out var card) && card is { Type: "MINION" } && card.Id == body.CardId &&
        (NativeCardDefinitions.IsRegistered(body.CardId) ? NativeCardDefinitions.IsAudited(snapshot.ClientBuild, body.CardId, card) : Coherent(card)) &&
        (body.CardId != SelfDoublerCardId || IsAuditedSelfDoubler(snapshot.ClientBuild, card)) &&
        body.Zone is "PLAY" or "HAND" && (body.Zone == "PLAY" || body.ControllerId == local) &&
        (Enum(body, "CARDTYPE", "202", "MINION", "4") || allowPinnedType && !body.Tags.ContainsKey("CARDTYPE") && !body.Tags.ContainsKey("202") &&
            !AssembledCardText.RequiresCustomText(body.Tags) && (NativeCardDefinitions.ExpectedType(body.CardId) == "MINION" || body.CardId == SelfDoublerCardId)) &&
        Enum(body, "ZONE", "49", body.Zone, body.Zone == "PLAY" ? "1" : "3") && Number(body, "CONTROLLER", "50", out var owner) &&
        owner is 1 or 2 && owner == body.ControllerId && Optional(body, "ENTITY_ID", "53", body.Id) &&
        Number(body, "HEALTH", "45", out var health) && health is null or > 0 &&
        Number(body, "DAMAGE", "44", out var damage) && damage is null or >= 0 &&
        (!health.HasValue || !damage.HasValue || damage < health) &&
        (!AssembledCardText.RequiresCustomText(body.Tags) || AssembledScriptAudit.Resolve(snapshot.ClientBuild, body.CardId, body.Tags, cards) is not null);

    private static bool PrintedDeathrattle(GameSnapshot snapshot, EntitySnapshot body, CardRecord card, IReadOnlyDictionary<string, CardRecord> cards, out bool printed)
    {
        printed = false;
        if (AssembledCardText.RequiresCustomText(body.Tags)) return AssembledScriptAudit.Resolve(snapshot.ClientBuild, body.CardId, body.Tags, cards) is not null;
        if (card.RawJson is not { Length: > 0 and <= 65536 }) return false;
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson);
            if (!NativeCardDefinitions.RecordMatchesRaw(card, raw.RootElement)) return false;
            if (!raw.RootElement.TryGetProperty("mechanics", out var list)) return true;
            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return false;
            printed = list.EnumerateArray().Any(value => value.GetString() == "DEATHRATTLE"); return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) { return false; }
    }
    private static bool Pinned(string? build, CardRecord? card, string id, int dbf, string type, string hash)
    {
        if (build != Build || card is null || card.Id != id || card.DbfId != dbf || card.Type != type || card.RawJson is not { Length: > 0 and <= 65536 }) return false;
        try { using var raw = JsonDocument.Parse(card.RawJson); return NativeCardDefinitions.RecordMatchesRaw(card, raw.RootElement) && NativeCardDefinitions.CanonicalHash(raw.RootElement) == hash; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) { return false; }
    }
    private static bool Coherent(CardRecord card)
    {
        if (card.RawJson is not { Length: > 0 and <= 65536 }) return false;
        try { using var raw = JsonDocument.Parse(card.RawJson); return NativeCardDefinitions.RecordMatchesRaw(card, raw.RootElement); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) { return false; }
    }
    private static bool Relevant(EntitySnapshot entity, EntitySnapshot body) => entity.Id == body.Id || entity.Kind == EntityKind.Game ||
        entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == body.ControllerId || entity.Zone == "PLAY" && entity.ControllerId == body.ControllerId;
    private static bool RefersTo(EntitySnapshot entity, int id) => entity.GetIntTag("ATTACHED") == id || entity.GetIntTag("40") == id;
    private static bool Boolean(EntitySnapshot entity, string name, string alias, bool fallback, out bool value)
    { value = fallback; if (!Number(entity, name, alias, out var raw) || raw is not (null or 0 or 1)) return false; if (raw.HasValue) value = raw == 1; return true; }
    private static bool Optional(EntitySnapshot entity, string name, string alias, int expected) => Number(entity, name, alias, out var value) && (value is null || value == expected);
    private static bool Visual(EntitySnapshot entity, string name, string alias) => Number(entity, name, alias, out var value) && value is null or 0 or 1;
    private static bool Number(EntitySnapshot entity, string name, string alias, out int? value)
    {
        value = null;
        foreach (var key in new[] { name, alias })
            if (entity.Tags.TryGetValue(key, out var raw))
            { if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || value.HasValue && value != parsed) return false; value = parsed; }
        return true;
    }
    private static bool Enum(EntitySnapshot entity, string name, string alias, string text, string number) =>
        (entity.Tags.ContainsKey(name) || entity.Tags.ContainsKey(alias)) &&
        (!entity.Tags.TryGetValue(name, out var first) || first == text || first == number) &&
        (!entity.Tags.TryGetValue(alias, out var second) || second == text || second == number);
}
