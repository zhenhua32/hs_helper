using System.Text.Json;
using System.Collections.Frozen;
using HsHelper.Core;

namespace HsHelper.Storage;

/// <summary>Buffers sanitized state differences. Flush is atomic; failed batches stay queued for retry.</summary>
public sealed class SessionRecorder
{
    private readonly SessionStore _store;
    private readonly SessionBinding _binding;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly List<PendingObservation> _pending = [];
    private GameSnapshot? _previous;
    private long _previousSourceLine;

    public SessionRecorder(SessionStore store, SessionBinding binding)
    {
        _store = store;
        _binding = binding;
        SessionStore.ValidateBinding(binding);
    }

    public int PendingCount { get { lock (_sync) return _pending.Count; } }
    public string? CurrentSessionId { get { lock (_sync) return _previous?.MatchId; } }

    /// <summary>Call for changed states. This method only enqueues in memory; call FlushAsync after a batch and on stop.</summary>
    public void Observe(GameSnapshot snapshot, long sourceLineNumber)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLineNumber);
        SessionStore.ValidatePerspective(snapshot, _binding, inspectEntities: false);
        lock (_sync)
        {
            var previous = _previous?.MatchId == snapshot.MatchId ? _previous : null;
            if (previous is not null && snapshot.EventSequence < previous.EventSequence)
                throw new InvalidOperationException("同一局的事件序号不能倒退，请为重新导入创建新的记录器。");
            if (previous is not null && snapshot.StateVersion < previous.StateVersion)
                throw new InvalidOperationException("同一局的状态版本不能倒退，请为重新导入创建新的记录器。");
            if (previous is not null && snapshot.EventSequence == previous.EventSequence && snapshot.StateVersion == previous.StateVersion)
                return;
            if (previous is null && _previous is not null)
                AddFinalCheckpoint();
            var qualityChanged = previous is null || !SamePersistentQuality(previous.Quality, snapshot.Quality);
            var saveSnapshot = previous is null || snapshot.Quality.IsStable ||
                (snapshot.Phase == GamePhase.Complete && previous.Phase != GamePhase.Complete) || qualityChanged;
            var storedObservation = saveSnapshot ? snapshot : MetadataOnly(snapshot);
            _pending.Add(new(storedObservation, sourceLineNumber, DateTimeOffset.UtcNow,
                BuildEvents(previous, snapshot, sourceLineNumber), saveSnapshot));
            _previous = snapshot;
            _previousSourceLine = sourceLineNumber;
        }
    }

    public Task ObserveAsync(GameSnapshot snapshot, long sourceLineNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observe(snapshot, sourceLineNumber);
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken);
        PendingObservation[] batch;
        try
        {
            lock (_sync) { batch = _pending.ToArray(); _pending.Clear(); }
            if (batch.Length == 0) return;
            try { await _store.SaveBatchAsync(_binding, batch, cancellationToken); }
            catch
            {
                lock (_sync) _pending.InsertRange(0, batch);
                throw;
            }
        }
        finally { _flushGate.Release(); }
    }

    /// <summary>Also saves the last observed partial state so stopping never loses its quality markers.</summary>
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) AddFinalCheckpoint();
        return FlushAsync(cancellationToken);
    }

    private void AddFinalCheckpoint()
    {
        if (_previous is not null)
            _pending.Add(new(_previous, _previousSourceLine, DateTimeOffset.UtcNow, [], true));
    }

    private static bool SamePersistentQuality(SnapshotQuality a, SnapshotQuality b)
        => a.HasGameStart == b.HasGameStart && a.IsComplete == b.IsComplete && a.HasLocalPerspective == b.HasLocalPerspective
           && PersistentWarnings(a).SequenceEqual(PersistentWarnings(b));
    private static IEnumerable<string> PersistentWarnings(SnapshotQuality quality)
        => quality.Warnings.Where(w => w is not ("尚未观察到当前任务列表结算完成" or "当前选择详情尚未完整读取"));
    private static GameSnapshot MetadataOnly(GameSnapshot snapshot)
    {
        var heroIds = snapshot.Entities.Values.Where(e => e.Kind == EntityKind.Player)
            .Select(e => e.GetIntTag("HERO_ENTITY")).Where(id => id.HasValue).ToHashSet();
        return snapshot with
        {
            Entities = snapshot.Entities.Where(pair => pair.Value.Kind != EntityKind.Card || heroIds.Contains(pair.Key))
                .ToFrozenDictionary(pair => pair.Key, pair => pair.Value),
            Choice = null, ChoiceHistory = null, TurnHistory = null, RecentEvents = [], DeckTracking = null
        };
    }

    private static IReadOnlyList<SessionEvent> BuildEvents(GameSnapshot? previous, GameSnapshot current, long sourceLine)
    {
        var events = new List<SessionEvent>();
        void Add(string kind, int? id, string? cardId, string? from, string? to, string summary, object data)
            => events.Add(new(current.EventSequence, events.Count, sourceLine, kind, id, cardId, from, to, summary,
                JsonSerializer.Serialize(data, SnapshotCodec.Options)));
        if (previous is null)
            Add("SessionObserved", null, null, null, null, current.Quality.HasGameStart ? "观察到对局开始" : "从不完整日志开始记录", new { current.Quality });
        if (previous?.Turn != current.Turn || previous?.Phase != current.Phase || previous?.ActivePlayerId != current.ActivePlayerId)
            Add("PhaseChanged", null, null, null, null, $"回合 {current.Turn?.ToString() ?? "未知"} · {current.Phase}",
                new { current.Turn, current.Phase, current.ActivePlayerId });
        foreach (var entity in current.Entities.Values.OrderBy(e => e.Id))
        {
            var old = previous?.Entities.GetValueOrDefault(entity.Id);
            if (ReferenceEquals(old, entity)) continue;
            SessionStore.ValidateEntity(entity);
            if (old is null)
                Add("EntityObserved", entity.Id, entity.CardId, null, entity.Zone, "记录实体", entity);
            else if (old.CardId != entity.CardId || old.Kind != entity.Kind || old.Tags.Count != entity.Tags.Count ||
                entity.Tags.Any(pair => !old.Tags.TryGetValue(pair.Key, out var value) || value != pair.Value))
            {
                var changes = entity.Tags.Where(pair => !old.Tags.TryGetValue(pair.Key, out var value) || value != pair.Value)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value);
                var removedTags = old.Tags.Keys.Where(key => !entity.Tags.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
                Add(old.Zone != entity.Zone ? "ZoneChanged" : "EntityChanged", entity.Id, entity.CardId, old.Zone, entity.Zone,
                    old.Zone != entity.Zone ? $"区域 {old.Zone ?? "未知"} → {entity.Zone ?? "未知"}" : "实体状态变化",
                    new { entity.Id, entity.CardId, entity.Kind, ChangedTags = changes, RemovedTags = removedTags });
            }
        }
        if (previous is not null)
            foreach (var removed in previous.Entities.Keys.Where(id => !current.Entities.ContainsKey(id)).Order())
                Add("EntityRemoved", removed, null, previous.Entities[removed].Zone, null, "实体离开当前记录", new { Id = removed });
        if (previous is not null && (!SamePersistentQuality(previous.Quality, current.Quality) || previous.Quality.IsStable != current.Quality.IsStable))
            Add("QualityChanged", null, null, null, null, "日志质量或结算状态变化", current.Quality);
        foreach (var observed in current.RecentEvents.Where(e => e.Sequence > (previous?.EventSequence ?? -1) && e.Sequence <= current.EventSequence))
            Add(observed.Kind, observed.EntityId, observed.CardId, observed.FromZone, observed.ToZone, observed.Summary, observed);
        return events.AsReadOnly();
    }
}
