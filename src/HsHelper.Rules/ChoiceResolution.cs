using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record ChoiceCardOutcome(int OptionEntityId, string CardId, string Name, string Origin,
    bool? AddedToHand, int? GrantedCostReduction, int? ResultingCost)
{
    public string Destination => AddedToHand switch { true => "Hand", false => "NotAddedHandFull", _ => "Unknown" };
}

public sealed record ChoiceDirectDeckChange(string RemovedCardId, int RemovedCopies,
    int? TotalCardsBefore, int? TotalCardsAfter, bool CompositionIsExact,
    IReadOnlyDictionary<string, int> KnownRemainingAfter);

/// <summary>Direct consequences of the selected offered card, not a fabricated next authoritative snapshot.</summary>
public sealed record ChoiceResolutionEvaluation(string MatchId, long StateVersion, long EventSequence,
    int? ChoiceId, LineVerification Verification, bool? IsLegal, int? SourceEntityId,
    string? SourceCardId, string? SourceName, IReadOnlyList<int> SelectedEntityIds,
    IReadOnlyList<ChoiceCardOutcome> CardOutcomes, int? HandCountBefore, int? HandCountAfter,
    int? HandLimit, ChoiceDirectDeckChange? DirectDeckChange, IReadOnlyList<string> Errors,
    IReadOnlyList<string> Assumptions, IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    public bool SelectionOnly => CardOutcomes.Count == 0;
    public bool ResolvesEffects => CardOutcomes.Count > 0;
    public bool CanEvaluateFollowupActions => false;
}

