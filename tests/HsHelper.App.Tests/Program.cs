using HsHelper.App;

var tests = new (string Name, Action Run)[]
{
    ("overlay isolates producer mutation and consumer writes", StateIsImmutable),
    ("overlay bounds potentially untrusted text and list entries", StateIsBounded),
    ("overlay accepts empty data", EmptyState),
    ("scale rejects nonfinite values and clamps extremes", ScaleLimits),
    ("position remains visible on a negative-coordinate monitor", NegativeMonitor),
    ("position shrinks to a tiny work area", SmallWorkArea),
    ("invalid saved geometry recovers within the work area", InvalidGeometry),
    ("invalid work areas fail explicitly", InvalidWorkAreas),
    ("secondary monitor work area uses its current WPF DPI transform", SecondaryMonitorDpi),
    ("negative and vertically offset monitors retain signed coordinates at high DPI", NegativeMonitorDpi),
    ("monitor DPI transitions preserve native screen location and refit", MonitorDpiTransition),
    ("invalid monitor transforms fail explicitly", InvalidMonitorTransforms)
}.Concat(BattleViewRegression.Cases()).Concat(SnapshotAccessRegression.Cases()).Concat(AssembledBattleViewChecks.Cases()).Concat(PlayTriggerToolChecks.Cases()).Concat(DiscoveryToolChecks.Cases()).Concat(SideboardEvidenceToolChecks.Cases()).Concat(MapToolChecks.Cases()).Concat(MagmaToolChecks.Cases()).ToArray();
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
try { HiddenWindowSmoke.Run(); Console.WriteLine("PASS hidden WPF initialization, plan versioning, archived advice, client-build sync, local tools and safe shutdown"); }
catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL hidden WPF interaction smoke: {error.Message}"); }
Console.WriteLine($"{tests.Length + 1 - failed}/{tests.Length + 1} App regression checks passed");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void StateIsImmutable()
{
    var hand = new List<string> { "visible-card" };
    var remaining = new List<string> { "known-card × 2" };
    var opponent = new List<string> { "public-card" };
    var state = new OverlayState("test", hand, remaining, opponent, "advice", "quality", true, true);
    hand[0] = "mutated"; remaining.Clear(); opponent.Add("future-card");
    Check(state.Hand.SequenceEqual(["visible-card"]), "producer changed the captured hand");
    Check(state.Remaining.SequenceEqual(["known-card × 2"]), "producer changed the captured deck");
    Check(state.OpponentPlayed.SequenceEqual(["public-card"]), "future event entered a captured state");
    try { ((IList<string>)state.Hand)[0] = "mutated"; throw new Exception("state allowed a consumer write"); }
    catch (NotSupportedException) { }
}

static void StateIsBounded()
{
    var state = new OverlayState("\0 " + new string('a', 1000), Enumerable.Repeat("entry", 500),
        Enumerable.Repeat("entry", 500), Enumerable.Repeat("entry", 500), new string('a', 5000), new string('a', 5000), false, false);
    Check(state.Title.Length <= 121 && !state.Title.Contains('\0'), "unsafe or unbounded title");
    Check(state.Hand.Count <= 81 && state.Remaining.Count <= 121 && state.OpponentPlayed.Count <= 101, "unbounded lists");
    Check(state.Advice.Length <= 501 && state.Quality.Length <= 601, "unbounded status text");
    Check(state.Hand[^1].Contains('…'), "truncated list does not signal omitted entries");
}

static void EmptyState()
{
    var state = new OverlayState("", [], [], [], "", "", false, false);
    Check(state.Hand.Count == 0 && state.Remaining.Count == 0 && state.OpponentPlayed.Count == 0, "empty lists changed");
    Check(!state.IsComplete && !state.IsStable, "missing data advertised as complete");
    Check(OverlayState.Empty.Quality.Length > 0, "initial state does not describe missing data");
}

static void ScaleLimits()
{
    foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        Check(OverlayLayout.ClampScale(invalid) == 1, "nonfinite scale did not recover to default");
    Check(OverlayLayout.ClampScale(-100) == 0.65 && OverlayLayout.ClampScale(100) == 1.8, "scale exceeded supported bounds");
    Check(OverlayLayout.ClampScale(1.25) == 1.25, "valid scale changed");
}

