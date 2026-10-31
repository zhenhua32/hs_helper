using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>Inclusive marginal bounds. A null maximum means no reliable upper bound, not zero.</summary>
public sealed record DrawNumberRange(long Minimum, long? Maximum)
{
    public bool IsExact => Maximum == Minimum;
}

/// <summary>
/// Counts immediately after a successful play has removed its source from hand, before its draw.
/// An overlay belongs to the original observed frame; it never supplies new card identities.
/// Set DeckCompositionStillMatchesSnapshot=false after an earlier unresolved deck change.
/// </summary>
public sealed record DrawResolutionState(string MatchId, long StateVersion, long EventSequence,
    int? HandCountBeforeDraw, int? MaximumHandSize, int? DeckCountBeforeDraw,
    int? FatigueBefore, int? HeroHealthBefore, int? HeroArmorBefore,
    bool? HeroImmune = false, bool DeckCompositionStillMatchesSnapshot = true);

/// <summary>
/// The isolated direct draw only; ranges are correlated and cannot be combined as independent outcomes.
/// FatigueDamage is damage before armor (zero under observed immunity). Unknown triggers are not a future state.
/// </summary>
public sealed record DrawResolutionOutcome(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, string? SourceCardId, string Kind, bool ResolvesEffects,
    DrawNumberRange RequestedDraws, DrawNumberRange CardsRemovedFromDeck,
    DrawNumberRange CardsAddedToHand, DrawNumberRange CardsBurned,
    DrawNumberRange EmptyDeckAttempts, DrawNumberRange FatigueDamage,
    DrawNumberRange? HandCountAfter, DrawNumberRange? DeckCountAfter,
    DrawNumberRange? FatigueAfter, DrawNumberRange? HeroHealthAfter, DrawNumberRange? HeroArmorAfter,
    bool MayStopAtHeroDeath, IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> RuleCoverage)
{
    public bool IsDirectEffectOnly => true;
    public bool CanEvaluateFollowupActions => false;
}

/// <summary>
/// Build 255790 FIR_921 / EDR_226: counts and direct fatigue without guessing a drawn identity, entity or cost.
/// Card definitions: https://api.hearthstonejson.com/v1/255790/zhCN/cards.json .
/// Ordinary fatigue: https://hearthstone.blizzard.com/en-us/news/22819074/endgame-moves-finessing-fatigue .
/// Draw/burn/fatigue packet reference: HearthSim/fireplace actions.py Draw and Fatigue (not fixed-client proof).
/// A selected-card draw requires an actual selected card in HearthSim/SabberStone DrawCardTask.cs;
/// this does not establish EDR_226's fixed-client empty-deck fallback, which remains a bounded branch here.
/// </summary>
public static class DrawResolutionRules
{
    private sealed record Scenario(long Removed, long Added, long Burned, long EmptyAttempts, long Damage,
        long HandAfter, long DeckAfter, long FatigueAfter, long HealthAfter, long ArmorAfter, bool Dead);

