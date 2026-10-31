using System.Collections.Frozen;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class KnowledgeNavigationChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("note navigation binds saved entry revision and original anchor", NoteBinding);
        yield return ("note navigation rejects history and source-generation changes", HistoryBinding);
        yield return ("new live match invalidates note navigation without a generation change", NewMatch);
        yield return ("cancelled note navigation cannot publish a former request", Cancelled);
    }
    private static CoachKnowledgeVersion Note => new("note", 2, "deck", CoachKnowledgeKind.MatchNote, CoachKnowledgeSource.UserNote,
        "锚点", "内容", null, null, "original-match", DateTimeOffset.UtcNow, false) { EventSequence = 42 };
    private static void NoteBinding()
    {
        var state = new KnowledgeNavigationState(); var note = Note; var request = state.Begin(note, 1, 2, 3, "live-a");
        Check(state.IsCurrent(request, note, 1, 2, 3, "live-a") && !state.IsCurrent(request, note with { Version = 3 }, 1, 2, 3, "live-a") &&
            !state.IsCurrent(request, note with { EventSequence = 41 }, 1, 2, 3, "live-a") && !state.IsCurrent(request, note, 2, 2, 3, "live-a"), "Navigation lost entry or editor binding.");
    }
    private static void HistoryBinding()
    {
        var state = new KnowledgeNavigationState(); var note = Note; var request = state.Begin(note, 1, 2, 3, "live-a");
        Check(!state.IsCurrent(request, note, 1, 3, 3, "live-a") && !state.IsCurrent(request, note, 1, 2, 4, "live-a"), "A new history/source scope accepted the old note request.");
    }
    private static void NewMatch()
    {
        var state = new KnowledgeNavigationState(); var note = Note; var request = state.Begin(note, 1, 2, 3, "live-a");
        Check(!state.IsCurrent(request, note, 1, 2, 3, "live-b"), "The next game was overwritten because its generation was unchanged.");
    }
    private static void Cancelled()
    {
        var state = new KnowledgeNavigationState(); var note = Note; var request = state.Begin(note, 1, 2, 3, "live-a"); state.Cancel();
        Check(!state.IsCurrent(request, note, 1, 2, 3, "live-a"), "Cancelled navigation remained publishable.");
    }
    public static void RunOnDispatcher(MainWindow window)
    {
        var store = (SessionStore)Field(window, "_store")!; var knowledge = (CoachKnowledgeStore)Field(window, "_knowledge")!;
        var source = Await(store.ListDeckVersionsAsync()).First(); var deck = Await(store.SaveDeckVersionAsync("knowledge-navigation-ui", source.Definition, "255790"));
        var plan = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, "旧打法", "PLAN_BEFORE")));
        var recorder = new SessionRecorder(store, new(deck.Id, "255790", 1));
        recorder.Observe(Frame("note-nav-original", 3, 42), 1); Await(recorder.FlushAsync());
        var point = Await(store.ListDecisionPointsAsync("note-nav-original", true)).Single();
        var note = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "导航原笔记", "NOTE_V1", MatchId: point.SessionId) { EventSequence = 42 }));
        note = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, note.Title, "NOTE_V2_LATE", MatchId: point.SessionId) { EventSequence = 42 }, note.EntryId, 1));
        Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, plan.Title, "PLAN_AFTER"), plan.EntryId, 1));
        recorder.Observe(Frame(point.SessionId, 4, 90, later: true), 2); Await(recorder.CompleteAsync());
        var missing = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "缺精确", "无42之后的43快照", MatchId: point.SessionId) { EventSequence = 43 }));
        var absent = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "缺原局", "原局未保存", MatchId: "note-nav-absent") { EventSequence = 1 }));
        var multiRecorder = new SessionRecorder(store, new(deck.Id, "255790", 1));
        multiRecorder.Observe(Frame("note-nav-multiple", 1, 1), 1);
        multiRecorder.Observe(Frame("note-nav-multiple", 5, 70), 2); multiRecorder.Observe(Frame("note-nav-multiple", 6, 70, later: true), 3); Await(multiRecorder.CompleteAsync());
        var multi = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "多个版本", "笔记未锚定状态版本", MatchId: "note-nav-multiple") { EventSequence = 70 }));
        var unboundRecorder = new SessionRecorder(store, new(null, "444444", 1));
        unboundRecorder.Observe(Frame("note-nav-unbound", 2, 20) with { ClientBuild = "444444" }, 1); Await(unboundRecorder.CompleteAsync());
        var unbound = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "缺套牌卡库", "不能回填当前资料", MatchId: "note-nav-unbound") { EventSequence = 20 }));
        for (var i = 0; i < 104; i++) Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, $"填充笔记 {i:D3}", "隔离合成资料")));

        var plans = Control<ComboBox>(window, "PlanDeckBox"); var originalDeck = Deck(plans.SelectedItem)?.Id;
        var type = Control<ComboBox>(window, "KnowledgeTypeFilterBox"); var titleFilter = Control<TextBox>(window, "KnowledgeTitleFilterBox");
        var originalType = type.SelectedIndex; var originalFilter = titleFilter.Text;
        var oldFrame = Field(window, "_liveFrame"); var oldContext = Field(window, "_historyContext"); var oldObserved = Field(window, "_historyObservedAt");
        var oldSourceDeck = Field(window, "_sourceDeckVersion");
        var list = Control<ListBox>(window, "KnowledgeList"); var open = Control<Button>(window, "OpenKnowledgeSnapshotButton");
        try
        {
            Await((Task)Invoke(window, "LoadDeckLibraryAsync", (object?)null)!);
            plans.SelectedItem = plans.Items.Cast<object>().Single(item => Deck(item)?.Id == deck.Id);
            type.SelectedIndex = 0; titleFilter.Text = ""; WaitKnowledge(window);
            Click(Control<Button>(window, "KnowledgeNextPageButton")); WaitKnowledge(window);
            Check(((KnowledgePagingState)Field(window, "_knowledgePaging")!).PageNumber == 2, "The old note did not require a later knowledge page.");
            list.SelectedItem = list.Items.Cast<object>().Single(item => Entry(item).EntryId == note.EntryId);
            Check(open.IsEnabled, "A saved anchored note was not navigable."); Click(open); WaitNavigation(window);
            var historical = (HistoricalContext)Field(window, "_historyContext")!;
            Check(historical.SessionId == point.SessionId && historical.Snapshot.EventSequence == 42 && historical.Snapshot.StateVersion == 3 &&
                historical.DeckVersionId == deck.Id && historical.CardBuild == "255790" && Equals(Field(window, "_historyObservedAt"), point.RecordedAt) &&
                ((SnapshotAccess)typeof(MainWindow).GetProperty("CurrentAccess", Private)!.GetValue(window)!).Kind == SnapshotViewKind.Review,
                "Note navigation used another page/session/version/deck/cutoff or remained a live view.");
            Check(!historical.Snapshot.Entities.ContainsKey(70) && Field(window, "_analysis") is null, "Navigation revealed later entities or automatically started analysis.");
            var library = new CoachLibraryContext(store, knowledge, new(point.SessionId, point.StateVersion, "255790", deck.Id, point.RecordedAt, EventSequence: point.EventSequence));
            var result = Await(library.ExecuteAsync("get_matchup_plan", new JsonObject(), CancellationToken.None));
            var entries = result["entries"]!.AsArray();
            Check(entries.Any(row => row!["EntryId"]!.GetValue<string>() == plan.EntryId && row["content"]!.GetValue<string>() == "PLAN_BEFORE") &&
                entries.All(row => row!["content"]!.GetValue<string>() != "NOTE_V2_LATE" && row["content"]!.GetValue<string>() != "PLAN_AFTER"), "Later note/plan revisions entered the old knowledge scope.");

            SelectNote(window, missing); var before = Display(window); Click(open); WaitNavigation(window);
            Check(ReferenceEquals(before, Display(window)) && NavStatus(window).Contains("精确快照", StringComparison.Ordinal) &&
                Await(store.GetSnapshotAtAsync(point.SessionId, 43))?.EventSequence == 42, "A predecessor snapshot was presented as the exact note anchor.");
            SelectNote(window, absent); Click(open); WaitNavigation(window); Check(NavStatus(window).Contains("不存在", StringComparison.Ordinal), "Missing original session was not explained.");
            SelectNote(window, multi); before = Display(window); Click(open); WaitNavigation(window);
            var versions = Control<ComboBox>(window, "KnowledgeSnapshotVersionBox");
            Check(versions.Items.Count == 2 && versions.SelectedIndex == -1 && ReferenceEquals(before, Display(window)), "An ambiguous anchor guessed a state version.");
            versions.SelectedIndex = 0; Click(open); WaitNavigation(window);
            historical = (HistoricalContext)Field(window, "_historyContext")!;
            var firstVersion = Await(store.ListDecisionPointsAsync(multi.MatchId!, true)).First(candidate => candidate.EventSequence == 70);
            Check(historical.Snapshot.StateVersion == firstVersion.StateVersion && !historical.Snapshot.Entities.ContainsKey(70) &&
                historical.RecentEvents.All(row => row.CardId != "LATER_PUBLIC_CARD") && Equals(Field(window, "_historyObservedAt"), firstVersion.RecordedAt), "Older same-event version received a newer snapshot/event identity or time.");
            // Explicit old timeline provenance gaps must remain visible after migration.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine((string)Field(window, "_dataRoot")!, "sessions.db"), Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE session_events SET observed_state_version=NULL WHERE session_id='note-nav-multiple' AND event_seq=70"; command.ExecuteNonQuery();
            }
            Click(open); WaitNavigation(window); versions.SelectedIndex = 0; Click(open); WaitNavigation(window);
            Check(NavStatus(window).Contains("同序号", StringComparison.Ordinal) && ((HistoricalContext)Field(window, "_historyContext")!).RecentEvents.All(row => row.CardId != "LATER_PUBLIC_CARD"), "Unattributed old timeline events were silently assigned to a version.");

            SelectNote(window, unbound); Click(open); WaitNavigation(window); historical = (HistoricalContext)Field(window, "_historyContext")!;
            Check(historical.DeckVersionId is null && historical.Deck is null && historical.CardBuild == "444444" &&
                NavStatus(window).Contains("未绑定套牌", StringComparison.Ordinal) && NavStatus(window).Contains("卡库", StringComparison.Ordinal), "Missing deck/catalog borrowed the note or current binding.");
            Set(window, "_sourceDeckVersion", deck); Invoke(window, "MarkDecisionClicked", window, new RoutedEventArgs());
            Check(Field(window, "_newNoteAnchor") is null && ((CoachKnowledgeVersion?)Field(window, "_editingKnowledge"))?.EntryId == unbound.EntryId,
                "Marking an unbound historical frame borrowed the current source deck and replaced its saved note.");
            Set(window, "_sourceDeckVersion", oldSourceDeck);
            SelectNote(window, note); before = Display(window); Click(open); Control<TextBox>(window, "KnowledgeContentBox").Text = "DIRTY_NAV_DRAFT";
            WaitNavigation(window); Check(ReferenceEquals(before, Display(window)) && Control<TextBox>(window, "KnowledgeContentBox").Text == "DIRTY_NAV_DRAFT" && !open.IsEnabled,
                "A late navigation result overwrote a dirty draft or changed the displayed frame.");
            Click(Control<Button>(window, "DiscardKnowledgeChangesButton")); WaitKnowledge(window);
            Invoke(window, "ReturnLiveClicked", window, new RoutedEventArgs());
            var liveFirst = Frame("note-live-a", 1, 1); var liveNext = Frame("note-live-b", 1, 1); Set(window, "_liveFrame", liveFirst);
            var generation = Field(window, "_sourceGeneration"); Click(open); Set(window, "_liveFrame", liveNext); WaitNavigation(window);
            Check(Equals(generation, Field(window, "_sourceGeneration")) && ReferenceEquals(liveNext, Display(window)) && Field(window, "_historyContext") is null,
                "A navigation completion overwrote the next live game without a generation change.");
            CheckCloseCleanup(note);
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Note navigation displayed a window or created a handle.");
        }
        finally
        {
            Invoke(window, "CancelKnowledgeNavigation", (object?)null); Invoke(window, "ClearKnowledgeEditor", true);
            type.SelectedIndex = originalType; titleFilter.Text = originalFilter;
            plans.SelectedItem = plans.Items.Cast<object>().FirstOrDefault(item => Deck(item)?.Id == originalDeck); WaitKnowledge(window);
            Set(window, "_liveFrame", oldFrame); Set(window, "_historyContext", oldContext); Set(window, "_historyObservedAt", oldObserved);
            Set(window, "_sourceDeckVersion", oldSourceDeck);
            Invoke(window, "RefreshAccessState");
        }
    }
    private static void CheckCloseCleanup(CoachKnowledgeVersion note)
    {
        var window = new MainWindow(); var state = (KnowledgeNavigationState)Field(window, "_knowledgeNavigation")!;
        var request = state.Begin(note, 1, 2, 3, "live-a"); var cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        Set(window, "_knowledgeNavigationCancellation", cancellation); Set(window, "_knowledgeNavigationLoading", true);
        var closed = false; window.Closed += (_, _) => closed = true; window.Close();
        Check(token.IsCancellationRequested && Field(window, "_knowledgeNavigationCancellation") is null && !state.IsCurrent(request, note, 1, 2, 3, "live-a"), "Closing retained a navigation CTS or publishable request.");
        WaitUntil(() => closed, "Hidden navigation cleanup window did not close.");
        Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "Navigation closing check created a native handle.");
    }
    private static GameSnapshot Frame(string match, long version, long sequence, bool later = false)
    {
        var frame = BattleViewRegression.Frame() with { MatchId = match, StateVersion = version, EventSequence = sequence, ClientBuild = "255790" };
        if (!later) return frame;
        var entities = frame.Entities.ToDictionary(pair => pair.Key, pair => pair.Value);
        entities[70] = new(70, "LATER_PUBLIC_CARD", EntityKind.Card, new Dictionary<string, string>
        { ["CONTROLLER"] = "2", ["ZONE"] = "PLAY", ["CARDTYPE"] = "MINION", ["ATK"] = "9", ["HEALTH"] = "9", ["DAMAGE"] = "0" }.ToFrozenDictionary());
        return frame with { Entities = entities.ToFrozenDictionary() };
    }
    private static void SelectNote(MainWindow window, CoachKnowledgeVersion note)
    {
        Control<TextBox>(window, "KnowledgeTitleFilterBox").Text = note.Title; WaitKnowledge(window);
        var list = Control<ListBox>(window, "KnowledgeList"); list.SelectedItem = list.Items.Cast<object>().Single(item => Entry(item).EntryId == note.EntryId);
    }
    private static void WaitKnowledge(MainWindow window) => WaitUntil(() => !((KnowledgePagingState)Field(window, "_knowledgePaging")!).IsLoading, "Knowledge query did not finish.");
    private static void WaitNavigation(MainWindow window) => WaitUntil(() => !(bool)Field(window, "_knowledgeNavigationLoading")!, "Note navigation did not finish.");
    private static string NavStatus(MainWindow window) => Control<TextBlock>(window, "KnowledgeNavigationStatusText").Text;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static void Set(object value, string name, object? data) => value.GetType().GetField(name, Private)!.SetValue(value, data);
    private static object? Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static object? Display(MainWindow window) => typeof(MainWindow).GetProperty("DisplaySnapshot", Private)!.GetValue(window);
    private static DeckVersion? Deck(object? item) => item?.GetType().GetProperty("Value")?.GetValue(item) as DeckVersion;
    private static CoachKnowledgeVersion Entry(object item) => (CoachKnowledgeVersion)item.GetType().GetProperty("Value")!.GetValue(item)!;
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static void Click(Button button) { Check(button.IsEnabled, "Disabled knowledge navigation action " + button.Name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Navigation fixture setup timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