public sealed partial class LineEvaluator
{
    /// <summary>
    /// Evaluate already offered, current own Discover choices. This does not generate random candidates,
    /// create hand entity IDs, reuse printed costs as current costs, or simulate later card plays.
    /// Card identities/text are audited against the fixed build's cards.json (see the regression fixture).
    /// </summary>
    public ChoiceResolutionEvaluation EvaluateChoice(GameSnapshot snapshot, IReadOnlyList<int> selectedEntityIds,
        IReadOnlyDictionary<string, CardRecord> cards, int? expectedChoiceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(selectedEntityIds);
        ArgumentNullException.ThrowIfNull(cards); cancellationToken.ThrowIfCancellationRequested();
        var selected = selectedEntityIds.ToArray();
        // Do not retain caller-owned option lists in either validation or the effect projection.
        if (snapshot.Choice is { } offered)
            snapshot = snapshot with { Choice = offered with
            {
                Options = Array.AsReadOnly(offered.Options?.ToArray() ?? []),
                IsReady = offered.IsReady && offered.Options is not null
            } };
        var validation = ValidateChoice(snapshot, selected, expectedChoiceId, cancellationToken);
        var errors = validation.Errors.ToList();
        var coverage = validation.RuleCoverage.ToList();
        var unknowns = new List<string>();
        var assumptions = new List<string>
        {
            "只推导当前已提供候选被选中后的直接后果，不生成候选、不创建新的手牌实体，也不把预览当作实际结算后的局面。"
        };
        int? sourceEntityId = null;
        string? sourceCardId = null, sourceName = null;
        int? handBefore = null, handAfter = null, handLimit = null;
        ChoiceDirectDeckChange? deckChange = null;
        var outcomes = new List<ChoiceCardOutcome>();
        if (validation.Verification != LineVerification.Verified)
        {
            unknowns.AddRange(validation.Assumptions);
            return Result(validation.Verification, validation.Verification == LineVerification.Invalid ? false : null);
        }
        var choice = snapshot.Choice!;
        if (snapshot.ClientBuild != AuditedBuild)
        { unknowns.Add("选择后牌效只审计了 build 255790，其他版本需要重新核对。"); return Result(LineVerification.Unsupported, true); }
        if (snapshot.Phase != GamePhase.Choice || choice.Type != "GENERAL" || selected.Length != 1 ||
            choice.CountMin != 1 || choice.CountMax != 1)
        { unknowns.Add("当前只展开 GENERAL 单选发现的直接后果；留牌、拼装或其他数量的选择仍只核对范围。"); return Result(LineVerification.Unsupported, true); }
        if (choice.Options.Any(option => option.ControllerId is { } owner && owner != snapshot.LocalPlayerId))
        { unknowns.Add("候选集合存在归属冲突，不能可靠推导选择后效果。"); return Result(LineVerification.Unsupported, true); }
        if (choice.SourceEntityId is not { } sourceId || !snapshot.Entities.TryGetValue(sourceId, out var source) ||
            source.Kind != EntityKind.Card || source.ControllerId != snapshot.LocalPlayerId ||
            source.Zone is not ("PLAY" or "GRAVEYARD") || source.CardId is null)
        { unknowns.Add("未确认我方已使用的选择来源，不能把手中、牌库中或对手的卡当作已触发的来源。"); return Result(LineVerification.Unsupported, true); }
        sourceEntityId = source.Id; sourceCardId = source.CardId;
        if (!cards.TryGetValue(source.CardId, out var sourceCard) || sourceCard.Id != source.CardId || string.IsNullOrWhiteSpace(sourceCard.Name))
        { unknowns.Add("固定版本卡库缺少选择来源的资料，不能核对其效果。"); return Result(LineVerification.Unsupported, true); }
        sourceName = sourceCard.Name;
        var origin = source.CardId switch
        {
            "CORE_DS1_184" when sourceCard.Type == "SPELL" => "Deck",
            "JAIL_831" when sourceCard.Type == "MINION" => "Sideboard",
            "TLC_824" when sourceCard.Type == "SPELL" => "DiscoverPool",
            _ => null
        };
        if (origin is null)
        { unknowns.Add("该来源的发现、拼装或回溯效果尚未展开。"); return Result(LineVerification.Unsupported, true); }
        var option = choice.Options.Single(option => option.EntityId == selected[0]);
        if (string.IsNullOrWhiteSpace(option.CardId))
        { unknowns.Add("所选候选身份未知，不能虚构入手卡牌。"); return Result(LineVerification.Unsupported, true); }
        if (snapshot.Entities.TryGetValue(option.EntityId, out var candidate) &&
            (candidate.CardId is { } observedCard && observedCard != option.CardId ||
             candidate.ControllerId is { } observedOwner && observedOwner != snapshot.LocalPlayerId ||
             candidate.Zone is "HAND" or "PLAY" or "GRAVEYARD"))
        { unknowns.Add("所选候选与实体身份、归属或已观察区域冲突，不能重复生成卡牌。"); return Result(LineVerification.Unsupported, true); }
        cancellationToken.ThrowIfCancellationRequested();
        var players = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player &&
            entity.GetIntTag("PLAYER_ID") == snapshot.LocalPlayerId).Take(2).ToArray();
        if (players.Length != 1)
        { unknowns.Add("我方玩家实体未唯一确认，无法推导手牌数量。"); return Result(LineVerification.Unsupported, true); }
        var player = players[0];
        var observedHand = player.GetIntTag("NUM_CARDS_IN_HAND");
        var entityHandCount = snapshot.Hand.Count();
        if (observedHand is { } count && (count < 0 || count != entityHandCount))
            unknowns.Add("手牌数量标签与当前实体数量冲突，入手后的数量保持未知。");
        else handBefore = observedHand ?? entityHandCount;
        handLimit = player.GetIntTag("MAXHANDSIZE") is >= 0 and var maximum ? maximum : null;
        bool? added = handBefore.HasValue && handLimit.HasValue ? handBefore.Value < handLimit.Value : null;
        handAfter = added switch { true => handBefore + 1, false => handBefore, _ => null };
        if (!handLimit.HasValue) unknowns.Add("当前手牌上限未观察到，不能确认是否会因满手而无法加入。");
        if (origin == "Deck")
        {
            var tracking = snapshot.DeckTracking;
            var matches = tracking is not null && tracking.MatchId == snapshot.MatchId &&
                tracking.StateVersion == snapshot.StateVersion && tracking.CardBuild == AuditedBuild && tracking.KnownRemaining is not null;
            var counts = matches ? tracking!.KnownRemaining!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : [];
            if (matches && (tracking!.UnknownCount < 0 || tracking.TotalCards < 0 || counts.Values.Any(copies => copies < 0) ||
                tracking.TotalCards is { } total && counts.Values.Sum(copies => (long)copies) > total))
            { matches = false; counts.Clear(); unknowns.Add("牌库台账数量存在冲突，已丢弃该组成。"); }
            var exact = matches && tracking is { IsExact: true, UnknownCount: 0, TotalCards: >= 0 } &&
                counts.Values.All(copies => copies >= 0) && counts.Values.Sum(copies => (long)copies) == tracking.TotalCards;
            var before = matches && tracking!.TotalCards is >= 0 ? tracking.TotalCards :
                player.GetIntTag("NUM_CARDS_IN_DECK") is >= 0 and var observedDeck ? observedDeck : null;
            if (before == 0 || exact && choice.Options.Where(candidateOption => candidateOption.CardId is not null)
                .Any(candidateOption => counts.GetValueOrDefault(candidateOption.CardId!) <= 0))
            { unknowns.Add("追踪术当前候选与已观察的空牌库或精确台账冲突，未推导牌库变化。"); return Result(LineVerification.Unsupported, true); }
            if (counts.GetValueOrDefault(option.CardId) > 0) counts[option.CardId]--;
            if (!exact) unknowns.Add("没有匹配且精确的牌库组成，只能确认追踪术直接移出所选一张牌，不能补齐其他未知牌。");
            deckChange = new(option.CardId, 1, before, before.HasValue ? before.Value - 1 : null,
                exact, counts.ToFrozenDictionary(StringComparer.Ordinal));
            coverage.AddRange(["CORE_DS1_184-selected-card-leaves-deck", "CORE_DS1_184-other-options-stay-in-deck"]);
        }
        else if (origin == "Sideboard")
        {
            coverage.AddRange(["JAIL_831-selected-sideboard-card", "JAIL_831-granted-cost-reduction-3"]);
            unknowns.Add("未模拟后续重复战吼的边栏候选池变化。");
        }
        else
        {
            coverage.Add("TLC_824-selected-offered-beast");
            unknowns.Add("地图在本回合使用所选牌后的追加选择尚未触发，本次不模拟该后续过程。");
        }
        cards.TryGetValue(option.CardId, out var selectedCard);
        if (selectedCard is null || selectedCard.Id != option.CardId)
            unknowns.Add("版本卡库缺少所选卡牌名称或效果，保留已观察的 CardId。");
        outcomes.Add(new(option.EntityId, option.CardId,
            selectedCard?.Id == option.CardId ? selectedCard.Name : "未知卡名", origin, added,
            origin == "Sideboard" ? 3 : null, null));
        coverage.AddRange(["current-offered-card-identity", "direct-hand-capacity-projection"]);
        if (added == false)
            assumptions.Add("当前手牌已满，所选牌不能加入手牌；追踪术仍直接移出该牌，不移除其他候选，也不虚构烧牌实体最终区域。");
        unknowns.Add("候选费用可能已含修正；最终入手费用、新手牌实体及其他入手/抽牌触发须以选择后日志为准。不能把卡库印刷费用或候选费用再次减费后用于后续动作。");
        return Result(LineVerification.Conditional, true);

        ChoiceResolutionEvaluation Result(LineVerification verification, bool? legal) => new(
            snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, snapshot.Choice?.Id,
            verification, legal, sourceEntityId, sourceCardId, sourceName,
            Array.AsReadOnly(selected), Array.AsReadOnly(outcomes.ToArray()), handBefore, outcomes.Count > 0 ? handAfter : null, handLimit, deckChange,
            Array.AsReadOnly(errors.ToArray()), Array.AsReadOnly(assumptions.ToArray()),
            Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(coverage.Distinct(StringComparer.Ordinal).ToArray()));
    }
}
