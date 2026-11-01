using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;

/// <summary>Actual live ingestion on the isolated STA, with synthetic logs and no native window or UIA client.</summary>
internal static class LiveWatchingUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Build = "900001";
    private const int ArmorChanges = 520;

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("input compatibility is opt-in and leaves the default startup path unchanged", () =>
        {
            var options = AppLaunchSettings.Parse(["--data-dir", Path.Combine(".local", "launch-default")]);
            Check(options.Error is null && !options.InputCompatibility && !options.Diagnostic,
                "The normal startup unexpectedly enabled compatibility or diagnostic mode.");
        });
        yield return ("input compatibility composes with explicit data and headless diagnostic paths", () =>
        {
            var data = Path.GetFullPath(Path.Combine(".local", "launch fixture", "data"));
            var report = Path.GetFullPath(Path.Combine(".local", "launch fixture", "report.json"));
            var options = AppLaunchSettings.Parse(["--input-compatibility", "--data-dir", data, "--diagnostic-output", report]);
            Check(options is { Error: null, InputCompatibility: true, Diagnostic: true } &&
                options.DataDirectory == data && options.DiagnosticOutput == report,
                "Compatibility parsing changed explicit data/diagnostic paths or failed to remain headless.");
        });
        yield return ("input compatibility does not accept unknown switches or missing option values", () =>
        {
            Check(AppLaunchSettings.Parse(["--input-compatibility", "--unknown"]).Error is not null &&
                AppLaunchSettings.Parse(["--input-compatibility", "--data-dir"]).Error is not null &&
                AppLaunchSettings.Parse(["--input-compatibility", "--diagnostic-output"]).Error is not null,
                "Compatibility mode bypassed the existing invalid-option checks.");
        });
    }

    public static void RunOnDispatcher()
    {
        CatchUpKeepsInputResponsiveAndRecordsEveryChange();
        StopWaitsForProducerAndSavesPartial("stop");
        StopWaitsForProducerAndSavesPartial("path");
        StopWaitsForProducerAndSavesPartial("player");
    }

    private static void CatchUpKeepsInputResponsiveAndRecordsEveryChange()
    {
        using var fixture = new Fixture("catch-up", settledEnd: true);
        var window = fixture.Window;
        var uiThread = Environment.CurrentManagedThreadId;
        var reads = 0;
        var heartbeats = 0;
        Set(window, "_liveReadStarted", (Func<CancellationToken, Task>)(async token =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "The live producer still runs on the UI thread.");
            Interlocked.Increment(ref reads);
            // Keep a deterministic observation window; parser/storage below still process the actual multi-batch log.
            await Task.Delay(15, token);
        }));
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1) };
        heartbeat.Tick += (_, _) => heartbeats++;
        heartbeat.Start();
        try
        {
            Await(InvokeTask(window, "StartWatchingAsync"));
            WaitUntil(() => Offset(window) == fixture.LogBytes && !Reading(window), "The live reader did not catch up with the synthetic log.");
        }
        finally { heartbeat.Stop(); }
        var frame = Frame(window);
        Check(reads >= 2 && heartbeats >= 2 && frame.Entities[10].GetIntTag("ARMOR") == ArmorChanges && frame.Quality.IsStable,
            "The actual multi-batch catch-up lost input heartbeats, changes or its final settled boundary.");
        Await(InvokeTask(window, "StopRecordingAsync"));
        fixture.AssertSaved(frame, expectPartial: false, expectedArmorChanges: ArmorChanges);
        Pass("real multi-batch start permits Input heartbeats and reopened storage retains every ordered change once");
    }

    private static void StopWaitsForProducerAndSavesPartial(string change)
    {
        using var fixture = new Fixture(change, settledEnd: false);
        var window = fixture.Window;
        var uiThread = Environment.CurrentManagedThreadId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReleaseOnCleanup = () => release.TrySetResult();
        var reads = 0;
        Set(window, "_liveReadStarted", (Func<CancellationToken, Task>)(async token =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "A held live producer blocked the UI thread.");
            if (Interlocked.Increment(ref reads) != 2) return;
            entered.TrySetResult();
            // Deliberately hold the old producer after cancellation, so Stop must wait rather than complete its recorder early.
            await release.Task;
            token.ThrowIfCancellationRequested();
        }));
        Await(InvokeTask(window, "StartWatchingAsync"));
        Await(entered.Task);
        var partial = Frame(window);
        Check(!partial.Quality.IsStable && partial.Entities[10].GetIntTag("ARMOR") is > 0 and < ArmorChanges,
            "The held second read did not leave a genuinely partial first-batch frame.");
        var oldGeneration = (long)Get(window, "_sourceGeneration")!;
        Task? stop = null;
        var replacement = Path.Combine(fixture.Directory, "replacement", "Power.log");
        if (change == "stop") stop = InvokeTask(window, "StopRecordingAsync");
        else if (change == "path") C<TextBox>(window, "LogPathBox").Text = replacement;
        else C<ComboBox>(window, "PlayerBox").SelectedIndex = 2;

        var inputRan = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputRan = true));
        WaitUntil(() => inputRan, "Input dispatch stopped while waiting for the live producer.");
        Check(Reading(window) && (stop is null || !stop.IsCompleted) && !Timer(window).IsEnabled &&
            (long)Get(window, "_sourceGeneration")! > oldGeneration && Get(window, "_capture") is not null,
            "Stop/source change completed capture or left the live timer running before its producer settled.");
        if (change == "player")
            Check(((PowerLogStateEngine)Get(window, "_engine")!).Snapshot().LocalPlayerId == 1,
                "The player selector mutated the engine while the old producer still owned it.");
        C<TextBlock>(window, "StatusText").Text = "NEW_VIEW_SENTINEL";
        C<TextBox>(window, "SessionBuildBox").Text = "777777";
        release.TrySetResult();
        if (stop is not null) Await(stop);
        WaitUntil(() => !Reading(window) && Get(window, "_capture") is null &&
            (change != "player" || ((PowerLogStateEngine)Get(window, "_engine")!).Snapshot().LocalPlayerId == 2),
            "A stopped or changed source did not settle after its producer returned.");
        Drain();
        Check(C<TextBox>(window, "SessionBuildBox").Text == "777777" && reads == 2 && !Timer(window).IsEnabled,
            "An old live completion replaced a newer build or resurrected a queued catch-up read.");
        if (change == "path") Check(C<TextBox>(window, "LogPathBox").Text == replacement, "The old read replaced the new path.");
        if (change == "stop") Check(C<TextBlock>(window, "StatusText").Text == "NEW_VIEW_SENTINEL", "The old read replaced the newer status.");
        fixture.AssertSaved(partial, expectPartial: true, expectedArmorChanges: partial.Entities[10].GetIntTag("ARMOR")!.Value);
        Pass($"real {change} waits for the held worker, rejects stale publication and safely saves the original partial binding");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; }
        public MainWindow Window { get; }
        public SessionStore Store { get; }
        public DeckVersion Deck { get; }
        public long LogBytes { get; }
        public Action? ReleaseOnCleanup { get; set; }
        private readonly string _storePath;
        private readonly RejectHttp _reject = new();
        private bool _closed;

        public Fixture(string name, bool settledEnd)
        {
            Directory = Path.Combine(Environment.CurrentDirectory, "live-watching", name + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            var settings = typeof(App).GetProperty(nameof(App.LaunchSettings))!;
            var previous = settings.GetValue(null);
            try
            {
                settings.SetValue(null, new AppLaunchSettings(Path.Combine(Directory, "data"), false, null, null));
                Window = new MainWindow();
            }
            finally { settings.SetValue(null, previous); }
            Window.Closed += (_, _) => _closed = true;
            var catalog = (CardCatalog)Get(Window, "_catalog")!;
            Await(catalog.InitializeAsync());
            _storePath = Path.Combine(Directory, "data", "sessions.db");
            Store = new SessionStore(_storePath); Await(Store.InitializeAsync()); Set(Window, "_store", Store);
            Deck = Await(Store.SaveDeckVersionAsync("synthetic-live-original", new(2, [31], [new(321, 1)], []), Build));
            Set(Window, "_selectedDeckVersion", Deck); Set(Window, "_deck", Deck.Definition);
            foreach (var field in new[] { "_http", "_cardHttp" })
            { ((HttpClient)Get(Window, field)!).Dispose(); Set(Window, field, new HttpClient(_reject)); }
            C<ComboBox>(Window, "PlayerBox").SelectedIndex = 1;
            var log = Path.Combine(Directory, "Power.log");
            File.WriteAllText(log, SyntheticLog(settledEnd), new UTF8Encoding(false));
            LogBytes = new FileInfo(log).Length;
            Check(LogBytes > 2 * 64 * 1024, "The live fixture must cross multiple production read budgets.");
            C<TextBox>(Window, "LogPathBox").Text = log;
        }

        public void AssertSaved(GameSnapshot frame, bool expectPartial, int expectedArmorChanges)
        {
            var reopened = new SessionStore(_storePath); Await(reopened.InitializeAsync());
            var session = Await(reopened.GetSessionAsync(frame.MatchId));
            var saved = Await(reopened.GetSnapshotAtAsync(frame.MatchId, long.MaxValue));
            Check(session is not null && session.DeckVersionId == Deck.Id && session.CardBuild == Build && session.LocalPlayerId == 1 &&
                session.LastEventSequence == frame.EventSequence && saved is not null && saved.StateVersion == frame.StateVersion &&
                saved.EventSequence == frame.EventSequence && saved.LocalPlayerId == 1 && saved.Quality.IsStable == !expectPartial &&
                saved.Entities[10].GetIntTag("ARMOR") == expectedArmorChanges,
                "Reopened live storage lost its final partial/settled snapshot or borrowed the new player/deck/build.");
            var events = Await(reopened.GetEventsAtAsync(frame.MatchId, long.MaxValue, 10000));
            Check(events.Count == events.Select(row => (row.EventSequence, row.EventIndex)).Distinct().Count() &&
                events.Zip(events.Skip(1)).All(pair => pair.First.EventSequence < pair.Second.EventSequence ||
                    pair.First.EventSequence == pair.Second.EventSequence && pair.First.EventIndex < pair.Second.EventIndex),
                "Live events were duplicated or reordered between producer flushes and final completion.");
            var armor = events.Where(row => row.Kind == "EntityChanged" && row.EntityId == 10)
                .Select(row => JsonNode.Parse(row.DataJson)?["ChangedTags"]?["ARMOR"]?.GetValue<string>())
                .Where(value => int.TryParse(value, out var parsed) && parsed > 0).Select(value => int.Parse(value!)).ToArray();
            Check(armor.SequenceEqual(Enumerable.Range(1, expectedArmorChanges)), "A live batch lost or repeated an actual armor change.");
            Check(_reject.Calls == 0, "A live ingestion regression invoked an external model or card request.");
        }

        public void Dispose()
        {
            ReleaseOnCleanup?.Invoke(); Set(Window, "_liveReadStarted", null);
            if (!_closed)
            {
                Await(InvokeTask(Window, "StopRecordingAsync"));
                Window.Close(); WaitUntil(() => _closed, "The live fixture did not close through its persistence workflow.");
            }
            Check(new WindowInteropHelper(Window).Handle == IntPtr.Zero, "A live regression created a native window handle.");
        }
    }

    private static string SyntheticLog(bool settledEnd)
    {
        var log = new StringBuilder();
        foreach (var line in new[]
        {
            "# SYNTHETIC live UI regression only; no user game data.",
            $"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED",
            "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=HERO_ENTITY value=10", "tag=CURRENT_PLAYER value=1", "tag=FIRST_PLAYER value=1", "tag=PLAYSTATE value=PLAYING",
            "Player EntityID=3 PlayerID=2", "tag=HERO_ENTITY value=20", "tag=CURRENT_PLAYER value=0", "tag=FIRST_PLAYER value=0", "tag=PLAYSTATE value=PLAYING",
            "FULL_ENTITY - Creating ID=10 CardID=SYN_WATCH_HUNTER", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0",
            "FULL_ENTITY - Creating ID=20 CardID=SYN_WATCH_MAGE", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0",
            "PowerProcessor.EndCurrentTaskList() - task complete"
        }) log.AppendLine(line);
        for (var armor = 1; armor <= ArmorChanges; armor++)
        {
            log.AppendLine("# SYNTHETIC padding " + new string('p', 240));
            log.AppendLine($"TAG_CHANGE Entity=10 tag=ARMOR value={armor}");
        }
        if (settledEnd) log.AppendLine("PowerProcessor.EndCurrentTaskList() - task complete");
        return log.ToString();
    }

    private static object? Get(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static Task InvokeTask(object target, string name) => (Task)target.GetType().GetMethod(name, Private)!.Invoke(target, null)!;
    private static T C<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static bool Reading(MainWindow window) => (bool)Get(window, "_reading")!;
    private static long Offset(MainWindow window) => (long)Get(window, "_currentLogOffset")!;
    private static GameSnapshot Frame(MainWindow window) => (GameSnapshot)Get(window, "_liveFrame")!;
    private static DispatcherTimer Timer(MainWindow window) => (DispatcherTimer)Get(window, "_timer")!;
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "The live fixture operation timed out."); task.GetAwaiter().GetResult(); }
    private static void Drain() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    private static void WaitUntil(Func<bool> complete, string message)
    {
        if (complete()) return;
        var elapsed = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(complete(), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string text) => Console.WriteLine("PASS live watching UI: " + text);
    private sealed class RejectHttp : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No HTTP is expected during synthetic live ingestion."); }
    }
}
