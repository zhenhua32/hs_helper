using System.Globalization;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record SideboardChoiceSelection(int ChoiceId, int SourceEntityId, int SelectedEntityId, string CardId,
    long OfferedAtEventSequence, long ChosenAtEventSequence);
public sealed record SideboardSelectedCount(string CardId, int ObservedSelections, bool IsLowerBound);
public sealed record SideboardSourceChoiceEvidence(int SourceEntityId, IReadOnlyList<SideboardChoiceSelection> Selections,
    IReadOnlyList<SideboardSelectedCount> SelectedCounts);
public sealed record SideboardCurrentOfferEvidence(int ChoiceId, int SourceEntityId, long OfferedAtEventSequence,
    int CountMin, int CountMax, IReadOnlyList<ChoiceOptionSnapshot> Options)
{
    public int OptionCount => Options.Count;
    public bool HasCompleteCardIdentities => Options.All(option => !string.IsNullOrWhiteSpace(option.CardId));
    public bool IsExactOffer => true;
    public bool IsCompleteSideboardPool => false;
}
public sealed record SideboardInitialCardReference(int DbfId, string? CardId, int InitialCopies);
public sealed record SideboardStartingReference(int OwnerDbfId, bool IsValid, int? TotalCards,
    IReadOnlyList<SideboardInitialCardReference> Cards, IReadOnlyList<string> Unknowns)
{
    public string? DeckVersionId { get; init; }
    public string? CardBuild { get; init; }
    public bool IsSessionBound { get; init; }
    public bool IsStartingReferenceOnly => true;
    public bool IsCurrentPool => false;
}
public sealed record SideboardChoiceEvidenceOutcome(string MatchId, long StateVersion, long EventSequence,
    int SourceEntityId, string? SourceCardId, bool ResolvesEvidence, bool IsHistoryComplete,
    IReadOnlyList<SideboardSourceChoiceEvidence> Sources, IReadOnlyList<SideboardSelectedCount> SelectedCounts,
    SideboardCurrentOfferEvidence? CurrentOffer, SideboardStartingReference? InitialReference,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    public bool IsCurrentSourceConfirmed { get; init; }
    public bool CurrentChoiceHasHistory { get; init; }
    public bool CurrentPoolIsExact => false;
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
}

/// <summary>
/// Records actual private-choice evidence for fixed 255790 JAIL_831/dbf125998. It does not turn deckstrings,
/// current offers or later copied entities into an unobserved sideboard balance. HDT's player-side model
/// subtracts observed sideboard receipts using COPIED_FROM_ENTITY_ID and starting SETASIDE entities:
/// https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/master/Hearthstone%20Deck%20Tracker/Hearthstone/Player.cs .
/// That reference implementation is not a fixed-client proof of this card's copy/steal or consumption script.
/// </summary>
public static class SideboardChoiceEvidenceRules
{
    private const string CardId = "JAIL_831";
    private const int OwnerDbf = 125998;

    public static SideboardChoiceEvidenceOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, DeckDefinition? initialDeck = null, StartingDeckBinding? startingDeck = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var coverage = new List<string>();
        var selections = new List<SideboardChoiceSelection>(); var historyComplete = false; var sourceConfirmed = false;
        var currentChoiceHasHistory = false;
        long latestHistoricalChoiceSequence = 0;
        SideboardCurrentOfferEvidence? currentOffer = null; SideboardStartingReference? initial = null;
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        if (local is not (1 or 2) ||
            snapshot.ClientBuild != "255790" || !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete ||
            !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Phase is not (GamePhase.Main or GamePhase.Choice or GamePhase.Complete) ||
            snapshot.Entities.Any(pair => pair.Value is null || pair.Value.Tags is null || pair.Key != pair.Value.Id) ||
            !snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => entity.GetIntTag("PLAYER_ID"))
                .Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("需要固定 build 255790 的完整稳定、非观战且已确认我方的局面，未返回私有边栏选择证据。"); return Result(false); }
        if (sourceEntityId <= 0 || !cards.TryGetValue(CardId, out var sourceCard) || !AuditedSource(sourceCard))
        { unknowns.Add("缺少固定下水道之王的来源 ID、DBF、类型或卡面定义。"); return Result(false); }

        ReadHistory(local.Value);
        sourceConfirmed = snapshot.Entities.TryGetValue(sourceEntityId, out var source) && source.Id == sourceEntityId &&
            source.Kind == EntityKind.Card && source.CardId == CardId && source.ControllerId == local &&
            source.Zone is "HAND" or "PLAY" or "GRAVEYARD" &&
            Alias(source.Tags, "50", value => value == local) && Alias(source.Tags, "49", value => value == Zone(source.Zone)) &&
            Type(source.Tags) && !AssembledCardText.RequiresCustomText(source.Tags);
        if (!sourceConfirmed && selections.All(selection => selection.SourceEntityId != sourceEntityId))
        {
            selections.Clear(); historyComplete = false;
            unknowns.Add("该来源没有当前可见的我方下水道之王身份，也没有同局实际已选的冻结来源证据。");
            return Result(false);
        }
        if (!sourceConfirmed) unknowns.Add("查询来源只由过去冻结的选择身份确认，不将后来变身、转移或隐藏的实体当作当前原生来源。");

        if (ChoiceVisibility.ConfirmedOwn(snapshot) is { } choice && choice.SourceEntityId == sourceEntityId)
        {
            // A current ID already present in a bound own ledger is not a fresh offer. This also
            // covers contradictory/duplicate records deliberately omitted from the selected facts.
            currentChoiceHasHistory = snapshot.ChoiceHistory is { Entries: not null } history &&
                history.MatchId == snapshot.MatchId && history.StateVersion == snapshot.StateVersion && history.EventSequence == snapshot.EventSequence &&
                history.Entries.Any(entry => entry is not null && entry.PlayerId == local && entry.ChoiceId == choice.Id);
            if (sourceConfirmed && source!.Zone is "PLAY" or "GRAVEYARD" && choice.Type == "GENERAL" && choice.CountMin == 1 && choice.CountMax == 1 &&
                choice.SourceCardId == CardId && !currentChoiceHasHistory && choice.OfferedAtEventSequence > 0 &&
                choice.OfferedAtEventSequence > latestHistoricalChoiceSequence && choice.OfferedAtEventSequence <= snapshot.EventSequence && choice.Options.Count <= 64 &&
                !selections.Any(selection => selection.ChoiceId == choice.Id))
            {
                currentOffer = new(choice.Id, sourceEntityId, choice.OfferedAtEventSequence, choice.CountMin, choice.CountMax,
                    Array.AsReadOnly(choice.Options.Select(option => option with { }).ToArray()));
                coverage.Add("JAIL_831-current-confirmed-own-offer-exact-count-only");
            }
            else unknowns.Add("当前候选缺少一致的提供时来源、事件序号或身份绑定，未当作当前边栏候选。");
        }
        if (startingDeck is not null)
        {
            var bound = StartingDeckBinding.FreezeForSnapshot(snapshot, snapshot.ClientBuild, startingDeck);
            if (bound is null || initialDeck is not null && !StartingDeckBinding.SameDefinition(initialDeck, bound.Definition))
                unknowns.Add("起始牌码参考的范围、来源或内容存在冲突，未返回其标识和卡牌，也未回退为未绑定参考。");
            else
            {
                initial = StartingReference(bound.Definition, cards, true) with
                { DeckVersionId = bound.DeckVersionId, CardBuild = bound.CardBuild, IsSessionBound = true };
                coverage.Add("original-session-starting-deck-reference-not-current-pool");
            }
        }
        else if (initialDeck is not null) initial = StartingReference(initialDeck, cards);
        unknowns.Add("已选次数只表示冻结来源为下水道之王的实际选择，不证明成功入手、剩余边栏或重复战吼的完整候选池。");
        unknowns.Add("不同来源实体的选择分别保留；复制、偷取及其它代为重复战吼的池继承未确认，不把新来源当全新三张，也不合并成精确剩余池。");
        if (currentOffer is not null) unknowns.Add("当前候选仅是本次实际提供的选项；不用于补齐过去选择，也不代表边栏完整余量或重复卡数量。");
        coverage.Add("JAIL_831-actual-chosen-rows-grouped-by-frozen-source");
        return Result(true);

        void ReadHistory(int localPlayer)
        {
            if (snapshot.ChoiceHistory is not { } history || history.Entries is null || history.Warnings is null ||
                history.MatchId != snapshot.MatchId || history.StateVersion != snapshot.StateVersion || history.EventSequence != snapshot.EventSequence ||
                history.TruncatedThroughSequence < 0 || history.TruncatedThroughSequence > snapshot.EventSequence || history.Entries.Count > 32)
            { unknowns.Add("缺少同局、同状态及同事件序号的选择台账，当前候选不能替代未观察历史。"); return; }
            historyComplete = history.IsComplete && history.TruncatedThroughSequence == 0 && history.Warnings.Count == 0;
            var duplicates = history.Entries.Where(entry => entry is not null).GroupBy(entry => entry.ChoiceId)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var entry in history.Entries)
            {
                if (entry is null || entry.PlayerId != localPlayer || entry.ChoiceId <= 0 || duplicates.Contains(entry.ChoiceId) ||
                    !entry.IsComplete || entry.OfferedAtEventSequence <= 0 || entry.ChosenAtEventSequence <= entry.OfferedAtEventSequence ||
                    entry.ChosenAtEventSequence > snapshot.EventSequence || string.IsNullOrWhiteSpace(entry.SourceCardId))
                { historyComplete = false; continue; }
                latestHistoricalChoiceSequence = Math.Max(latestHistoricalChoiceSequence, entry.ChosenAtEventSequence);
                if (entry.SourceCardId != CardId) continue;
                if (entry.Type != "GENERAL" || entry.SourceEntityId is not > 0 || entry.Options is null || entry.ChosenEntityIds is null ||
                    snapshot.Entities.TryGetValue(entry.SourceEntityId.Value, out var historicalSource) && historicalSource.Kind is EntityKind.Game or EntityKind.Player ||
                    entry.Options.Count > 64 || entry.Options.Any(option => option is null || option.EntityId <= 0 || option.ControllerId is { } owner && owner != localPlayer) ||
                    entry.Options.Select(option => option.EntityId).Distinct().Count() != entry.Options.Count ||
                    entry.CountMin < 0 || entry.CountMax < entry.CountMin || entry.CountMax > entry.Options.Count ||
                    entry.ChosenEntityIds.Count < entry.CountMin || entry.ChosenEntityIds.Count > entry.CountMax ||
                    entry.ChosenEntityIds.Distinct().Count() != entry.ChosenEntityIds.Count ||
                    entry.ChosenEntityIds.Any(id => id <= 0 || entry.Options.Count(option => option.EntityId == id && !string.IsNullOrWhiteSpace(option.CardId)) != 1 ||
                        snapshot.Entities.TryGetValue(id, out var selectedEntity) && selectedEntity.Kind is EntityKind.Game or EntityKind.Player))
                { historyComplete = false; continue; }
                foreach (var id in entry.ChosenEntityIds)
                    selections.Add(new(entry.ChoiceId, entry.SourceEntityId.Value, id, entry.Options.Single(option => option.EntityId == id).CardId!,
                        entry.OfferedAtEventSequence, entry.ChosenAtEventSequence));
            }
            if (!historyComplete) unknowns.Add("选择台账有缺口、截断、迟到或不一致记录；已列选择与次数只作已观察下限，缺失明细不会当作零次。");
            coverage.Add("match-state-sequence-bound-chosen-history-no-future-identity");
        }

        SideboardChoiceEvidenceOutcome Result(bool resolves)
        {
            var ordered = selections.OrderBy(selection => selection.ChosenAtEventSequence).ThenBy(selection => selection.ChoiceId)
                .ThenBy(selection => selection.SelectedEntityId).ToArray();
            var sources = ordered.GroupBy(selection => selection.SourceEntityId).OrderBy(group => group.Key)
                .Select(group => new SideboardSourceChoiceEvidence(group.Key, Array.AsReadOnly(group.ToArray()), Counts(group, !historyComplete))).ToArray();
            return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, sourceEntityId, resolves ? CardId : null,
                resolves, historyComplete, Array.AsReadOnly(sources), Counts(ordered, !historyComplete), currentOffer, initial,
                Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(coverage.ToArray()))
                { IsCurrentSourceConfirmed = sourceConfirmed, CurrentChoiceHasHistory = currentChoiceHasHistory };
        }
    }

    private static IReadOnlyList<SideboardSelectedCount> Counts(IEnumerable<SideboardChoiceSelection> selections, bool lowerBound) =>
        Array.AsReadOnly(selections.GroupBy(selection => selection.CardId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SideboardSelectedCount(group.Key, group.Count(), lowerBound)).ToArray());

    private static SideboardStartingReference StartingReference(DeckDefinition deck, IReadOnlyDictionary<string, CardRecord> cards, bool sessionBound = false)
    {
        var unknowns = new List<string>();
        if (deck.Cards is null || deck.Sideboards is null || deck.Cards.Any(card => card is null) || deck.Sideboards.Any(board => board is null) ||
            deck.Cards.All(card => card.DbfId != OwnerDbf || card.Count < 1) || deck.Sideboards.Count(board => board.OwnerDbfId == OwnerDbf) != 1)
            return Invalid("提供的起始牌码未唯一包含下水道之王与其边栏；不能视为空的当前池。");
        var board = deck.Sideboards.Single(board => board.OwnerDbfId == OwnerDbf);
        if (board.Cards is null || board.Cards.Count > 3 || board.Cards.Any(card => card is null || card.DbfId <= 0 || card.Count is < 1 or > 3) ||
            board.Cards.Select(card => card.DbfId).Distinct().Count() != board.Cards.Count || board.Cards.Sum(card => (long)card.Count) != 3)
            return Invalid("起始参考边栏数量或条目不合法；不补齐三张，也不把重复条目合并为观察事实。");
        var result = board.Cards.OrderBy(card => card.DbfId).Select(card =>
        {
            var matches = cards.Values.Where(definition => definition is not null && definition.DbfId == card.DbfId &&
                !string.IsNullOrWhiteSpace(definition.Id) && cards.TryGetValue(definition.Id, out var keyed) && ReferenceEquals(keyed, definition) &&
                DefinitionIdentity(definition)).ToArray();
            var id = matches.Length == 1 ? matches[0].Id : null;
            if (id is null) unknowns.Add("部分起始参考 DBF 没有唯一固定卡牌身份，保留 DBF 与份数，不由候选或历史回填。");
            return new SideboardInitialCardReference(card.DbfId, id, card.Count);
        }).ToArray();
        unknowns.Add(sessionBound
            ? "仅为本局记录的原始牌码参考，不证明当前完整池、选择消耗或复制、偷取后的池继承，也不声明剩余数量。"
            : "仅为调用方提供的起始牌码参考，未核验其对局/套牌版本绑定；不声明当前边栏或剩余数量。");
        return new(OwnerDbf, true, 3, Array.AsReadOnly(result), Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()));
        SideboardStartingReference Invalid(string reason) => new(OwnerDbf, false, null, Array.AsReadOnly(Array.Empty<SideboardInitialCardReference>()), Array.AsReadOnly(new[] { reason }));
    }

    private static bool Alias(IReadOnlyDictionary<string, string> tags, string alias, Func<int, bool> match) =>
        !tags.TryGetValue(alias, out var value) || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && match(parsed);
    private static int Zone(string? value) => value switch { "PLAY" => 1, "HAND" => 3, "GRAVEYARD" => 4, _ => -1 };
    private static bool Type(IReadOnlyDictionary<string, string> tags) =>
        (!tags.TryGetValue("CARDTYPE", out var named) || named is "MINION" or "4") &&
        (!tags.TryGetValue("202", out var numeric) || numeric is "MINION" or "4");
    private static bool AuditedSource(CardRecord? card)
    {
        if (!NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, CardId, card)) return false;
        if (card is null || card.Id != CardId || card.DbfId != OwnerDbf || card.Type != "MINION" ||
            card.Text != "构筑套牌时，选择三张私藏的野兽牌。<b>战吼：</b>从中<b>发现</b>一张，其法力值消耗减少（3）点。") return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            var mechanics = raw.GetProperty("mechanics").EnumerateArray().Select(value => value.GetString()).ToHashSet();
            return raw.GetProperty("id").GetString() == CardId && raw.GetProperty("dbfId").GetInt32() == OwnerDbf &&
                raw.GetProperty("type").GetString() == "MINION" && raw.GetProperty("text").GetString() == card.Text &&
                mechanics.Contains("BATTLECRY") && mechanics.Contains("DISCOVER");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return false; }
    }

    private static bool DefinitionIdentity(CardRecord card)
    {
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return raw.GetProperty("id").GetString() == card.Id && raw.GetProperty("dbfId").GetInt32() == card.DbfId;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return false; }
    }
}
