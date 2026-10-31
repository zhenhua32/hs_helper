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
    public bool? KindredActive { get; init; }
    public int? KindredCostReduction { get; init; }
    public long? KindredSourcePlaySequence { get; init; }
    public long? KindredPreviousOwnTurnStart { get; init; }
    public long? KindredMatchingPlaySequence { get; init; }
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
        KindredEvidence? kindred = null;
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
            kindred = EvaluateKindred(snapshot, source, choice, cards);
            if (kindred.Active is null)
                unknowns.Add("缺少可绑定本次实际出牌及上个自己回合的完整时序/当时种族证据，延系保持未知；静态延系标签和当前实体身份不能补写历史。");
            else coverage.Add(kindred.Active.Value ? "raptor-kindred-prior-own-turn-type-match" :
                kindred.PreviousOwnTurnStart is null ? "raptor-kindred-first-own-turn-from-game-start" : "raptor-kindred-complete-prior-own-turn-no-match");
            unknowns.Add("延系减费只单列本来源的原生授予，不再从候选当前费用中扣减；黑暗之赐自身的减费另列，最终费用仍须等待结算日志。");
            unknowns.Add("增量和授予文字不代表最终属性、费用或权威实体；其他入手触发、复制、亡语和后续选择未展开。");
        }
        return new(stage, resolves, adds, directId, directName, targetEntityId, targetId, targetName,
            giftId, giftName, attack, health, Array.AsReadOnly(keywords), text, discount, destination,
            keep, rewind, bound, Array.AsReadOnly(unknowns.ToArray()), Array.AsReadOnly(coverage.ToArray()))
        {
            KindredActive = kindred?.Active,
            KindredCostReduction = kindred?.Active switch { true => 1, false => 0, _ => null },
            KindredSourcePlaySequence = kindred?.SourcePlaySequence,
            KindredPreviousOwnTurnStart = kindred?.PreviousOwnTurnStart,
            KindredMatchingPlaySequence = kindred?.MatchingPlaySequence
        };

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

    private sealed record KindredEvidence(bool? Active, long? SourcePlaySequence = null,
        long? PreviousOwnTurnStart = null, long? MatchingPlaySequence = null);

    private static KindredEvidence EvaluateKindred(GameSnapshot snapshot, EntitySnapshot source,
        CurrentChoiceSnapshot choice, IReadOnlyDictionary<string, CardRecord> cards)
    {
        var unknown = new KindredEvidence(null);
        var history = snapshot.TurnHistory;
        if (history is null || history.MatchId != snapshot.MatchId || history.StateVersion != snapshot.StateVersion ||
            history.EventSequence != snapshot.EventSequence || !history.IsComplete || history.TruncatedThroughSequence != 0 ||
            history.Warnings is not { Count: 0 } || history.Turns is not { Count: > 0 } ||
            snapshot.ActivePlayerId != snapshot.LocalPlayerId || choice.OfferedAtEventSequence <= 0 ||
            choice.OfferedAtEventSequence > snapshot.EventSequence || history.Turns.Any(turn => turn is null)) return unknown;
        var turns = history.Turns.OrderBy(turn => turn.StartedAtEventSequence).ToArray();
        if (turns.Any(turn => turn.TurnNumber <= 0 || turn.PlayerId is not (1 or 2) || !turn.HasStartBoundary ||
                turn.StartedAtEventSequence <= 0 || turn.StartedAtEventSequence > snapshot.EventSequence ||
                turn.EndedAtEventSequence is { } end && (end <= turn.StartedAtEventSequence || end > snapshot.EventSequence) ||
                turn.Plays is null || turn.Plays.Any(play => play is null || play.AtPlay is null || play.AtPlay.Tags is null ||
                    play.AtPlay.Id <= 0 || play.AtPlay.Kind != EntityKind.Card ||
                    play.AtPlay.Zone != "HAND" ||
                    play.AtPlay.ControllerId is { } owner && owner != turn.PlayerId ||
                    play.Sequence <= turn.StartedAtEventSequence || play.Sequence > snapshot.EventSequence ||
                    turn.EndedAtEventSequence is { } closed && play.Sequence >= closed)) ||
            turns.Select(turn => turn.StartedAtEventSequence).Distinct().Count() != turns.Length ||
            turns.SelectMany(turn => turn.Plays).Select(play => play.Sequence).Distinct().Count() != turns.Sum(turn => turn.Plays.Count)) return unknown;
        for (var index = 1; index < turns.Length; index++)
            if (turns[index].TurnNumber != turns[index - 1].TurnNumber + 1 ||
                turns[index - 1].EndedAtEventSequence is not { } priorEnd || priorEnd >= turns[index].StartedAtEventSequence) return unknown;
        var current = turns[^1];
        if (current.PlayerId != snapshot.LocalPlayerId || current.TurnNumber != snapshot.Turn || current.EndedAtEventSequence is not null ||
            current.StartedAtEventSequence >= choice.OfferedAtEventSequence) return unknown;
        // Choose the latest actual play of this entity, then check its frozen identity. Never fall back to an older matching play.
        var sourcePlay = current.Plays.Where(play => play.AtPlay.Id == source.Id).OrderByDescending(play => play.Sequence).FirstOrDefault();
        if (sourcePlay is null || sourcePlay.Sequence >= choice.OfferedAtEventSequence ||
            sourcePlay.AtPlay.CardId != source.CardId || sourcePlay.AtPlay.ControllerId != snapshot.LocalPlayerId ||
            PlayedTypes(sourcePlay.AtPlay, cards) is not { IsMinion: true } sourceTypes || sourceTypes.Types.Count == 0) return unknown;
        var previous = turns.Take(turns.Length - 1).LastOrDefault(turn => turn.PlayerId == snapshot.LocalPlayerId);
        // Only a continuous observation from actual turn one proves this is the first own turn.
        if (previous is null)
            return turns[0].TurnNumber == 1 ? new(false, sourcePlay.Sequence) : unknown;
        if (previous.EndedAtEventSequence is null) return unknown;
        var evidence = new KindredEvidence(null, sourcePlay.Sequence, previous.StartedAtEventSequence);
        var allTypesKnown = true;
        foreach (var play in previous.Plays)
        {
            var types = play.AtPlay.ControllerId == snapshot.LocalPlayerId ? PlayedTypes(play.AtPlay, cards) : null;
            if (types is null) { allTypesKnown = false; continue; }
            if (!types.IsMinion || types.Types.Count == 0) continue;
            if (types.Types.Contains("ALL") || sourceTypes.Types.Contains("ALL") || types.Types.Overlaps(sourceTypes.Types))
                return evidence with { Active = true, MatchingPlaySequence = play.Sequence };
        }
        // A complete observed own turn with every played type known proves absence; a short list never does.
        return previous.IsComplete && allTypesKnown ? evidence with { Active = false } : evidence;
    }

    private sealed record PlayedTypeEvidence(bool IsMinion, HashSet<string> Types);

    private static PlayedTypeEvidence? PlayedTypes(EntitySnapshot atPlay, IReadOnlyDictionary<string, CardRecord> cards)
    {
        var kind = atPlay.Tags.GetValueOrDefault("CARDTYPE");
        if (kind is "SPELL" or "5" or "WEAPON" or "7" or "HERO" or "3" or "LOCATION" or "39") return new(false, []);
        if (kind is not ("MINION" or "4")) return null;
        var primary = RaceName(atPlay.Tags.GetValueOrDefault("CARDRACE"));
        if (primary is "BEAST" or "ALL") return new(true, new(StringComparer.Ordinal) { primary });
        // Other observed primary types may have a native second type. Only a definition bound to the frozen
        // CardId with the same native primary may establish that second type, never a current entity lookup.
        if (primary is null || !TryCard(atPlay.CardId, cards, out var card) || card.Type != "MINION" || string.IsNullOrWhiteSpace(card.RawJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("race", out var native) ||
                native.ValueKind != JsonValueKind.String || native.GetString() != primary) return null;
            var types = new HashSet<string>(StringComparer.Ordinal) { primary };
            if (root.TryGetProperty("races", out var races))
            {
                if (races.ValueKind != JsonValueKind.Array) return null;
                foreach (var race in races.EnumerateArray())
                {
                    if (race.ValueKind != JsonValueKind.String || RaceName(race.GetString()) is not { } known) return null;
                    types.Add(known);
                }
            }
            return new(true, types);
        }
        catch (JsonException) { return null; }
    }

    private static string? RaceName(string? value) => value switch
    {
        "BEAST" or "20" => "BEAST", "ALL" or "26" => "ALL", "UNDEAD" or "11" => "UNDEAD",
        "MURLOC" or "14" => "MURLOC", "DEMON" or "15" => "DEMON", "MECHANICAL" or "17" => "MECHANICAL",
        "ELEMENTAL" or "18" => "ELEMENTAL", "TOTEM" or "21" => "TOTEM", "PIRATE" or "23" => "PIRATE",
        "DRAGON" or "24" => "DRAGON", "QUILBOAR" or "43" => "QUILBOAR", "NAGA" or "92" => "NAGA", _ => null
    };

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
