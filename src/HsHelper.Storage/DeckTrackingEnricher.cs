using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Storage;

/// <summary>Same version-pinned tracking enrichment for desktop ingestion and CLI imports.</summary>
public sealed class DeckTrackingEnricher
{
    private readonly DeckVersion? _deck;
    private readonly string _build;
    private readonly IReadOnlyDictionary<string, int>? _cards;
    private readonly string? _expectedClass;
    private readonly Dictionary<string, DeckTracker> _trackers = new();
    private readonly PublicPlayTracker _publicPlays = new();
    private DeckTrackingEnricher(DeckVersion? deck, string build, IReadOnlyDictionary<string, int>? cards, string? expectedClass)
    { _deck = deck; _build = build; _cards = cards; _expectedClass = expectedClass; }

    public static async Task<DeckTrackingEnricher> CreateAsync(CardCatalog catalog, DeckVersion? deck, string? build,
        CancellationToken token = default)
    {
        if (deck is null || string.IsNullOrWhiteSpace(build) || build == "unknown") return new(deck, build ?? "unknown", null, null);
        var definitions = await catalog.GetByDbfIdsAsync(deck.Definition.Cards.Select(c => c.DbfId).Concat(deck.Definition.Heroes), build,
            cancellationToken: token);
        var lookup = definitions.Where(c => c.DbfId.HasValue).GroupBy(c => c.DbfId!.Value).ToDictionary(g => g.Key, g => g.First());
        if (deck.Definition.Cards.Any(c => !lookup.ContainsKey(c.DbfId))) return new(deck, build, null, null);
        var counts = deck.Definition.Cards.GroupBy(c => lookup[c.DbfId].Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        var expected = definitions.FirstOrDefault(c => c.DbfId is { } id && deck.Definition.Heroes.Contains(id))?.CardClass;
        return new(deck, build, counts, expected);
    }

    public GameSnapshot Observe(GameSnapshot snapshot)
    {
        _publicPlays.Observe(snapshot);
        snapshot = snapshot with { PublicPlays = _publicPlays.Snapshot() };
        if (_deck is null || _cards is null || snapshot.LocalPlayerId is not { } local) return snapshot;
        if (_expectedClass is not null && snapshot.LocalClass is { } actual && actual != _expectedClass)
            return snapshot with { DeckTracking = new(snapshot.MatchId, snapshot.StateVersion, _deck.Id, _build, null,
                FrozenDictionary<string, int>.Empty, 0, false, ["当前视角职业与确认套牌不一致，牌库组成保持未知。"] ) };
        if (!_trackers.TryGetValue(snapshot.MatchId, out var tracker))
            _trackers[snapshot.MatchId] = tracker = new(PinnedDeck.Create(_deck.Id, _build, local, _cards));
        tracker.Observe(snapshot);
        return snapshot.Quality.IsStable || snapshot.Phase == GamePhase.Complete
            ? snapshot with { DeckTracking = tracker.Snapshot() } : snapshot;
    }
}
