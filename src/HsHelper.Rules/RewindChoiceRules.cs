using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>A direct choice preview, never a restored timeline or an authoritative future hand entity.</summary>
public sealed record RewindChoiceResolution(string Stage, bool ResolvesEffects, bool? AddsNewHandCard,
    string? DirectCardId, string? DirectCardName, int? TargetEntityId, string? TargetCardId,
    string? TargetCardName, string? GrantedDarkGiftId, string? GrantedDarkGiftName,
    int? AttackDelta, int? HealthDelta, IReadOnlyList<string> GrantedKeywords,
    string? GrantedEffectText, int? GrantedCostReduction, string? DestinationAfterGift,
    bool KeepsTimeline, bool RequestsRewind, bool HistoryBound, IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RuleCoverage)
{
    public bool? KindredActive => null;
    public int? ResultingCost => null;
    public int? ResultingAttack => null;
    public int? ResultingHealth => null;
    public bool? RestoresSourceToHand => null;
    public int? RestoredMana => null;
    public int? RestoredHandCount => null;
    public bool CanEvaluateFollowupActions => false;
}

/// <summary>
/// Build 255790 Raptor Herald and its actually offered Dark Gifts / timeline buttons.
/// Fixed CardDefs and cards.json establish gift deltas; no random pool or candidate ordering is generated.
/// Official Rewind semantics: https://hs.blizzard.cn/news/24245210/index.html .
/// Kindred needs the previous own turn, not the previous card:
/// https://news.blizzard.com/en-us/article/24204896/adventure-calls-from-the-lost-city-of-ungoro-hearthstones-next-expansion .
/// The choice ledger contains no pre-play snapshot, so it cannot reconstruct mana, hand or board on rewind.
/// </summary>
public static class RewindChoiceRules
{
    private sealed record Gift(int DbfId, int? Attack, int? Health, int? Discount, string[] Keywords, string? Destination = null);
    // IDs are effect buttons, not minions added to hand. Only a button in the current offer is considered.
    private static readonly IReadOnlyDictionary<string, Gift> Gifts = new Dictionary<string, Gift>(StringComparer.Ordinal)
    {
        ["EDR_100t"] = new(113796, 3, null, null, ["LIFESTEAL"]),
        ["EDR_100t1"] = new(113797, 2, 2, null, ["ELUSIVE"]),
        ["EDR_100t2"] = new(113798, -2, null, 2, []),
        ["EDR_100t3"] = new(113799, null, 4, null, ["TAUNT"]),
        ["EDR_100t4"] = new(113800, null, null, null, ["DEATHRATTLE"]),
        ["EDR_100t5"] = new(113801, null, null, null, []),
        ["EDR_100t6"] = new(113802, null, null, null, ["CHARGE"]),
        ["EDR_100t7"] = new(113882, null, null, null, []),
        ["EDR_100t8"] = new(113884, 4, 5, null, [], "DeckTop"),
        ["EDR_100t9"] = new(113886, null, null, null, ["REBORN"]),
        // This is a three-hit shield, deliberately not an ordinary DIVINE_SHIELD flag.
        ["EDR_100t10"] = new(115658, null, null, null, []),
        ["EDR_100t13"] = new(120225, null, null, null, ["DIVINE_SHIELD", "WINDFURY"])
    };

