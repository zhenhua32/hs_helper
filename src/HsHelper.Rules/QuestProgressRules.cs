using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>Covered simulated progress bound to the original observation; null never borrows an earlier value.</summary>
public sealed record QuestProgressState(string MatchId, long StateVersion, long EventSequence,
    int QuestEntityId, int? Progress, int? Total)
{
    public bool PriorResolutionPending { get; init; }
    /// <summary>Runtime has successfully installed this originally HAND task and initialized its new progress to zero.</summary>
    public bool IsInstalled { get; init; }
}

public sealed record QuestProgressOutcome(string MatchId, long StateVersion, long EventSequence,
    int QuestEntityId, string? QuestCardId, int? CurrentProgress, int? Total, int? PlayedEntityId,
    bool? PlayedCardQualifies, int? Increment, int? ResultingProgress, bool? CompletesQuest,
    bool RequiresRewardChoice, bool CanProjectProgress, IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    public bool ResolvesEffects => CanProjectProgress;
    public bool IsConditionalPreview { get; init; }
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
}

/// <summary>
/// Build 255790 TLC_EVENT_400 / DBF 120648: play three Beast or Undead cards, then craft a Zombeast.
/// Uses actual QUEST_PROGRESS (534), QUEST_PROGRESS_TOTAL (535), and the card's frozen HAND evidence.
/// This describes one successfully used card after its preceding effects settle, not play legality or a reward card.
/// Fixed CardDefs/cards.json: https://hearthstone.blizzard.com/en-us/cards/120648-storm-the-gates .
/// </summary>
public static class QuestProgressRules
{
    private readonly record struct Number(bool Present, bool Valid, int? Value);

