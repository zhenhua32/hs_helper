using System.Collections.Frozen;

namespace HsHelper.Core;

public sealed record PinnedDeck(string Version, string CardBuild, int LocalPlayerId,
    FrozenDictionary<string, int> Cards, string Format = "Standard")
{
    public static PinnedDeck Create(string version, string cardBuild, int localPlayerId,
        IEnumerable<KeyValuePair<string, int>> cards, string format = "Standard")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardBuild);
        if (localPlayerId is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(localPlayerId));
        var entries = cards.ToArray();
        if (entries.Length == 0 || entries.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is < 1 or > 100))
            throw new ArgumentException("起始套牌需要有效卡牌及数量。", nameof(cards));
        if (entries.Sum(pair => pair.Value) > 1000) throw new ArgumentException("套牌数量超出追踪范围。", nameof(cards));
        return new(version, cardBuild, localPlayerId, entries.ToFrozenDictionary(StringComparer.Ordinal), format);
    }
}

public sealed record DeckTrackingSnapshot(string MatchId, long StateVersion, string DeckVersion, string CardBuild,
    int? TotalCards, IReadOnlyDictionary<string, int> KnownRemaining, int UnknownCount, bool IsExact,
    IReadOnlyList<string> Warnings);

public sealed record DrawOddsResult(double? Probability, bool IsExact, string? Reason,
    int? Population, int? TargetCopies, int Draws);

/// <summary>
/// Tracks the complement of the original deck's entity instances plus cards entering the deck later.
/// Unknown removals reduce every candidate count to a conservative lower bound; they never become
/// a guessed identity. Bind once at game start and observe snapshots in event order.
/// </summary>
public sealed class DeckTracker(PinnedDeck deck)
{
    private readonly HashSet<int> _originals = [];
    private readonly Dictionary<int, string> _originalIdentities = [];
    private readonly Dictionary<int, string> _lastKnownIdentities = [];
    private readonly HashSet<int> _transformed = [];
    private string? _matchId;
    private GameSnapshot? _state;
    private bool _baseline;
    private bool _qualityLost;
    private long _lastEventSequence;

    public PinnedDeck Deck { get; } = deck;

    public void Observe(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_matchId != snapshot.MatchId)
        {
            _matchId = snapshot.MatchId;
            _originals.Clear(); _originalIdentities.Clear(); _lastKnownIdentities.Clear(); _transformed.Clear();
            _baseline = false; _qualityLost = false; _lastEventSequence = 0; _state = null;
        }
        if (_state is not null && (snapshot.EventSequence < _state.EventSequence || snapshot.StateVersion < _state.StateVersion))
            throw new InvalidOperationException("记牌器不接受倒序局面，回看历史时应创建独立记牌器。");
        _state = snapshot;
        if (snapshot.LocalPlayerId != Deck.LocalPlayerId)
        {
            if (_baseline) _qualityLost = true;
            return;
        }
        if (_baseline && (!snapshot.Quality.IsComplete || snapshot.EventHistoryTruncatedThrough > _lastEventSequence)) _qualityLost = true;

