using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Coaching;
using HsHelper.Storage;

public static class KnowledgePagingChecks
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("knowledge pages reject stale scope and out-of-order completions", OutOfOrder);
        yield return ("knowledge type and literal-title filters reset the first page", FilterReset);
        yield return ("knowledge browsing freezes as-of and refresh renews it", FrozenCutoff);
        yield return ("cancelled knowledge reads cannot publish or fail a newer page", CancelledRead);
    }
    private static CoachKnowledgePage Page(KnowledgePageRequest request, bool more = false) =>
        new(Array.AsReadOnly(Array.Empty<CoachKnowledgeVersion>()), request.PageNumber, 100, more, request.AsOf);
    private static void OutOfOrder()
    {
        var state = new KnowledgePagingState(); var scope = new CoachKnowledgeFilter(DeckVersionId: "deck-a");
        var first = state.Begin(scope, 1, Now); state.TryApply(first, Page(first, true));
        var slow = state.Begin(scope, 2, Now.AddSeconds(1)); var current = state.Begin(scope, 1, Now.AddSeconds(2));
        Check(!state.TryApply(slow, Page(slow)) && !state.TryFail(slow) && state.IsLoading &&
            !state.TryApply(current, Page(current) with { AsOf = Now.AddHours(1) }) && state.TryApply(current, Page(current, true)) &&
            state.PageNumber == 1 && state.HasMore, "An old response, failure or different cutoff replaced current knowledge results.");
    }
    private static void FilterReset()
    {
        var state = new KnowledgePagingState(); var scope = new CoachKnowledgeFilter(DeckVersionId: "deck-a");
        var first = state.Begin(scope, 1, Now); state.TryApply(first, Page(first, true));
        var old = state.Begin(scope, 2, Now);
        var filtered = state.Begin(scope with { Kind = CoachKnowledgeKind.DeckPlan, TitleContains = " %_ " }, 2, Now.AddMinutes(1));
        Check(filtered.PageNumber == 1 && filtered.Scope.TitleContains == " %_ " && !state.TryApply(old, Page(old)) &&
            state.TryApply(filtered, Page(filtered)), "Type/title filtering retained the previous page or changed the literal query.");
    }
    private static void FrozenCutoff()
    {
        var state = new KnowledgePagingState(); var scope = new CoachKnowledgeFilter(DeckVersionId: "deck-a");
        var first = state.Begin(scope, 1, Now); state.TryApply(first, Page(first, true));
        var second = state.Begin(scope, 2, Now.AddMinutes(1)); state.TryApply(second, Page(second));
        var refresh = state.Begin(scope, 2, Now.AddMinutes(2), refresh: true);
        Check(second.AsOf == Now && refresh.AsOf == Now.AddMinutes(2) && refresh.PageNumber == 1 && !state.TryApply(second, Page(second)),
            "Paging moved its historical cutoff or refresh reused the prior cutoff/page.");
    }
    private static void CancelledRead()
    {
        var state = new KnowledgePagingState(); var scope = new CoachKnowledgeFilter(DeckVersionId: "deck-a");
        var old = state.Begin(scope, 1, Now); state.Cancel();
        Check(!state.IsLoading && !state.TryApply(old, Page(old)) && !state.TryFail(old), "Cancelled knowledge results remained publishable.");
        var latest = state.Begin(scope, 1, Now.AddMinutes(1));
        Check(!state.TryFail(old) && state.IsLoading && state.TryApply(latest, Page(latest)), "A cancelled error cleared a newer loading state.");
    }

    /// <summary>Use the existing isolated STA/hidden main window; never opens a window or calls a model.</summary>
    public static void RunOnDispatcher(MainWindow window)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Knowledge UI check requires the existing STA.");
        var store = (SessionStore)Field(window, "_store")!; var knowledge = (CoachKnowledgeStore)Field(window, "_knowledge")!;
        var source = Await(store.ListDeckVersionsAsync()).First();
        var deck = Await(store.SaveDeckVersionAsync("knowledge-paging-ui", source.Definition, "255790"));
        var plan = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, "原始计划 %_", "PLAN_V1", "MAGE", true)));
        var note = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "带锚点的旧笔记", "NOTE_V1", "MAGE", true, "knowledge-ui-match") { EventSequence = 7 }));
        for (var i = 0; i < 104; i++)
            Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, $"后续笔记 {i:D3}", "SYNTHETIC_ONLY")));

        var deckBox = Control<ComboBox>(window, "PlanDeckBox"); var originalDeck = Deck(deckBox.SelectedItem)?.Id;
        var type = Control<ComboBox>(window, "KnowledgeTypeFilterBox"); var filter = Control<TextBox>(window, "KnowledgeTitleFilterBox");
        var list = Control<ListBox>(window, "KnowledgeList"); var title = Control<TextBox>(window, "KnowledgeTitleBox");
        var content = Control<TextBox>(window, "KnowledgeContentBox"); var save = Control<Button>(window, "SaveKnowledgeButton");
        var previous = Control<Button>(window, "KnowledgePrevPageButton"); var next = Control<Button>(window, "KnowledgeNextPageButton");
        try
        {
            Await((Task)Invoke(window, "LoadDeckLibraryAsync", (object?)null)!);
            deckBox.SelectedItem = deckBox.Items.Cast<object>().Single(item => Deck(item)?.Id == deck.Id);
            WaitForPage(window, 1, 100);
            Check(next.IsEnabled && Control<TextBlock>(window, "KnowledgePageText").Text.Contains("后面还有", StringComparison.Ordinal), "First page hid additional knowledge.");
            var frozen = Paging(window).AsOf;
            Click(next); WaitForPage(window, 2, 6);
            Check(Paging(window).AsOf == frozen && previous.IsEnabled && !next.IsEnabled, "Second page changed the browsing cutoff or bounds.");
            list.SelectedItem = list.Items.Cast<object>().Single(item => Entry(item).EntryId == plan.EntryId);
            Check(Editing(window)?.EntryId == plan.EntryId && content.Text == "PLAN_V1", "Second-page plan was not editable.");

            // Change the query while a dirty edit exists; the delayed query must only replace the list.
            title.Text = "修改后的计划 %_"; filter.Text = "原始计划"; content.Text = "PLAN_V2";
            WaitForPage(window, 1, 1);
            Check(content.Text == "PLAN_V2" && title.Text == "修改后的计划 %_" && Editing(window)?.EntryId == plan.EntryId,
                "A list query overwrote the editor draft.");
            Click(save);
            Check(Saving(window), "The save did not retain its busy state through list refresh.");
            AssertCloseBlocked(window, "正在保存");
            WaitUntil(() => !Saving(window) && Editing(window) is { Version: 2 } && !Paging(window).IsLoading, "Old plan did not save v2.");
            var revised = Editing(window)!;
            Check(list.Items.Count == 0 && content.Text == "PLAN_V2" && Control<TextBlock>(window, "KnowledgeEditorStatusText").Text.Contains("不在当前页或筛选", StringComparison.Ordinal) &&
                revised.DeckVersionId == plan.DeckVersionId && revised.MatchId == plan.MatchId && revised.EventSequence == plan.EventSequence,
                "A filtered-out save cleared its editor or changed immutable binding.");
            Check(Await(knowledge.GetAsOfAsync(plan.EntryId, plan.RecordedAt))?.Content == "PLAN_V1", "WPF revision changed the old plan version.");
            var currentLibrary = new CoachLibraryContext(store, knowledge, new("knowledge-ui-analysis", 1, "255790", deck.Id, DateTimeOffset.UtcNow,
                "MAGE", true, EventSequence: 9));
            var oldLibrary = new CoachLibraryContext(store, knowledge, new("knowledge-ui-analysis", 1, "255790", deck.Id, plan.RecordedAt,
                "MAGE", true, EventSequence: 9));
            var currentResult = Await(currentLibrary.ExecuteAsync("get_matchup_plan", new JsonObject(), CancellationToken.None));
            var oldResult = Await(oldLibrary.ExecuteAsync("get_matchup_plan", new JsonObject(), CancellationToken.None));
            Check(ToolPlan(currentResult, plan.EntryId, 2, "PLAN_V2") && ToolPlan(oldResult, plan.EntryId, 1, "PLAN_V1"),
                "Actual WPF→store→model library lost the current or as-of plan under more than 100 notes.");

            filter.Text = ""; WaitForPage(window, 1, 100);
            content.Text = "UNSAVED_DRAFT";
            AssertCloseBlocked(window, "未保存修改");
            Click(next); WaitForPage(window, 2, 6);
            type.SelectedIndex = 2; WaitForPage(window, 1, 100);
            Click(Control<Button>(window, "NewKnowledgeButton"));
            Click(Control<Button>(window, "DeleteKnowledgeButton"));
            Invoke(window, "MarkDecisionClicked", window, new RoutedEventArgs());
            list.SelectedItem = list.Items[0];
            var otherDeck = deckBox.Items.Cast<object>().First(item => Deck(item)?.Id != deck.Id);
            deckBox.SelectedItem = otherDeck;
            Check(content.Text == "UNSAVED_DRAFT" && Editing(window)?.EntryId == plan.EntryId && Deck(deckBox.SelectedItem)?.Id == deck.Id &&
                Await(knowledge.GetAsOfAsync(plan.EntryId, DateTimeOffset.MaxValue)) is { IsDeleted: false, Version: 2 },
                "Navigation, new, mark, delete, or deck change discarded a dirty draft or changed its binding.");
            Click(Control<Button>(window, "DiscardKnowledgeChangesButton"));
            Check(content.Text == "PLAN_V2", "Explicit discard did not restore the saved version.");

            // Literal filtering must not turn percent/underscore into wildcards.
            type.SelectedIndex = 1; filter.Text = " %_"; WaitForPage(window, 1, 1);
            Check(Entry(list.Items[0]).EntryId == plan.EntryId, "Literal title symbols or leading spaces were lost.");
            type.SelectedIndex = 2; filter.Text = "带锚点"; WaitForPage(window, 1, 1);
            list.SelectedIndex = 0; content.Text = "NOTE_V2"; Click(save);
            WaitUntil(() => !Saving(window) && Editing(window) is { Version: 2 } entry && entry.EntryId == note.EntryId && !Paging(window).IsLoading, "Anchored note revision did not finish.");
            Check(Editing(window) is { MatchId: "knowledge-ui-match", EventSequence: 7 } && Editing(window)!.DeckVersionId == deck.Id &&
                Await(knowledge.GetAsOfAsync(note.EntryId, note.RecordedAt))?.Content == "NOTE_V1", "Editing changed the note's deck/event binding or history.");
            type.SelectedIndex = 1; filter.Text = " %_"; WaitForPage(window, 1, 1); list.SelectedIndex = 0;
            Click(Control<Button>(window, "DeleteKnowledgeButton"));
            WaitUntil(() => !Saving(window) && Editing(window) is null && !Paging(window).IsLoading && list.Items.Count == 0, "Deleting an old filtered plan did not refresh the list.");
            Check(Await(knowledge.GetAsOfAsync(plan.EntryId, DateTimeOffset.MaxValue, includeDeleted: true)) is { IsDeleted: true, Version: 3 } &&
                Await(knowledge.GetAsOfAsync(plan.EntryId, plan.RecordedAt))?.Content == "PLAN_V1" &&
                Await(knowledge.GetAsOfAsync(plan.EntryId, revised.RecordedAt))?.Content == "PLAN_V2", "Deleting a cross-page plan erased its previous versions.");

            // A refresh can adopt a newer version only while the editor remains clean.
            var concurrent = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, "并发修订计划", "EXTERNAL_V1")));
            filter.Text = "并发修订"; WaitForPage(window, 1, 1); list.SelectedIndex = 0;
            var external2 = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, concurrent.Title, "EXTERNAL_V2"), concurrent.EntryId, 1));
            Click(Control<Button>(window, "KnowledgeRefreshButton"));
            WaitUntil(() => !Paging(window).IsLoading && Editing(window)?.Version == 2, "Clean editor did not load the external current version.");
            Check(content.Text == "EXTERNAL_V2", "Clean refresh kept an obsolete edit baseline.");
            content.Text = "LOCAL_UNSAVED";
            var external3 = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, concurrent.Title, "EXTERNAL_V3"), concurrent.EntryId, 2));
            Click(Control<Button>(window, "KnowledgeRefreshButton")); WaitForPage(window, 1, 1);
            Check(content.Text == "LOCAL_UNSAVED" && Editing(window)?.Version == 2 && Control<TextBlock>(window, "KnowledgeEditorStatusText").Text.Contains("较新版本", StringComparison.Ordinal),
                "External revision replaced dirty content or left the conflict undisclosed.");
            Click(Control<Button>(window, "DiscardKnowledgeChangesButton"));
            WaitUntil(() => !Paging(window).IsLoading && Editing(window)?.Version == 3, "Explicit discard could not load the current external revision.");
            Check(content.Text == "EXTERNAL_V3" && Editing(window)?.EntryId == concurrent.EntryId, "Discard failed to recover the immutable entry binding.");
            content.Text = "UNSAVED_AFTER_EXTERNAL_DELETE"; Await(knowledge.DeleteAsync(concurrent.EntryId, external3.Version));
            Click(Control<Button>(window, "DiscardKnowledgeChangesButton"));
            WaitUntil(() => !Paging(window).IsLoading && Editing(window) is null && list.Items.Count == 0, "Discard did not clear an externally deleted entry.");
            Check(content.Text == "" && Await(knowledge.GetAsOfAsync(concurrent.EntryId, external2.RecordedAt))?.Content == "EXTERNAL_V2",
                "External delete recovery retained a stale draft or erased earlier content.");

            var request = Paging(window).Begin(new CoachKnowledgeFilter(DeckVersionId: deck.Id), 1, DateTimeOffset.UtcNow);
            using var cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            Set(window, "_knowledgeListCancellation", cancellation); Invoke(window, "CancelKnowledgeListRead");
            Check(token.IsCancellationRequested && Field(window, "_knowledgeListCancellation") is null && !Paging(window).TryApply(request, Page(request)),
                "Knowledge cleanup left a token or publishable request behind.");
        }
        finally
        {
            Invoke(window, "ClearKnowledgeEditor", true);
            type.SelectedIndex = 0; filter.Text = "";
            deckBox.SelectedItem = deckBox.Items.Cast<object>().FirstOrDefault(item => Deck(item)?.Id == originalDeck);
            Await((Task)Invoke(window, "RefreshKnowledgeAsync", (object?)null)!);
        }
    }
    private static bool ToolPlan(JsonNode result, string id, int version, string content) => result["entries"]!.AsArray().Any(row =>
        row!["EntryId"]!.GetValue<string>() == id && row["Version"]!.GetValue<int>() == version && row["content"]!.GetValue<string>() == content);
    private static void AssertCloseBlocked(MainWindow window, string reason)
    {
        var closed = false; EventHandler handler = (_, _) => closed = true; window.Closed += handler;
        try
        {
            window.Close();
            Check(!closed && !(bool)Field(window, "_closing")! && Control<TextBlock>(window, "StatusText").Text.Contains(reason, StringComparison.Ordinal),
                "Closing discarded a busy/dirty knowledge editor or failed to explain why it stayed open.");
        }
        finally { window.Closed -= handler; }
    }
    private static void WaitForPage(MainWindow window, int page, int count) => WaitUntil(() => !Paging(window).IsLoading && Paging(window).PageNumber == page &&
        Control<ListBox>(window, "KnowledgeList").Items.Count == count, $"Knowledge page {page}/{count} did not load.");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static KnowledgePagingState Paging(MainWindow window) => (KnowledgePagingState)Field(window, "_knowledgePaging")!;
    private static CoachKnowledgeVersion? Editing(MainWindow window) => (CoachKnowledgeVersion?)Field(window, "_editingKnowledge");
    private static bool Saving(MainWindow window) => (bool)Field(window, "_knowledgeSaving")!;
    private static DeckVersion? Deck(object? item) => item?.GetType().GetProperty("Value")?.GetValue(item) as DeckVersion;
    private static CoachKnowledgeVersion Entry(object item) => (CoachKnowledgeVersion)item.GetType().GetProperty("Value")!.GetValue(item)!;
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static void Click(Button button) { Check(button.IsEnabled, "Knowledge action was disabled: " + button.Name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Knowledge fixture operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> complete, string message)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(complete(), message);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
