using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record MapFollowupOption(int EntityId, string CardId, string Name, int OriginalOptionEntityId);

/// <summary>An observed Map choice and its direct hand quantity; never a new entity or authoritative next frame.</summary>
public sealed record MapFollowupOutcome(string MatchId, long StateVersion, long EventSequence, int MapSourceEntityId,
    string Stage, bool ResolvesEffects, bool? FollowupConditionMet, int? InitialChoiceId, int? InitialSelectedEntityId,
    string? InitialSelectedCardId, long? InitialSelectedPlayedAtEventSequence, int? CurrentChoiceId,
    int? SelectedOptionEntityId, string? SelectedCardId, string? SelectedCardName, bool? AddedToHand,
    int? HandCountBefore, int? HandCountAfter, int? HandLimit, IReadOnlyList<MapFollowupOption> RemainingOptions,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public int? TurnNumber { get; init; }
    public long? InitialMapPlayedAtEventSequence { get; init; }
    public long? InitialChosenAtEventSequence { get; init; }
    public bool CanEvaluateFollowupActions => false;
}

/// <summary>
/// Build 255790 TLC_824/dbf117596 and its one-turn enchant TLC_824e/dbf117597 explicitly say
/// to choose one of the other two options after playing the discovered card this turn.
/// HDT OddMap.cs filters native odd-Attack Beasts; ChoicesHandler attaches actual chosen entity IDs.
/// https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/7c0550fe8388bf45e0f3dbc5d2ff25ada2e1a3ae/Hearthstone%20Deck%20Tracker/Hearthstone/RelatedCardsSystem/Cards/Hunter/OddMap.cs
/// Neither supplies a general candidate-to-new-hand-entity mapping. This helper accepts only same-ID evidence,
/// not a same-name/CardId or guessed CREATOR connection. A second offer must already exist.
/// </summary>
public static class MapFollowupRules
{
    public static MapFollowupOutcome Evaluate(GameSnapshot snapshot, int mapSourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, int? selectedOptionEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        var remaining = new List<MapFollowupOption>(); var stage = "Unconfirmed"; bool resolves = false; bool? condition = null;
        int? initialId = null, initialEntity = null, currentId = null, chosenId = null, turnNumber = null;
        string? initialCard = null, chosenCard = null, chosenName = null;
        long? playedAt = null, mapPlayedAt = null, chosenAt = null;
        bool? added = null; int? handBefore = null, handAfter = null, handLimit = null;
        if (snapshot.ClientBuild != "255790" || snapshot.IsSpectating || snapshot.LocalPlayerId is not (1 or 2) ||
            !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable ||
            !snapshot.Quality.HasLocalPerspective || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Phase is not (GamePhase.Main or GamePhase.Choice) ||
            snapshot.Entities.Any(pair => !Valid(pair.Value) || pair.Key != pair.Value.Id))
        { unknowns.Add("需要固定 build 255790、完整稳定的非观战我方视角，未展开地图追加选择。"); return Result(); }
        var local = snapshot.LocalPlayerId.Value;
        var players = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).ToArray();
        if (!players.Select(player => player.GetIntTag("PLAYER_ID")).Order().SequenceEqual(new int?[] { 1, 2 }) ||
            !snapshot.Entities.TryGetValue(mapSourceEntityId, out var source) || !Own(source, local) || source.CardId != "TLC_824" ||
            source.Zone is not ("PLAY" or "GRAVEYARD") || !Type(source, "SPELL", "5") ||
            AssembledCardText.RequiresCustomText(source.Tags) || !FixedMap(cards))
        { unknowns.Add("地图的版本资料、确切来源、已使用区域、归属或原生文字未确认，不能按基础 ID 推导。"); return Result(); }
        var current = snapshot.Choice;
        if (current is not null)
        {
            if (ChoiceVisibility.ConfirmedOwn(snapshot) is not { } own || own.Id != current.Id || snapshot.Phase != GamePhase.Choice ||
                snapshot.ActivePlayerId != local || current.Type != "GENERAL" || current.SourceEntityId != source.Id ||
                current.SourceCardId != "TLC_824" || current.CountMin != 1 || current.CountMax != 1 ||
                current.OfferedAtEventSequence <= 0 || current.OfferedAtEventSequence > snapshot.EventSequence ||
                !Options(current.Options, local) || current.Options.Count is not (2 or 3) ||
                current.Options.Any(option => !AuditedOption(option, snapshot.Entities.GetValueOrDefault(option.EntityId), cards, local, true)))
            { unknowns.Add("当前地图选择的来源、玩家、候选数量或原生奇数攻击野兽身份冲突，未当作新的发现重发卡牌。"); return Result(); }
            if (selectedOptionEntityId is { } selected && !current.Options.Any(option => option.EntityId == selected))
            { unknowns.Add("所选实体不在当前地图候选中，未产生入手后果。"); return Result(); }
            currentId = current.Id;
            if (AlreadyChosenCurrent(snapshot, current.Id))
            { stage = "FollowupAlreadyChosen"; unknowns.Add("当前选择已有实际已选记录，不能因残留选择界面再次入手。"); return Result(); }
            if (current.Options.Count == 3)
            {
                stage = "InitialChoice"; initialId = current.Id;
                coverage.AddRange(["TLC_824-audited-initial-odd-attack-beast-options", "TLC_824-initial-selection-is-one-card"]);
                unknowns.Add("首选实际入手实体及本回合使用证据尚未发生，不提前创建追加选择或第二张牌。");
                ProjectSelection(current); return Result();
            }
        }
        else if (selectedOptionEntityId.HasValue)
        { unknowns.Add("没有当前已提供的地图选择，不能按历史候选直接发牌。"); return Result(); }

