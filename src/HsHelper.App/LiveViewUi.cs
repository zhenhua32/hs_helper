using System.IO;
using System.Windows;
using HsHelper.Core;
using HsHelper.Coaching;
using HsHelper.Rules;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private DateTimeOffset? _lastSuccessfulReadAt;
    private DateTimeOffset? _sourceFileLastWriteAt;
    private bool _sourceFailed;
    private bool _catchingUp;
    private bool _viewIsFileReplay;
    private SnapshotViewKind? _lastAccessKind;
    private long _battleRenderRevision;
    private LocalToolsWindow? _localTools;

    private StartingDeckBinding? StartingDeckForSnapshot(GameSnapshot snapshot, string? build)
    {
        if (_historyContext is { } history)
        {
            if (history.SessionId != snapshot.MatchId || history.Snapshot.MatchId != snapshot.MatchId ||
                history.Snapshot.StateVersion != snapshot.StateVersion || history.Snapshot.EventSequence != snapshot.EventSequence ||
                history.Snapshot.LocalPlayerId != snapshot.LocalPlayerId || history.CardBuild != build) return null;
            return StartingDeckContext.FromSavedDeck(snapshot, build, history.Deck, history.DeckVersionId);
        }
        return _sourceBindingEstablished
            ? StartingDeckContext.FromSavedDeck(snapshot, build, _sourceDeckVersion, _sourceDeckVersion?.Id) : null;
    }

    private SnapshotAccess CurrentAccess => SnapshotAccessPolicy.Evaluate(DisplaySnapshot, DateTimeOffset.UtcNow,
        _historyContext is not null, _viewIsFileReplay, _timer.IsEnabled, _loadingSource || _historyLoading,
        _catchingUp, _sourceFailed, _lastSuccessfulReadAt, _sourceFileLastWriteAt);

    private void RefreshAccessState()
    {
        var access = CurrentAccess;
        if (_lastAccessKind is { } previous && previous != access.Kind)
        {
            InvalidateAdvice(); _localTools?.Invalidate(access.Reason);
            _ = Run(UpdateOverlayAsync);
        }
        _lastAccessKind = access.Kind;
        if (AccessText is not null) AccessText.Text = $"{access.Label} · {access.Reason}";
        UpdateConnectionStatus();
    }

    private async Task RenderBattleAsync()
    {
        var revision = ++_battleRenderRevision;
        var snapshot = DisplaySnapshot;
        var build = _historyContext?.CardBuild ?? snapshot.ClientBuild ?? SessionBuildBox.Text.Trim();
        var history = _historyContext;
        var sourceDeck = _sourceDeckVersion;
        var cards = string.IsNullOrWhiteSpace(build) || build == "unknown" ? [] :
            await _catalog.GetByIdsAsync(BattleViewBuilder.VisibleCardIds(snapshot, build), build);
        var donorDbfIds = BattleViewBuilder.VisibleEffectDbfIds(snapshot, build);
        if (donorDbfIds.Count > 0)
            cards = cards.Concat(await _catalog.GetByDbfIdsAsync(donorDbfIds, build)).DistinctBy(card => card.Id).ToArray();
        if (revision != _battleRenderRevision || DisplaySnapshot.MatchId != snapshot.MatchId ||
            DisplaySnapshot.StateVersion != snapshot.StateVersion || DisplaySnapshot.EventSequence != snapshot.EventSequence ||
            DisplaySnapshot.LocalPlayerId != snapshot.LocalPlayerId ||
            !ReferenceEquals(history, _historyContext) || !ReferenceEquals(sourceDeck, _sourceDeckVersion) ||
            (_historyContext?.CardBuild ?? DisplaySnapshot.ClientBuild ?? SessionBuildBox.Text.Trim()) != build) return;
        var view = BattleViewBuilder.Build(snapshot, cards.ToDictionary(c => c.Id), build);
        BattleViewRoot.DataContext = view;
        RefreshAccessState();
    }

    private void LocalToolsClicked(object sender, RoutedEventArgs e)
    {
        var tools = BindLocalToolsWindow();
        if (tools is not null) { tools.Owner = this; tools.Show(); }
    }

    private LocalToolsWindow? BindLocalToolsWindow()
    {
        var snapshot = DisplaySnapshot;
        if (snapshot.Entities.Count == 0) { StatusText.Text = "请先读取或选择一个局面。"; return null; }
        var build = _historyContext?.CardBuild ?? snapshot.ClientBuild ?? SessionBuildBox.Text.Trim();
        var historical = CurrentAccess.Kind != SnapshotViewKind.Live;
        var startingDeck = StartingDeckForSnapshot(snapshot, build);
        if (_localTools is null)
        {
            _localTools = new LocalToolsWindow(snapshot, build, _catalog, historical, startingDeck);
            _localTools.Closed += (_, _) => _localTools = null;
        }
        else _localTools.UpdateBinding(snapshot, build, historical, startingDeck);
        if (_loadingSource || _historyLoading) _localTools.Invalidate("日志或历史局面正在读取，请稍后重新绑定。");
        return _localTools;
    }

    private async void ReviewLastClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_loadingSource || _reading) throw new InvalidOperationException("日志正在读取，请稍后再固定局面。");
        var frame = _liveFrame ?? _engine.Snapshot();
        if (frame.Entities.Count == 0) throw new InvalidOperationException("尚无可复盘的局面。");
        var revision = ++_historyRevision;
        var deck = _sourceDeckVersion;
        var build = frame.ClientBuild ?? SessionBuildBox.Text.Trim();
        var context = await _store.GetHistoricalContextAtAsync(frame.MatchId, frame.EventSequence);
        if (revision != _historyRevision) return;
        var points = await _store.ListDecisionPointsAsync(frame.MatchId, includeUnstable: true);
        if (revision != _historyRevision) return;
        _historyObservedAt = points.FirstOrDefault(p => p.StateVersion == frame.StateVersion && p.EventSequence == frame.EventSequence)?.RecordedAt
            ?? _lastStateObservedAt ?? DateTimeOffset.UtcNow;
        _historyContext = context is not null && context.Snapshot.LocalPlayerId == frame.LocalPlayerId
            ? context with { Snapshot = frame }
            : new HistoricalContext(frame.MatchId, string.IsNullOrWhiteSpace(build) ? "unknown" : build, deck?.Id, deck, frame, []);
        InvalidateAdvice(); RenderSnapshot();
        TaskBox.SelectedIndex = 2; MainTabs.SelectedIndex = MainTabs.Items.Count - 1;
        StatusText.Text = "已固定最后观察局面供复盘；不会把它当作当前游戏步骤。";
    });

    private async void RebuildClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_loadingSource) throw new InvalidOperationException("正在回放，可先停止后再重建。");
        var wasLive = _timer.IsEnabled;
        var path = _currentLogFile;
        if (path is null || !File.Exists(path)) throw new InvalidOperationException("没有可重新读取的源文件，请先选择日志。");
        await StopRecordingAsync();
        StatusText.Text = "正在从日志起点重建，旧记录保留；不足的信息仍会标记未知。";
        if (wasLive) await StartWatchingAsync();
        else await ReplayPathAsync(path);
    });

    private async void SyncCurrentBuildClicked(object sender, RoutedEventArgs e) => await CatalogOperation(async () =>
    {
        var build = _liveFrame?.ClientBuild ?? _engine.Snapshot().ClientBuild;
        if (build is null)
            build = HsHelper.Ingestion.HearthstoneDiagnostics.Inspect(string.IsNullOrWhiteSpace(LogPathBox.Text) ? null : LogPathBox.Text).ObservedLogBuild;
        if (string.IsNullOrWhiteSpace(build) || !build.All(char.IsAsciiDigit))
            throw new InvalidOperationException("尚未确认客户端 build，请先诊断或连接日志。");
        StatusText.Text = $"正在同步客户端版本 {build} 的中文卡库…";
        await Task.Run(() => _catalog.SyncAsync(_cardHttp, build));
        await RefreshCatalogAsync(); await RenderBattleAsync();
        StatusText.Text = $"已缓存客户端版本 {build}；历史分析仍使用各自固定版本。";
    });
}
