using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record MalorneChoiceOutcome(int SelectedEntityId, string? CardId, string? Name,
    bool? AddedToHand, bool? ImbueConditionMet, int? GrantedSetCost, ImbueCountEvidence Imbues,
    bool ResolvesEffects, IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    public int? ResultingCost => null;
    public bool CanEvaluateFollowupActions => false;
}

/// <summary>
/// Fixed build 255790 EDR_888: four cumulative Imbues set the discovered Wild God's cost to one, not minus four.
/// https://hearthstone.blizzard.com/en-us/cards/115186-malorne-the-waywatcher/ ; fixed CardDefs/cards.json.
/// The reviewed Wild God identities also match the official card's related-card list; candidates are never generated here.
/// </summary>
public static class MalorneChoiceRules
{
    private static readonly IReadOnlyDictionary<string, int> WildGods = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["EDR_031"] = 113434, ["EDR_209"] = 113588, ["EDR_259"] = 114868,
        ["EDR_421"] = 113606, ["EDR_430"] = 113700, ["EDR_471"] = 114353,
        ["EDR_480"] = 114254, ["EDR_489"] = 115203, ["EDR_527"] = 114527,
        ["EDR_819"] = 116018, ["EDR_895"] = 115264
    };

    public static MalorneChoiceOutcome Evaluate(GameSnapshot snapshot, int selectedEntityId,
        IReadOnlyDictionary<string, CardRecord> cards)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var imbues = ImbueEvidence.Evaluate(snapshot);
        var unknowns = new List<string>();
        var coverage = new List<string>();
        string? cardId = null, name = null;
        bool? added = null, condition = null;
        int? setCost = null;
        var resolves = false;
        var choice = ChoiceVisibility.ConfirmedOwn(snapshot);
        if (snapshot.ClientBuild != "255790" || snapshot.Phase != GamePhase.Choice ||
            choice is not { Type: "GENERAL", CountMin: 1, CountMax: 1, SourceCardId: "EDR_888" } ||
            choice.SourceEntityId is not { } sourceId || !snapshot.Entities.TryGetValue(sourceId, out var source) ||
            source.Id != sourceId || source.Kind != EntityKind.Card || source.ControllerId != snapshot.LocalPlayerId ||
            AssembledCardText.RequiresCustomText(source.Tags) ||
            source.CardId != "EDR_888" || source.Zone is not ("PLAY" or "GRAVEYARD") ||
            source.Tags.GetValueOrDefault("CARDTYPE") is { } sourceType && sourceType is not ("MINION" or "4") ||
            !cards.TryGetValue("EDR_888", out var sourceCard) || sourceCard is not { Id: "EDR_888", DbfId: 115186, Type: "MINION" } ||
            !choice.Options.Any(option => option.EntityId == selectedEntityId))
        { unknowns.Add("未确认固定版本玛洛恩来源及当前我方单选，未展开后果。"); return Result(); }

        foreach (var option in choice.Options)
        {
            if (option.CardId is null || !WildGods.TryGetValue(option.CardId, out var dbf) ||
                !cards.TryGetValue(option.CardId, out var card) || card is null || card.Id != option.CardId ||
                card.DbfId != dbf || card.Type != "MINION" || string.IsNullOrWhiteSpace(card.Name) ||
                snapshot.Entities.TryGetValue(option.EntityId, out var entity) &&
                    (entity.Id != option.EntityId || entity.Kind != EntityKind.Card ||
                     entity.CardId is { } observedId && observedId != option.CardId ||
                     entity.ControllerId is { } owner && owner != snapshot.LocalPlayerId ||
                     entity.Zone is "HAND" or "PLAY" or "GRAVEYARD" or "DECK" ||
                     entity.Tags.GetValueOrDefault("CARDTYPE") is { } type && type is not ("MINION" or "4")))
            { unknowns.Add("候选未对应已审计荒野之神，或当前身份、类别、归属及区域冲突。"); return Result(); }
        }

        var selected = choice.Options.Single(option => option.EntityId == selectedEntityId);
        cardId = selected.CardId;
        name = cards[cardId!].Name;
        condition = imbues.AtLeast(4);
        if (condition == true) setCost = 1;
        else if (condition is null) unknowns.Add("累计灌注未确认，是否将所选牌费用设为1保持未知。");
        resolves = true;
        var player = snapshot.Entities.Values.Single(entity => entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == snapshot.LocalPlayerId);
        var observedCount = player.GetIntTag("NUM_CARDS_IN_HAND");
        var handCount = snapshot.Hand.Count();
        if (observedCount is { } count && (count < 0 || count != handCount) ||
            player.GetIntTag("MAXHANDSIZE") is not (>= 0 and var limit))
            unknowns.Add("手牌数量或上限未确认，本次是否新增手牌保持未知。");
        else added = handCount < limit;
        coverage.Add("EDR_888-current-offered-wild-god");
        if (condition.HasValue) coverage.Add(condition.Value ? "EDR_888-cumulative-four-set-cost-one" : "EDR_888-cumulative-four-not-met-no-cost-grant");
        unknowns.Add("设费授予不等于最终入手费用；其他修正、入手触发和新实体须以结算后日志为准。");
        return Result();

        MalorneChoiceOutcome Result() => new(selectedEntityId, cardId, name, added, condition, setCost, imbues, resolves,
            Array.AsReadOnly(unknowns.Concat(imbues.Unknowns).Distinct(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(coverage.ToArray()));
    }
}
