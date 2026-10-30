using HsHelper.Cards;
using HsHelper.Core;
using System.Text.Json;

namespace HsHelper.Rules;

public sealed record CraftComponentOutcome(int ChoiceId, int EntityId, string? CardId, string Name,
    int? ObservedCost, int? ObservedAttack, int? ObservedHealth, bool FromHistory);

/// <summary>Conditional direct consequences; numeric projections never create an authoritative hand entity.</summary>
public sealed record CraftChoiceOutcome(string Mechanism, string Stage,
    IReadOnlyList<CraftComponentOutcome> Components, int? PrimaryEntityId, bool? AddedToHand,
    int? GrantedCostReduction, int? ResultingCost, int? ResultingAttack, int? ResultingHealth,
    bool? RequiresAnotherChoice, bool ResolvesEffects, IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RuleCoverage)
{
    public bool CanEvaluateFollowupActions => false;
}

/// <summary>
/// Fixed build 255790: TLC_EVENT_400 crafts a two-component Zombeast and grants a three-mana reduction.
/// BE_044 instead uses Assemble: the primary minion retains its name, cost, stats and types while text is replaced.
/// Sources: fixed client CardDefs/cards.json,
/// https://news.blizzard.com/en-gb/article/20888401/join-the-knights-of-the-frozen-throne , and HearthstoneTeam's
/// https://www.reddit.com/r/hearthstone/comments/1wf8f4k/assemble_a_new_keyword_in_reign_of_the_black/ .
/// No stage, primary linkage, generated identity, or printed component statistics are inferred from names or cost.
/// </summary>
public static class CraftChoiceRules
{
    public static CraftChoiceOutcome Evaluate(GameSnapshot snapshot, int selectedEntityId,
        IReadOnlyDictionary<string, CardRecord> cards)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>();
        var coverage = new List<string>();
        var components = new List<CraftComponentOutcome>();
        var mechanism = "Unknown";
        var stage = "Unconfirmed";
        bool? added = null, another = null;
        int? reduction = null, cost = null, attack = null, health = null;
        var resolves = false;
        if (snapshot.Entities.Values.Any(entity => entity is null || entity.Tags is null))
        { unknowns.Add("当前实体属性缺失，不能安全连接构筑证据。"); return Result(); }
        var choice = ChoiceVisibility.ConfirmedOwn(snapshot);
        if (snapshot.ClientBuild != "255790" || choice is null || snapshot.Phase != GamePhase.Choice ||
            choice.Type != "GENERAL" || choice.CountMin != 1 || choice.CountMax != 1 ||
            !choice.Options.Any(option => option.EntityId == selectedEntityId))
        {
            unknowns.Add("未确认 build 255790 的当前我方 GENERAL 单选，未展开构筑后果。");
            return Result();
        }
        if (choice.SourceEntityId is not { } sourceId || !snapshot.Entities.TryGetValue(sourceId, out var source) ||
            source.Id != sourceId || source.Kind != EntityKind.Card || source.ControllerId != snapshot.LocalPlayerId ||
            source.Zone is not ("PLAY" or "GRAVEYARD") || source.CardId is not ("TLC_EVENT_400" or "BE_044") ||
            choice.SourceCardId is { } offeredSource && offeredSource != source.CardId ||
            !cards.TryGetValue(source.CardId, out var sourceCard) || sourceCard is null || sourceCard.Id != source.CardId ||
            sourceCard.Type != (source.CardId == "BE_044" ? "MINION" : "SPELL"))
        {
            unknowns.Add("构筑来源的身份、已使用区域、归属或固定版本资料未确认。");
            return Result();
        }
        if (source.Tags.GetValueOrDefault("CARDTYPE") is { } sourceKind &&
            (source.CardId == "BE_044" ? sourceKind is not ("MINION" or "4") : sourceKind is not ("SPELL" or "5")))
        { unknowns.Add("构筑来源的已观察卡牌类别与固定卡库冲突。"); return Result(); }
        mechanism = source.CardId == "BE_044" ? "Assemble" : "BuildABeast";
        var option = choice.Options.Single(option => option.EntityId == selectedEntityId);
        snapshot.Entities.TryGetValue(selectedEntityId, out var observed);
        if (string.IsNullOrWhiteSpace(option.CardId) || observed is not null &&
            (observed.Id != option.EntityId || observed.Kind != EntityKind.Card || observed.ControllerId is { } owner && owner != snapshot.LocalPlayerId ||
             observed.CardId is { } observedId && observedId != option.CardId ||
             observed.Zone is "HAND" or "PLAY" or "GRAVEYARD"))
        {
            unknowns.Add("当前组件身份未知或与已观察实体冲突，未推导生成卡牌。");
            return Result();
        }
        components.Add(Component(choice.Id, option, observed, false));
        coverage.Add("current-offered-craft-component");
        if (mechanism == "Assemble")
        {
            coverage.Add("BE_044-assemble-is-not-zombeast");
            unknowns.Add("F3TCH3R 的拼装保留主随从的费用、属性、名字和类型，仅替换部分文字；不能将两张候选的费用或属性相加。");
            unknowns.Add("当前协议未确认拼装阶段、主随从实体及被替换的半段文字；不能凭四费、名字、候选位置或 CREATOR 猜测关联，也不能重复将已在手牌的主随从入手。");
            return Result();
        }

