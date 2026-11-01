using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record DreamboundTriggerRange(int Minimum, int? Maximum);

/// <summary>A host-produced overlay at the after-play boundary, bound to its original observed frame.</summary>
public sealed record DreamboundRaptorState
{
    public string MatchId { get; init; }
    public long StateVersion { get; init; }
    public long EventSequence { get; init; }
    public EntitySnapshot PlayedBefore { get; init; }
    public EntitySnapshot PlayedAfter { get; init; }
    private IReadOnlyList<EntitySnapshot> _before;
    private IReadOnlyList<EntitySnapshot> _after;
    public IReadOnlyList<EntitySnapshot> SourcesBeforePlay { get => _before; init => _before = Freeze(value); }
    public IReadOnlyList<EntitySnapshot> SourcesAfterPlay { get => _after; init => _after = Freeze(value); }
    public bool PriorResolutionPending { get; init; }

    public DreamboundRaptorState(string matchId, long stateVersion, long eventSequence,
        EntitySnapshot playedBefore, EntitySnapshot playedAfter, IReadOnlyList<EntitySnapshot> sourcesBeforePlay,
        IReadOnlyList<EntitySnapshot> sourcesAfterPlay)
    {
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence;
        PlayedBefore = playedBefore; PlayedAfter = playedAfter;
        _before = Freeze(sourcesBeforePlay); _after = Freeze(sourcesAfterPlay);
    }
    private static IReadOnlyList<EntitySnapshot> Freeze(IReadOnlyList<EntitySnapshot> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Array.AsReadOnly(source.Select(entity => entity is null ? null! : entity with
            { Tags = entity.Tags?.ToFrozenDictionary(StringComparer.Ordinal)! }).ToArray());
    }
}

public sealed record DreamboundRaptorTrigger(int SourceEntityId, int TargetEntityId,
    DreamboundTriggerRange Count, string Eligibility, IReadOnlyList<string> EligibleBonusEffects,
    string? GuaranteedBonusEffect);

/// <summary>Counts eligible native grants, never an observed random keyword or a new authoritative board.</summary>
public sealed record DreamboundRaptorOutcome(string MatchId, long StateVersion, long EventSequence,
    int TargetEntityId, string? TargetCardId, bool ResolvesTriggerCount, DreamboundTriggerRange TriggerCount,
    IReadOnlyList<DreamboundRaptorTrigger> Triggers, bool RequiresObservation, bool PriorResolutionPending,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool CanEvaluateFollowupActions => false;
    public bool IsBonusPoolComplete { get; init; }
}