    public static RewindChoiceResolution? Evaluate(GameSnapshot snapshot, EntitySnapshot source,
        ChoiceOptionSnapshot selectedOption, IReadOnlyDictionary<string, CardRecord> cards)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedOption); ArgumentNullException.ThrowIfNull(cards);
        if (snapshot.Entities.Values.Any(entity => entity is null || entity.Tags is null)) return null;
        var choice = ChoiceVisibility.ConfirmedOwn(snapshot);
        if (snapshot.ClientBuild != "255790" || snapshot.Phase != GamePhase.Choice ||
            choice is not { Type: "GENERAL", CountMin: 1, CountMax: 1 } ||
            !snapshot.Entities.TryGetValue(source.Id, out var observedSource) ||
            observedSource.Kind != EntityKind.Card || observedSource.ControllerId != snapshot.LocalPlayerId ||
            observedSource.Zone is not ("PLAY" or "GRAVEYARD") ||
            observedSource.Tags.TryGetValue("CARDTYPE", out var sourceType) && sourceType is not ("MINION" or "4") ||
            observedSource.CardId is not ("CORE_EDR_004_2026" or "CORE_EDR_004") ||
            source.CardId != observedSource.CardId || choice.SourceEntityId != source.Id ||
            choice.SourceCardId != observedSource.CardId ||
            !TryCard(observedSource.CardId, cards, out var sourceCard) || sourceCard.Type != "MINION" ||
            sourceCard.DbfId != (observedSource.CardId == "CORE_EDR_004_2026" ? 131356 : 119798))
            return null;
        source = observedSource;
        var selected = choice.Options.SingleOrDefault(option => option.EntityId == selectedOption.EntityId);
        if (selected is null || selected != selectedOption || !TryCard(selected.CardId, cards, out var card) ||
            choice.Options.Any(option => snapshot.Entities.TryGetValue(option.EntityId, out var entity) &&
                (entity.Id != option.EntityId || entity.Kind != EntityKind.Card || entity.Zone is "HAND" or "PLAY" or "GRAVEYARD")))
            return null;

        var stage = "Unknown";
        var resolves = false;
        bool? adds = null;
        string? directId = null, directName = null, targetId = null, targetName = null;
        int? targetEntityId = null, attack = null, health = null, discount = null;
        string? giftId = null, giftName = null, text = null, destination = null;
        string[] keywords = [];
        var keep = false; var rewind = false; var bound = false;
        var unknowns = new List<string>(); var coverage = new List<string>();
        if (choice.Options.Count == 2 && choice.Options.Select(option => option.CardId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["TIME_000ta", "TIME_000tb"]) && choice.Options.All(option => IsTimelineButton(option.CardId, cards)))
        {
            keep = card.Id == "TIME_000ta"; rewind = !keep;
            stage = keep ? "KeepTimeline" : "RewindTimeline";
            resolves = true;
            adds = keep ? false : null;
            coverage.Add(keep ? "TIME_000ta-keeps-current-timeline" : "TIME_000tb-requests-source-effect-rewind");
            if (rewind) unknowns.Add("已确认选择回溯这张牌的效果；台账没有打出前的完整快照，不能恢复法力、手牌、场面或生成重选候选。");
        }
        else if (choice.Options.All(option => IsBeast(option.CardId, cards,
            snapshot.Entities.GetValueOrDefault(option.EntityId))))
        {
            stage = "DiscoverBeast"; resolves = true;
            directId = card.Id; directName = card.Name;
            coverage.Add("raptor-herald-selects-offered-beast-for-dark-gift");
            unknowns.Add("仅选定本次已提供的野兽，黑暗之赐尚未选择；甜美梦境可将它置于牌库顶，不能提前宣称新增手牌或最终去向。");
        }
        else if (choice.Options.All(option => IsGift(option.CardId, cards)))
        {
            stage = "DarkGift";
            // The current gift identity is safe, but applying it requires an actual, bound previously chosen Beast.
            giftId = card.Id; giftName = card.Name;
            var prior = PreviousBeastChoice();
            if (prior is not null)
            {
                var chosen = prior.Options.Single(option => option.EntityId == prior.ChosenEntityIds[0]);
                if (snapshot.Entities.TryGetValue(chosen.EntityId, out var target) && target.Id == chosen.EntityId && target.Kind == EntityKind.Card &&
                    target.ControllerId == snapshot.LocalPlayerId && target.CardId == chosen.CardId &&
                    target.Zone is "HAND" or "SETASIDE" && IsBeast(chosen.CardId, cards, target) &&
                    TryCard(chosen.CardId, cards, out var targetCard))
                {
                    var effect = Gifts[card.Id];
                    bound = true; resolves = true;
                    targetEntityId = target.Id; targetId = targetCard.Id; targetName = targetCard.Name;
                    directId = targetCard.Id; directName = targetCard.Name;
                    attack = effect.Attack; health = effect.Health; discount = effect.Discount;
                    keywords = effect.Keywords.ToArray(); text = card.Text; destination = effect.Destination;
                    // An already observed hand card is not added again. SETASIDE -> HAND timing remains unknown.
                    adds = effect.Destination == "DeckTop" || target.Zone == "HAND" ? false : null;
                    coverage.AddRange(["dark-gift-bound-immediately-prior-chosen-beast", "dark-gift-audited-grant-deltas", card.Id + "-direct-grant"]);
                    if (effect.Destination == "DeckTop") coverage.Add("EDR_100t8-target-to-own-deck-top");
                }
                else unknowns.Add("前次确实选中的野兽未以同一实体、同一身份和我方归属出现在当前手牌或待处理区域，或当前类别/种族存在冲突；不能按卡名找另一张牌替代目标，也不能用印刷类型覆盖当前标签。");
            }
            if (!resolves) unknowns.Add("已确认所选黑暗之赐按钮，但未确认其当前目标，不能宣称已授予属性或额外效果。");
        }
        else unknowns.Add("候选集合未唯一对应已审计的野兽发现、黑暗之赐或回溯按钮阶段，不能凭位置或名称猜测阶段。");

        if (stage is "DiscoverBeast" or "DarkGift")
        {
            unknowns.Add("延系要求自己上回合曾使用同种族的牌；当前选择台账不能证明此前完整回合，不能把静态延系标签当已激活或直接授予一点减费。");
            unknowns.Add("增量和授予文字不代表最终属性、费用或权威实体；其他入手触发、复制、亡语和后续选择未展开。");
        }
        return new(stage, resolves, adds, directId, directName, targetEntityId, targetId, targetName,
            giftId, giftName, attack, health, Array.AsReadOnly(keywords), text, discount, destination,
            keep, rewind, bound, Array.AsReadOnly(unknowns.ToArray()), Array.AsReadOnly(coverage.ToArray()));

        ChoiceHistoryEntry? PreviousBeastChoice()
        {
            var history = snapshot.ChoiceHistory;
            if (choice.OfferedAtEventSequence <= 0 || choice.OfferedAtEventSequence > snapshot.EventSequence ||
                choice.PreviousOfferedAtEventSequence <= 0 || choice.PreviousOfferedAtEventSequence >= choice.OfferedAtEventSequence ||
                choice.SourceCardId != source.CardId || history is null || history.MatchId != snapshot.MatchId ||
                history.StateVersion != snapshot.StateVersion || history.EventSequence != snapshot.EventSequence ||
                !history.IsComplete || history.TruncatedThroughSequence != 0 || history.Entries is null ||
                history.Warnings is null || history.Warnings.Count != 0 || history.Entries.Any(entry => entry is null) ||
                history.Entries.Select(entry => entry.ChoiceId).Distinct().Count() != history.Entries.Count ||
                history.Entries.Select(entry => entry.OfferedAtEventSequence).Distinct().Count() != history.Entries.Count ||
                history.Entries.Any(entry => entry.ChoiceId <= 0 || entry.ChoiceId == choice.Id ||
                    entry.OfferedAtEventSequence <= 0 || entry.ChosenAtEventSequence <= entry.OfferedAtEventSequence ||
                    entry.ChosenAtEventSequence >= choice.OfferedAtEventSequence ||
                    entry.OfferedAtEventSequence > choice.PreviousOfferedAtEventSequence))
            { unknowns.Add("缺少匹配当前对局、版本、事件序号及紧邻提供时序的完整未截断选择台账，不能连接黑暗之赐目标。"); return null; }
            var previous = history.Entries.SingleOrDefault(entry => entry.OfferedAtEventSequence == choice.PreviousOfferedAtEventSequence);
            if (previous is null || !previous.IsComplete || previous.PlayerId != snapshot.LocalPlayerId ||
                previous.Type != "GENERAL" || previous.SourceEntityId != source.Id || previous.SourceCardId != source.CardId ||
                previous.CountMin != 1 || previous.CountMax != 1 || previous.ChosenEntityIds is not { Count: 1 } ||
                previous.Options is not { Count: > 0 } || previous.OptionEntities is null ||
                previous.Options.Any(option => option is null || option.EntityId <= 0 ||
                    option.ControllerId is { } owner && owner != snapshot.LocalPlayerId || !IsBeast(option.CardId, cards)) ||
                previous.Options.Select(option => option.EntityId).Distinct().Count() != previous.Options.Count ||
                !previous.Options.Any(option => option.EntityId == previous.ChosenEntityIds[0]) ||
                previous.Options.Any(option => choice.Options.Any(current => current.EntityId == option.EntityId)) ||
                previous.OptionEntities.Any(entity => entity is null) ||
                previous.OptionEntities.Select(entity => entity.Id).Distinct().Count() != previous.OptionEntities.Count ||
                previous.OptionEntities.Any(entity => !previous.Options.Any(option => option.EntityId == entity.Id) ||
                    entity.Kind != EntityKind.Card || entity.Tags is null || entity.ControllerId is { } owner && owner != snapshot.LocalPlayerId ||
                    entity.CardId is { } id && id != previous.Options.Single(option => option.EntityId == entity.Id).CardId ||
                    entity.Zone is "HAND" or "PLAY" or "GRAVEYARD" || !ObservedBeastIsCompatible(entity)))
            { unknowns.Add("紧邻前次发现的来源、野兽候选、归属或实际单选结果未完整确认，不能从所有历史候选猜测目标。"); return null; }
            return previous;
        }
    }

    private static bool TryCard(string? id, IReadOnlyDictionary<string, CardRecord> cards, out CardRecord card)
    {
        card = null!;
        return !string.IsNullOrWhiteSpace(id) && cards.TryGetValue(id, out card!) && card is not null &&
            card.Id == id && !string.IsNullOrWhiteSpace(card.Name);
    }

    private static bool IsGift(string? id, IReadOnlyDictionary<string, CardRecord> cards) =>
        id is not null && Gifts.TryGetValue(id, out var gift) && TryCard(id, cards, out var card) &&
        card.Type == "SPELL" && card.DbfId == gift.DbfId;

    private static bool IsTimelineButton(string? id, IReadOnlyDictionary<string, CardRecord> cards) =>
        TryCard(id, cards, out var card) && card.Type == "SPELL" &&
        (id == "TIME_000ta" && card.DbfId == 119589 || id == "TIME_000tb" && card.DbfId == 119636);

    private static bool ObservedBeastIsCompatible(EntitySnapshot? entity) => entity is null ||
        entity.Kind == EntityKind.Card && entity.Tags is not null &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var type) || type is "MINION" or "4") &&
        (!entity.Tags.TryGetValue("CARDRACE", out var race) || race is "BEAST" or "20" or "ALL" or "26");

    private static bool IsBeast(string? id, IReadOnlyDictionary<string, CardRecord> cards, EntitySnapshot? observed = null)
    {
        if (!TryCard(id, cards, out var card) || card.Type != "MINION" || string.IsNullOrWhiteSpace(card.RawJson) ||
            !ObservedBeastIsCompatible(observed)) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("race", out var race) && race.ValueKind == JsonValueKind.String && race.GetString() == "BEAST" ||
                 root.TryGetProperty("races", out var races) && races.ValueKind == JsonValueKind.Array &&
                    races.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "BEAST"));
        }
        catch (JsonException) { return false; }
    }
}
