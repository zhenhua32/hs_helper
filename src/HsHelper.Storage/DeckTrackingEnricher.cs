using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Storage;

/// <summary>Same version-pinned tracking enrichment for desktop ingestion and CLI imports.</summary>
public sealed class DeckTrackingEnricher
{
    private readonly DeckVersion? _deck;
    private readonly CardCatalog _catalog;
    private string _build = "unknown";
    private IReadOnlyDictionary<string, int>? _cards;
    private string? _expectedClass;
    private readonly Dictionary<string, DeckTracker> _trackers = new();
    private readonly PublicPlayTracker _publicPlays = new();
    private DeckTrackingEnricher(CardCatalog catalog, DeckVersion? deck)
    { _catalog = catalog; _deck = deck; }

    public static async Task<DeckTrackingEnricher> CreateAsync(CardCatalog catalog, DeckVersion? deck, string? build,
        CancellationToken token = default)
    {
        var tracking = new DeckTrackingEnricher(catalog, deck);
        if (!string.IsNullOrWhiteSpace(build) && build != "unknown")
            await tracking.BindBuildAsync(build, token).ConfigureAwait(false);
        return tracking;
    }

    private async Task BindBuildAsync(string build, CancellationToken token)
    {
        if (_deck is null) { _build = build; return; }
        var definitions = await _catalog.GetByDbfIdsBatchedAsync(_deck.Definition.Cards.Select(c => c.DbfId).Concat(_deck.Definition.Heroes), build,
            cancellationToken: token).ConfigureAwait(false);
        var lookup = definitions.Where(c => c.DbfId.HasValue).GroupBy(c => c.DbfId!.Value).ToDictionary(g => g.Key, g => g.First());
        _cards = _deck.Definition.Cards.All(c => lookup.ContainsKey(c.DbfId))
            ? _deck.Definition.Cards.GroupBy(c => lookup[c.DbfId].Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count)) : null;
        _expectedClass = definitions.FirstOrDefault(c => c.DbfId is { } id && _deck.Definition.Heroes.Contains(id))?.CardClass;
        _trackers.Clear();
        _build = build;
    }

    public async ValueTask<GameSnapshot> ObserveAsync(GameSnapshot snapshot, CancellationToken token = default)
    {
        // A live Power.log can be empty at attachment. CREATE_GAME binds the actual game's build.
        if (snapshot.ClientBuild is { Length: > 0 } observed && observed != "unknown" && observed != _build)
            await BindBuildAsync(observed, token).ConfigureAwait(false);
        return Observe(snapshot);
    }

    public GameSnapshot Observe(GameSnapshot snapshot)
    {
        _publicPlays.Observe(snapshot);
        snapshot = snapshot with { PublicPlays = _publicPlays.Snapshot() };
        if (_deck is null || snapshot.LocalPlayerId is not { } local) return snapshot;
        if (_cards is null)
            return snapshot with { DeckTracking = new(snapshot.MatchId, snapshot.StateVersion, _deck.Id, _build, null,
                FrozenDictionary<string, int>.Empty, 0, false, [_build == "unknown" ? "等待本局日志提供客户端版本。" :
                    $"本地版本 {_build} 的卡库缺少套牌定义，请在中文卡库页同步客户端版本。"]) };
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
