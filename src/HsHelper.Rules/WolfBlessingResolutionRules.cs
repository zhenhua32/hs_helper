using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>Current simulated values bound to the original observation; absent values never borrow printed/old values.</summary>
public sealed record WolfBlessingResolutionState(string MatchId, long StateVersion, long EventSequence,
    int? Level, IReadOnlyList<EntitySnapshot> HandBeforeBlessing)
{
    public string? CurrentSourceCardId { get; init; }
}

public sealed record WolfBlessingBranch(int TargetEntityId, string CardId, string Name,
    int? CurrentAttack, int? CurrentCost, int? ResultingAttack, int? ResultingCost);

public sealed record WolfBlessingResolutionOutcome(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, string? SourceCardId, int? Level, bool ResolvesEffects, bool IsCandidateSetComplete,
    IReadOnlyList<WolfBlessingBranch> Branches, IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    public bool IsRandom => true;
    public bool? IsLegal => null;
    public bool CanChooseBranch => false;
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
}

/// <summary>
/// Build 255790 EDR_850p (DBF 114052): one random Beast in your hand gains the current text's attack and cost reduction.
/// Fixed CardDefs/cards.json and https://hearthstone.blizzard.com/en-us/cards/114052-blessing-of-the-wolf/ .
/// Branches describe a single native effect, not a chosen random result or a legal activation/next state.
/// </summary>
public static class WolfBlessingResolutionRules
{
    private readonly record struct Number(bool Present, bool Valid, int? Value);

