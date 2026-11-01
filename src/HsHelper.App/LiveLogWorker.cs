using System.IO;
using System.Windows.Threading;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Ingestion;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private Task _liveReadTask = Task.CompletedTask;
    private Func<CancellationToken, Task>? _liveReadStarted = null;
    private LiveLogWorker? _liveWorker;
    private DateTimeOffset _lastLiveRenderAt = DateTimeOffset.MinValue;
    private int _sourceTransitionCount;
    private Task? _stopRecordingTask;
    private static readonly TimeSpan LiveRenderInterval = TimeSpan.FromMilliseconds(350);
    private const string CatchingUpStatus = "正在读取已有日志并追赶最新局面，可随时停止。";

    // This object belongs to one producer. The dispatcher reads it only after that producer settles.
    private sealed class LiveLogWorker(PowerLogStateEngine engine, DeckTrackingEnricher? tracking,
        LogRecordingPipeline? capture, long sourceLine, GameSnapshot frame, DateTimeOffset? observedAt)
    {
        public PowerLogStateEngine Engine { get; private set; } = engine;
        public DeckTrackingEnricher? Tracking { get; private set; } = tracking;
        public LogRecordingPipeline? Capture { get; private set; } = capture;
        public long SourceLine { get; private set; } = sourceLine;
        public GameSnapshot Frame { get; private set; } = frame;
        public DateTimeOffset? ObservedAt { get; private set; } = observedAt;
        public string? Path { get; private set; }
        public string? Build { get; private set; }
        public long Offset { get; private set; }
        public bool HasMoreData { get; private set; }
        public DateTimeOffset? LastReadAt { get; private set; }
        public DateTimeOffset? LastActivityAt { get; private set; }
        public DateTimeOffset? FileWriteAt { get; private set; }

        public async Task<LogReadBatch> ReadAsync(PowerLogTailer tailer, string root, CardCatalog catalog,
            SessionStore store, DeckVersion? deck, int playerIndex, Func<CancellationToken, Task>? started,
            CancellationToken token)
        {
            if (started is not null) await started(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var batch = await tailer.ReadAvailableAsync(root, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (batch.Path is null) return batch;
            Path = batch.Path; Offset = batch.ByteOffset; HasMoreData = batch.HasMoreData;
            LastReadAt = DateTimeOffset.UtcNow;
            var written = File.GetLastWriteTimeUtc(batch.Path);
            FileWriteAt = written == DateTime.FromFileTimeUtc(0) ? null : new DateTimeOffset(written);
            if (batch.Lines.Count > 0) LastActivityAt = DateTimeOffset.UtcNow;
            if (batch.ResetReason != LogResetReason.None)
            {
                if (Capture is not null) await Capture.CompleteAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Build = ClientBuildMetadata.ReadPowerLogBuild(batch.Path);
                var identity = LogSourceIdentity.Create(batch.Path, playerIndex, deck?.Id);
                if (batch.ResetReason is LogResetReason.Truncated or LogResetReason.Replaced)
                    identity += "-" + Guid.NewGuid().ToString("N")[..8];
                Engine = new PowerLogStateEngine(playerIndex is 1 or 2 ? playerIndex : null, identity,
                    autoDetectPerspective: playerIndex == 0);
                Frame = Engine.Snapshot(); ObservedAt = null; SourceLine = 0;
                Capture = new LogRecordingPipeline(store, deck?.Id, string.IsNullOrWhiteSpace(Build) ? "unknown" : Build);
                Tracking = await DeckTrackingEnricher.CreateAsync(catalog, deck, Build).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            foreach (var line in batch.Lines)
            {
                token.ThrowIfCancellationRequested();
                SourceLine++;
                if (Engine.ProcessLine(line))
                {
                    var current = Engine.Snapshot();
                    Frame = Tracking?.Observe(current) ?? current;
                    ObservedAt = DateTimeOffset.UtcNow;
                    Capture?.Observe(Frame, SourceLine);
                }
                if (Capture is { PendingCount: >= 512 } pending)
                    await pending.FlushAsync(token).ConfigureAwait(false);
            }
            if (Capture is not null) await Capture.FlushAsync(token).ConfigureAwait(false);
            return batch;
        }
    }

    private Task ReadLiveAsync()
    {
        if (_reading || !_timer.IsEnabled || _closing) return Task.CompletedTask;
        _reading = true;
        return _liveReadTask = ReadLiveCoreAsync();
    }

    private async Task ReadLiveCoreAsync()
    {
        _lastReadAttemptAt = DateTimeOffset.UtcNow;
        var generation = _sourceGeneration;
        var token = _source.Token;
        var tailer = _tailer;
        var root = _liveRoot;
        var deck = _sourceDeckVersion;
        var playerIndex = PlayerBox.SelectedIndex;
        var started = _liveReadStarted;
        // No UI path may snapshot the mutable engine while the producer is advancing it.
        _liveFrame ??= _engine.Snapshot();
        var worker = _liveWorker ??= new LiveLogWorker(_engine, _trackingFrames, _capture, _sourceLine,
            _liveFrame, _lastStateObservedAt);
        var catchUp = false;
        try
        {
            var batch = await Task.Run(() => worker.ReadAsync(tailer, root, _catalog, _store, deck,
                playerIndex, started, token), token);
            if (generation != _sourceGeneration || _closing) return;
            PublishLiveWorker(worker);
            _sourceFailed = false;
            catchUp = batch.HasMoreData;
            if (_historyContext is null && batch.ResetReason != LogResetReason.None)
                SessionBuildBox.Text = worker.Build ?? "";
            if (_historyContext is null && (worker.Frame.MatchId != _renderedMatchId ||
                worker.Frame.StateVersion != _renderedVersion)) InvalidateAdvice();
            var now = DateTimeOffset.UtcNow;
            if (!batch.HasMoreData || now - _lastLiveRenderAt >= LiveRenderInterval)
            {
                _lastLiveRenderAt = now;
                if (batch.Lines.Count > 0 || batch.ResetReason != LogResetReason.None) RenderSnapshot();
                UpdateConnectionStatus(force: true);
            }
            if (!batch.HasMoreData && StatusText.Text == CatchingUpStatus)
                StatusText.Text = batch.Path is null ? "尚未找到 Power.log，正在等待日志。" : "已追赶当前日志，正在监听新增内容。";
            if (batch.Lines.Count > 0) ScheduleAutomaticAdvice(batch.HasMoreData);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _sourceGeneration)
            { _sourceFailed = true; _timer.Stop(); StatusText.Text = "日志读取停止：" + error.Message; }
        }
        finally
        {
            // Even cancellation keeps the exact processed prefix for the stop/final-checkpoint path.
            if (ReferenceEquals(_liveWorker, worker)) PublishLiveWorker(worker);
            _reading = false;
            if (generation == _sourceGeneration) UpdateConnectionStatus();
            if (catchUp && generation == _sourceGeneration && _timer.IsEnabled && !_closing)
                _ = Dispatcher.BeginInvoke(new Action(async () =>
                {
                    // A queued catch-up callback must not resurrect a stopped or replaced source.
                    if (generation == _sourceGeneration && _timer.IsEnabled && !_closing) await ReadLiveAsync();
                }), DispatcherPriority.Background);
        }
    }

    private void PublishLiveWorker(LiveLogWorker worker)
    {
        _engine = worker.Engine; _trackingFrames = worker.Tracking; _capture = worker.Capture;
        _sourceLine = worker.SourceLine; _liveFrame = worker.Frame; _lastStateObservedAt = worker.ObservedAt;
        if (worker.Path is null) return;
        _currentLogFile = worker.Path; _currentLogOffset = worker.Offset;
        _lastSuccessfulReadAt = worker.LastReadAt; _sourceFileLastWriteAt = worker.FileWriteAt;
        _lastLogActivityAt = worker.LastActivityAt; _catchingUp = worker.HasMoreData;
    }
}