static void NegativeMonitor()
{
    var fit = OverlayLayout.Fit(-3000, 9999, 360, 620, -1920, 0, 1920, 1080);
    Check(fit == new OverlayPlacement(-1920, 460, 360, 620), "negative-coordinate placement was not clamped into its monitor");
}

static void SmallWorkArea()
{
    var fit = OverlayLayout.Fit(9999, 9999, 360, 620, 20, 30, 150, 200);
    Check(fit == new OverlayPlacement(20, 30, 150, 200), "overlay cannot fit on a work area smaller than its minimum");
}

static void InvalidGeometry()
{
    var fit = OverlayLayout.Fit(double.NaN, double.PositiveInfinity, double.NaN, double.NegativeInfinity, 0, 0, 1920, 1080);
    Check(fit == new OverlayPlacement(0, 0, 360, 620), "invalid saved geometry did not recover to defaults");
    var negative = OverlayLayout.Fit(0, 0, -1, -1, 0, 0, 1920, 1080);
    Check(negative.Width > 0 && negative.Height > 0, "negative dimensions were not repaired");
}

static void InvalidWorkAreas()
{
    foreach (var (x, y, width, height) in new[] { (0d, 0d, 0d, 100d), (0d, 0d, 100d, -1d), (double.NaN, 0d, 100d, 100d), (0d, 0d, double.PositiveInfinity, 100d) })
    {
        try { OverlayLayout.Fit(0, 0, 360, 620, x, y, width, height); throw new Exception("invalid area accepted"); }
        catch (ArgumentException) { }
    }
}

static void SecondaryMonitorDpi()
{
    var device = new OverlayPlacement(1920, 60, 2560, 1380);
    var logical = OverlayMonitorLayout.WorkAreaInLogicalUnits(device, 1.5, 1.5);
    Check(logical == new OverlayPlacement(1280, 40, 2560 / 1.5, 920), "secondary work area used the primary monitor origin or DPI");
    var fit = OverlayLayout.Fit(1400, 100, 360, 620, logical.Left, logical.Top, logical.Width, logical.Height);
    Check(fit.Left == 1400 && fit.Top == 100, "secondary monitor placement was pulled back to primary");
}

static void NegativeMonitorDpi()
{
    var logical = OverlayMonitorLayout.WorkAreaInLogicalUnits(new(-2560, -1440, 2560, 1400), 2, 1.25);
    Check(logical == new OverlayPlacement(-1280, -1152, 1280, 1120), "signed origin or separate X/Y scales were lost");
    var fit = OverlayLayout.Fit(-1100, -1000, 360, 620, logical.Left, logical.Top, logical.Width, logical.Height);
    Check(fit.Left == -1100 && fit.Top == -1000, "valid negative-monitor position was changed");
}

static void MonitorDpiTransition()
{
    var native = new OverlayPlacement(1920, 0, 2560, 1440);
    var low = OverlayMonitorLayout.WorkAreaInLogicalUnits(native, 1, 1);
    var high = OverlayMonitorLayout.WorkAreaInLogicalUnits(native, 2, 2);
    var location = OverlayLayout.Fit(2100 / 2d, 100 / 2d, 360, 620, high.Left, high.Top, high.Width, high.Height);
    Check(low.Left == 1920 && high.Left == 960 && location.Left * 2 == 2100 && location.Top * 2 == 100,
        "DPI change transformed dimensions but not the desktop position");
    var afterRemoval = OverlayLayout.Fit(location.Left, location.Top, 360, 620, 0, 0, 1280, 720);
    Check(afterRemoval.Left + afterRemoval.Width <= 1280 && afterRemoval.Top + afterRemoval.Height <= 720,
        "removed monitor did not refit into the replacement work area");
}

static void InvalidMonitorTransforms()
{
    foreach (var scale in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
    {
        try { OverlayMonitorLayout.WorkAreaInLogicalUnits(new(0, 0, 1920, 1080), scale, 1); throw new Exception("invalid transform accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    try { OverlayMonitorLayout.WorkAreaInLogicalUnits(new(0, 0, 0, 1080), 1, 1); throw new Exception("invalid monitor area accepted"); }
    catch (ArgumentException) { }
}
