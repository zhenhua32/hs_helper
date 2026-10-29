using HsHelper.Core;

namespace HsHelper.Storage;

/// <summary>Shares one recording policy between live ingestion and offline import, binding perspective separately per game.</summary>
public sealed class LogRecordingPipeline
{
    private sealed class UnknownView(GameSnapshot first, long line)
    {
        public GameSnapshot First { get; } = first;
        public long FirstLine { get; } = line;
        public GameSnapshot Last { get; set; } = first;
        public long LastLine { get; set; } = line;
    }

    private readonly SessionStore _store;
    private readonly string? _deckVersionId;
    private readonly string _cardBuild;
    private readonly string _format;
    private readonly string _region;
    private readonly string? _playerClass;
    private readonly Dictionary<string, SessionRecorder> _recorders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perspectives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnknownView> _unknown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _spectated = new(StringComparer.Ordinal);

    public LogRecordingPipeline(SessionStore store, string? deckVersionId, string cardBuild,
        string format = "STANDARD", string region = "CN", string? playerClass = null)
    {
        SessionStore.ValidateBinding(new(deckVersionId, cardBuild, null, format, region, playerClass));
        _store = store; _deckVersionId = deckVersionId; _cardBuild = cardBuild;
        _format = format; _region = region; _playerClass = playerClass;
    }

    public string? CurrentSessionId { get; private set; }
    public int RecordedMatchCount => _recorders.Count;
    public int UnconfirmedMatchCount => _unknown.Count;
    public int SpectatedMatchCount => _spectated.Count;
    public int PendingCount => _recorders.Values.Sum(r => r.PendingCount);

    public void Observe(GameSnapshot snapshot, long sourceLineNumber)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLineNumber);
        if (!snapshot.Quality.HasGameStart && snapshot.Entities.Count == 0) return;
        if (snapshot.IsSpectating)
        {
            _spectated.Add(snapshot.MatchId);
            CurrentSessionId = null;
            return;
        }
        if (!_recorders.TryGetValue(snapshot.MatchId, out var recorder))
        {
            if (snapshot.LocalPlayerId is not { } local || !snapshot.Quality.HasLocalPerspective)
            {
                if (!_unknown.TryGetValue(snapshot.MatchId, out var buffer)) _unknown[snapshot.MatchId] = buffer = new(snapshot, sourceLineNumber);
                buffer.Last = snapshot; buffer.LastLine = sourceLineNumber;
                CurrentSessionId = null;
                return;
            }
            recorder = new SessionRecorder(_store, new(_deckVersionId,
                _cardBuild == "unknown" ? snapshot.ClientBuild ?? _cardBuild : _cardBuild,
                local, snapshot.Format ?? _format, _region, snapshot.LocalClass ?? _playerClass));
            _recorders.Add(snapshot.MatchId, recorder);
            _perspectives.Add(snapshot.MatchId, local);
            if (_unknown.Remove(snapshot.MatchId, out var unknown))
            {
                if (unknown.First.EventSequence < snapshot.EventSequence) recorder.Observe(unknown.First, unknown.FirstLine);
                if (unknown.Last.EventSequence > unknown.First.EventSequence && unknown.Last.EventSequence < snapshot.EventSequence)
                    recorder.Observe(unknown.Last, unknown.LastLine);
            }
        }
        if (snapshot.LocalPlayerId is { } current && current != _perspectives[snapshot.MatchId])
            throw new InvalidOperationException("该局的记录视角已固定，切换视角需重新建立独立会话。");
        recorder.Observe(snapshot, sourceLineNumber);
        CurrentSessionId = snapshot.MatchId;
    }

    public Task ObserveAsync(GameSnapshot snapshot, long sourceLineNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observe(snapshot, sourceLineNumber);
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        foreach (var recorder in _recorders.Values.ToArray()) await recorder.FlushAsync(cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var recorder in _recorders.Values.ToArray()) await recorder.CompleteAsync(cancellationToken);
    }
}
