using System.Collections.Frozen;
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

public static class LastObservedReviewChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "last-observed-review"); Directory.CreateDirectory(directory);
        var settings = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = settings.GetValue(null);
        settings.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var reject = new RejectModel(); using var http = new HttpClient(reject);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
            var definitions = JsonNode.Parse(BattleViewRegression.CardsJson())!.AsArray();
            definitions.Add(JsonNode.Parse("""{"id":"TAIL_REVIEW_CARD","dbfId":880001,"name":"后批中文牌名","text":"","cardClass":"NEUTRAL","type":"MINION","set":"CORE","cost":1,"attack":1,"health":1,"collectible":false}"""));
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(definitions.ToJsonString()))) Await(catalog.ImportAsync(input, "255790", source: "fixture://last-observed-review"));
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var original = Await(store.SaveDeckVersionAsync("original-review-A", new(2, [31], [new(321, 1)], []), "244444"));
            var current = Await(store.SaveDeckVersionAsync("current-selector-B", new(2, [31], [new(322, 1)], []), "333333"));
            var source = Frame("last-review-exact", 1, 1);
            var early = source with { StateVersion = 5, EventSequence = 70 };
            var entities = early.Entities.ToDictionary(); entities[700] = Entity(700, "LATER_PUBLIC_CARD", "PLAY", 2);
            var later = early with { StateVersion = 6, Entities = entities.ToFrozenDictionary() };
            var recorder = new SessionRecorder(store, new(original.Id, "255790", 1));
            recorder.Observe(source, 1); recorder.Observe(early, 2); recorder.Observe(later, 3); Await(recorder.CompleteAsync());
            var point = Await(store.ListDecisionPointsAsync(source.MatchId, true)).Single(entry => entry.StateVersion == 5);
            Set(window, "_sourceBindingEstablished", true); Set(window, "_sourceDeckVersion", current); Set(window, "_selectedDeckVersion", current);
            Set(window, "_liveFrame", early); Set(window, "_lastStateObservedAt", DateTimeOffset.UtcNow.AddDays(2));
            C<TextBox>(window, "SessionBuildBox").Text = "333333";
            var stale = new[] { new { EventSequence = 999, Kind = "OTHER_MATCH", Summary = "UNRELATED_HISTORY_EVENT" } };
            C<DataGrid>(window, "EventsGrid").ItemsSource = stale; C<TextBox>(window, "HistorySnapshotText").Text = "OLD_HISTORY_DETAILS";
            ClickReview(window); WaitReview(window);
            var pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Check(SnapshotCodec.Serialize(pinned.Snapshot) == SnapshotCodec.Serialize(early) && pinned.CardBuild == "255790" && pinned.DeckVersionId == original.Id &&
                Equals(Field(window, "_historyObservedAt"), point.RecordedAt) && !pinned.RecentEvents.Any(entry => entry.CardId == "LATER_PUBLIC_CARD") &&
                !ReferenceEquals(stale, C<DataGrid>(window, "EventsGrid").ItemsSource) && C<DataGrid>(window, "EventsGrid").Items.OfType<SessionEvent>().All(entry => entry.CardId != "LATER_PUBLIC_CARD") &&
                C<TextBox>(window, "HistorySnapshotText").Text.Contains("#70 · v5") && !C<TextBox>(window, "HistorySnapshotText").Text.Contains("OLD_HISTORY_DETAILS") &&
                C<TextBox>(window, "SessionBuildBox").Text == "255790" && ReferenceEquals(early, Field(window, "_liveFrame")) &&
                C<ComboBox>(window, "TaskBox").SelectedIndex == 2 && C<TabControl>(window, "MainTabs").SelectedIndex == C<TabControl>(window, "MainTabs").Items.Count - 1,
                "Review-last did not use the exact original saved frame, timeline, deck/build and RecordedAt or retained unrelated details.");
            Pass("the actual review-last button uses exact same-sequence state provenance and replaces stale details without changing the live frame");

            var conflicting = early.Entities.ToDictionary(); var changed = conflicting.Values.First(entry => entry.Kind == EntityKind.Card);
            conflicting[changed.Id] = changed with { Tags = changed.Tags.ToDictionary().Append(new KeyValuePair<string, string>("LAST_REVIEW_CONFLICT", "1")).ToFrozenDictionary() };
            Set(window, "_liveFrame", early with { Entities = conflicting.ToFrozenDictionary() }); ClickReview(window); WaitReview(window);
            Check(Field(window, "_historyContext") is null && C<TextBlock>(window, "StatusText").Text.Contains("内容或视角") &&
                C<DataGrid>(window, "EventsGrid").Items.Count == 0, "The same keys silently substituted a different saved snapshot for the last observed contents.");

            var memory = early with { StateVersion = 7, RecentEvents = [new(70, "MemoryOnly", null, 1, null, null, null, "OWN_MEMORY_EVENT")] };
            var observed = DateTimeOffset.UtcNow.AddHours(-2); Set(window, "_liveFrame", memory); Set(window, "_lastStateObservedAt", observed);
            Set(window, "_sourceDeckVersion", current); Set(window, "_sourceBindingEstablished", true);
            ClickReview(window); WaitReview(window); pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Check(ReferenceEquals(memory, pinned.Snapshot) && pinned.DeckVersionId == original.Id && pinned.RecentEvents.Count == 0 &&
                Equals(observed, Field(window, "_historyObservedAt")) && C<TextBox>(window, "HistorySnapshotText").Text.Contains("未精确保存") &&
                C<TextBox>(window, "HistorySnapshotText").Text.Contains("原会话绑定不一致") && pinned.CardBuild == "255790" &&
                C<DataGrid>(window, "EventsGrid").Items.Cast<object>().Single().GetType().GetProperty("Kind")!.GetValue(C<DataGrid>(window, "EventsGrid").Items[0])?.ToString() == "MemoryOnly" &&
                !C<TextBox>(window, "HistorySnapshotText").Text.Contains("LATER_PUBLIC_CARD"), "An unsaved memory frame borrowed a neighbouring stored timeline or discarded its proven source/time.");
            Pass("unsaved memory frames retain only their own events and confirmed source/time while conflicting saved contents are refused");

            Set(window, "_liveFrame", memory with { MatchId = "last-review-no-session", StateVersion = 8, EventSequence = 71, ClientBuild = null });
            Set(window, "_sourceBindingEstablished", false); Set(window, "_lastStateObservedAt", null);
            C<TextBox>(window, "SessionBuildBox").Text = "333333"; ClickReview(window); WaitReview(window);
            pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Check(pinned.CardBuild == "unknown" && pinned.Deck is null && pinned.DeckVersionId is null && Field(window, "_historyObservedAt") is null &&
                C<TextBox>(window, "SessionBuildBox").Text == "" && C<TextBox>(window, "HistorySnapshotText").Text.Contains("历史知识截止未知"),
                "Unknown memory provenance borrowed the current selector, build textbox, prior record or current clock.");
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            C<TextBox>(window, "EndpointBox").Text = "https://last-review.invalid/v1/chat/completions"; C<TextBox>(window, "ModelBox").Text = "last-review-fixture";
            var analyze = (Task)Invoke(window, "AnalyzeCoreAsync")!;
            try { Await(analyze); throw new InvalidOperationException("An unknown historical cutoff was accepted for model analysis."); }
            catch (InvalidOperationException error) when (error.Message.Contains("缺少记录时刻")) { }
            Check(reject.Calls == 0, "Unknown cutoff analysis reached an HTTP handler.");
            var engine = new PowerLogStateEngine(1, "last-review-engine-only");
            foreach (var line in new[] { "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
                "tag=STATE value=RUNNING", "Player EntityID=2 PlayerID=1", "Player EntityID=3 PlayerID=2", "PowerProcessor.EndCurrentTaskList() - complete" }) engine.ProcessLine(line);
            Set(window, "_engine", engine); Set(window, "_liveFrame", null); Set(window, "_sourceBindingEstablished", true); Set(window, "_sourceDeckVersion", current);
            Set(window, "_lastStateObservedAt", observed); ClickReview(window); WaitReview(window); pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Check(pinned.DeckVersionId is null && pinned.Deck is null && Field(window, "_historyObservedAt") is null,
                "An engine-only frame borrowed the current live-source deck or an unrelated last-observed clock.");
            Set(window, "_liveFrame", engine.Snapshot()); ClickReview(window); WaitReview(window); pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Check(pinned.DeckVersionId == current.Id && Equals(observed, Field(window, "_historyObservedAt")),
                "A true live frame matching the current engine lost its established source declaration.");
            var perspectiveRecorder = new SessionRecorder(store, new(original.Id, "255790", 1));
            perspectiveRecorder.Observe(engine.Snapshot(), 1); Await(perspectiveRecorder.CompleteAsync());
            C<ComboBox>(window, "PlayerBox").SelectedIndex = 2;
            Check(engine.Snapshot().LocalPlayerId == 2 && Field(window, "_liveFrame") is null, "The real perspective picker did not update the engine-only frame.");
            ClickReview(window); WaitReview(window); pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Check(pinned.Snapshot.LocalPlayerId == 2 && pinned.Deck is null && pinned.DeckVersionId is null &&
                C<TextBox>(window, "HistorySnapshotText").Text.Contains("此帧视角与原会话记录不一致"),
                "An engine perspective change attributed player one's saved deck to player two.");
            Pass("missing memory source/build/time remains unknown and blocks historical model access without preventing local viewing");

            var largeEntities = early.Entities.ToDictionary();
            for (var index = 0; index < 1001; index++) largeEntities[2000 + index] = Entity(2000 + index, "UNKNOWN_REVIEW_" + index, "GRAVEYARD", 1);
            var frozen = largeEntities.ToFrozenDictionary(); var tail = frozen.Values.Where(entry => entry.Id >= 2000).Last();
            largeEntities[tail.Id] = Entity(tail.Id, "TAIL_REVIEW_CARD", "HAND", 1);
            var large = early with { MatchId = "last-review-large", StateVersion = 10, EventSequence = 100, Entities = largeEntities.ToFrozenDictionary() };
            Check(large.Entities.Values.Select(entry => entry.CardId).OfType<string>().Distinct().ToList().IndexOf("TAIL_REVIEW_CARD") >= 500, "The late-name fixture did not reach a later batch.");
            var largeRecorder = new SessionRecorder(store, new(original.Id, "255790", 1)); largeRecorder.Observe(large, 1); Await(largeRecorder.CompleteAsync());
            Set(window, "_liveFrame", large); ClickReview(window); WaitReview(window);
            Check(C<TextBox>(window, "HistorySnapshotText").Text.Contains("后批中文牌名"), "Strict historical reading silently truncated names after the first 500 identities.");
            Pass("strict historical review loads a visible Chinese card name from beyond the first five hundred distinct identities");

            foreach (var change in new[] { "revision", "source", "match", "close" })
            foreach (var fail in new[] { false, true }) StaleRead(window, store, early, change, fail);
            LateDecisionErrors(window, point, Await(store.ListDecisionPointsAsync(source.MatchId, true)).Single(entry => entry.StateVersion == 6));
            Pass("post-IO successes and failures cannot replace a new revision source match or closed view");
            Check(reject.Calls == 0 && !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Last review checks contacted a model or created a visible window.");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); throw; }
        finally { Set(window, "_allowClose", true); window.Close(); settings.SetValue(null, previous); }
    }

    private static void LateDecisionErrors(MainWindow window, DecisionPoint oldPoint, DecisionPoint newPoint)
    {
        var itemType = typeof(MainWindow).GetNestedType("DecisionItem", BindingFlags.NonPublic)!;
        foreach (var change in new[] { "return-live", "new-selection", "current-error" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_historicalReadCompleted", (Func<DecisionPoint, CancellationToken, Task>)(async (point, _) =>
            { if (point.Id == oldPoint.Id) { entered.SetResult(); await release.Task; throw new IOException("CURRENT_DECISION_ERROR"); } }));
            var list = C<ListBox>(window, "DecisionsList");
            try
            {
                list.ItemsSource = new[] { Activator.CreateInstance(itemType, oldPoint)!, Activator.CreateInstance(itemType, newPoint)! };
                list.SelectedIndex = 0; Await(entered.Task);
                if (change == "return-live") Invoke(window, "ReturnLiveClicked", window, new RoutedEventArgs());
                if (change == "new-selection") { list.SelectedIndex = 1; WaitReview(window); }
                var current = Field(window, "_historyContext");
                C<TextBlock>(window, "StatusText").Text = "CURRENT_VIEW_STATUS";
                release.SetResult(); WaitReview(window); Pump();
                Check(change == "current-error" ? C<TextBlock>(window, "StatusText").Text == "CURRENT_DECISION_ERROR"
                    : C<TextBlock>(window, "StatusText").Text == "CURRENT_VIEW_STATUS" && ReferenceEquals(current, Field(window, "_historyContext")),
                    "The ordinary decision reader hid a current error or published an old error over a newer view: " + change);
            }
            finally { release.TrySetResult(); Set(window, "_historicalReadCompleted", null); list.SelectedItem = null; }
        }
    }

    private static void StaleRead(MainWindow main, SessionStore store, GameSnapshot frame, string change, bool fail)
    {
        var window = change == "close" ? new MainWindow() : main; var closed = false; window.Closed += (_, _) => closed = true;
        Set(window, "_store", store); Set(window, "_liveFrame", frame); Set(window, "_sourceBindingEstablished", false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_historicalReadCompleted", (Func<DecisionPoint, CancellationToken, Task>)(async (_, _) => { entered.SetResult(); await release.Task; if (fail) throw new IOException("SYNTHETIC_LATE_REVIEW_ERROR"); }));
        var operation = (Task)Invoke(window, "ReviewLastObservedAsync")!; Await(entered.Task);
        try
        {
            if (change == "revision") Invoke(window, "ReturnLiveClicked", window, new RoutedEventArgs());
            if (change == "source") Invoke(window, "BeginSource");
            if (change == "match") Set(window, "_liveFrame", frame with { MatchId = "next-live-game" });
            if (change == "close") { window.Close(); WaitUntil(() => closed, "The actual close workflow did not finish."); }
            C<TextBlock>(window, "StatusText").Text = "NEW_VIEW_STATUS"; C<TextBox>(window, "HistorySnapshotText").Text = "NEW_VIEW_DETAILS";
            var events = C<DataGrid>(window, "EventsGrid").ItemsSource;
            release.SetResult(); Await(operation);
            Check(Field(window, "_historyContext") is null && C<TextBlock>(window, "StatusText").Text == "NEW_VIEW_STATUS" &&
                C<TextBox>(window, "HistorySnapshotText").Text == "NEW_VIEW_DETAILS" && ReferenceEquals(events, C<DataGrid>(window, "EventsGrid").ItemsSource),
                $"A stale {change}/{fail} read changed the newer or closed view.");
        }
        finally
        {
            release.TrySetResult(); Set(window, "_historicalReadCompleted", null);
            if (!ReferenceEquals(window, main) && !closed) { Set(window, "_allowClose", true); window.Close(); }
        }
    }

    private static GameSnapshot Frame(string id, long version, long sequence) => BattleViewRegression.Frame() with
    { MatchId = id, StateVersion = version, EventSequence = sequence, ClientBuild = "255790", DeckTracking = null, PublicPlays = null, Choice = null, ChoiceHistory = null, TurnHistory = null, RecentEvents = [] };
    private static EntitySnapshot Entity(int id, string card, string zone, int owner) => new(id, card, EntityKind.Card,
        new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "MINION", ["ATK"] = "1", ["HEALTH"] = "1", ["DAMAGE"] = "0", ["COST"] = "1" }.ToFrozenDictionary());
    private static void ClickReview(MainWindow window)
    { var button = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "复盘最后观察局面")); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void WaitReview(MainWindow window) => WaitUntil(() => !(bool)Field(window, "_historyLoading")!, "Last observed review did not finish.");
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static void Set(object value, string name, object? field) => value.GetType().GetField(name, Private)!.SetValue(value, field);
    private static object? Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static T C<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Last observed fixture operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var elapsed = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Pass(string message) => Console.WriteLine("PASS last observed review: " + message);
    private sealed class RejectModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No model HTTP is expected in last-observed viewing checks."); }
    }
}
