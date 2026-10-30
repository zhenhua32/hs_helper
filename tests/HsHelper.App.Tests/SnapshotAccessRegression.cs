using System.Collections.Frozen;
using HsHelper.App;
using HsHelper.Core;

public static class SnapshotAccessRegression
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("fresh listening observations permit current advice at freshness limits", FreshObservation);
        yield return ("stale reads or stale source writes prevent current advice", StaleObservation);
        yield return ("stopped, failed or catching-up sources cannot serve current advice", SourceUnavailable);
        yield return ("loading prevents analysis even when a historical view is selected", Loading);
        yield return ("explicit history and file replay stay review-only without a live source", FixedReview);
        yield return ("completed games offer review rather than current actions", CompletedGame);
        yield return ("future read or write clocks cannot establish a live observation", ClockRollback);
        yield return ("missing read or write timestamps cannot establish a live observation", MissingClocks);
        yield return ("empty, unstable, unconfirmed or spectator frames cannot be analyzed", UnusableFrames);
    }

    private static void FreshObservation()
    {
        foreach (var access in new[] { Access(), Access(readAgeSeconds: 5, writeAgeSeconds: 90) })
            Check(access.Kind == SnapshotViewKind.Live && access.CanAnalyze && !access.IsHistorical,
                "A fresh confirmed observation, including the allowed boundary, was not available as live.");
    }

    private static void StaleObservation()
    {
        foreach (var access in new[] { Access(readAgeSeconds: 5.001), Access(writeAgeSeconds: 90.001) })
            Denied(access, "A stale read or source write remained available for current advice.");
    }

    private static void SourceUnavailable()
    {
        foreach (var access in new[] { Access(listening: false), Access(sourceFailed: true), Access(catchingUp: true) })
            Denied(access, "Stopping, failing or catching up a source advertised the last frame as current.");
    }

    private static void Loading()
    {
        foreach (var access in new[] { Access(loading: true), Access(loading: true, historical: true), Access(loading: true, fileReplay: true) })
            Denied(access, "An in-flight source/history load left an old frame analyzable.");
    }

    private static void FixedReview()
    {
        foreach (var access in new[]
        {
            Access(historical: true, listening: false, sourceFailed: true, catchingUp: true, readAgeSeconds: 300, writeAgeSeconds: 300),
            Access(fileReplay: true, listening: false, sourceFailed: true, catchingUp: true, readAgeSeconds: 300, writeAgeSeconds: 300)
        })
            Check(access.Kind == SnapshotViewKind.Review && access.CanAnalyze && access.IsHistorical,
                "An explicit fixed-frame review depended on a live source or was presented as current actions.");
    }

    private static void CompletedGame()
    {
        var access = Access(BattleViewRegression.Frame() with { Phase = GamePhase.Complete });
        Check(access.Kind == SnapshotViewKind.Review && access.CanAnalyze && access.IsHistorical,
            "An ended game remained available as current game actions.");
    }

    private static void ClockRollback()
    {
        foreach (var access in new[] { Access(readAgeSeconds: -0.001), Access(writeAgeSeconds: -0.001) })
            Denied(access, "A future timestamp after a clock rollback established a live observation.");
    }

    private static void MissingClocks()
    {
        foreach (var clocks in new (DateTimeOffset? Read, DateTimeOffset? Write)[] { (null, Now), (Now, null), (null, null) })
            Denied(SnapshotAccessPolicy.Evaluate(BattleViewRegression.Frame(), Now,
                historical: false, fileReplay: false, listening: true, loading: false, catchingUp: false,
                sourceFailed: false, lastSuccessfulRead: clocks.Read, fileLastWrite: clocks.Write),
                "Missing source timing evidence was treated as a fresh live source.");
    }

    private static void UnusableFrames()
    {
        var frame = BattleViewRegression.Frame();
        foreach (var unusable in new[]
        {
            frame with { Entities = FrozenDictionary<int, EntitySnapshot>.Empty },
            frame with { Quality = frame.Quality with { IsStable = false } },
            frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
            frame with { IsSpectating = true }
        })
            foreach (var access in new[] { Access(unusable), Access(unusable, historical: true), Access(unusable, fileReplay: true) })
                Check(!access.CanAnalyze, "An empty, unstable, unconfirmed or spectator frame was accepted for analysis.");
    }

    private static SnapshotAccess Access(GameSnapshot? frame = null, bool historical = false, bool fileReplay = false,
        bool listening = true, bool loading = false, bool catchingUp = false, bool sourceFailed = false,
        double readAgeSeconds = 0, double writeAgeSeconds = 0) =>
        SnapshotAccessPolicy.Evaluate(frame ?? BattleViewRegression.Frame(), Now, historical, fileReplay, listening,
            loading, catchingUp, sourceFailed, Now.AddSeconds(-readAgeSeconds), Now.AddSeconds(-writeAgeSeconds));

    private static void Denied(SnapshotAccess access, string message) =>
        Check(access.Kind == SnapshotViewKind.Unavailable && !access.CanAnalyze && !access.IsHistorical, message);

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
