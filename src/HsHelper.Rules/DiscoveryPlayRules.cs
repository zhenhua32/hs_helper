using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>Trusted runtime evidence immediately before one native effect, bound to its original observed source.</summary>
public sealed record DiscoveryPlayState(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, int? DeckCountBeforeEffect, bool? BattlecryActive)
{
    /// <summary>The host has successfully paid and moved this particular source out of HAND.</summary>
    public bool SourceHasLeftHand { get; init; }
    public bool PriorResolutionPending { get; init; }
    /// <summary>Confirmed eligible candidates for this source's current sideboard; missing is not zero or the printed three.</summary>
    public int? SideboardCandidateCount { get; init; }
}

/// <summary>One native operation's choice requirements; no candidate identities, generated hand or restored timeline.</summary>
public sealed record DiscoveryPlayOutcome(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, string? SourceCardId, string Kind, bool ResolvesEffects, bool? EffectActive,
    bool? RequiresChoice, int? DeckCountBeforeEffect, int? SideboardCandidateCount, string Scope,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool? RequiresDiscoveryChoice { get; init; }
    public bool? RewindActive { get; init; }
    public bool IsConditionalPreview { get; init; }
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
    public bool? IsLegal => null;
}

/// <summary>
/// Native build-255790 use of this deck's six Discover/Assemble/Rewind sources, including the reviewed Raptor alias.
/// Fixed CardDefs and https://api.hearthstonejson.com/v1/255790/zhCN/cards.json establish exact text and mechanics.
/// Rewind is a separate after-play keyword, not part of Battlecry:
/// https://news.blizzard.com/en-gb/article/24245210/across-the-timeways-is-now-live .
/// </summary>
public static class DiscoveryPlayRules
{
    private sealed record Definition(int DbfId, string Type, string Text, string Kind, string Scope, string[] Mechanics, bool Rewind = false);
    private readonly record struct Number(bool Present, bool Valid, int? Value);
    private static readonly IReadOnlyDictionary<string, Definition> Definitions = new Dictionary<string, Definition>(StringComparer.Ordinal)
    {
        ["CORE_DS1_184"] = new(69545, "SPELL", "从你的牌库中<b>发现</b>一张牌。", "DeckDiscover", "当前我方牌库；不抽取或生成未观察候选。", ["DISCOVER"]),
        ["TLC_824"] = new(117596, "SPELL", "<b>发现</b>一张攻击力为奇数的野兽牌，如果你在本回合中使用该牌，再从其余选项中选择一张。",
            "OddAttackBeastDiscover", "奇数攻击力野兽发现池；本回合使用所选牌后的再次选择尚待实际实体和历史绑定。", ["DISCOVER"]),
        ["EDR_888"] = new(115186, "MINION", "<b>战吼：</b><b>发现</b>一张<b>传说</b>荒野之神。如果你已<b>灌注</b>过你的英雄技能4次，则将发现的荒野之神的法力值消耗变为（1）点。",
            "WildGodDiscover", "传说荒野之神池；累计四次灌注条件及设费授予留待实际已提供候选，不当作最终费用。", ["BATTLECRY", "DISCOVER"]),
        ["JAIL_831"] = new(125998, "MINION", "构筑套牌时，选择三张私藏的野兽牌。<b>战吼：</b>从中<b>发现</b>一张，其法力值消耗减少（3）点。",
            "SideboardDiscover", "该来源实际绑定的私藏野兽边栏；不从主套牌、名称或印刷三张推测当前候选。", ["BATTLECRY", "DISCOVER"]),
        ["BE_044"] = new(128892, "MINION", "<b>战吼：</b><b>拼装</b>一张法力值消耗为（4）的随从牌。",
            "AssembleFourCostMinion", "原生四费随从拼装范围；主体、半段、生成时序、是否入手及最终费用尚待观测。", ["BATTLECRY"]),
        ["CORE_EDR_004_2026"] = Raptor(131356),
        ["CORE_EDR_004"] = Raptor(119798)
    };

