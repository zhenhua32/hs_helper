using System.Text.Json.Serialization;

namespace HsHelper.Core;

/// <summary>One observed hand-to-play transition. Replaying the same entity creates another record.</summary>
public sealed record PlayedCardRecord(long Sequence, int EntityId, string? CardId, int? ControllerId);

/// <summary>A frozen ledger of facts known at this state version, including unresolved plays.</summary>
public sealed record PublicPlayTrackingSnapshot
{
    // Never overrides the storage codec's global IgnoreReadOnlyProperties setting as well.
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string MatchId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long StateVersion { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<PlayedCardRecord> Plays { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsComplete { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<string> Warnings { get; }

    public PublicPlayTrackingSnapshot(string matchId, long stateVersion, IReadOnlyList<PlayedCardRecord> plays,
        bool isComplete, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(plays);
        ArgumentNullException.ThrowIfNull(warnings);
        MatchId = matchId;
        StateVersion = stateVersion;
        Plays = Array.AsReadOnly(plays.ToArray());
        IsComplete = isComplete;
        Warnings = Array.AsReadOnly(warnings.ToArray());
    }
}

/// <summary>
/// Consumes the perspective-filtered event stream in order. Only HAND to PLAY counts as a play;
/// summoned tokens and other graveyard arrivals cannot manufacture a play. Never uses the current
/// entity definition to fill an earlier identity. A later explicit reveal may resolve an unknown
/// record only while that exact play remains in PLAY and no replacement or data gap intervenes.
/// </summary>
public sealed class PublicPlayTracker
{
    private readonly List<PlayedCardRecord> _plays = [];
    private readonly Dictionary<int, int> _pendingReveals = [];
    private GameSnapshot? _state;
    private long _lastEventSequence;
    private bool _historyLost;
    private bool _wasComplete;

    public void Observe(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_state?.MatchId != snapshot.MatchId)
        {
            _plays.Clear(); _pendingReveals.Clear();
            _lastEventSequence = 0; _historyLost = false; _wasComplete = false; _state = null;
        }
        if (_state is not null && (snapshot.EventSequence < _state.EventSequence || snapshot.StateVersion < _state.StateVersion))
            throw new InvalidOperationException("公开出牌台账不接受倒序局面，回看历史时应使用已保存的台账或独立追踪器。");

        if (snapshot.EventHistoryTruncatedThrough > _lastEventSequence ||
            _wasComplete && !snapshot.Quality.IsComplete || !snapshot.Quality.HasGameStart)
        {
            _historyLost = true;
            // A lost zone change or transformation would make a later reveal ambiguous.
            _pendingReveals.Clear();
        }
        _wasComplete |= snapshot.Quality.IsComplete;
        foreach (var observed in snapshot.RecentEvents.Where(value => value.Sequence > _lastEventSequence &&
                     value.Sequence <= snapshot.EventSequence).OrderBy(value => value.Sequence))
        {
            if (observed.EntityId is not { } id) continue;
            if (observed.Kind == "ZoneChanged")
            {
                _pendingReveals.Remove(id);
                if (observed.FromZone != "HAND" || observed.ToZone != "PLAY") continue;
                var card = string.IsNullOrWhiteSpace(observed.CardId) ? null : observed.CardId;
                _plays.Add(new(observed.Sequence, id, card, observed.ControllerId));
                if (card is null) _pendingReveals[id] = _plays.Count - 1;
            }
            else if (observed.Kind is "CardChanged" or "EntityCreated")
                _pendingReveals.Remove(id);
            else if (observed.Kind == "CardRevealed" && !string.IsNullOrWhiteSpace(observed.CardId) &&
                     _pendingReveals.Remove(id, out var index))
            {
                // Replace an immutable value in this version; previously returned arrays remain unchanged.
                _plays[index] = _plays[index] with { CardId = observed.CardId };
            }
        }
        _lastEventSequence = snapshot.EventSequence;
        _state = snapshot;
    }

    public PublicPlayTrackingSnapshot Snapshot()
    {
        if (_state is not { } state) return new("", 0, [], false, ["尚未观察到对局局面。"]);
        var warnings = new List<string>();
        if (_historyLost) warnings.Add("公开出牌记录存在缺口，当前列表可能遗漏打出的牌。");
        if (!state.Quality.IsComplete) warnings.Add("局面信息尚不完整，公开出牌台账暂不标为完整。");
        if (_plays.Any(play => play.CardId is null)) warnings.Add("部分打出记录的公开身份未知，不会用后来变形或当前实体身份回填。");
        if (_plays.Any(play => play.ControllerId is null)) warnings.Add("部分打出记录未能确认打出时的控制方。");
        return new(state.MatchId, state.StateVersion, _plays, warnings.Count == 0, warnings);
    }
}