        if (!_baseline && snapshot.Quality.IsComplete && snapshot.Quality.HasGameStart &&
            (snapshot.Phase is GamePhase.Starting or GamePhase.Mulligan || snapshot.Turn is <= 1))
        {
            var initial = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card &&
                entity.ControllerId == Deck.LocalPlayerId && entity.Zone is "DECK" or "HAND" &&
                entity.CardId != "GAME_005" && entity.GetIntTag("CREATOR") is not > 0).ToArray();
            if (initial.Length == Deck.Cards.Values.Sum())
            {
                foreach (var entity in initial)
                {
                    _originals.Add(entity.Id);
                    if (entity.CardId is { } card) _originalIdentities[entity.Id] = card;
                }
                _baseline = true;
            }
        }

        foreach (var observed in snapshot.RecentEvents.Where(value => value.Sequence > _lastEventSequence).OrderBy(value => value.Sequence))
        {
            if (observed.EntityId is not { } id) continue;
            if (observed.Kind == "CardChanged")
            {
                _transformed.Add(id);
                if (observed.CardId is null) _lastKnownIdentities.Remove(id);
            }
            if (observed.CardId is { } card)
            {
                _lastKnownIdentities[id] = card;
                if (_originals.Contains(id) && !_originalIdentities.ContainsKey(id) && !_transformed.Contains(id))
                    _originalIdentities[id] = card;
            }
        }
        _lastEventSequence = snapshot.EventSequence;
        if (snapshot.Choice is { PlayerId: var chooser } choice && chooser == Deck.LocalPlayerId)
        {
            foreach (var option in choice.Options.Where(option => option.CardId is not null))
            {
                _lastKnownIdentities[option.EntityId] = option.CardId!;
                if (_originals.Contains(option.EntityId) && !_originalIdentities.ContainsKey(option.EntityId) && !_transformed.Contains(option.EntityId))
                    _originalIdentities[option.EntityId] = option.CardId!;
            }
        }
        foreach (var entity in snapshot.Entities.Values)
        {
            if (entity.CardId is not { } card) continue;
            _lastKnownIdentities[entity.Id] = card;
            if (_originals.Contains(entity.Id) && !_originalIdentities.ContainsKey(entity.Id) && !_transformed.Contains(entity.Id))
                _originalIdentities[entity.Id] = card;
        }
    }

    public DeckTrackingSnapshot Snapshot()
    {
        var warnings = new List<string>();
        var state = _state;
        if (state is null)
            return new("", 0, Deck.Version, Deck.CardBuild, null, FrozenDictionary<string, int>.Empty, 0, false,
                ["尚未观察到对局局面。"]);
        var ownDeck = state.Entities.Values.Where(entity => entity.Kind == EntityKind.Card &&
            entity.ControllerId == Deck.LocalPlayerId && entity.Zone == "DECK").ToArray();
        int? total = state.Quality.IsComplete ? ownDeck.Length : null;
        if (state.LocalPlayerId != Deck.LocalPlayerId) warnings.Add("当前视角与固定套牌的我方身份不一致。");
        if (!_baseline) warnings.Add("未在开局确认原始牌库实体，当前组成保持未知。");
        if (_qualityLost || !state.Quality.IsComplete) warnings.Add("记录存在缺口，不能计算精确牌库组成。");
        if (!state.Quality.IsStable) warnings.Add("当前效果尚未结算到稳定决策点。");
        if (state.ClientBuild is { } build && build != Deck.CardBuild) warnings.Add("固定卡库版本与该局客户端 build 不一致。");
        if (!_baseline || total is null || _qualityLost || state.LocalPlayerId != Deck.LocalPlayerId)
            return Result(FrozenDictionary<string, int>.Empty, total ?? 0, false);

        var counts = Deck.Cards.ToDictionary(StringComparer.Ordinal);
        var unknownRemoved = 0;
        var unknownAdded = 0;
        var removed = 0;
        var added = 0;
        foreach (var id in _originals)
        {
            state.Entities.TryGetValue(id, out var entity);
            var inOwnDeck = entity?.Zone == "DECK" && entity.ControllerId == Deck.LocalPlayerId;
            var transformed = _transformed.Contains(id);
            var originalCard = _originalIdentities.GetValueOrDefault(id);
            var currentCard = entity?.CardId ?? _lastKnownIdentities.GetValueOrDefault(id);
            if (inOwnDeck && (!transformed || originalCard is not null && currentCard == originalCard)) continue;
            removed++;
            if (originalCard is null) unknownRemoved++;
            else counts[originalCard] = counts.GetValueOrDefault(originalCard) - 1;
            if (inOwnDeck)
            {
                added++;
                if (currentCard is null) unknownAdded++;
                else counts[currentCard] = counts.GetValueOrDefault(currentCard) + 1;
            }
        }
        foreach (var entity in ownDeck.Where(entity => !_originals.Contains(entity.Id)))
        {
            added++;
            var card = entity.CardId ?? _lastKnownIdentities.GetValueOrDefault(entity.Id);
            if (card is null) unknownAdded++;
            else counts[card] = counts.GetValueOrDefault(card) + 1;
        }
        var expectedTotal = Deck.Cards.Values.Sum() - removed + added;
        if (expectedTotal != total || counts.Values.Any(count => count < 0))
        {
            warnings.Add("观察到的牌库与导入套牌或区域记录不一致，组成保持未知。");
            return Result(FrozenDictionary<string, int>.Empty, total.Value, false);
        }
        var lowerBounds = counts.Where(pair => pair.Value > unknownRemoved)
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value - unknownRemoved, StringComparer.Ordinal);
        var knownTotal = lowerBounds.Values.Sum();
        if (knownTotal > total)
        {
            warnings.Add("已知组成超过当前牌库数量，需重新核对记录。");
            return Result(FrozenDictionary<string, int>.Empty, total.Value, false);
        }
        if (unknownRemoved > 0 || unknownAdded > 0) warnings.Add("存在身份未公开的移出、变形或洗入，已知数量仅为可靠下界。");
        var exact = knownTotal == total && state.Quality.IsStable &&
            (state.ClientBuild is null || state.ClientBuild == Deck.CardBuild);
        return Result(lowerBounds, total.Value - knownTotal, exact);

        DeckTrackingSnapshot Result(IReadOnlyDictionary<string, int> known, int unknown, bool exact) =>
            new(state.MatchId, state.StateVersion, Deck.Version, Deck.CardBuild, total, known, unknown, exact,
                Array.AsReadOnly(warnings.ToArray()));
    }

    public DrawOddsResult CalculateDrawOdds(string cardId, int draws)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentOutOfRangeException.ThrowIfNegative(draws);
        var state = Snapshot();
        if (_state?.Phase == GamePhase.Choice)
            return new(null, false, "当前处于选择阶段，选择结果可能改变牌库，应完成选择后再计算抽牌概率。", state.TotalCards, null, draws);
        if (!state.IsExact || state.TotalCards is null)
            return new(null, false, string.Join("；", state.Warnings), state.TotalCards, null, draws);
        if (draws > state.TotalCards) return new(null, false, "抽牌次数超过当前牌库数量，疲劳不属于此概率模型。", state.TotalCards, null, draws);
        var copies = state.KnownRemaining.GetValueOrDefault(cardId);
        return new(DrawProbability.AtLeastOne(state.TotalCards.Value, copies, draws), true, null, state.TotalCards, copies, draws);
    }
}

public static class DrawProbability
{
    /// <summary>Uniform sampling without replacement, conditioned on the supplied deck composition.</summary>
    public static double AtLeastOne(int population, int successes, int draws)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(population);
        if (successes < 0 || successes > population) throw new ArgumentOutOfRangeException(nameof(successes));
        if (draws < 0 || draws > population) throw new ArgumentOutOfRangeException(nameof(draws));
        if (draws == 0 || successes == 0) return 0;
        if (draws > population - successes) return 1;
        var none = 1.0;
        for (var index = 0; index < draws; index++) none *= (population - successes - index) / (double)(population - index);
        return Math.Clamp(1 - none, 0, 1);
    }
}
