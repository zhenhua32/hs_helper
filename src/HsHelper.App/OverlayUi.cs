using System.Windows;
using System.Windows.Threading;
using HsHelper.Core;

namespace HsHelper.App;

public partial class MainWindow
{
    private OverlayWindow? _overlay;
    private GlobalHotkeys? _hotkeys;
    private string _overlayAdvice = "等待局面分析。";
    private long _overlayRevision;
    private readonly DispatcherTimer _decisionTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private (string Match, long Version)? _automaticCandidate;
    private (string Match, long Version)? _lastAutomatic;

    private void InitializeOverlay()
    {
        _hotkeys = new GlobalHotkeys(this, ToggleOverlay, () => _ = Run(AnalyzeCurrentAsync),
            () => { if (_overlay is not null) _overlay.SetClickThrough(!_overlay.IsClickThrough); });
        _hotkeys.StatusChanged += (_, _) => HotkeyText.Text = _hotkeys.Status;
        _decisionTimer.Tick += async (_, _) =>
        {
            _decisionTimer.Stop();
            var state = DisplaySnapshot;
            if (_automaticCandidate != (state.MatchId, state.StateVersion) || _lastAutomatic == _automaticCandidate ||
                AutoAdviceBox.IsChecked != true || _analysis is not null || !_timer.IsEnabled || _historyContext is not null || _historyLoading) return;
            _lastAutomatic = _automaticCandidate;
            await Run(AnalyzeCurrentAsync);
        };
        Closed += (_, _) => { _hotkeys.Dispose(); _decisionTimer.Stop(); _overlay?.Close(); };
    }

    private void ToggleOverlay()
    {
        if (_overlay is null) _overlay = new OverlayWindow();
        if (_overlay.IsVisible) _overlay.Hide();
        else { _overlay.Show(); _ = Run(UpdateOverlayAsync); }
    }
    private void OverlayClicked(object sender, RoutedEventArgs e) => ToggleOverlay();
    private void OverlaySettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_overlay is null || !_overlay.IsVisible) ToggleOverlay();
        _overlay!.OpenSettings();
    }
    private void AutomaticAdviceChanged(object sender, RoutedEventArgs e)
    {
        _decisionTimer.Stop(); _automaticCandidate = null;
        if (AutoAdviceBox.IsChecked == true) StatusText.Text = "自动建议已开启：仅在实时稳定决策点发送过滤后的局面，受单局预算限制。";
    }
    private void ScheduleAutomaticAdvice(bool catchingUp)
    {
        var state = DisplaySnapshot;
        if (catchingUp || AutoAdviceBox.IsChecked != true || !_timer.IsEnabled || _historyContext is not null || _historyLoading ||
            CurrentAccess.Kind != SnapshotViewKind.Live || !CurrentAccess.CanAnalyze ||
            !state.Quality.IsStable || !state.Quality.HasLocalPerspective || state.IsSpectating || state.Phase == GamePhase.Complete ||
            (state.ActivePlayerId != state.LocalPlayerId && state.Phase is not (GamePhase.Mulligan or GamePhase.Choice)))
        { _decisionTimer.Stop(); _automaticCandidate = null; return; }
        var candidate = (state.MatchId, state.StateVersion);
        if (_automaticCandidate != candidate) { _decisionTimer.Stop(); _automaticCandidate = candidate; }
        if (!_decisionTimer.IsEnabled && _automaticCandidate != _lastAutomatic && _analysis is null) _decisionTimer.Start();
    }

    private Task UpdateOverlayAsync()
    {
        if (_closing || _overlay is not { IsVisible: true } overlay) return Task.CompletedTask;
        return UpdateOverlayStateAsync(overlay, true);
    }

    private async Task UpdateOverlayStateAsync(OverlayWindow overlay, bool requireVisible)
    {
        var revision = ++_overlayRevision;
        var state = DisplaySnapshot;
        var build = _historyContext?.CardBuild ?? state.ClientBuild ?? SessionBuildBox.Text.Trim();
        var history = _historyContext;
        var token = _source.Token;
        var readCompleted = _aggregatedCardReadCompleted;
        bool Current() => !_closing && !token.IsCancellationRequested && revision == _overlayRevision &&
            ReferenceEquals(_overlay, overlay) && (!requireVisible || overlay.IsVisible) &&
            ReferenceEquals(history, _historyContext) && DisplaySnapshot.MatchId == state.MatchId &&
            DisplaySnapshot.StateVersion == state.StateVersion && DisplaySnapshot.EventSequence == state.EventSequence &&
            DisplaySnapshot.LocalPlayerId == state.LocalPlayerId &&
            (_historyContext?.CardBuild ?? DisplaySnapshot.ClientBuild ?? SessionBuildBox.Text.Trim()) == build;
        try
        {
            var visible = AttachmentVisibility.Project(state);
            var ids = visible.Entities.Values.Select(e => e.CardId).OfType<string>()
                .Concat(state.DeckTracking?.KnownRemaining.Keys ?? [])
                .Concat(state.PublicPlays?.Plays.Select(p => p.CardId).OfType<string>() ?? []).Distinct().ToArray();
            var cards = string.IsNullOrWhiteSpace(build) ? [] : await _catalog.GetByIdsBatchedAsync(ids, build, cancellationToken: token);
            if (readCompleted is not null) await readCompleted("overlay", state, build, token);
            if (!Current()) return;
            var names = cards.ToDictionary(c => c.Id, c => c.Name);
            string Name(string? id) => id is null ? "未知卡牌" : names.GetValueOrDefault(id, id);
            var hand = visible.Entities.Values.Where(e => state.LocalPlayerId is not null && e.ControllerId == state.LocalPlayerId && e.Zone == "HAND")
                .OrderBy(e => e.ZonePosition).Select(e => $"({e.Cost?.ToString() ?? "?"}) {Name(e.CardId)}");
            var remaining = new List<string>();
            if (state.DeckTracking is { } tracking)
            {
                remaining.Add($"共 {tracking.TotalCards?.ToString() ?? "?"} 张 · {(tracking.IsExact ? "组成已确认" : "组成不完整，下列为已知下限")}");
                remaining.AddRange(tracking.KnownRemaining.Where(p => p.Value > 0).OrderBy(p => Name(p.Key)).Select(p => $"{p.Value}x {Name(p.Key)}"));
                if (tracking.UnknownCount > 0) remaining.Add($"未知身份：{tracking.UnknownCount} 张");
                remaining.AddRange(tracking.Warnings);
            }
            var plays = state.PublicPlays?.Plays.Where(p => state.LocalPlayerId is not null && p.ControllerId is not null && p.ControllerId != state.LocalPlayerId)
                .Select(p => $"#{p.Sequence} {Name(p.CardId)}").ToList() ?? [];
            if (state.PublicPlays is not { IsComplete: true }) plays.Add("记录可能不完整");
            overlay.Update(new OverlayState($"{(CurrentAccess.Kind == SnapshotViewKind.Live ? "最近观察" : "历史/未确认实时")} · 回合 {state.Turn?.ToString() ?? "?"} · v{state.StateVersion}",
                hand, remaining, plays, _overlayAdvice,
                $"{(state.Quality.IsStable ? "稳定" : "结算中")} · {(state.Quality.IsComplete ? "记录完整" : "记录有缺口")}\n" +
                    CurrentAccess.Reason + "\n" + string.Join("；", state.Quality.Warnings),
                state.Quality.IsComplete, state.Quality.IsStable));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (!Current()) { }
    }
}