    public static DrawResolutionOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, bool? imbuedTwice = null,
        DrawResolutionState? beforeDraw = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        string? sourceId = null; var kind = "Unknown";
        var unknown = new DrawNumberRange(0, null);
        var requested = unknown;
        if (snapshot.Entities.Values.Any(entity => entity is null || entity.Tags is null) ||
            snapshot.Entities.Any(pair => pair.Key != pair.Value.Id) ||
            snapshot.ClientBuild != "255790" || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable ||
            !snapshot.Quality.HasLocalPerspective || snapshot.Quality.OpenBlockCount != 0 || snapshot.IsSpectating ||
            snapshot.LocalPlayerId is not (1 or 2) || snapshot.ActivePlayerId != snapshot.LocalPlayerId || snapshot.Phase != GamePhase.Main)
        { unknowns.Add("需要固定 build 255790、完整稳定且轮到我方的非观战局面，未计算抽牌数量。"); return Unresolved(); }
        var players = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).ToArray();
        if (!players.Select(player => player.GetIntTag("PLAYER_ID")).Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("双方玩家实体未唯一确认，未连接私有牌库或手牌计数。"); return Unresolved(); }
        var player = players.Single(entity => entity.GetIntTag("PLAYER_ID") == snapshot.LocalPlayerId);
        if (!snapshot.Entities.TryGetValue(sourceEntityId, out var source) || source.Kind != EntityKind.Card ||
            source.ControllerId != snapshot.LocalPlayerId || source.CardId is not ("FIR_921" or "EDR_226") ||
            source.Zone != "HAND" && !(beforeDraw is not null && source.Zone == "PLAY") ||
            source.Tags.GetValueOrDefault("CARDTYPE") is { } sourceType && sourceType is not ("MINION" or "4"))
        { unknowns.Add("抽牌来源的身份、随从类型或我方归属未确认；场上旧随从不能自行再次触发战吼。"); return Unresolved(); }
        sourceId = source.CardId; kind = sourceId == "FIR_921" ? "Ordinary" : "Beast";
        if (AssembledCardText.RequiresCustomText(source.Tags))
        { unknowns.Add("抽牌来源具有拼装文字，不能按原卡ID展开原生抽牌。"); return Unresolved(); }
        if (!cards.TryGetValue(sourceId, out var sourceCard) || sourceCard is null || sourceCard.Id != sourceId ||
            sourceCard.Type != "MINION" || sourceCard.DbfId != (sourceId == "FIR_921" ? 115648 : 114018) || !AuditedSource(sourceCard))
        { unknowns.Add("缺少与固定来源 ID、DBF 和战吼定义一致的卡牌资料，未按同名或印刷费用猜测牌效。"); return Unresolved(); }
        if (beforeDraw is not null && (beforeDraw.MatchId != snapshot.MatchId || beforeDraw.StateVersion != snapshot.StateVersion || beforeDraw.EventSequence != snapshot.EventSequence))
        { unknowns.Add("抽牌前计数覆盖不属于当前对局、状态版本和事件序号，未使用过期计数。"); return Unresolved(); }

        var silence = Flag(source, "SILENCED", false, "188");
        var battlecry = And(silence.HasValue ? !silence.Value : null, Flag(source, "BATTLECRY", true, "218"));
        var active = sourceId == "FIR_921" ? And(battlecry, imbuedTwice) : battlecry;
        var amount = sourceId == "FIR_921" ? 2 : 1;
        requested = active switch { true => new(amount, amount), false => new(0, 0), _ => new(0, amount) };
        if (sourceId == "FIR_921" && imbuedTwice is null && battlecry != false)
            unknowns.Add("累计灌注是否达到两次尚未确认，本次直接抽牌为零或两次；当前英雄技能强度不证明累计次数。");

        var hand = beforeDraw is null ? ObservedCount("NUM_CARDS_IN_HAND", "HAND") : Nonnegative(beforeDraw.HandCountBeforeDraw, "抽牌前手牌数");
        if (beforeDraw is null && source.Zone == "HAND" && hand is { } observedHand) hand = observedHand >= 1 ? observedHand - 1 : null;
        var maximum = beforeDraw is null ? Nonnegative(Tag(player, "MAXHANDSIZE", "28"), "手牌上限") : Nonnegative(beforeDraw.MaximumHandSize, "手牌上限");
        var deck = beforeDraw is null ? ObservedCount("NUM_CARDS_IN_DECK", "DECK") : Nonnegative(beforeDraw.DeckCountBeforeDraw, "抽牌前牌库数");
        var fatigue = beforeDraw is null ? Nonnegative(Tag(player, "FATIGUE", "22"), "已有疲劳计数") : Nonnegative(beforeDraw.FatigueBefore, "已有疲劳计数");
        var hero = OwnHero();
        var health = beforeDraw is null ? ObservedHealth(hero) : Nonnegative(beforeDraw.HeroHealthBefore, "英雄当前生命");
        var armor = beforeDraw is null ? Nonnegative(hero?.GetIntTag("ARMOR"), "英雄护甲") : Nonnegative(beforeDraw.HeroArmorBefore, "英雄护甲");
        bool? immunity = beforeDraw is not null ? beforeDraw.HeroImmune : hero is not null ? Flag(hero, "IMMUNE", false, "240") : null;
        var fatiguePrevention = Or(Flag(player, "CANT_BE_FATIGUED", false, "456"), hero is not null ? Flag(hero, "CANT_BE_FATIGUED", false, "456") : null);
        if (health == 0) { unknowns.Add("抽牌前我方英雄已无生命，不能预览游戏结束后的抽牌。"); return Unresolved(); }
        var damageModifiers = snapshot.Entities.Values.Any(entity => entity.Zone == "PLAY" || entity.Kind == EntityKind.Player
            ? new[] { "HEALTH_MINIMUM", "MINIMUM_HEALTH", "DAMAGE_CANT_KILL", "INCOMING_DAMAGE_MULTIPLIER", "HERO_CANT_BE_DESTROYED", "MULTIPLY_HERO_DAMAGE" }
                .Any(tag => entity.GetIntTag(tag) is > 0) : false);
        if (damageModifiers)
        { unknowns.Add("存在未覆盖的伤害或最低生命修正，疲劳包数量与标称伤害可预览，但英雄结算后生命和护甲保持未知。"); health = null; armor = null; }
        if (maximum is null) unknowns.Add("手牌上限未可靠观察，入手与满手损失各自只给范围，不默认上限为十。");
        if (deck is null) unknowns.Add("牌库总数未知或与实体计数冲突，移出数量与空库尝试保留范围。");
        var beast = kind == "Beast" ? BeastCounts() : null;
        if (kind == "Beast") coverage.Add("EDR_226-counts-without-selected-beast-identity");
        else coverage.Add("FIR_921-historical-condition-zero-or-two-draws");

        var scenarios = new List<Scenario>();
        var requestValues = active switch { true => new[] { amount }, false => new[] { 0 }, _ => new[] { 0, amount } };
        var cantDraw = Flag(player, "CANT_DRAW", false, "232");
        var blockers = cantDraw switch { true => new[] { true }, false => new[] { false }, _ => new[] { false, true } };
        var immunityValues = immunity switch { true => new[] { true }, false => new[] { false }, _ => new[] { false, true } };
        var preventionValues = fatiguePrevention switch { true => new[] { true }, false => new[] { false }, _ => new[] { false, true } };
        var deckValues = deck is { } deckKnown ? new long[] { deckKnown } : new long[] { 0, 1, 2 };
        var slots = hand is { } handKnown && maximum is { } maxKnown
            ? new long[] { Math.Max(0L, (long)maxKnown - handKnown) } : new long[] { 0, 1, 2 };
        // The high representative exceeds every observed Int32 health/armor/counter. Unknown
        // fatigue remains unbounded in the result rather than adopting this internal sample.
        const long high = 1L << 40;
        var healthValues = health is { } h ? new long[] { h } : new long[] { 1, high };
        var armorValues = armor is { } a ? new long[] { a } : new long[] { 0, high };
        var fatigueValues = fatigue is { } f ? new long[] { f } : new long[] { 0, 1, high,
            Math.Max(0L, (long)(health ?? 1) + (armor ?? 0) - 1) };
        var uncertainTargetedEmpty = false;
        foreach (var count in requestValues)
        foreach (var blocked in blockers)
        foreach (var deckValue in deckValues)
        foreach (var capacity in slots)
        foreach (var heroHealth in healthValues)
        foreach (var heroArmor in armorValues)
        foreach (var immune in immunityValues)
        foreach (var preventFatigue in preventionValues)
        foreach (var fatigueBefore in fatigueValues.Distinct())
        {
            if (kind == "Ordinary") AddScenario(count, blocked, deckValue, capacity, heroHealth, heroArmor, fatigueBefore, null, false, immune, preventFatigue);
            else
            {
                var canFind = beast is { Minimum: > 0 } ? new[] { true } : beast?.Maximum == 0 || deckValue == 0 ? new[] { false } : new[] { false, true };
                foreach (var found in canFind)
                {
                    AddScenario(count, blocked, deckValue, capacity, heroHealth, heroArmor, fatigueBefore, found, false, immune, preventFatigue);
                    if (!found && deckValue == 0 && count > 0 && !blocked)
                    {
                        uncertainTargetedEmpty = true;
                        AddScenario(count, blocked, deckValue, capacity, heroHealth, heroArmor, fatigueBefore, false, true, immune, preventFatigue);
                    }
                }
            }
        }
        if (uncertainTargetedEmpty)
            unknowns.Add("固定 build 的定向抽野兽在空牌库时是否进入普通疲劳分支尚缺实现证据；本次空库尝试和疲劳保留零至一次范围，不能宣称确定免疲劳。");
        var empty = Range(scenarios.Select(value => value.EmptyAttempts));
        if (scenarios.Any(value => value.Damage > 0) && fatigue is null) unknowns.Add("已有疲劳计数未知；只能给直接疲劳伤害下界，不能默认下一次为一点。");
        if (scenarios.Any(value => value.Removed > 0)) unknowns.Add("未选择抽到或满手损失的牌 ID、实体或费用；不得用牌库组成或印刷费用构造新手牌动作。");
        assumptions.Add("计数指本体成功离手后的一次直接战吼；支付、反制、重复战吼、额外抽牌、施放时抽取、入手及受伤触发未在这里展开。");
        assumptions.Add("各字段为同一组直接结算分支的边界，不能将不同字段的最有利端点拼成一条实际序列；未知牌序不会被补为任意实体顺序。");
        coverage.AddRange(["source-leaves-hand-before-draw", "direct-deck-removal-and-hand-capacity", "ordinary-increasing-fatigue-packets", "stop-direct-draw-at-hero-death"]);
        var damage = Range(scenarios.Select(value => value.Damage));
        if (fatigue is null && scenarios.Any(value => value.Damage > 0)) damage = damage with { Maximum = null };
        var afterFatigue = Range(scenarios.Select(value => value.FatigueAfter));
        if (fatigue is null) afterFatigue = afterFatigue with { Maximum = null };
        return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, sourceEntityId, sourceId, kind, true,
            requested, Range(scenarios.Select(value => value.Removed)), Range(scenarios.Select(value => value.Added)),
            Range(scenarios.Select(value => value.Burned)), empty, damage,
            hand.HasValue ? Range(scenarios.Select(value => value.HandAfter)) : null,
            deck.HasValue ? Range(scenarios.Select(value => value.DeckAfter)) : new(0, null), afterFatigue,
            health.HasValue ? Range(scenarios.Select(value => value.HealthAfter)) : null,
            armor.HasValue ? Range(scenarios.Select(value => value.ArmorAfter)) : null,
            scenarios.Any(value => value.Dead), ReadOnly(unknowns), ReadOnly(assumptions), ReadOnly(coverage));

        void AddScenario(int count, bool blocked, long initialDeck, long capacity, long initialHealth,
            long initialArmor, long initialFatigue, bool? targetedFound, bool targetedFatigue, bool immune, bool preventFatigue)
        {
            long removed = 0, added = 0, burned = 0, failed = 0, damage = 0;
            var d = initialDeck; var hp = initialHealth; var ar = initialArmor; var counter = initialFatigue;
            if (!blocked)
                for (var index = 0; index < count && hp > 0; index++)
                {
                    var hasCard = targetedFound ?? (d > 0);
                    if (hasCard && d > 0)
                    {
                        d--; removed++;
                        if (added < capacity) added++; else burned++;
                    }
                    else if (targetedFound is null || targetedFatigue)
                    {
                        failed++;
                        if (preventFatigue) continue;
                        counter++;
                        var packet = immune ? 0 : counter; damage += packet;
                        var absorbed = Math.Min(ar, packet); ar -= absorbed; hp = Math.Max(0, hp - (packet - absorbed));
                    }
                }
            scenarios.Add(new(removed, added, burned, failed, damage, (hand ?? 0) + added, d, counter, hp, ar, hp == 0));
        }

        int? ObservedCount(string tag, string zone)
        {
            var observed = Tag(player, tag); var actual = snapshot.Entities.Values.Count(entity => entity.Kind == EntityKind.Card && entity.ControllerId == snapshot.LocalPlayerId && entity.Zone == zone);
            if (player.Tags.ContainsKey(tag) && (observed is null or < 0 || observed != actual))
            { unknowns.Add(zone == "HAND" ? "手牌数标签与已观察实体不一致，未采用任何一方作为精确手牌数。" : "牌库数标签与已观察实体不一致，未采用任何一方作为精确牌库数。"); return null; }
            return observed ?? actual;
        }
        int? Nonnegative(int? value, string label)
        { if (value is < 0) { unknowns.Add(label + "为负，按未知处理。"); return null; } return value; }
        EntitySnapshot? OwnHero()
        {
            var heroes = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.ControllerId == snapshot.LocalPlayerId && entity.Zone == "PLAY" &&
                entity.Tags.GetValueOrDefault("CARDTYPE") is "HERO" or "3").ToArray();
            if (heroes.Length != 1 || player.GetIntTag("HERO_ENTITY") is { } link && link != heroes[0].Id) return null;
            return heroes[0];
        }
        int? ObservedHealth(EntitySnapshot? entity)
        {
            if (entity?.GetIntTag("HEALTH") is not (> 0 and var maximumHealth) || entity.GetIntTag("DAMAGE") is not (>= 0 and var damageTaken) || damageTaken > maximumHealth) return null;
            return maximumHealth - damageTaken;
        }
        DrawNumberRange? BeastCounts()
        {
            var tracking = snapshot.DeckTracking;
            if (beforeDraw?.DeckCompositionStillMatchesSnapshot == false || tracking is null || tracking.MatchId != snapshot.MatchId ||
                tracking.StateVersion != snapshot.StateVersion || tracking.CardBuild != "255790" || tracking.KnownRemaining is null ||
                tracking.TotalCards is not (>= 0 and var total) || deck is null || total != deck || tracking.UnknownCount < 0 ||
                tracking.KnownRemaining.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0) ||
                tracking.KnownRemaining.Values.Sum(value => (long)value) + tracking.UnknownCount != total ||
                tracking.IsExact && tracking.UnknownCount != 0)
            { unknowns.Add("没有匹配当前牌库数量、对局、状态与固定版本的可靠组成，抽到野兽的数量保持零至一张。"); return deck == 0 ? new(0, 0) : null; }
            long beasts = 0, nonBeasts = 0;
            foreach (var pair in tracking.KnownRemaining)
            {
                var classification = ClassifyBeast(pair.Key, cards);
                if (classification == true) beasts += pair.Value;
                else if (classification == false) nonBeasts += pair.Value;
            }
            coverage.Add("matching-deck-composition-beast-lower-and-upper-bounds");
            return new(beasts, total - nonBeasts);
        }
        DrawResolutionOutcome Unresolved() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            sourceEntityId, sourceId, kind, false, requested, unknown, unknown, unknown, unknown, unknown,
            null, null, null, null, null, false, ReadOnly(unknowns), ReadOnly(assumptions), ReadOnly(coverage));
    }

    private static DrawNumberRange Range(IEnumerable<long> values)
    { var array = values.ToArray(); return new(array.Min(), array.Max()); }
    private static IReadOnlyList<string> ReadOnly(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
    private static bool? And(bool? first, bool? second) => first == false || second == false ? false : first == true && second == true ? true : null;
    private static bool? Or(bool? first, bool? second) => first == true || second == true ? true : first == false && second == false ? false : null;
    private static bool? Flag(EntitySnapshot entity, string tag, bool? absent = null, string? numericAlias = null) =>
        entity.Tags.ContainsKey(tag) || numericAlias is not null && entity.Tags.ContainsKey(numericAlias)
            ? Tag(entity, tag, numericAlias) is { } value && value >= 0 ? value > 0 : null : absent;
    private static int? Tag(EntitySnapshot entity, string tag, string? numericAlias = null)
    {
        var value = entity.GetIntTag(tag); var numeric = numericAlias is not null ? entity.GetIntTag(numericAlias) : null;
        if (entity.Tags.ContainsKey(tag) && value is null || numericAlias is not null && entity.Tags.ContainsKey(numericAlias) && numeric is null) return null;
        return value.HasValue && numeric.HasValue && value != numeric ? null : value ?? numeric;
    }
    private static bool AuditedSource(CardRecord card)
    {
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson);
            var root = raw.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.GetProperty("id").GetString() == card.Id &&
                root.GetProperty("dbfId").TryGetInt32(out var id) && id == card.DbfId && root.GetProperty("type").GetString() == "MINION" &&
                root.GetProperty("mechanics").EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "BATTLECRY");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
    }
    private static bool? ClassifyBeast(string id, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (!cards.TryGetValue(id, out var card) || card is null || card.Id != id) return null;
        if (card.Type != "MINION") return string.IsNullOrWhiteSpace(card.Type) ? null : false;
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson); var root = raw.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var rawId) || rawId.ValueKind != JsonValueKind.String || rawId.GetString() != id) return null;
            var races = new List<string>();
            if (root.TryGetProperty("race", out var race)) { if (race.ValueKind != JsonValueKind.String) return null; races.Add(race.GetString()!); }
            if (root.TryGetProperty("races", out var list))
            { if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return null; races.AddRange(list.EnumerateArray().Select(value => value.GetString()!)); }
            return races.Any(value => value is "BEAST" or "ALL");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return null; }
    }
}