    public static bool SupportsCardId(string? cardId) => cardId is not null && Definitions.ContainsKey(cardId);

    public static DiscoveryPlayOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, DiscoveryPlayState? beforeEffect = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        string? sourceId = null; var kind = "Unknown"; var scope = "来源尚未确认。";
        bool? active = null, choice = null, discovery = null, rewind = false; var resolves = false;
        int? deck = null, sideboard = null;
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        if (snapshot.ClientBuild != "255790" || local is not (1 or 2) || !snapshot.Quality.HasGameStart ||
            !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Phase != GamePhase.Main || snapshot.ActivePlayerId != local ||
            snapshot.Entities.Any(pair => pair.Value is null || pair.Value.Tags is null || pair.Key != pair.Value.Id))
        { unknowns.Add("需要 build 255790 的完整稳定我方回合，未展开原生使用效果。"); return Result(); }
        var players = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).ToArray();
        if (!players.Select(entity => entity.GetIntTag("PLAYER_ID")).Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("双方玩家实体未唯一确认。"); return Result(); }
        var player = players.Single(entity => entity.GetIntTag("PLAYER_ID") == local);
        if (!SemanticAliases(player.Tags, "CONTROLLER", "50", Parse, local, required: false))
        { unknowns.Add("我方玩家实体的归属别名冲突。"); return Result(); }
        if (beforeEffect is not null && (beforeEffect.MatchId != snapshot.MatchId || beforeEffect.StateVersion != snapshot.StateVersion ||
            beforeEffect.EventSequence != snapshot.EventSequence || beforeEffect.SourceEntityId != sourceEntityId))
        { unknowns.Add("使用覆盖未绑定当前对局、状态、事件序号和来源实体。"); return Result(); }
        if (!snapshot.Entities.TryGetValue(sourceEntityId, out var source) || source.Kind != EntityKind.Card || source.Id <= 0 ||
            source.ControllerId != local || source.CardId is null || !Definitions.TryGetValue(source.CardId, out var definition) ||
            source.Zone != "HAND" && !(beforeEffect?.SourceHasLeftHand == true && source.Zone == (definition.Type == "SPELL" ? "GRAVEYARD" : "PLAY")) ||
            !SemanticAliases(source.Tags, "CARDTYPE", "202", TypeNumber, TypeNumber(definition.Type), required: true) ||
            !SemanticAliases(source.Tags, "CONTROLLER", "50", Parse, local, required: true) ||
            !SemanticAliases(source.Tags, "ZONE", "49", ZoneNumber, ZoneNumber(source.Zone), required: true) ||
            !SemanticAliases(source.Tags, "ENTITY_ID", "53", Parse, sourceEntityId, required: false) ||
            AssembledCardText.RequiresCustomText(source.Tags))
        { unknowns.Add("来源的原生身份、我方归属、使用阶段或类型/实体别名未确认；拼装来源不能执行基础卡原生效果。"); return Result(); }
        sourceId = source.CardId;
        if (!cards.TryGetValue(sourceId, out var card) || card is null || !Audited(card, sourceId, definition))
        { unknowns.Add("来源 ID、DBF、类别、完整原生文字或机制与固定定义不一致。"); return Result(); }
        kind = definition.Kind; scope = definition.Scope;
        coverage.Add("build-255790-native-use:" + sourceId);

        if (definition.Type == "SPELL") active = true;
        else
        {
            var silence = Flag(source.Tags, "SILENCED", "188", false);
            var battlecry = Flag(source.Tags, "BATTLECRY", "218", true);
            var original = silence.Valid && battlecry.Valid ? !silence.Value!.Value && battlecry.Value!.Value : (bool?)null;
            active = beforeEffect is null ? original : beforeEffect.BattlecryActive;
            if (original is null || active == true && original == false) active = null;
            if (active is null) unknowns.Add("战吼或沉默证据缺失/非法/冲突，不能选一份有效值或用覆盖修复原始反证。");
            else if (active == false) coverage.Add("observed-battlecry-suppressed-no-native-discovery");
            if (definition.Rewind)
            {
                var current = Read(source.Tags, "REWIND", "3934");
                var used = Read(source.Tags, "USED_REWIND", "3945");
                rewind = !current.Valid || current.Value is < 0 || !used.Valid || used.Value is < 0 ? null :
                    current.Present ? current.Value > 0 : used.Value is > 0 || !silence.Valid || silence.Value == true ? null : true;
                if (rewind is null) unknowns.Add("当前回溯是否仍可用未知；已使用标记或沉默不能独自证明剩余回溯次数为零。");
                coverage.Add("raptor-rewind-is-independent-of-battlecry");
            }
        }

        bool? available = true;
        if (kind == "DeckDiscover")
        {
            var observed = ObservedDeck(snapshot, player, local.Value);
            deck = observed.Valid ? beforeEffect is null ? observed.Value : beforeEffect.DeckCountBeforeEffect : null;
            if (deck is < 0) deck = null;
            if (!observed.Valid) unknowns.Add("原始牌库计数与已观察实体或绑定台账冲突，覆盖不能修复该反证。");
            if (deck is null) unknowns.Add("当前牌库数没有可靠标签或绑定计数；零个已创建实体不证明空库。");
            available = deck.HasValue ? deck.Value > 0 : null;
            coverage.Add("tracking-current-deck-count-only-no-draw-or-fatigue");
        }
        else if (kind == "SideboardDiscover")
        {
            sideboard = beforeEffect?.SideboardCandidateCount;
            if (sideboard is < 0 or > 3) sideboard = null;
            available = sideboard.HasValue ? sideboard.Value > 0 : null;
            if (sideboard is null) unknowns.Add("当前该来源的可选边栏范围未确认；缺失不等于零，也不等于印刷的三张。");
        }
        discovery = And(active, available);
        choice = Or(discovery, rewind);
        resolves = true;
        if (discovery == false) coverage.Add("no-native-discovery-choice-for-this-operation");
        if (beforeEffect?.PriorResolutionPending == true)
            unknowns.Add("前序结算尚未确认；本次仅给条件预览，不表示实际效果或选择已经发生。");
        assumptions.Add("以来源已成功使用且只结算一次原生效果为条件；支付、入场、反制、重复战吼和其他触发由完整动作规则核验。");
        assumptions.Add("发现/拼装池仅描述范围，实际候选及当前选择以客户端观测为准；不创建新牌或声明最终费用、回溯恢复状态。待选期间需等待新局面。");
        return Result();

        DiscoveryPlayOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            sourceEntityId, sourceId, kind, resolves, active, choice, deck, sideboard, scope,
            Freeze(unknowns), Freeze(assumptions), Freeze(coverage))
            { RequiresDiscoveryChoice = discovery, RewindActive = rewind, IsConditionalPreview = beforeEffect?.PriorResolutionPending == true };
    }

    private static Definition Raptor(int dbf) => new(dbf, "MINION",
        "<b>回溯</b>。<b>战吼：</b><b>发现</b>一张具有<b>黑暗之赐</b>的野兽牌。<b>延系：</b>其法力值消耗减少（1）点。",
        "RewindDarkGiftBeastDiscover", "野兽发现、黑暗之赐和独立回溯阶段；延系条件留待绑定的真实回合历史，不恢复旧时间线。", ["BATTLECRY", "DISCOVER"], true);
    private static bool Audited(CardRecord card, string id, Definition definition)
    {
        if (!NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, id, card) || card.Id != id || card.DbfId != definition.DbfId || card.Type != definition.Type || card.Text != definition.Text) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            if (raw.ValueKind != JsonValueKind.Object || raw.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != raw.EnumerateObject().Count() ||
                raw.GetProperty("id").GetString() != id || raw.GetProperty("dbfId").GetInt32() != definition.DbfId ||
                raw.GetProperty("type").GetString() != definition.Type || raw.GetProperty("text").GetString() != definition.Text) return false;
            var mechanics = raw.GetProperty("mechanics").EnumerateArray().Select(value => value.GetString()).ToArray();
            return mechanics.Length == definition.Mechanics.Length && mechanics.ToHashSet(StringComparer.Ordinal).SetEquals(definition.Mechanics);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException) { return false; }
    }
    private static Number ObservedDeck(GameSnapshot snapshot, EntitySnapshot player, int local)
    {
        var tagged = Read(player.Tags, "NUM_CARDS_IN_DECK", "NUM_CARDS_IN_DECK");
        var entities = snapshot.Entities.Values.Count(entity => entity.Kind == EntityKind.Card && entity.ControllerId == local && entity.Zone == "DECK");
        if (!tagged.Valid || tagged.Value is < 0 || tagged.Value < entities) return new(true, false, null);
        var tracker = snapshot.DeckTracking;
        var bound = tracker is not null && tracker.MatchId == snapshot.MatchId && tracker.StateVersion == snapshot.StateVersion && tracker.CardBuild == "255790";
        if (!bound) return tagged;
        if (tracker!.KnownRemaining is null || tracker.UnknownCount < 0 ||
            tracker.KnownRemaining.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0) ||
            tracker.TotalCards is < 0 || tracker.TotalCards < entities || tagged.Value.HasValue && tracker.TotalCards.HasValue && tagged.Value != tracker.TotalCards)
            return new(true, false, null);
        var known = tracker.KnownRemaining.Sum(pair => (long)pair.Value);
        var lowerBound = known + tracker.UnknownCount;
        var effectiveTotal = tagged.Value ?? tracker.TotalCards;
        if (effectiveTotal.HasValue && lowerBound > effectiveTotal ||
            tracker.IsExact && (tracker.TotalCards is null || tracker.UnknownCount != 0 || known != tracker.TotalCards))
            return new(true, false, null);
        return tagged.Present ? tagged : new(tracker.TotalCards.HasValue, true, tracker.TotalCards);
    }
    private readonly record struct Boolean(bool Valid, bool? Value);
    private static Boolean Flag(IReadOnlyDictionary<string, string> tags, string name, string alias, bool absent)
    { var value = Read(tags, name, alias); return value.Valid && (!value.Present || value.Value is 0 or 1) ? new(true, value.Present ? value.Value == 1 : absent) : new(false, null); }
    private static Number Read(IReadOnlyDictionary<string, string> tags, string name, string alias)
    {
        var hasName = tags.TryGetValue(name, out var named); var hasAlias = tags.TryGetValue(alias, out var numeric);
        if (!hasName && !hasAlias) return new(false, true, null);
        var first = Parse(named!); var second = Parse(numeric!);
        return hasName && first is null || hasAlias && second is null || hasName && hasAlias && first != second
            ? new(true, false, null) : new(true, true, hasName ? first : second);
    }
    private static bool SemanticAliases(IReadOnlyDictionary<string, string> tags, string name, string alias,
        Func<string, int?> parse, int? expected, bool required) => expected.HasValue && (!required || tags.ContainsKey(name) || tags.ContainsKey(alias)) &&
        (!tags.TryGetValue(name, out var named) || parse(named) == expected) && (!tags.TryGetValue(alias, out var numeric) || parse(numeric) == expected);
    private static int? Parse(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static int? TypeNumber(string value) => value switch { "MINION" => 4, "SPELL" => 5, _ => Parse(value) };
    private static int? ZoneNumber(string? value) => value switch { "HAND" => 3, "PLAY" => 1, "GRAVEYARD" => 4, _ => value is null ? null : Parse(value) };
    private static bool? And(bool? first, bool? second) => first == false || second == false ? false : first == true && second == true ? true : null;
    private static bool? Or(bool? first, bool? second) => first == true || second == true ? true : first == false && second == false ? false : null;
    private static IReadOnlyList<string> Freeze(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
}
