using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>Inclusive marginal bounds; endpoints from different fields are not independent branches.</summary>
public sealed record GenerationNumberRange(long Minimum, long? Maximum)
{
    public bool IsExact => Maximum == Minimum;
}

/// <summary>
/// Counts after the successfully played source has left hand, before one native generation.
/// The overlay is bound to the original observed frame and does not supply new card identities.
/// </summary>
public sealed record GenerationResolutionState(string MatchId, long StateVersion, long EventSequence,
    int? HandCountBeforeGeneration, int? MaximumHandSize);

/// <summary>One native generation's quantities only, never a new authoritative hand or playable entity.</summary>
public sealed record GenerationResolutionOutcome(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, string? SourceCardId, string Kind, bool ResolvesEffects,
    GenerationNumberRange RequestedCards, GenerationNumberRange CardsAddedToHand,
    GenerationNumberRange CardsLostToHandLimit, GenerationNumberRange? HandCountAfter,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool IsDirectEffectOnly => true;
    public bool RequiresChoice => false;
    public bool CanEvaluateFollowupActions => false;
}

/// <summary>
/// Build 255790 CORE_UNG_912: one native Battlecry requests one random Beast for the hand.
/// Fixed cards.json/CardDefs (dbf 97376) contain no variable count parameter.
/// Official card identity: https://hearthstone.blizzard.com/en-us/cards/97376-jeweled-macaw .
/// This is generation, not drawing: no deck composition, fatigue, candidate pool, identity or cost is invented.
/// BE_044 Assemble's primary creation/choice timing is deliberately outside this audited operation.
/// </summary>
public static class GenerationResolutionRules
{
    public static GenerationResolutionOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, GenerationResolutionState? beforeGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        string? sourceId = null; var kind = "Unknown";
        var unbounded = new GenerationNumberRange(0, null);
        if (snapshot.Entities.Values.Any(entity => entity is null || entity.Tags is null) ||
            snapshot.Entities.Any(pair => pair.Key != pair.Value.Id) || snapshot.ClientBuild != "255790" ||
            !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable ||
            !snapshot.Quality.HasLocalPerspective || snapshot.Quality.OpenBlockCount != 0 || snapshot.IsSpectating ||
            snapshot.LocalPlayerId is not (1 or 2) || snapshot.ActivePlayerId != snapshot.LocalPlayerId || snapshot.Phase != GamePhase.Main)
        { unknowns.Add("需要固定 build 255790、完整稳定且轮到我方的非观战局面，未展开随机生成数量。"); return Unresolved(); }
        var players = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).ToArray();
        if (!players.Select(player => player.GetIntTag("PLAYER_ID")).Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("双方玩家实体未唯一确认，不能连接我方手牌数量。"); return Unresolved(); }
        var player = players.Single(entity => entity.GetIntTag("PLAYER_ID") == snapshot.LocalPlayerId);
        if (!snapshot.Entities.TryGetValue(sourceEntityId, out var source) || source.Kind != EntityKind.Card ||
            source.ControllerId != snapshot.LocalPlayerId || source.CardId != "CORE_UNG_912" ||
            source.Zone != "HAND" && !(beforeGeneration is not null && source.Zone == "PLAY") ||
            !ObservedMinion(source))
        { unknowns.Add("未确认我方宝石鹦鹉的实体、原生随从类别或使用阶段，场上旧随从不能自行重触发战吼。"); return Unresolved(); }
        sourceId = source.CardId; kind = "RandomBeastToHand";
        if (AssembledCardText.RequiresCustomText(source.Tags))
        { unknowns.Add("来源具有拼装半段、候选标记或冲突标签，不能按基础卡 ID 展开原生生成效果。"); return Unresolved(); }
        if (!cards.TryGetValue(sourceId, out var card) || card is null || card.Id != sourceId || card.DbfId != 97376 ||
            card.Type != "MINION" || !AuditedSource(card))
        { unknowns.Add("缺少与固定 ID、DBF、随从类型和原生战吼定义一致的卡牌资料，不能用同名牌或印刷费用推测数量。"); return Unresolved(); }
        if (beforeGeneration is not null && (beforeGeneration.MatchId != snapshot.MatchId ||
            beforeGeneration.StateVersion != snapshot.StateVersion || beforeGeneration.EventSequence != snapshot.EventSequence))
        { unknowns.Add("生成前计数覆盖与当前对局、状态版本或事件序号不一致，未使用过期或未来计数。"); return Unresolved(); }

        var silenced = Flag(source, "SILENCED", "188", false);
        var battlecry = Flag(source, "BATTLECRY", "218", true);
        var active = silenced == true || battlecry == false ? false : silenced == false && battlecry == true ? true : (bool?)null;
        if (active is null) unknowns.Add("沉默或战吼属性存在缺失解析/别名冲突，原生生成保留零至一次分支。");
        int? hand;
        if (beforeGeneration is not null) hand = Nonnegative(beforeGeneration.HandCountBeforeGeneration, "生成前手牌数");
        else
        {
            var actual = snapshot.Hand.Count(); var observed = player.GetIntTag("NUM_CARDS_IN_HAND");
            if (player.Tags.ContainsKey("NUM_CARDS_IN_HAND") && (observed is null or < 0 || observed != actual))
            { hand = null; unknowns.Add("手牌数标签与实体数量冲突，未采用任一方作为精确手牌数。"); }
            else hand = actual >= 1 ? actual - 1 : null;
        }
        var maximum = beforeGeneration is not null ? Nonnegative(beforeGeneration.MaximumHandSize, "手牌上限") :
            Nonnegative(Tag(player, "MAXHANDSIZE", "28"), "手牌上限");
        var amounts = active switch { true => new[] { 1L }, false => new[] { 0L }, _ => new[] { 0L, 1L } };
        var capacities = hand is { } count && maximum is { } limit ? new[] { Math.Max(0L, (long)limit - count) } : new[] { 0L, 1L };
        var scenarios = (from amount in amounts from capacity in capacities
            let added = Math.Min(amount, capacity) select (Requested: amount, Added: added, Lost: amount - added)).ToArray();
        var requested = Range(scenarios.Select(value => value.Requested));
        if (requested.Maximum > 0)
        {
            if (hand is null) unknowns.Add("生成前手牌数量未可靠观察，入手与满手损失只给边界。");
            if (maximum is null) unknowns.Add("当前手牌上限未可靠观察，不默认十张，入手与满手损失只给边界。");
            unknowns.Add("随机野兽的候选池、实际身份、实体 ID 和最终费用未观察；不会据此创建新牌或继续模拟其出牌。");
        }
        assumptions.Add("本预览以本体已成功离手并进行一次原生战吼为条件；支付、反制、重复战吼及其他生成/入手触发未展开。");
        assumptions.Add("覆盖计数已在来源离手后，不再扣除一次本体；无覆盖时先从当前手牌数扣除本体，再检查容量。");
        assumptions.Add("这是直接置入手牌而非抽牌，不改变牌库计数或产生直接疲劳；未加入手牌只记录数量，不虚构其最终区域。各数量边界关联，不能拼接为一条实际序列。");
        coverage.AddRange(["CORE_UNG_912-native-random-beast-count-one", "source-leaves-hand-before-generation",
            "observed-hand-capacity-only", "generation-is-not-a-deck-draw", "no-generated-card-identity-or-current-cost"]);
        return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, sourceEntityId, sourceId, kind, true,
            requested, Range(scenarios.Select(value => value.Added)), Range(scenarios.Select(value => value.Lost)),
            hand.HasValue ? Range(scenarios.Select(value => (long)hand.Value + value.Added)) : null,
            Freeze(unknowns), Freeze(assumptions), Freeze(coverage));

        int? Nonnegative(int? value, string label)
        { if (value is < 0) { unknowns.Add(label + "为负，按未知处理。"); return null; } return value; }
        GenerationResolutionOutcome Unresolved() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            sourceEntityId, sourceId, kind, false, unbounded, unbounded, unbounded, null,
            Freeze(unknowns), Freeze(assumptions), Freeze(coverage));
    }

    private static GenerationNumberRange Range(IEnumerable<long> values)
    { var array = values.ToArray(); return new(array.Min(), array.Max()); }
    private static IReadOnlyList<string> Freeze(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
    private static bool ObservedMinion(EntitySnapshot entity) =>
        (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named is "MINION" or "4") &&
        (!entity.Tags.TryGetValue("202", out var numeric) || numeric is "MINION" or "4");
    private static int? Tag(EntitySnapshot entity, string name, string alias)
    {
        var first = entity.GetIntTag(name); var second = entity.GetIntTag(alias);
        if (entity.Tags.ContainsKey(name) && first is null || entity.Tags.ContainsKey(alias) && second is null ||
            first.HasValue && second.HasValue && first != second) return null;
        return first ?? second;
    }
    private static bool? Flag(EntitySnapshot entity, string name, string alias, bool absent) =>
        entity.Tags.ContainsKey(name) || entity.Tags.ContainsKey(alias)
            ? Tag(entity, name, alias) is >= 0 and var value ? value > 0 : null : absent;
    private static bool AuditedSource(CardRecord card)
    {
        if (!NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, card.Id, card)) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return raw.ValueKind == JsonValueKind.Object && raw.GetProperty("id").GetString() == card.Id &&
                raw.GetProperty("dbfId").TryGetInt32(out var dbf) && dbf == card.DbfId && raw.GetProperty("type").GetString() == "MINION" &&
                raw.GetProperty("text").GetString() == card.Text && !string.IsNullOrWhiteSpace(card.Text) &&
                raw.GetProperty("mechanics").EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "BATTLECRY");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
    }
}
