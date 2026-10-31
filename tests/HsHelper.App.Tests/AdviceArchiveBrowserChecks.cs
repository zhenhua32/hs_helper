using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class AdviceArchiveBrowserChecks
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("archive pages reject late results failures and wrong time bounds", LateResults);
        yield return ("archive filters return to page one without moving the browse cutoff", FilterReset);
        yield return ("archive refresh renews its cutoff while retaining narrower caller bounds", RefreshCutoff);
        yield return ("closed archive pages cannot publish or start new reads", CloseBoundary);
        yield return ("archive details retain historical actions and actual unknown sources", ReadOnlyDetails);
    }
    private static AdviceArchivePage Page(AdviceArchivePageRequest request, bool more = false) => new(
        Array.AsReadOnly(Array.Empty<ArchivedAdviceRecord>()), request.PageNumber, 100, more, request.AsOf);
    private static void LateResults()
    {
        var state = new AdviceArchivePagingState(); var scope = new AdviceArchiveFilter(MatchId: "match-a");
        var first = state.Begin(scope, 1, Now); state.TryApply(first, Page(first, true));
        var delayed = state.Begin(scope, 2, Now.AddSeconds(1)); var current = state.Begin(scope, 1, Now.AddSeconds(2));
        Check(!state.TryApply(delayed, Page(delayed)) && !state.TryFail(delayed) && state.IsLoading &&
            !state.TryApply(current, Page(current) with { AsOf = Now.AddMinutes(1) }) && state.TryApply(current, Page(current)), "An old archive response or failure replaced a newer request.");
    }
    private static void FilterReset()
    {
        var state = new AdviceArchivePagingState(); var filter = new AdviceArchiveFilter(MatchId: "match-a");
        var first = state.Begin(filter, 1, Now); state.TryApply(first, Page(first, true));
        var second = state.Begin(filter, 2, Now.AddMinutes(1)); state.TryApply(second, Page(second));
        var changed = state.Begin(filter with { Task = "", TextContains = " %_ " }, 2, Now.AddHours(1));
        Check(changed.PageNumber == 1 && changed.AsOf == Now && changed.Filter.Task == "" && changed.Filter.TextContains == " %_ " &&
            !state.TryApply(second, Page(second)), "Archive filtering widened time, retained a later page, or changed exact/literal values.");
    }
    private static void RefreshCutoff()
    {
        var state = new AdviceArchivePagingState(); var filter = new AdviceArchiveFilter(MatchId: "match-a");
        var first = state.Begin(filter, 1, Now); state.TryApply(first, Page(first));
        var refresh = state.Begin(filter, 2, Now.AddHours(1), refresh: true);
        var narrow = state.Begin(filter with { RecordedBefore = Now.AddDays(-1) }, 1, Now.AddHours(2));
        Check(refresh.PageNumber == 1 && refresh.AsOf == Now.AddHours(1) && narrow.AsOf == Now.AddDays(-1) && !state.TryApply(refresh, Page(refresh)),
            "Refresh or an existing earlier filter failed to bind the actual browsing upper limit.");
    }
    private static void CloseBoundary()
    {
        var state = new AdviceArchivePagingState(); var filter = new AdviceArchiveFilter(MatchId: "match-a");
        var pending = state.Begin(filter, 1, Now); state.Close();
        Check(!state.IsLoading && !state.TryApply(pending, Page(pending)) && !state.TryFail(pending), "A closed archive accepted a late continuation.");
        try { state.Begin(filter, 1, Now); throw new Exception("Closed archive started another read."); }
        catch (InvalidOperationException) { }
    }
    private static void ReadOnlyDetails()
    {
        var record = new ArchivedAdviceRecord("old-record", Now, Draft("old-match", 0) with { DeckVersionId = null, Model = null, AnalysisRunId = null, Task = null! });
        var text = AdviceArchiveWindow.Render(record);
        Check(text.Contains("事后资料", StringComparison.Ordinal) && text.Contains("old-record", StringComparison.Ordinal) && text.Contains(Now.ToLocalTime().ToString("yyyy-MM-dd"), StringComparison.Ordinal) &&
            text.Contains("套牌版本：未记录", StringComparison.Ordinal) && text.Contains("当时的归档动作", StringComparison.Ordinal) && text.Contains("结束回合", StringComparison.Ordinal) &&
            text.Contains("结束回合 · 来源 #2", StringComparison.Ordinal) && text.Contains("#991", StringComparison.Ordinal) && text.Contains("#992", StringComparison.Ordinal) && text.Contains("斩杀标记：是", StringComparison.Ordinal) &&
            text.Contains("不是对当前局面的认证", StringComparison.Ordinal) && text.Contains("旧记录未保存来源", StringComparison.Ordinal), "Archive rendering lost actions, unknown provenance or historical scope.");
    }
    private static AdviceRecordDraft Draft(string match, int index) => new(match, index, index, index == 0 ? "111111" : "255790", "archive-fixture/turn", index == 2 ? "摘要 NEEDLE %_ 原样" : $"隔离建议 {index:D3}")
    {
        Task = index switch { 0 => "", 1 => "CustomLegacy", _ => "Turn" }, UserQuestion = index == 2 ? "字面 question_" : "当时的问题",
        Reasons = ["保留当时依据"], Risks = ["当时未知项"], Evidence = [new("evaluate_line", "当时工具依据")],
        Verification = "verified", VerifiedLethal = true, Actions = [new("end_turn", 2), new("attack", 991, 992), new("choose", 77, 78)]
    };

    public static void RunOnDispatcher(MainWindow main)
    {
        var store = (SessionStore)Field(main, "_store")!; var archive = (AdviceArchiveStore)Field(main, "_adviceArchive")!;
        var source = Await(store.ListDeckVersionsAsync()).First();
        var deck = Await(store.SaveDeckVersionAsync("archive-browser-ui", source.Definition, "255790"));
        var otherDeck = Await(store.SaveDeckVersionAsync("archive-browser-ui", source.Definition, "255790", deck.Id));
        const string match = "archive-browser-selected"; const string otherMatch = "archive-browser-other";
        var frame = BattleViewRegression.Frame() with { MatchId = match, StateVersion = 200, EventSequence = 200 };
        var recorder = new SessionRecorder(store, new(null, "255790", 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync());
        var session = Await(store.ListSessionsAsync(new(Limit: 10000))).Single(row => row.Id == match);
        var sessionRecords = new List<ArchivedAdviceRecord>(); var deckRecords = new List<ArchivedAdviceRecord>();
        for (var i = 0; i < 105; i++)
        {
            sessionRecords.Add(Await(archive.SaveAsync(Draft(match, i) with { DeckVersionId = i == 0 ? null : deck.Id }, "archive-session-" + i.ToString("D3"))));
            deckRecords.Add(Await(archive.SaveAsync(Draft("deck-" + deck.Id, i) with { DeckVersionId = i == 0 ? null : deck.Id, Task = i switch { 0 => "", 1 => "CustomLegacy", _ => "DeckPlan" } }, "archive-deck-" + i.ToString("D3"))));
        }
        Await(archive.SaveAsync(Draft("deck-" + otherDeck.Id, 0) with { DeckVersionId = otherDeck.Id }, "archive-other-deck"));
        Await(archive.SaveAsync(Draft(otherMatch, 0) with { DeckVersionId = deck.Id }, "archive-other-match"));
        // Real legacy JSON can lack Task or explicitly store null; both remain readable/exact-empty matches.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine((string)Field(main, "_dataRoot")!, "advice-archive.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE coach_advice_records SET payload_json=json_set(payload_json,'$.Task',NULL) WHERE id='archive-session-000'"; command.ExecuteNonQuery();
            command.CommandText = "UPDATE coach_advice_records SET payload_json=json_remove(payload_json,'$.Task') WHERE id='archive-deck-000'"; command.ExecuteNonQuery();
        }

        var grid = Control<DataGrid>(main, "SessionsGrid"); var plans = Control<ComboBox>(main, "PlanDeckBox");
        var oldItems = grid.ItemsSource; var oldSelection = grid.SelectedItem; var oldContext = Field(main, "_historyContext");
        var oldSourceDeck = Field(main, "_sourceDeckVersion");
        var oldDeckId = Deck(plans.SelectedItem)?.Id; var oldPresenter = Field(main, "_showAdviceArchiveWindow");
        var browsers = new List<AdviceArchiveWindow>();
        Set(main, "_showAdviceArchiveWindow", new Action<AdviceArchiveWindow>(browser => { browsers.Add(browser); _ = browser.InitializeAsync(); }));
        try
        {
            Await((Task)Invoke(main, "LoadDeckLibraryAsync", (object?)null)!);
            plans.SelectedItem = plans.Items.Cast<object>().Single(item => Deck(item)?.Id == deck.Id);
            WaitUntil(() => !((KnowledgePagingState)Field(main, "_knowledgePaging")!).IsLoading, "Archive setup left knowledge loading.");
            var sessionItemType = typeof(MainWindow).GetNestedType("SessionItem", BindingFlags.NonPublic)!;
            var selectedItem = Activator.CreateInstance(sessionItemType, session, "未绑定套牌")!;
            grid.ItemsSource = new[] { selectedItem }; grid.SelectedItem = selectedItem;
            WaitUntil(() => !(bool)Field(main, "_historyLoading")!, "Archive session selection did not finish.");
            var pinned = new HistoricalContext(otherMatch, "255790", deck.Id, deck, frame with { MatchId = otherMatch }, []);
            Set(main, "_historyContext", pinned);
            Click(FindButton(main, "查看本局建议记录"));
            var sessionBrowser = browsers.Single(); WaitForPage(sessionBrowser, 1, 100);
            Check(sessionBrowser.Scope.MatchId == match && sessionBrowser.Scope.DeckVersionId is null && sessionBrowser.Scope.OriginEventSequence is null,
                "Selected unbound session borrowed the pinned frame's deck or event binding.");
            Click(FindButton(main, "查看套牌分析记录"));
            var deckBrowser = browsers.Last(); WaitForPage(deckBrowser, 1, 100);
            Check(deckBrowser.Scope.MatchId == "deck-" + deck.Id && deckBrowser.Title.Contains("套牌分析", StringComparison.Ordinal), "Plan entry used a session or same-name version scope.");
            grid.SelectedItem = null;
            Set(main, "_sourceDeckVersion", deck);
            Set(main, "_historyContext", new HistoricalContext(otherMatch, "255790", null, null, frame with { MatchId = otherMatch }, []));
            Click(FindButton(main, "查看本局建议记录"));
            var unboundHistoryBrowser = browsers.Last(); WaitForPage(unboundHistoryBrowser, 1, 1);
            Check(unboundHistoryBrowser.Scope.MatchId == otherMatch && unboundHistoryBrowser.Scope.DeckVersionId is null,
                "An unbound historical display borrowed the current source deck.");
            unboundHistoryBrowser.Close(); Set(main, "_sourceDeckVersion", oldSourceDeck);
            plans.SelectedItem = plans.Items.Cast<object>().Single(item => Deck(item)?.Id == otherDeck.Id);
            WaitUntil(() => !((KnowledgePagingState)Field(main, "_knowledgePaging")!).IsLoading, "Changing the parent deck did not settle.");
            var adviceBefore = Control<TextBox>(main, "AdviceBox").Text; var overlayBefore = Field(main, "_overlayAdvice");
            var displayBefore = Display(main); var historyBefore = Field(main, "_historyContext");
            foreach (var (browser, records) in new[] { (sessionBrowser, sessionRecords), (deckBrowser, deckRecords) })
            {
                var cutoff = browser.BrowsingAsOf;
                Click(Control<Button>(browser, "ArchiveNextButton")); WaitForPage(browser, 2, 5);
                Check(browser.BrowsingAsOf == cutoff && browser.Records.Any(record => record.Id == records[0].Id) && browser.Records.All(record => record.Advice.MatchId == browser.Scope.MatchId),
                    "Older archives were unreachable or mixed scopes/cutoffs.");
                var list = Control<ListBox>(browser, "ArchiveRecordsList"); list.SelectedIndex = list.Items.Count - 1;
                Check(browser.SelectedRecord?.Id == records[0].Id && Control<TextBox>(browser, "ArchiveDetailText").Text.Contains("套牌版本：未记录", StringComparison.Ordinal) &&
                    Control<TextBox>(browser, "ArchiveDetailText").Text.Contains("#991", StringComparison.Ordinal), "Old archive lost actions or invented missing provenance.");
                Click(Control<Button>(browser, "ArchivePreviousButton")); WaitForPage(browser, 1, 100);
                Check(browser.SelectedRecord?.Id == records[0].Id && Control<TextBlock>(browser, "ArchiveSelectionText").Text.Contains("不在当前页或筛选", StringComparison.Ordinal), "Paging silently replaced the selected archive details.");
                var all = Control<CheckBox>(browser, "ArchiveAllTasksBox"); var task = Control<ComboBox>(browser, "ArchiveTaskFilterBox");
                task.Text = "CustomLegacy"; all.IsChecked = false; WaitForPage(browser, 1, 1);
                Check(browser.Records[0].Id == records[1].Id && browser.SelectedRecord?.Id == records[0].Id, "Exact unknown task filtering lost records or selection.");
                task.Text = ""; WaitForPage(browser, 1, 1); Check(browser.Records[0].Id == records[0].Id, "Explicit empty task was treated as all tasks.");
                task.SelectedIndex = browser.Scope.Kind == AdviceArchiveScopeKind.DeckPlan ? 4 : 0; WaitForPage(browser, 1, 100);
                Check(browser.Records.All(record => record.Advice.Task == (browser.Scope.Kind == AdviceArchiveScopeKind.DeckPlan ? "DeckPlan" : "Turn")), "Known task labels did not map to the exact stored value.");
                all.IsChecked = true;
                var query = Control<TextBox>(browser, "ArchiveTextFilterBox"); query.Text = " NEEDLE %_ "; WaitForPage(browser, 1, 1);
                Check(browser.Records[0].Id == records[2].Id && browser.BrowsingAsOf == cutoff, "Literal text filtering changed symbols/spaces or browse cutoff.");
                query.Text = "no-match"; query.Text = "question_"; WaitForPage(browser, 1, 1);
                Check(browser.Records[0].Id == records[2].Id, "A delayed filter result replaced the newest question search.");
                query.Text = ""; WaitForPage(browser, 1, 100);
                var newRecord = Await(archive.SaveAsync(Draft(browser.Scope.MatchId, 106), "archive-new-" + browser.Scope.Kind));
                Click(Control<Button>(browser, "ArchiveNextButton")); WaitForPage(browser, 2, 5);
                Check(!browser.Records.Any(record => record.Id == newRecord.Id), "New insertion shifted an existing browsing window.");
                Click(Control<Button>(browser, "ArchiveRefreshButton")); WaitForPage(browser, 1, 100);
                Check(browser.Records[0].Id == newRecord.Id && browser.BrowsingAsOf > cutoff && browser.SelectedRecord?.Id == records[0].Id,
                    "Refresh failed to include new archives or overwrote the pinned selection.");
                Check(!browser.IsVisible && new WindowInteropHelper(browser).Handle == IntPtr.Zero, "Archive regression showed a window or created a handle.");
            }
            Check(ReferenceEquals(displayBefore, Display(main)) && ReferenceEquals(historyBefore, Field(main, "_historyContext")) &&
                Control<TextBox>(main, "AdviceBox").Text == adviceBefore && Equals(overlayBefore, Field(main, "_overlayAdvice")), "Archive actions or browsing changed the main advice, overlay or fixed snapshot.");
            var closingBrowser = browsers[0]; Control<TextBox>(closingBrowser, "ArchiveTextFilterBox").Text = "cancel this read";
            var selected = closingBrowser.SelectedRecord; closingBrowser.Close();
            WaitUntil(() => !closingBrowser.IsLoading, "Closed archive kept a loading operation.");
            Pump(); Check(ReferenceEquals(selected, closingBrowser.SelectedRecord) && Field(closingBrowser, "_readCancellation") is null, "Closed archive published a late result or retained its cancellation source.");
            Check(!main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero, "Archive entry created a main-window handle.");
        }
        finally
        {
            foreach (var browser in browsers) if (!(bool)typeof(AdviceArchivePagingState).GetProperty("IsClosed")!.GetValue(Field(browser, "_paging"))!) browser.Close();
            Set(main, "_showAdviceArchiveWindow", oldPresenter);
            grid.ItemsSource = oldItems; grid.SelectedItem = oldSelection;
            WaitUntil(() => !(bool)Field(main, "_historyLoading")!, "Restoring session selection did not settle.");
            Set(main, "_historyContext", oldContext);
            Set(main, "_sourceDeckVersion", oldSourceDeck);
            plans.SelectedItem = plans.Items.Cast<object>().FirstOrDefault(item => Deck(item)?.Id == oldDeckId);
            WaitUntil(() => !((KnowledgePagingState)Field(main, "_knowledgePaging")!).IsLoading, "Restoring the plan deck did not settle.");
        }
    }
    private static void WaitForPage(AdviceArchiveWindow browser, int page, int count) => WaitUntil(() => !browser.IsLoading && browser.PageNumber == page && browser.Records.Count == count, $"Archive page {page}/{count} did not load.");
    private static object? Display(MainWindow main) => typeof(MainWindow).GetProperty("DisplaySnapshot", Private)!.GetValue(main);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static void Set(object value, string name, object? data) => value.GetType().GetField(name, Private)!.SetValue(value, data);
    private static object? Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static DeckVersion? Deck(object? item) => item?.GetType().GetProperty("Value")?.GetValue(item) as DeckVersion;
    private static T Control<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static Button FindButton(DependencyObject root, string text)
    {
        if (root is Button button && Equals(button.Content, text)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { try { return FindButton(VisualTreeHelper.GetChild(root, i), text); } catch (KeyNotFoundException) { } }
        if (root is ContentControl content && content.Content is DependencyObject child) { try { return FindButton(child, text); } catch (KeyNotFoundException) { } }
        if (root is ItemsControl items) foreach (var item in items.Items.OfType<DependencyObject>()) { try { return FindButton(item, text); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException("Missing button " + text);
    }
    private static void Click(Button button) { Check(button.IsEnabled, "Disabled archive action " + button.Name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Archive fixture timed out."); task.GetAwaiter().GetResult(); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || clock.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