        if (!IsBeastMinion(option, observed))
        {
            unknowns.Add("当前僵尸兽组件的野兽随从类别或候选属性未确认/存在冲突，未合并属性。");
            return Result();
        }
        if (source.GetIntTag("QUEST_PROGRESS") is < 3 || source.GetIntTag("QUEST_PROGRESS_TOTAL") is { } total && total != 3 ||
            source.Tags.ContainsKey("QUEST_PROGRESS") && source.GetIntTag("QUEST_PROGRESS") is null ||
            source.Tags.ContainsKey("QUEST_PROGRESS_TOTAL") && source.GetIntTag("QUEST_PROGRESS_TOTAL") is null)
        {
            unknowns.Add("围攻城门的已观察任务进度与三次使用后奖励冲突，未展开构筑后果。");
            return Result();
        }

        if (!TryHistory(out var previous)) return Result();
        if (previous is null)
        {
            if (source.GetIntTag("QUEST_PROGRESS") is not (>= 3) || source.GetIntTag("QUEST_PROGRESS_TOTAL") != 3)
            {
                unknowns.Add("完整台账尚无该来源的前次组件，但也缺少当前支线已完成的正向进度证据，不能只凭没有历史认定第一阶段。");
                return Result();
            }
            stage = "ComponentOne";
            added = false;
            another = true;
            resolves = true;
            coverage.Add("TLC_EVENT_400-first-component-only");
            unknowns.Add("本次只选定第一个组件，不新增手牌；第二个组件尚未选择，不能提前合成僵尸兽或授予减费。");
            return Result();
        }
        var previousOption = previous.Options.Single(candidate => candidate.EntityId == previous.ChosenEntityIds[0]);
        var previousEntity = previous.OptionEntities.SingleOrDefault(entity => entity.Id == previousOption.EntityId);
        components.Insert(0, Component(previous.ChoiceId, previousOption, previousEntity, true));
        stage = "ComponentTwo";
        another = false;
        resolves = true;
        reduction = 3;
        cost = Add(components[0].ObservedCost, components[1].ObservedCost, "费用") is { } sum ? Math.Max(0, sum - 3) : null;
        attack = Add(components[0].ObservedAttack, components[1].ObservedAttack, "攻击力");
        health = Add(components[0].ObservedHealth, components[1].ObservedHealth, "生命值");
        added = HandCapacity();
        coverage.AddRange(["TLC_EVENT_400-bound-prior-component", "zombeast-observed-component-sums", "TLC_EVENT_400-reward-cost-reduction-3"]);
        unknowns.Add("合成数值只按两次候选当时已观察的费用、攻击力和生命值相加，再应用本奖励的三点减费；未回填印刷数值。新实体、合成文字及额外入手费用修正须以结算后日志为准，不能据此继续模拟出牌。");
        return Result();