    public static WolfBlessingResolutionOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, WolfBlessingResolutionState? beforeBlessing = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var coverage = new List<string>(); var branches = new List<WolfBlessingBranch>();
        string? sourceCardId = null; int? level = null;
        var complete = false; var resolves = false;
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        if (local is null || snapshot.ClientBuild != "255790" || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable ||
            snapshot.Quality.OpenBlockCount != 0 || snapshot.Phase != GamePhase.Main || snapshot.ActivePlayerId != local ||
            snapshot.Entities.Any(pair => pair.Value is null || pair.Value.Tags is null || pair.Key != pair.Value.Id))
        { unknowns.Add("需要 build 255790 的完整稳定我方回合，未展开巨狼分支。"); return Result(); }
        if (!snapshot.Entities.TryGetValue(sourceEntityId, out var source) || source.Kind != EntityKind.Card ||
            source.ControllerId != local || source.Zone != "PLAY" || string.IsNullOrWhiteSpace(source.CardId) ||
            !ObservedType(source.Tags, "HERO_POWER", required: true))
        { unknowns.Add("当前技能实体、身份、类型或归属未确认。"); return Result(); }
        if (beforeBlessing is not null && (beforeBlessing.MatchId != snapshot.MatchId ||
            beforeBlessing.StateVersion != snapshot.StateVersion || beforeBlessing.EventSequence != snapshot.EventSequence ||
            beforeBlessing.HandBeforeBlessing is null))
        { unknowns.Add("技能前覆盖不属于当前对局、状态版本及事件序号。"); return Result(); }
        sourceCardId = beforeBlessing?.CurrentSourceCardId ?? source.CardId;
        if (sourceCardId != "EDR_850p" || !cards.TryGetValue(sourceCardId, out var powerCard) ||
            powerCard is not { Id: "EDR_850p", DbfId: 114052, Type: "HERO_POWER" } ||
            !NativeCardDefinitions.IsAudited(snapshot.ClientBuild, sourceCardId, powerCard) ||
            AssembledCardText.RequiresCustomText(source.Tags))
        { unknowns.Add("技能不是已确认的原生巨狼祝福，不能按基础ID推导自定义效果。"); return Result(); }
        var powers = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.ControllerId == local && entity.Zone == "PLAY" &&
            ObservedType(entity.Tags, "HERO_POWER", required: true)).ToArray();
        var player = snapshot.Entities.Values.Single(entity => entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == local);
        if (powers.Length != 1 || powers[0].Id != sourceEntityId ||
            player.GetIntTag("HERO_POWER_ENTITY") is { } linked && linked != sourceEntityId)
        { unknowns.Add("当前英雄技能未唯一绑定，未展开随机分支。"); return Result(); }

        var attackParameter = Read(source.Tags, "TAG_SCRIPT_DATA_NUM_1", "2");
        var discountParameter = Read(source.Tags, "TAG_SCRIPT_DATA_NUM_2", "3");
        // An overlay can carry a covered Imbue transition, but cannot repair invalid original aliases.
        if (source.CardId == "EDR_850p" && (!attackParameter.Valid || !discountParameter.Valid ||
            attackParameter.Present && attackParameter.Value is not (>= 1) ||
            discountParameter.Present && discountParameter.Value is not (>= 1) ||
            attackParameter.Value.HasValue && discountParameter.Value.HasValue && attackParameter.Value != discountParameter.Value))
        { unknowns.Add("巨狼当前参数非法或别名冲突，未按可用的一份值修复。"); return Result(); }
        level = beforeBlessing is not null ? beforeBlessing.Level :
            attackParameter.Value is >= 1 && attackParameter.Value == discountParameter.Value ? attackParameter.Value : null;
        if (level is not (>= 1))
        { level = null; unknowns.Add("攻击增益与减费等级未确认，不能回填印刷初值或累计灌注次数。"); }

        var hand = (beforeBlessing?.HandBeforeBlessing ?? snapshot.Entities.Values
            .Where(entity => entity.ControllerId == local && entity.Zone == "HAND").ToArray()).ToArray();
        if (hand.Any(entity => entity is null || entity.Tags is null || entity.Id <= 0 || entity.Id == sourceEntityId ||
                entity.Kind != EntityKind.Card || entity.ControllerId != local || entity.Zone != "HAND") ||
            hand.Select(entity => entity.Id).Distinct().Count() != hand.Length)
        { unknowns.Add("技能前手牌集合存在身份、归属、区域或重复实体冲突。"); return Result(); }
        complete = true;
        if (beforeBlessing is null && player.Tags.TryGetValue("NUM_CARDS_IN_HAND", out var handCount) &&
            (!int.TryParse(handCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0 || count != hand.Length))
        { complete = false; unknowns.Add("手牌数标签与实体集合冲突，候选集合不完整。"); }

        foreach (var entity in hand.OrderBy(entity => entity.Id))
        {
            if (entity.CardId is null || !cards.TryGetValue(entity.CardId, out var card) || card is null ||
                card.Id != entity.CardId || string.IsNullOrWhiteSpace(card.Name))
            { complete = false; unknowns.Add("存在身份或固定资料不明的手牌，不能排除额外随机候选。"); continue; }
            if (!ObservedType(entity.Tags, card.Type))
            { complete = false; unknowns.Add("手牌当前类别与固定身份冲突，候选集合保持不完整。"); continue; }
            if (NativeCardDefinitions.IsRegistered(card.Id) && !NativeCardDefinitions.IsAudited(snapshot.ClientBuild, card.Id, card))
            { complete = false; unknowns.Add("参与候选分类的原生固定定义不一致，未借原CardId推断野兽或增益。"); continue; }
            if (card.Type != "MINION") continue;
            var beast = IsBeast(entity, card);
            if (beast is null)
            { complete = false; unknowns.Add("手牌当前种族未确认，不能用印刷种族覆盖冲突。"); continue; }
            if (beast == false) continue;
            var attack = Read(entity.Tags, "ATK", "47"); var cost = Read(entity.Tags, "COST", "48");
            var currentAttack = attack.Valid && attack.Value is >= 0 ? attack.Value : null;
            var currentCost = cost.Valid && cost.Value is >= 0 ? cost.Value : null;
            int? afterAttack = null, afterCost = null;
            var custom = AssembledCardText.RequiresCustomText(entity.Tags) ||
                snapshot.Entities.TryGetValue(entity.Id, out var original) && AssembledCardText.RequiresCustomText(original.Tags);
            var uncertainInteraction = custom || UnreviewedInteraction(entity.Tags);
            if (uncertainInteraction)
                unknowns.Add("候选带有自定义文字或未审计的免疫/增益修正，仅列当前值，增益后数值保持未知。");
            else if (level.HasValue)
            {
                if (currentAttack is { } current && (long)current + level.Value <= int.MaxValue) afterAttack = current + level.Value;
                if (currentCost is { } mana) afterCost = (int)Math.Max(0L, (long)mana - level.Value);
            }
            if (currentAttack is null || currentCost is null) unknowns.Add("候选当前攻击力或费用缺失/冲突，不回填印刷或覆盖前的数值。");
            if (level.HasValue && currentAttack is { } a && (long)a + level.Value > int.MaxValue) unknowns.Add("候选攻击力增益溢出，结果保持未知。");
            branches.Add(new(entity.Id, entity.CardId, card.Name, currentAttack, currentCost, afterAttack, afterCost));
        }
        if (branches.Count == 0) unknowns.Add("没有已确认野兽候选；空放技能是否合法仍未证明。");
        resolves = complete && branches.Count > 0 && level.HasValue;
        coverage.Add("EDR_850p-observed-hand-beast-branches");
        if (level.HasValue) coverage.Add("EDR_850p-current-level-direct-attack-and-clamped-cost");
        unknowns.Add("分支只表示一次原生增益的可能命中，不选择随机结果、不声明概率；其他触发和后续动作需新观测。使用是否合法另行验证。");
        return Result();

        WolfBlessingResolutionOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            sourceEntityId, sourceCardId, level, resolves, complete, Array.AsReadOnly(branches.ToArray()),
            Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(coverage.ToArray()));
    }

    private static Number Read(IReadOnlyDictionary<string, string> tags, string name, string numeric)
    {
        var hasName = tags.TryGetValue(name, out var named); var hasNumber = tags.TryGetValue(numeric, out var numbered);
        if (!hasName && !hasNumber) return new(false, true, null);
        var namedOk = int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first);
        var numberOk = int.TryParse(numbered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second);
        return hasName && !namedOk || hasNumber && !numberOk || hasName && hasNumber && first != second
            ? new(true, false, null) : new(true, true, hasName ? first : second);
    }

    private static bool TypeMatches(string observed, string type) => observed == type || (type, observed) is
        ("MINION", "4") or ("SPELL", "5") or ("WEAPON", "7") or ("LOCATION", "39") or ("HERO", "3") or ("HERO_POWER", "10");

    private static bool ObservedType(IReadOnlyDictionary<string, string> tags, string type, bool required = false)
    {
        var named = tags.TryGetValue("CARDTYPE", out var first); var numeric = tags.TryGetValue("202", out var second);
        return (!required || named || numeric) && (!named || TypeMatches(first!, type)) && (!numeric || TypeMatches(second!, type));
    }

    private static bool? IsBeast(EntitySnapshot entity, CardRecord card)
    {
        var observed = entity.Tags.GetValueOrDefault("CARDRACE") ?? entity.Tags.GetValueOrDefault("200");
        if (entity.Tags.TryGetValue("CARDRACE", out var named) && entity.Tags.TryGetValue("200", out var numeric) && Race(named) != Race(numeric)) return null;
        var current = observed is null ? null : Race(observed);
        if (observed is not null && current is null) return null;
        if (current is "BEAST" or "ALL") return true;
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson);
            if (raw.RootElement.ValueKind != JsonValueKind.Object) return null;
            var races = new HashSet<string>(StringComparer.Ordinal);
            string? native = null;
            if (raw.RootElement.TryGetProperty("race", out var primary))
            { if (primary.ValueKind != JsonValueKind.String || Race(primary.GetString()) is not { } race) return null; native = race; races.Add(race); }
            if (raw.RootElement.TryGetProperty("races", out var types))
            {
                if (types.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in types.EnumerateArray())
                { if (item.ValueKind != JsonValueKind.String || Race(item.GetString()) is not { } race) return null; races.Add(race); }
            }
            if (observed is null) return races.Overlaps(["BEAST", "ALL"]);
            if (card.Id == "CORE_TTN_866" && current == "DEMON" && native == "DEMON" && races.SetEquals(["DEMON", "BEAST"])) return true;
            return false;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    private static string? Race(string? value) => value switch
    {
        "BEAST" or "20" => "BEAST", "ALL" or "26" => "ALL", "UNDEAD" or "11" => "UNDEAD", "MURLOC" or "14" => "MURLOC",
        "DEMON" or "15" => "DEMON", "MECHANICAL" or "17" => "MECHANICAL", "ELEMENTAL" or "18" => "ELEMENTAL", "TOTEM" or "21" => "TOTEM",
        "PIRATE" or "23" => "PIRATE", "DRAGON" or "24" => "DRAGON", "QUILBOAR" or "43" => "QUILBOAR", "NAGA" or "92" => "NAGA",
        "DRAENEI" or "2" => "DRAENEI", "NONE" or "INVALID" or "0" => "NONE", _ => null
    };

    private static bool UnreviewedInteraction(IReadOnlyDictionary<string, string> tags) => new[]
    { "IMMUNE", "240", "CANT_BE_TARGETED_BY_HERO_POWERS", "332", "CANT_BE_TARGETED_BY_SPELLS", "311", "MULTIPLY_BUFF_VALUE", "HERO_POWER_DOUBLE" }
        .Any(tag => tags.TryGetValue(tag, out var value) && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flag) || flag != 0));
}