        if (!Histories(snapshot, out var choices, out var turns))
        { unknowns.Add("缺少与当前帧绑定且无缺口的实际已选与真实回合记录，不能从同 CardId 推定地图给出的实体。"); return Result(); }
        var candidates = choices.Entries.Where(entry => entry.PlayerId == local && entry.SourceEntityId == source.Id &&
            entry.SourceCardId == "TLC_824" && entry.Options is { Count: 3 } &&
            (current is null || entry.ChosenAtEventSequence < current.OfferedAtEventSequence) &&
            ValidInitial(entry, cards, local)).ToArray();
        if (current is not null)
            candidates = candidates.Where(entry => SameRemaining(entry, current.Options)).ToArray();
        if (candidates.Length != 1)
        { unknowns.Add("无法将本次追加选择唯一连接到该地图某一次完整的实际首选；不按最近同名牌或全局历史猜测。"); return Result(); }
        var first = candidates[0]; var selectedFirst = first.Options.Single(option => option.EntityId == first.ChosenEntityIds[0]);
        var mapWindows = turns.Turns.Where(turn => turn.PlayerId == local && Contains(turn, first.OfferedAtEventSequence) &&
            Contains(turn, first.ChosenAtEventSequence)).ToArray();
        if (mapWindows.Length != 1)
        { unknowns.Add("首选提供与实际选中事件未同处一个已观察的己方回合窗口，不能使用全局 turn 差值补足。"); return Result(); }
        var window = mapWindows[0];
        var mapPlay = window.Plays.Where(play => play.AtPlay.Id == source.Id && play.Sequence < first.OfferedAtEventSequence)
            .OrderByDescending(play => play.Sequence).FirstOrDefault();
        if (mapPlay is null || !Own(mapPlay.AtPlay, local) || mapPlay.AtPlay.CardId != "TLC_824" ||
            mapPlay.AtPlay.Zone != "HAND" || !FrozenType(mapPlay.AtPlay, "SPELL", "5") ||
            AssembledCardText.RequiresCustomText(mapPlay.AtPlay.Tags) || choices.TruncatedThroughSequence >= mapPlay.Sequence ||
            turns.TruncatedThroughSequence >= window.StartedAtEventSequence)
        { unknowns.Add("没有先于首次提供候选、身份当时已冻结的实际地图 HAND 使用证据，或相关窗口已截断。"); return Result(); }
        initialId = first.ChoiceId; initialEntity = selectedFirst.EntityId; initialCard = selectedFirst.CardId; chosenAt = first.ChosenAtEventSequence;
        mapPlayedAt = mapPlay.Sequence; turnNumber = window.TurnNumber;
        var playsOfSelectedId = window.Plays.Where(play => play.AtPlay.Id == selectedFirst.EntityId && play.Sequence > first.ChosenAtEventSequence)
            .OrderBy(play => play.Sequence).ToArray();
        var firstPlay = playsOfSelectedId.FirstOrDefault();
        if (firstPlay is not null && (firstPlay.AtPlay.CardId != selectedFirst.CardId || !Own(firstPlay.AtPlay, local) ||
            !FrozenType(firstPlay.AtPlay, "MINION", "4") || firstPlay.AtPlay.Zone != "HAND" ||
            current is not null && firstPlay.Sequence >= current.OfferedAtEventSequence))
        { unknowns.Add("实际首选实体第一次再使用时身份、归属、随从类型或时序冲突，不能回退到更晚同名记录。"); return Result(); }
        if (firstPlay is null)
        {
            if (window.EndedAtEventSequence.HasValue && window.IsComplete)
            { stage = "ConditionNotMet"; condition = false; coverage.Add("TLC_824-complete-original-turn-without-selected-entity-play"); }
            else { stage = "AwaitingSelectedCardPlay"; unknowns.Add("尚无首选同一实体在原回合内的实际 HAND 使用记录；同名的另一张牌不满足这一连接。"); }
            if (current is not null) { stage = "Unconfirmed"; condition = null; unknowns.Add("第二次选择虽已提供，但缺少首选确切实体的实际使用证据，不能推导其入手。"); }
            return Result();
        }
        initialCard = selectedFirst.CardId; playedAt = firstPlay.Sequence; condition = true;
        coverage.AddRange(["TLC_824-actual-EntitiesChosen-bound-by-entity-id", "TLC_824-selected-hand-play-in-original-actual-own-turn",
            "TLC_824-no-same-card-id-or-creator-inference"]);
        var completed = choices.Entries.Where(entry => entry.PlayerId == local && entry.SourceEntityId == source.Id &&
            entry.SourceCardId == "TLC_824" && entry.OfferedAtEventSequence > firstPlay.Sequence &&
            entry.ChoiceId != first.ChoiceId && entry.Type == "GENERAL" && entry.CountMin == 1 && entry.CountMax == 1 &&
            entry.ChosenEntityIds is { Count: 1 } && Options(entry.Options, local) && entry.Options.Count == 2 && SameRemaining(first, entry.Options)).ToArray();
        if (completed.Length > 0)
        {
            stage = "FollowupAlreadyChosen";
            unknowns.Add("已记录该原始剩余两项的后续实际已选，追加机会只消费一次，不再次发牌。"); return Result();
        }
        if (current is null)
        {
            if (window.EndedAtEventSequence.HasValue)
            {
                stage = "FollowupWindowEnded";
                unknowns.Add("首选曾在原回合实际使用，但该回合已经结束；不能在稍后的回合凭历史创建新的追加选择。"); return Result();
            }
            stage = "AwaitingFollowupOffer";
            unknowns.Add("已证实首选在原己方回合被实际使用，等待日志真正提供追加选择；不把历史剩余项直接变成当前候选或手牌。");
            return Result();
        }
        if (snapshot.Turn != window.TurnNumber || snapshot.ActivePlayerId != local || window.EndedAtEventSequence.HasValue ||
            !Contains(window, current.OfferedAtEventSequence) || current.OfferedAtEventSequence <= firstPlay.Sequence)
        { stage = "Unconfirmed"; condition = null; unknowns.Add("当前追加提供不在首选的同一个真实己方回合窗口内，不能跨回合复用条件。"); return Result(); }
        stage = "FollowupChoice";
        foreach (var option in current.Options)
        {
            var card = cards[option.CardId!]; remaining.Add(new(option.EntityId, option.CardId!, card.Name, option.EntityId));
        }
        coverage.AddRange(["TLC_824-current-followup-is-exact-original-two-remaining-entities", "TLC_824-followup-selects-one-not-new-discover-pool"]);
        ProjectSelection(current);
        return Result();