        CraftComponentOutcome Component(int choiceId, ChoiceOptionSnapshot candidate, EntitySnapshot? entity, bool historical)
        {
            cards.TryGetValue(candidate.CardId ?? "", out var card);
            // HEALTH is the observed maximum, never RemainingHealth. A damaged/non-candidate entity is not a fresh component.
            if (entity?.Zone != "SETASIDE" || entity.GetIntTag("DAMAGE") is not (null or 0)) entity = null;
            return new(choiceId, candidate.EntityId, candidate.CardId,
                card is not null && card.Id == candidate.CardId ? card.Name : "未知卡名",
                Nonnegative(entity?.Cost), Nonnegative(entity?.Attack),
                entity?.GetIntTag("DAMAGE") is not (> 0) && entity?.GetIntTag("HEALTH") is > 0 and var observedHealth
                    ? observedHealth : null, historical);
        }

        bool IsBeastMinion(ChoiceOptionSnapshot candidate, EntitySnapshot? entity)
        {
            if (!cards.TryGetValue(candidate.CardId ?? "", out var card) || card is null || card.Id != candidate.CardId || card.Type != "MINION" ||
                entity?.Tags.GetValueOrDefault("CARDTYPE") is { } kind && kind is not ("MINION" or "4") ||
                entity?.Tags.GetValueOrDefault("CARDRACE") is { } raceTag && raceTag is not ("BEAST" or "20" or "ALL" or "26") ||
                entity?.Zone is { } zone && zone != "SETASIDE" || entity?.GetIntTag("DAMAGE") is not (null or 0) ||
                entity?.Tags.ContainsKey("DAMAGE") == true && entity.GetIntTag("DAMAGE") is null) return false;
            try
            {
                using var raw = JsonDocument.Parse(card.RawJson);
                return raw.RootElement.ValueKind == JsonValueKind.Object &&
                    (raw.RootElement.TryGetProperty("race", out var race) && race.ValueKind == JsonValueKind.String && race.GetString() == "BEAST" ||
                     raw.RootElement.TryGetProperty("races", out var races) && races.ValueKind == JsonValueKind.Array &&
                     races.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "BEAST"));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException) { return false; }
        }

        bool TryHistory(out ChoiceHistoryEntry? prior)
        {
            prior = null;
            var history = snapshot.ChoiceHistory;
            if (choice.OfferedAtEventSequence <= 0 || choice.OfferedAtEventSequence > snapshot.EventSequence ||
                choice.SourceCardId != source.CardId || history is null || history.MatchId != snapshot.MatchId ||
                history.StateVersion != snapshot.StateVersion || history.EventSequence != snapshot.EventSequence ||
                !history.IsComplete || history.TruncatedThroughSequence != 0 || history.Entries is null ||
                history.Warnings is null || history.Warnings.Count != 0 || history.Entries.Any(entry => entry is null) ||
                history.Entries.Select(entry => entry.ChoiceId).Distinct().Count() != history.Entries.Count)
            {
                unknowns.Add("缺少绑定当前对局、状态版本和事件序号的完整未截断选择台账，或当前提供时序/来源未固定；不能确认这是第几个组件。");
                return false;
            }
            // Every closed choice must precede the current offer; reject fabricated/reordered ledgers.
            if (history.Entries.Any(entry => !entry.IsComplete || entry.PlayerId != snapshot.LocalPlayerId ||
                entry.ChoiceId <= 0 || entry.ChoiceId == choice.Id ||
                entry.OfferedAtEventSequence <= 0 || entry.ChosenAtEventSequence <= entry.OfferedAtEventSequence ||
                entry.ChosenAtEventSequence >= choice.OfferedAtEventSequence) ||
                choice.PreviousOfferedAtEventSequence != history.Entries.Select(entry => entry.OfferedAtEventSequence).DefaultIfEmpty(0).Max())
            {
                unknowns.Add("选择台账的身份或提供/选中时序与当前选择冲突，不能连接组件。");
                return false;
            }
            var sameSource = history.Entries.Where(entry => entry.SourceEntityId == source.Id).ToArray();
            if (sameSource.Length == 0) return true;
            if (sameSource.Length != 1)
            {
                unknowns.Add("同一支线来源对应多个已完成选择，无法唯一连接本次组件。");
                return false;
            }
            var entry = sameSource[0];
            if (choice.PreviousOfferedAtEventSequence != entry.OfferedAtEventSequence ||
                !entry.IsComplete || entry.PlayerId != snapshot.LocalPlayerId || entry.Type != "GENERAL" ||
                entry.SourceCardId != source.CardId || entry.CountMin != 1 || entry.CountMax != 1 ||
                entry.Options is not { Count: > 0 } || entry.OptionEntities is null ||
                entry.ChosenEntityIds is not { Count: 1 } ||
                entry.Options.Any(candidate => candidate is null || candidate.EntityId <= 0 ||
                    string.IsNullOrWhiteSpace(candidate.CardId) || candidate.ControllerId is { } owner && owner != snapshot.LocalPlayerId) ||
                entry.Options.Select(candidate => candidate.EntityId).Distinct().Count() != entry.Options.Count ||
                entry.OptionEntities.Any(entity => entity is null || entity.Tags is null) ||
                entry.OptionEntities.Select(entity => entity.Id).Distinct().Count() != entry.OptionEntities.Count ||
                entry.OptionEntities.Any(entity => !entry.Options.Any(candidate => candidate.EntityId == entity.Id)) ||
                !entry.Options.Any(candidate => candidate.EntityId == entry.ChosenEntityIds[0]) ||
                entry.Options.Any(candidate => choice.Options.Any(current => current.EntityId == candidate.EntityId)))
            {
                unknowns.Add("前次组件选择的来源、归属、候选或单选结果不完整/冲突，不能合并。");
                return false;
            }
            foreach (var candidate in entry.Options)
            {
                var entity = entry.OptionEntities.SingleOrDefault(observation => observation.Id == candidate.EntityId);
                if (entity is not null && (entity.Kind != EntityKind.Card ||
                    entity.ControllerId is { } owner && owner != snapshot.LocalPlayerId ||
                    entity.CardId is { } observedId && observedId != candidate.CardId ||
                    entity.Zone is "HAND" or "PLAY" or "GRAVEYARD"))
                {
                    unknowns.Add("前次候选的冻结实体与当时身份、归属或区域冲突，不能以后来实体补写历史。");
                    return false;
                }
                if (!IsBeastMinion(candidate, entity))
                {
                    unknowns.Add("前次候选的野兽随从类别或属性未确认/存在冲突，不能作为僵尸兽组件。");
                    return false;
                }
            }
            prior = entry;
            return true;
        }

        int? Add(int? first, int? second, string attribute)
        {
            if (first is null || second is null)
            { unknowns.Add($"组件的当时{attribute}未完整观察，合成{attribute}保持未知。"); return null; }
            var sum = (long)first.Value + second.Value;
            if (sum > int.MaxValue)
            { unknowns.Add($"组件{attribute}之和超出可表示范围，保持未知。"); return null; }
            return (int)sum;
        }

        bool? HandCapacity()
        {
            var player = snapshot.Entities.Values.Single(entity => entity.Kind == EntityKind.Player &&
                entity.GetIntTag("PLAYER_ID") == snapshot.LocalPlayerId);
            var observedCount = player.GetIntTag("NUM_CARDS_IN_HAND");
            var count = snapshot.Hand.Count();
            if (observedCount is { } observed && (observed < 0 || observed != count) ||
                player.GetIntTag("MAXHANDSIZE") is not (>= 0 and var maximum))
            { unknowns.Add("当前手牌数量或上限未可靠确认，本次合成是否新增手牌保持未知。"); return null; }
            return count < maximum;
        }

        CraftChoiceOutcome Result() => new(mechanism, stage, Array.AsReadOnly(components.ToArray()),
            null, added, reduction, cost, attack, health, another, resolves,
            Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(coverage.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static int? Nonnegative(int? value) => value is >= 0 ? value : null;
}