    public static QuestProgressOutcome Evaluate(GameSnapshot snapshot, int questEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, EntitySnapshot? playedFromHand = null,
        QuestProgressState? beforePlay = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var coverage = new List<string>();
        string? questCardId = null; int? progress = null, total = null, increment = null, resulting = null;
        bool? qualifies = null, completes = null; var reward = false; var project = false;
        var pending = beforePlay?.PriorResolutionPending == true;
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        if (local is not (1 or 2) || snapshot.ClientBuild != "255790" || !snapshot.Quality.HasGameStart ||
            !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Phase != GamePhase.Main || snapshot.ActivePlayerId != local ||
            snapshot.Entities.Any(pair => pair.Value is null || pair.Value.Tags is null || pair.Key != pair.Value.Id))
        { unknowns.Add("需要 build 255790 的完整稳定我方回合，未展开任务计数。"); return Result(); }
        if (beforePlay is not null && (beforePlay.MatchId != snapshot.MatchId || beforePlay.StateVersion != snapshot.StateVersion ||
            beforePlay.EventSequence != snapshot.EventSequence || beforePlay.QuestEntityId != questEntityId))
        { unknowns.Add("任务覆盖未绑定当前对局、状态、事件序号及任务实体。"); return Result(); }
        if (!snapshot.Entities.TryGetValue(questEntityId, out var quest) || quest.Id <= 0 || quest.Kind != EntityKind.Card ||
            quest.ControllerId != local || quest.Zone != "SECRET" && !(quest.Zone == "HAND" && beforePlay?.IsInstalled == true) || quest.CardId != "TLC_EVENT_400" ||
            !ObservedType(quest.Tags, "SPELL", required: true) || !AliasMatches(quest.Tags, "50", value => value == local) ||
            !AliasMatches(quest.Tags, "49", value => value == (quest.Zone == "HAND" ? 3 : 7)) ||
            !FlagCompatible(quest.Tags, "SIDE_QUEST", "1192") || AssembledCardText.RequiresCustomText(quest.Tags))
        { unknowns.Add("未确认我方 SECRET 中的原生围攻城门，或任务身份/类型/别名冲突。"); return Result(); }
        questCardId = quest.CardId;
        if (!cards.TryGetValue(questCardId, out var task) || task is null || task.Id != questCardId || task.DbfId != 120648 || task.Type != "SPELL" ||
            task.Text != "<b>支线任务：</b>使用亡灵或野兽牌，总计3张。<b>奖励：</b>制造一个自定义的僵尸兽，其法力值消耗减少（3）点。" ||
            !NativeCardDefinitions.IsAudited(snapshot.ClientBuild, questCardId, task) ||
            !Profile(task, out _, out _, out var mechanics) || !mechanics.Contains("SIDE_QUEST"))
        { unknowns.Add("缺少固定围攻城门的 ID、DBF、类别及支线任务定义。"); return Result(); }
        if (snapshot.Entities.Values.Count(entity => entity.Kind == EntityKind.Card && entity.ControllerId == local &&
            entity.Zone == "SECRET" && entity.CardId == questCardId) != (quest.Zone == "SECRET" ? 1 : 0))
        { unknowns.Add("同一支线任务在 SECRET 中不唯一，未选取一份继续结算。"); return Result(); }

        // A newly installed native sidequest starts a new counter; old HAND tags are not installed progress.
        var observedProgress = quest.Zone == "HAND" ? new Number(false, true, null) : Read(quest.Tags, "QUEST_PROGRESS", "534");
        var observedTotal = quest.Zone == "HAND" ? new Number(false, true, null) : Read(quest.Tags, "QUEST_PROGRESS_TOTAL", "535");
        if (!observedProgress.Valid || !observedTotal.Valid || observedProgress.Value is < 0 or > 3 ||
            observedTotal.Present && observedTotal.Value != 3)
        { unknowns.Add("已观察任务进度或总数非法/冲突，覆盖不能修复反证。"); return Result(); }
        progress = beforePlay is null ? observedProgress.Value : beforePlay.Progress;
        total = beforePlay is null ? observedTotal.Value : beforePlay.Total;
        if (progress is < 0 or > 3 || total.HasValue && total != 3)
        { progress = null; total = null; unknowns.Add("模拟任务计数超出原生范围。"); return Result(); }
        if (progress is null || total is null)
            unknowns.Add("当前进度或总数缺失；不补零、不用全局使用历史或印刷总数回填。");
        coverage.Add("TLC_EVENT_400-observed-progress-534-total-535");
        if (progress == 3 && total == 3)
        { resulting = 3; increment = 0; completes = true; reward = true; unknowns.Add("任务已达完成计数，奖励选择需新观测，不能再次递增或生成奖励。"); return Result(); }
        if (playedFromHand is null) return Result();
        if (!PlayedEvidenceValid(playedFromHand))
        { unknowns.Add("被使用牌缺少可信的我方 HAND 时身份、类别、归属或种族证据。"); return Result(); }
        var played = cards[playedFromHand.CardId!];
        if (NativeCardDefinitions.IsRegistered(played.Id) && !NativeCardDefinitions.IsAudited(snapshot.ClientBuild, played.Id, played))
        { unknowns.Add("参与任务条件判断的原生牌固定定义不一致，未借原ID分类或递增。"); return Result(); }
        if ((AssembledCardText.RequiresCustomText(playedFromHand.Tags) && AssembledScriptAudit.Resolve(snapshot.ClientBuild, playedFromHand.CardId, playedFromHand.Tags, cards) is null) ||
            snapshot.Entities.TryGetValue(playedFromHand.Id, out var original) && AssembledCardText.RequiresCustomText(original.Tags) &&
            AssembledScriptAudit.Resolve(snapshot.ClientBuild, original.CardId, original.Tags, cards) is null)
        { unknowns.Add("被使用牌带拼装/半段标记，未按基础卡推导任务增量。"); return Result(); }
        if (!Profile(played, out var native, out var races, out _))
        { unknowns.Add("被使用牌的固定种族定义缺失或冲突。"); return Result(); }
        var namedRace = playedFromHand.Tags.GetValueOrDefault("CARDRACE");
        var numericRace = playedFromHand.Tags.GetValueOrDefault("200");
        var first = Race(namedRace); var second = Race(numericRace);
        if (namedRace is null && numericRace is null && races.Count > 0 || namedRace is not null && first is null || numericRace is not null && second is null ||
            first.HasValue && second.HasValue && first != second)
        { unknowns.Add("HAND 时种族缺失或别名冲突，不能读取后来的变身身份。"); return Result(); }
        var currentRace = first ?? second;
        qualifies = currentRace is 11 or 20 or 26 || currentRace.HasValue && currentRace == native && races.Overlaps([11, 20, 26]);
        increment = qualifies == true ? 1 : 0;
        coverage.Add("TLC_EVENT_400-one-play-one-increment-using-frozen-hand-race");
        if (progress.HasValue && total == 3)
        {
            resulting = progress.Value + increment.Value;
            completes = resulting == 3; reward = completes == true;
            project = !pending;
            if (reward) unknowns.Add("达到三张后进入僵尸兽组件选择边界；未创建手牌、实体或最终费用。");
        }
        if (pending) unknowns.Add("前序抽牌/生成或其他结算尚待观测；增量仅为条件预览，不投影计数或提前发放奖励。");
        return Result();

        bool PlayedEvidenceValid(EntitySnapshot played) => played.Tags is not null && played.Id > 0 && played.Id != questEntityId &&
            played.Kind == EntityKind.Card && played.Zone == "HAND" && played.ControllerId == local && !string.IsNullOrWhiteSpace(played.CardId) &&
            cards.TryGetValue(played.CardId, out var card) && card is not null && card.Id == played.CardId &&
            card.Type is "MINION" or "SPELL" or "WEAPON" or "HERO" or "LOCATION" && ObservedType(played.Tags, card.Type, required: true) &&
            AliasMatches(played.Tags, "50", value => value == local) && AliasMatches(played.Tags, "49", value => value == 3) &&
            snapshot.Entities.TryGetValue(played.Id, out var observed) && observed.Kind == EntityKind.Card && observed.ControllerId == local &&
            (beforePlay is not null || observed.CardId == played.CardId && observed.Zone == "HAND" &&
                observed.Tags.Count == played.Tags.Count && observed.Tags.All(pair => played.Tags.TryGetValue(pair.Key, out var value) && value == pair.Value));

        QuestProgressOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, questEntityId,
            questCardId, progress, total, playedFromHand?.Id, qualifies, increment, resulting, completes, reward, project,
            Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(coverage.ToArray()))
            { IsConditionalPreview = pending };
    }

    private static Number Read(IReadOnlyDictionary<string, string> tags, string name, string alias)
    {
        var hasName = tags.TryGetValue(name, out var named); var hasAlias = tags.TryGetValue(alias, out var numeric);
        if (!hasName && !hasAlias) return new(false, true, null);
        var firstOk = int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first);
        var secondOk = int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second);
        return hasName && !firstOk || hasAlias && !secondOk || hasName && hasAlias && first != second ? new(true, false, null) : new(true, true, hasName ? first : second);
    }
    private static bool FlagCompatible(IReadOnlyDictionary<string, string> tags, string name, string alias)
    { var value = Read(tags, name, alias); return value.Valid && (!value.Present || value.Value == 1); }
    // Named zone values use protocol names, while numeric alias values use the enum value.
    private static bool AliasMatches(IReadOnlyDictionary<string, string> tags, string alias, Func<int, bool> matches) =>
        !tags.TryGetValue(alias, out var value) || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && matches(parsed);
    private static bool ObservedType(IReadOnlyDictionary<string, string> tags, string type, bool required) =>
        (!required || tags.ContainsKey("CARDTYPE") || tags.ContainsKey("202")) &&
        (!tags.TryGetValue("CARDTYPE", out var named) || TypeMatches(named, type)) &&
        (!tags.TryGetValue("202", out var numeric) || TypeMatches(numeric, type));
    private static bool TypeMatches(string value, string type) => value == type || (type, value) is
        ("MINION", "4") or ("SPELL", "5") or ("WEAPON", "7") or ("HERO", "3") or ("LOCATION", "39");

    private static bool Profile(CardRecord card, out int? native, out HashSet<int> races, out HashSet<string> mechanics)
    {
        native = null; races = []; mechanics = new(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            if (raw.ValueKind != JsonValueKind.Object || raw.GetProperty("id").GetString() != card.Id ||
                raw.GetProperty("dbfId").GetInt32() != card.DbfId || raw.GetProperty("type").GetString() != card.Type ||
                (raw.TryGetProperty("text", out var text) ? text.GetString() : "") != card.Text) return false;
            if (raw.TryGetProperty("race", out var race))
            { native = Race(race.GetString()); if (native is null) return false; races.Add(native.Value); }
            if (raw.TryGetProperty("races", out var several))
            {
                var listed = new HashSet<int>();
                foreach (var item in several.EnumerateArray())
                { if (Race(item.GetString()) is not { } parsed || !listed.Add(parsed)) return false; }
                if (listed.Count == 0 || native.HasValue && !listed.Contains(native.Value)) return false;
                races.UnionWith(listed);
            }
            if (raw.TryGetProperty("mechanics", out var list))
                foreach (var item in list.EnumerateArray())
                { if (item.GetString() is not { } mechanic || !mechanics.Add(mechanic)) return false; }
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException) { return false; }
    }

    private static int? Race(string? value) => value switch
    {
        "INVALID" or "0" => 0, "BLOODELF" or "1" => 1, "DRAENEI" or "2" => 2, "DWARF" or "3" => 3,
        "GNOME" or "4" => 4, "GOBLIN" or "5" => 5, "HUMAN" or "6" => 6, "NIGHTELF" or "7" => 7,
        "ORC" or "8" => 8, "TAUREN" or "9" => 9, "TROLL" or "10" => 10, "UNDEAD" or "11" => 11,
        "WORGEN" or "12" => 12, "GOBLIN2" or "13" => 13, "MURLOC" or "14" => 14, "DEMON" or "15" => 15,
        "SCOURGE" or "16" => 16, "MECHANICAL" or "17" => 17, "ELEMENTAL" or "18" => 18, "OGRE" or "19" => 19,
        "BEAST" or "20" or "PET" => 20, "TOTEM" or "21" => 21, "NERUBIAN" or "22" => 22, "PIRATE" or "23" => 23,
        "DRAGON" or "24" => 24, "BLANK" or "25" => 25, "ALL" or "26" => 26, "EGG" or "38" => 38,
        "QUILBOAR" or "43" => 43, "NAGA" or "92" => 92, _ => null
    };
}