        void ProjectSelection(CurrentChoiceSnapshot choice)
        {
            if (selectedOptionEntityId is not { } selected) return;
            var option = choice.Options.Single(option => option.EntityId == selected); var card = cards[option.CardId!];
            chosenId = selected; chosenCard = option.CardId; chosenName = card.Name; resolves = true;
            var player = players.Single(entity => entity.GetIntTag("PLAYER_ID") == local);
            var actual = snapshot.Hand.Count(); var reported = Int(player, "NUM_CARDS_IN_HAND");
            var countersOwned = !player.Tags.ContainsKey("CONTROLLER") && !player.Tags.ContainsKey("50") || Int(player, "CONTROLLER", "50") == local;
            handBefore = !countersOwned || player.Tags.ContainsKey("NUM_CARDS_IN_HAND") && (reported is null or < 0 || reported != actual) ? null : actual;
            handLimit = countersOwned && Int(player, "MAXHANDSIZE", "28") is >= 0 and var limit ? limit : null;
            added = handBefore.HasValue && handLimit.HasValue ? handBefore.Value < handLimit.Value : null;
            handAfter = added switch { true => handBefore + 1, false => handBefore, _ => null };
            if (handBefore is null || handLimit is null) unknowns.Add("当前手牌数量或上限缺失/冲突，直接入手与满手损失保持未知，不默认十张。");
            if (added == false) assumptions.Add("当前手牌已满，本次选中不增加手牌；只记录该一次直接后果，不虚构烧牌实体或最终区域。");
            assumptions.Add("只将本次已提供选项中的一张加入手牌；不重发首选、不授予两张剩余牌、不创建实际新实体或最终费用。");
            unknowns.Add("实际入手实体、当前费用和其他入手触发仍须选择后的真实观测，预览不能接续出牌或证明斩杀。");
            coverage.Add("TLC_824-one-current-selection-one-direct-hand-capacity-projection");
        }
        MapFollowupOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, mapSourceEntityId,
            stage, resolves, condition, initialId, initialEntity, initialCard, playedAt, currentId, chosenId, chosenCard, chosenName,
            added, handBefore, handAfter, handLimit, Array.AsReadOnly(remaining.ToArray()), Freeze(unknowns), Freeze(assumptions), Freeze(coverage))
            { TurnNumber = turnNumber, InitialMapPlayedAtEventSequence = mapPlayedAt, InitialChosenAtEventSequence = chosenAt };
    }

    private static bool Histories(GameSnapshot snapshot, out ChoiceHistorySnapshot choices, out TurnHistorySnapshot turns)
    {
        choices = snapshot.ChoiceHistory!; turns = snapshot.TurnHistory!;
        if (choices is null || turns is null || choices.MatchId != snapshot.MatchId || turns.MatchId != snapshot.MatchId ||
            choices.StateVersion != snapshot.StateVersion || turns.StateVersion != snapshot.StateVersion ||
            choices.EventSequence != snapshot.EventSequence || turns.EventSequence != snapshot.EventSequence ||
            !choices.IsComplete || !turns.IsComplete || choices.Warnings is not { Count: 0 } || turns.Warnings is not { Count: 0 } ||
            choices.Entries is null || turns.Turns is not { Count: > 0 } || choices.TruncatedThroughSequence < 0 || turns.TruncatedThroughSequence < 0 ||
            choices.Entries.Any(entry => entry is null || !entry.IsComplete || entry.OfferedAtEventSequence <= 0 ||
                entry.ChosenAtEventSequence <= entry.OfferedAtEventSequence || entry.ChosenAtEventSequence > snapshot.EventSequence ||
                entry.Options is null || entry.Options.Any(option => option is null || option.EntityId <= 0) ||
                entry.Options.Select(option => option.EntityId).Distinct().Count() != entry.Options.Count ||
                entry.CountMin < 0 || entry.CountMax < entry.CountMin || entry.CountMax > entry.Options.Count ||
                entry.ChosenEntityIds is null || entry.ChosenEntityIds.Count < entry.CountMin || entry.ChosenEntityIds.Count > entry.CountMax ||
                entry.ChosenEntityIds.Distinct().Count() != entry.ChosenEntityIds.Count ||
                entry.ChosenEntityIds.Any(id => !entry.Options.Any(option => option.EntityId == id))) ||
            choices.Entries.Select(entry => entry.ChoiceId).Distinct().Count() != choices.Entries.Count) return false;
        var windows = turns.Turns.OrderBy(turn => turn?.StartedAtEventSequence).ToArray();
        if (windows.Any(turn => turn is null || turn.TurnNumber <= 0 || turn.PlayerId is not (1 or 2) || !turn.HasStartBoundary ||
            turn.StartedAtEventSequence <= 0 || turn.StartedAtEventSequence > snapshot.EventSequence ||
            turn.EndedAtEventSequence is { } end && (end <= turn.StartedAtEventSequence || end > snapshot.EventSequence) ||
            turn.EndedAtEventSequence.HasValue && !turn.IsComplete ||
            turn.Plays is null || turn.Plays.Any(play => play is null || !Valid(play.AtPlay) || play.AtPlay.Kind != EntityKind.Card ||
                play.Sequence <= turn.StartedAtEventSequence || play.Sequence > snapshot.EventSequence ||
                turn.EndedAtEventSequence is { } closed && play.Sequence >= closed || play.AtPlay.Zone != "HAND" ||
                play.AtPlay.ControllerId is { } owner && owner != turn.PlayerId)) ||
            windows.Select(turn => turn.StartedAtEventSequence).Distinct().Count() != windows.Length ||
            windows.SelectMany(turn => turn.Plays).Select(play => play.Sequence).Distinct().Count() != windows.Sum(turn => turn.Plays.Count)) return false;
        for (var index = 1; index < windows.Length; index++)
            if (windows[index].TurnNumber != windows[index - 1].TurnNumber + 1 || windows[index - 1].EndedAtEventSequence is not { } end ||
                end >= windows[index].StartedAtEventSequence) return false;
        return windows[^1].TurnNumber == snapshot.Turn && windows[^1].PlayerId == snapshot.ActivePlayerId &&
            windows[^1].EndedAtEventSequence is null;
    }
    private static bool ValidInitial(ChoiceHistoryEntry entry, IReadOnlyDictionary<string, CardRecord> cards, int local) =>
        entry.IsComplete && entry.Type == "GENERAL" && entry.CountMin == 1 && entry.CountMax == 1 && Options(entry.Options, local) &&
        entry.ChosenEntityIds is { Count: 1 } && entry.Options.Any(option => option.EntityId == entry.ChosenEntityIds[0]) &&
        entry.OptionEntities is not null && entry.OptionEntities.All(entity => Valid(entity) &&
            entry.Options.Any(option => option.EntityId == entity.Id)) &&
        entry.OptionEntities.Select(entity => entity.Id).Distinct().Count() == entry.OptionEntities.Count &&
        entry.Options.All(option => AuditedOption(option, entry.OptionEntities.SingleOrDefault(entity => entity.Id == option.EntityId), cards, local, true));
    private static bool SameRemaining(ChoiceHistoryEntry first, IReadOnlyList<ChoiceOptionSnapshot> offered)
    {
        if (first.ChosenEntityIds is not { Count: 1 } || offered.Any(option => option is null)) return false;
        var expected = first.Options.Where(option => option.EntityId != first.ChosenEntityIds[0]).ToArray();
        return expected.Length == 2 && offered.Count == 2 && expected.All(option =>
            offered.Count(current => current.EntityId == option.EntityId && current.CardId == option.CardId &&
                current.ControllerId == option.ControllerId) == 1);
    }
    private static bool AlreadyChosenCurrent(GameSnapshot snapshot, int choiceId) => snapshot.ChoiceHistory is { } history &&
        history.MatchId == snapshot.MatchId && history.StateVersion == snapshot.StateVersion && history.EventSequence == snapshot.EventSequence &&
        history.Entries is not null && history.Entries.Any(entry => entry is not null && entry.ChoiceId == choiceId);
    private static bool Options(IReadOnlyList<ChoiceOptionSnapshot>? options, int local) => options is not null &&
        options.All(option => option is not null && option.EntityId > 0 && !string.IsNullOrWhiteSpace(option.CardId) && option.ControllerId == local) &&
        options.Select(option => option.EntityId).Distinct().Count() == options.Count &&
        options.Select(option => option.Position).Order().SequenceEqual(Enumerable.Range(0, options.Count).Select(index => (int?)index));
    private static bool AuditedOption(ChoiceOptionSnapshot option, EntitySnapshot? entity, IReadOnlyDictionary<string, CardRecord> cards, int local, bool unplayed)
    {
        if (entity is not null)
        {
            if (!Valid(entity) || entity.Id != option.EntityId || entity.Kind is EntityKind.Game or EntityKind.Player ||
                !Own(entity with { Kind = EntityKind.Card }, local) || unplayed && entity.Zone is not ("SETASIDE" or "REMOVEDFROMGAME")) return false;
            // Explicit offer identity can be known before the underlying entity has a bound CardId.
            // Its residual type, race, half markers and numbers belong to no confirmed card and cannot be reused.
            if (entity.CardId is { } observed && (observed != option.CardId || entity.Kind != EntityKind.Card ||
                !Type(entity, "MINION", "4") || !Race(entity) || AssembledCardText.RequiresCustomText(entity.Tags))) return false;
        }
        if (option.CardId is not { } id || !cards.TryGetValue(id, out var card) || card is null || card.Id != id || card.Type != "MINION") return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return RawCard(card, raw) && raw.GetProperty("attack").TryGetInt32(out var attack) && attack >= 0 && attack % 2 == 1 &&
                (raw.TryGetProperty("race", out var primary) && primary.GetString() is "BEAST" or "ALL" ||
                 raw.TryGetProperty("races", out var races) && races.ValueKind == JsonValueKind.Array && races.EnumerateArray().Any(race => race.GetString() is "BEAST" or "ALL"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { return false; }
    }
    private static bool FixedMap(IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (!NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, "TLC_824", cards.GetValueOrDefault("TLC_824"))) return false;
        if (!cards.TryGetValue("TLC_824", out var card) || card is null || card.Id != "TLC_824" || card.DbfId != 117596 || card.Type != "SPELL" ||
            card.Text != "<b>发现</b>一张攻击力为奇数的野兽牌，如果你在本回合中使用该牌，再从其余选项中选择一张。") return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return RawCard(card, raw) && raw.GetProperty("mechanics").EnumerateArray().Any(value => value.GetString() == "DISCOVER");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { return false; }
    }
    private static bool RawCard(CardRecord card, JsonElement raw) => raw.ValueKind == JsonValueKind.Object &&
        raw.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == raw.EnumerateObject().Count() &&
        raw.GetProperty("id").GetString() == card.Id &&
        raw.GetProperty("dbfId").GetInt32() == card.DbfId && raw.GetProperty("type").GetString() == card.Type && raw.GetProperty("text").GetString() == card.Text;
    private static bool Valid(EntitySnapshot? entity) => entity is not null && entity.Tags is not null;
    private static bool Type(EntitySnapshot entity, string named, string number) =>
        (!entity.Tags.TryGetValue("CARDTYPE", out var type) || type == named || type == number) &&
        (!entity.Tags.TryGetValue("202", out var alias) || alias == named || alias == number);
    private static bool FrozenType(EntitySnapshot entity, string named, string number) =>
        (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) && Type(entity, named, number);
    private static bool Race(EntitySnapshot entity) => new[] { "CARDRACE", "200" }.All(key =>
        !entity.Tags.TryGetValue(key, out var race) || race is "BEAST" or "20" or "ALL" or "26");
    private static bool Own(EntitySnapshot entity, int local) => entity.Kind == EntityKind.Card && entity.ControllerId == local &&
        (!entity.Tags.ContainsKey("50") || Int(entity, "CONTROLLER", "50") == local) &&
        (!entity.Tags.ContainsKey("ENTITY_ID") && !entity.Tags.ContainsKey("53") || Int(entity, "ENTITY_ID", "53") == entity.Id) &&
        (!entity.Tags.TryGetValue("49", out var zone) || ZoneNumber(entity.Zone) is { } expected && (zone == entity.Zone || zone == expected));
    private static string? ZoneNumber(string? zone) => zone switch { "PLAY" => "1", "DECK" => "2", "HAND" => "3", "GRAVEYARD" => "4", "REMOVEDFROMGAME" => "5", "SETASIDE" => "6", "SECRET" => "7", _ => null };
    private static int? Int(EntitySnapshot entity, string name, string? alias = null)
    {
        var hasFirst = entity.Tags.TryGetValue(name, out var first); var hasSecond = alias is not null && entity.Tags.TryGetValue(alias, out _);
        var a = hasFirst && int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        var b = hasSecond && int.TryParse(entity.Tags[alias!], NumberStyles.Integer, CultureInfo.InvariantCulture, out var other) ? other : (int?)null;
        return hasFirst && a is null || hasSecond && b is null || hasFirst && hasSecond && a != b ? null : a ?? b;
    }
    private static bool Contains(TurnHistoryEntry turn, long sequence) => sequence > turn.StartedAtEventSequence &&
        (!turn.EndedAtEventSequence.HasValue || sequence < turn.EndedAtEventSequence.Value);
    private static IReadOnlyList<string> Freeze(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
}