/// <summary>
/// Fixed build 255790 EDR_849/dbf114933: after a minion is played, one native source requests one random Bonus Effect.
/// Fixed CardDefs references BONUS_EFFECTS/2934 but supplies neither its complete pool nor trigger-order script.
/// Official 30.2 notes replace Stealth with Elusive and restrict Rush by current attack state:
/// https://hearthstone.blizzard.com/en-us/news/24122902 . These facts do not prove a complete current pool,
/// duplicate filtering, self-registration, or ordering. They must not be filled from wiki lists or printed traits.
/// </summary>
public static class DreamboundRaptorRules
{
    public static DreamboundRaptorOutcome Evaluate(GameSnapshot snapshot, int playedEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, DreamboundRaptorState? afterPlay = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        var triggers = new List<DreamboundRaptorTrigger>(); string? targetCardId = null;
        var pending = afterPlay?.PriorResolutionPending == true;
        if (snapshot.ClientBuild != "255790" || snapshot.IsSpectating || snapshot.LocalPlayerId is not (1 or 2) ||
            snapshot.ActivePlayerId != snapshot.LocalPlayerId || snapshot.Phase != GamePhase.Main ||
            !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable ||
            !snapshot.Quality.HasLocalPerspective || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Entities.Any(pair => !ValidEntity(pair.Value) || pair.Key != pair.Value.Id))
        { unknowns.Add("需要固定 build 255790、完整稳定且轮到我方的非观战局面，未展开使用后触发。"); return Unresolved(); }
        var players = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).ToArray();
        if (!players.Select(player => player.GetIntTag("PLAYER_ID")).Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("双方玩家身份未唯一确认，不能绑定使用者与触发来源。"); return Unresolved(); }
        if (!snapshot.Entities.TryGetValue(playedEntityId, out var observed) || !OwnCard(observed, snapshot.LocalPlayerId) ||
            string.IsNullOrWhiteSpace(observed.CardId) || observed.Zone != "HAND" && !(afterPlay is not null && observed.Zone == "PLAY"))
        { unknowns.Add("未确认我方已知手牌随从的确切身份与使用阶段，旧场上随从不能自行重触发使用后效果。"); return Unresolved(); }
        targetCardId = observed.CardId;
        if (afterPlay is not null && (afterPlay.MatchId != snapshot.MatchId || afterPlay.StateVersion != snapshot.StateVersion ||
            afterPlay.EventSequence != snapshot.EventSequence))
        { unknowns.Add("入场覆盖与当前对局、状态版本或事件序号不一致，未采用过期或未来实体。"); return Unresolved(); }
        if (!Minion(observed, cards))
        {
            if (afterPlay is null && KnownNativeNonMinion(observed, cards))
                return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, playedEntityId, targetCardId,
                    true, new(0, 0), Array.AsReadOnly(Array.Empty<DreamboundRaptorTrigger>()), false, false,
                    Freeze(unknowns), Freeze(["已确认的原生非随从牌不满足梦缚迅猛龙的随从使用后事件条件。"]),
                    Freeze(["EDR_849-native-after-minion-play-excludes-confirmed-non-minions"]));
            unknowns.Add("当前实体类别与固定随从资料未一致确认，不能将未知类别视为必定有触发或必定零触发。"); return Unresolved();
        }

        EntitySnapshot before = afterPlay?.PlayedBefore ?? observed;
        EntitySnapshot? after = afterPlay?.PlayedAfter;
        if (afterPlay is not null && after is null || !ValidEntity(before) || before.Id != playedEntityId || before.CardId != targetCardId || before.Zone != "HAND" ||
            !OwnCard(before, snapshot.LocalPlayerId) || !Minion(before, cards) ||
            after is not null && (!ValidEntity(after) || after.Id != playedEntityId || after.CardId != targetCardId ||
                !OwnCard(after, snapshot.LocalPlayerId) || !Minion(after, cards)))
        { unknowns.Add("使用前后实体的 ID、身份、归属或当时随从类别不能对应，未把覆盖当成成功使用证据。"); return Unresolved(); }

        var sources = afterPlay?.SourcesBeforePlay ?? Array.AsReadOnly(snapshot.Entities.Values.Where(entity =>
            OwnCard(entity, snapshot.LocalPlayerId) && entity.Zone == "PLAY" && entity.CardId == "EDR_849").ToArray());
        var ending = afterPlay?.SourcesAfterPlay;
        if (sources.Any(entity => !ValidEntity(entity)) || sources.Select(entity => entity.Id).Distinct().Count() != sources.Count ||
            ending is not null && (ending.Any(entity => !ValidEntity(entity)) || ending.Select(entity => entity.Id).Distinct().Count() != ending.Count))
        { unknowns.Add("触发来源集合有空实体、空标签或重复 ID，未重复计次。"); return Unresolved(); }
        foreach (var source in sources)
        {
            if (source.Id == playedEntityId || !OwnCard(source, snapshot.LocalPlayerId) || source.Zone != "PLAY" || source.CardId != "EDR_849" ||
                !snapshot.Entities.TryGetValue(source.Id, out var origin) || !OwnCard(origin, snapshot.LocalPlayerId) ||
                origin.CardId != source.CardId || origin.Zone is not ("PLAY" or "HAND") ||
                !NativeRaptor(source, cards) || !NativeRaptor(origin, cards))
            { unknowns.Add("来源未绑定我方已知原生梦缚迅猛龙；拼装、候选半段或类别冲突不能复用基础牌效。"); return Unresolved(); }
        }
        if (ending is not null && (sources.Any(source => ending.All(entity => entity.Id != source.Id)) ||
            ending.Any(entity => sources.All(source => source.Id != entity.Id) && entity.Id != playedEntityId)))
        { unknowns.Add("入场后来源集合缺少原有来源的去向或包含未绑定的新实体，未推断触发数量。"); return Unresolved(); }

        foreach (var source in sources)
        {
            var end = ending?.Single(entity => entity.Id == source.Id);
            if (end is not null && (!OwnCard(end, snapshot.LocalPlayerId) || end.CardId != source.CardId || !NativeRaptor(end, cards)))
            {
                // Ownership changes, transformations and replaced text require a different audited trigger binding.
                Add(source.Id, null, "UnconfirmedAfterPlayIdentity");
                unknowns.Add("来源在入场后发生身份、归属、随从类别或文字变化，不能继续使用其原生触发。"); continue;
            }
            var priorActive = Active(source); var active = end is null ? null : Active(end);
            var targetAlive = after is null ? null : Recipient(after);
            bool? eligible;
            if (pending) eligible = null;
            else if (afterPlay is null) eligible = null;
            else if (active == false || targetAlive == false) eligible = false;
            else eligible = priorActive == true && active == true && targetAlive == true ? true : null;
            Add(source.Id, eligible, eligible switch { true => "EligibleNativeAfterPlay", false => "InactiveOrNoLiveTarget", _ => "AwaitingAfterPlayState" });
        }

        // Whether a just-played Raptor registers for its own PLAY event is not established by the printed trigger.
        // Keep it as a separate possible source rather than asserting a buff or silently excluding it.
        if (targetCardId == "EDR_849" && !AssembledCardText.RequiresCustomText(before.Tags))
        {
            if (!NativeRaptor(before, cards) || after is not null && !NativeRaptor(after, cards))
            { unknowns.Add("本次使用牌的迅猛龙基础身份带有自定义文字或类别冲突，不能据此增加原生自身触发。"); return Unresolved(); }
            if (ending is not null && ending.Any(entity => entity.Id == playedEntityId && !SameState(entity, after!)))
            { unknowns.Add("入场后来源集合与被使用实体自身的属性不一致，未绑定自身触发。"); return Unresolved(); }
            var inactive = !pending && after is not null && Active(after) == false;
            Add(playedEntityId, inactive ? false : null, inactive ? "InactiveNewSource" : "SelfRegistrationUnconfirmed");
            if (!inactive) unknowns.Add("固定卡面与已核对的一手资料未证明刚使用的迅猛龙是否接收自身使用后事件，自身贡献保留零至一次。");
        }
        if (pending) unknowns.Add("前置战吼、抽牌、生成或其关联触发尚待观察；当前并未完成使用后额外效果授予。");
        if (after is not null && Alive(after) != false && Flag(after, "DORMANT", "1518") != false)
            unknowns.Add("入场后的目标处于休眠或休眠标签冲突，尚未确认本次原生授予对它的处理，不能按普通可受效果随从计为确定一次。");
        if (afterPlay is null) unknowns.Add("这是以手牌随从成功使用为条件的预览；未提供成功入场后的来源、目标存活状态，不能把当前场面直接当作使用后场面。");
        if (triggers.Any(trigger => trigger.Count.Maximum > 0))
        {
            unknowns.Add("额外效果的完整当前随机池、已有关键词排重及实际随机结果未获脚本或日志证实；空候选列表表示未知，不表示无可授予效果。");
            unknowns.Add("多个来源的实际结算顺序与每次授予后的目标属性未观察，不按实体 ID、场上位置或印刷关键词选择结果。");
        }
        assumptions.Add("仅核算本次成功使用随从后的原生触发资格；一次确认来源贡献一次请求，不重算费用、攻击或生命，不生成新实体。");
        assumptions.Add("覆盖由本地模拟器提供，使用前来源集合必须完整，使用后集合保留每个原有来源的最终去向；它与原始观察绑定，不是新的权威快照。");
        coverage.AddRange(["EDR_849-dbf114933-build255790-native-after-minion-play", "one-request-per-confirmed-existing-source",
            "silence-dormant-life-and-after-play-boundary", "no-invented-bonus-pool-or-random-keyword", "no-authoritative-followup-state"]);
        var total = new DreamboundTriggerRange(triggers.Sum(trigger => trigger.Count.Minimum), triggers.Sum(trigger => trigger.Count.Maximum ?? 1));
        return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, playedEntityId, targetCardId,
            afterPlay is not null && triggers.All(trigger => trigger.Count.Minimum == trigger.Count.Maximum) && !pending, total, Array.AsReadOnly(triggers.ToArray()),
            pending || total.Maximum > 0 || triggers.Any(trigger => trigger.Count.Minimum != trigger.Count.Maximum), pending,
            Freeze(unknowns), Freeze(assumptions), Freeze(coverage));

        void Add(int sourceId, bool? eligible, string reason) => triggers.Add(new(sourceId, playedEntityId,
            eligible switch { true => new(1, 1), false => new(0, 0), _ => new(0, 1) }, reason, Array.AsReadOnly(Array.Empty<string>()), null));
        DreamboundRaptorOutcome Unresolved() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            playedEntityId, targetCardId, false, new(0, null), Array.AsReadOnly(Array.Empty<DreamboundRaptorTrigger>()), true, pending,
            Freeze(unknowns), Freeze(assumptions), Freeze(coverage));
    }

    private static IReadOnlyList<string> Freeze(IEnumerable<string> source) => Array.AsReadOnly(source.Distinct(StringComparer.Ordinal).ToArray());
    private static bool ValidEntity(EntitySnapshot? entity) => entity is not null && entity.Tags is not null;
    private static bool OwnCard(EntitySnapshot entity, int? player) => entity.Kind == EntityKind.Card && entity.ControllerId == player &&
        (!entity.Tags.ContainsKey("50") || Tag(entity, "CONTROLLER", "50") == player) &&
        (!entity.Tags.ContainsKey("49") || Tag(entity, "ZONE", "49", true) is not null);
    private static bool Minion(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards) =>
        (!entity.Tags.TryGetValue("CARDTYPE", out var type) || type is "MINION" or "4") &&
        (!entity.Tags.TryGetValue("202", out var alias) || alias is "MINION" or "4") &&
        entity.CardId is { } id && cards.TryGetValue(id, out var card) && card is not null && card.Id == id && card.Type == "MINION" && AuditedMinion(card);
    private static bool KnownNativeNonMinion(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (AssembledCardText.RequiresCustomText(entity.Tags) || entity.CardId is not { } id || !cards.TryGetValue(id, out var card) ||
            card is null || card.Id != id || card.Type is not ("SPELL" or "WEAPON" or "HERO" or "LOCATION")) return false;
        var number = card.Type switch { "SPELL" => "5", "WEAPON" => "7", "HERO" => "3", "LOCATION" => "39", _ => "" };
        if (entity.Tags.TryGetValue("CARDTYPE", out var type) && type != card.Type && type != number ||
            entity.Tags.TryGetValue("202", out var alias) && alias != card.Type && alias != number) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return raw.GetProperty("id").GetString() == id && raw.GetProperty("dbfId").GetInt32() == card.DbfId &&
                raw.GetProperty("type").GetString() == card.Type && raw.GetProperty("text").GetString() == card.Text;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { return false; }
    }
    private static bool SameState(EntitySnapshot first, EntitySnapshot second) => first.Id == second.Id && first.CardId == second.CardId &&
        first.Kind == second.Kind && first.Tags.Count == second.Tags.Count && first.Tags.All(pair => second.Tags.TryGetValue(pair.Key, out var value) && pair.Value == value);
    private static bool AuditedMinion(CardRecord card)
    {
        if (NativeCardDefinitions.IsRegistered(card.Id) && !NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, card.Id, card)) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return raw.GetProperty("id").GetString() == card.Id && raw.GetProperty("dbfId").GetInt32() == card.DbfId &&
                raw.GetProperty("type").GetString() == "MINION" && raw.GetProperty("text").GetString() == card.Text;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { return false; }
    }
    private static bool NativeRaptor(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards) =>
        entity.CardId == "EDR_849" && Minion(entity, cards) && !AssembledCardText.RequiresCustomText(entity.Tags) &&
        cards.TryGetValue("EDR_849", out var card) && AuditedRaptor(card);
    private static bool AuditedRaptor(CardRecord card)
    {
        if (!NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, "EDR_849", card)) return false;
        if (card.DbfId != 114933 || card.Text != "在你使用一张随从牌后，随机使其获得一项<b>额外效果</b>。") return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return raw.GetProperty("id").GetString() == card.Id && raw.GetProperty("dbfId").GetInt32() == card.DbfId &&
                raw.GetProperty("type").GetString() == "MINION" && raw.GetProperty("text").GetString() == card.Text &&
                raw.GetProperty("mechanics").EnumerateArray().Any(value => value.GetString() == "TRIGGER_VISUAL");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { return false; }
    }
    private static bool? Active(EntitySnapshot entity)
    {
        var alive = Alive(entity); var silence = Flag(entity, "SILENCED", "188"); var dormant = Flag(entity, "DORMANT", "1518");
        if (alive == false || silence == true || dormant == true) return false;
        return alive == true && silence == false && dormant == false ? true : null;
    }
    private static bool? Alive(EntitySnapshot entity)
    {
        if (entity.Zone != "PLAY") return false;
        var health = Tag(entity, "HEALTH", "45"); var damage = Tag(entity, "DAMAGE", "44");
        if (health is null or < 0 || damage is null or < 0) return null;
        return (long)health.Value - damage.Value > 0;
    }
    private static bool? Recipient(EntitySnapshot entity) => Alive(entity) == false ? false :
        Flag(entity, "DORMANT", "1518") == false ? Alive(entity) : null;
    private static bool? Flag(EntitySnapshot entity, string named, string numeric) =>
        !entity.Tags.ContainsKey(named) && !entity.Tags.ContainsKey(numeric) ? false : Tag(entity, named, numeric) is >= 0 and var value ? value > 0 : null;
    private static int? Tag(EntitySnapshot entity, string named, string numeric, bool zone = false)
    {
        int? Parse(string value) => zone ? value switch { "PLAY" => 1, "DECK" => 2, "HAND" => 3, "GRAVEYARD" => 4,
            "REMOVEDFROMGAME" => 5, "SETASIDE" => 6, "SECRET" => 7, _ => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null } :
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        var hasFirst = entity.Tags.TryGetValue(named, out var first); var hasSecond = entity.Tags.TryGetValue(numeric, out var second);
        var a = hasFirst ? Parse(first!) : null; var b = hasSecond ? Parse(second!) : null;
        return hasFirst && a is null || hasSecond && b is null || hasFirst && hasSecond && a != b ? null : a ?? b;
    }
}
