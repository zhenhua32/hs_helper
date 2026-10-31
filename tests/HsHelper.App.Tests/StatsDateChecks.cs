using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using HsHelper.App;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class StatsDateChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("history dates retain empty and one-sided local calendar bounds", EmptyAndLocalBounds);
        yield return ("history dates reject inverted or unrepresentable UTC ranges", InvalidBounds);
        yield return ("last-seven and last-thirty ranges include today and remain fixed through paging", FixedRecentDays);
        yield return ("history calendar bounds cover synthetic 23-hour and 25-hour days", DaylightDays);
        yield return ("history dates reject a nonexistent midnight rather than silently shift it", MissingMidnight);
        yield return ("history ambiguous midnight starts at its earliest UTC occurrence", AmbiguousStart);
        yield return ("history ambiguous final clock time ends at its latest UTC occurrence", AmbiguousEnd);
    }
    private static TimeZoneInfo Fixed(int hours) => TimeZoneInfo.CreateCustomTimeZone("stats-fixed-" + hours, TimeSpan.FromHours(hours), "fixed", "fixed");
    private static void EmptyAndLocalBounds()
    {
        var zone = Fixed(8); var date = new DateTime(2026, 2, 24, 15, 0, 0, DateTimeKind.Utc);
        var empty = HistoryDateRange.FromDates(null, null, zone);
        var start = HistoryDateRange.FromDates(date, null, zone); var end = HistoryDateRange.FromDates(null, date, zone);
        Check(empty.Since is null && empty.Until is null && start.Until is null && end.Since is null &&
            start.Since == new DateTimeOffset(2026, 2, 23, 16, 0, 0, TimeSpan.Zero) &&
            end.Until == new DateTimeOffset(2026, 2, 24, 16, 0, 0, TimeSpan.Zero).AddTicks(-1),
            "Empty dates or local date components were replaced with the machine zone, a current cutoff or an exclusive end.");
    }
    private static void InvalidBounds()
    {
        Throws<ArgumentException>(() => HistoryDateRange.FromDates(new(2026, 3, 2), new(2026, 3, 1), Fixed(0)));
        Throws<ArgumentException>(() => HistoryDateRange.FromDates(DateTime.MinValue, null, Fixed(8)));
        Throws<ArgumentException>(() => HistoryDateRange.FromDates(null, DateTime.MaxValue, Fixed(-8)));
        Throws<ArgumentOutOfRangeException>(() => HistoryDateRange.RecentDates(new(2026, 3, 2), 0));
    }
    private static void FixedRecentDays()
    {
        var today = new DateTime(2026, 3, 2, 23, 59, 59);
        foreach (var days in new[] { 7, 30 })
        {
            var selected = HistoryDateRange.RecentDates(today, days);
            Check(selected.Until == new DateTime(2026, 3, 2) && selected.Since == new DateTime(2026, 3, 2).AddDays(1 - days),
                "Recent dates described rolling hours or excluded today.");
            var range = HistoryDateRange.FromDates(selected.Since, selected.Until, Fixed(8));
            var state = new HistoryPagingState(); var filter = new SessionFilter(Since: range.Since, Until: range.Until);
            var first = state.Begin(filter, 1, new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero));
            Check(state.TryApply(first, new([], 1, 100, true)), "Initial recent-days page was not accepted.");
            var next = state.Begin(filter, 2, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero));
            Check(next.PageNumber == 2 && next.Scope.Since == first.Scope.Since && next.Scope.Until == first.Scope.Until && next.Query.Until == first.Query.Until,
                "Page navigation recomputed the recent-days dates or moved the frozen scope.");
        }
    }
    private static TimeZoneInfo DaylightZone(string name, int startMonth, int startDay, int startHour, int endMonth, int endDay, int endHour)
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, startHour, 0, 0), startMonth, startDay);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, endHour, 0, 0), endMonth, endDay);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2026, 1, 1), new(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(name, TimeSpan.Zero, name, "standard", "daylight", [rule]);
    }
    private static void DaylightDays()
    {
        var zone = DaylightZone("stats-normal-dst", 3, 8, 2, 11, 1, 2);
        foreach (var (date, hours) in new[] { (new DateTime(2026, 3, 8), 23), (new DateTime(2026, 11, 1), 25) })
        {
            var range = HistoryDateRange.FromDates(date, date, zone);
            Check((range.Until!.Value - range.Since!.Value + TimeSpan.FromTicks(1)).TotalHours == hours,
                "A local calendar day was treated as a fixed 24-hour period across a DST change.");
        }
    }
    private static void MissingMidnight()
    {
        var zone = DaylightZone("stats-missing-midnight", 3, 1, 0, 11, 1, 1);
        Check(zone.IsInvalidTime(new(2026, 3, 1)), "The synthetic missing-midnight fixture was not invalid.");
        Throws<ArgumentException>(() => HistoryDateRange.FromDates(new(2026, 3, 1), null, zone));
    }
    private static void AmbiguousStart()
    {
        var zone = DaylightZone("stats-ambiguous-midnight", 3, 1, 0, 11, 1, 1);
        Check(zone.IsAmbiguousTime(new(2026, 11, 1)), "The synthetic midnight fixture was not ambiguous.");
        Check(HistoryDateRange.FromDates(new(2026, 11, 1), null, zone).Since == new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero),
            "An ambiguous start dropped its first occurrence.");
    }
    private static void AmbiguousEnd()
    {
        var zone = DaylightZone("stats-ambiguous-day-end", 3, 1, 0, 11, 2, 0);
        var end = new DateTime(2026, 11, 2).AddTicks(-1);
        Check(zone.IsAmbiguousTime(end), "The synthetic final-clock fixture was not ambiguous.");
        Check(HistoryDateRange.FromDates(null, new(2026, 11, 1), zone).Until == new DateTimeOffset(end, TimeSpan.Zero),
            "An ambiguous day end dropped its last occurrence.");
    }

    public static void RunOnDispatcher(MainWindow window)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Stats UI checks require the existing hidden STA.");
        Passive(window);
        var store = (SessionStore)Field(window, "_store")!;
        var source = Await(store.ListDeckVersionsAsync()).First();
        var deck = Await(store.SaveDeckVersionAsync("stats-date-ui", source.Definition, "255790"));
        var firstDay = new DateTime(2026, 2, 24); var lastDay = new DateTime(2026, 3, 5);
        Seed(store, Path.Combine((string)Field(window, "_dataRoot")!, "sessions.db"), deck.Id, firstDay, lastDay);
        var since = Control<StatsDatePicker>(window, "StatsSincePicker"); var until = Control<StatsDatePicker>(window, "StatsUntilPicker");
        var deckFilter = Control<TextBox>(window, "StatsDeckBox"); var buildFilter = Control<TextBox>(window, "StatsBuildBox");
        var opponent = Control<TextBox>(window, "StatsOpponentBox"); var first = Control<ComboBox>(window, "StatsFirstBox");
        var ranked = Control<CheckBox>(window, "RankedOnlyBox"); var grid = Control<DataGrid>(window, "SessionsGrid");
        var groups = Control<DataGrid>(window, "StatsMatchupsGrid"); var next = Control<Button>(window, "SessionNextPageButton");
        var previous = Control<Button>(window, "SessionPrevPageButton"); var error = Control<TextBlock>(window, "StatsDateErrorText");
        var oldSince = since.DateValue; var oldUntil = until.DateValue;
        var oldDeck = deckFilter.Text; var oldBuild = buildFilter.Text; var oldOpponent = opponent.Text;
        var oldFirst = first.SelectedIndex; var oldRanked = ranked.IsChecked;
        var oldClock = Field(window, "_historyNow"); var oldZone = Field(window, "_statsTimeZone"); var oldQuery = Field(window, "_historyQueryOverride");
        var oldContext = Field(window, "_historyContext"); var oldObserved = Field(window, "_historyObservedAt");
        var adviceBox = Control<TextBox>(window, "AdviceBox"); var oldAdvice = adviceBox.Text;
        var fixedNow = new DateTimeOffset(2026, 10, 31, 15, 30, 0, TimeSpan.Zero);
        HistoricalContext? pinned = null; DateTimeOffset? pinnedAt = null;
        try
        {
            Set(window, "_historyNow", (Func<DateTimeOffset>)(() => fixedNow)); Set(window, "_statsTimeZone", Fixed(8));
            buildFilter.Text = "255790"; opponent.Text = ""; first.SelectedIndex = 0; ranked.IsChecked = true;
            deckFilter.Text = deck.Id; since.SetDate(firstDay); until.SetDate(lastDay);
            Click(window, "HistoryRefreshButton"); WaitPage(window, 1, 100);
            Check(Paging(window).Scope?.Since == LocalUtc(firstDay) && Paging(window).Scope?.Until == LocalUtc(lastDay.AddDays(1).AddTicks(-1)) &&
                next.IsEnabled && !previous.IsEnabled && error.Text.Length == 0, "The actual date controls did not produce inclusive local-day UTC bounds.");
            var query = Paging(window).Scope!;
            var actual = Await(store.GetStatsAsync(query)); AssertStats(actual);
            AssertGroups(groups);
            Check(Control<TextBlock>(window, "StatsText").Text.Contains("207", StringComparison.Ordinal) &&
                Control<TextBlock>(window, "StatsText").Text.Contains(actual.WinRate!.Value.ToString("P1", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                "Displayed aggregate counted only the visible page or used the wrong win-rate denominator.");
            var ids = grid.Items.Cast<object>().Select(Session).Select(s => s.Id).ToList();
            Click(window, "SessionNextPageButton"); WaitPage(window, 2, 100);
            ids.AddRange(grid.Items.Cast<object>().Select(Session).Select(s => s.Id));
            Check(Paging(window).Scope == query && groups.Items.Count == 3 && Control<TextBlock>(window, "StatsText").Text.Contains("207", StringComparison.Ordinal),
                "Page two changed the date scope or replaced whole-range statistics with page statistics.");
            Click(window, "SessionNextPageButton"); WaitPage(window, 3, 7);
            ids.AddRange(grid.Items.Cast<object>().Select(Session).Select(s => s.Id));
            Check(ids.Distinct().Count() == 207 && ids.Contains("stats-date-start") && ids.Contains("stats-date-end") &&
                !ids.Contains("stats-date-before") && !ids.Contains("stats-date-next") && previous.IsEnabled && !next.IsEnabled,
                "Date-end last tick/start midnight was lost, the next day leaked, or older matches were unreachable.");
            grid.SelectedIndex = 0;
            WaitUntil(() => Control<ListBox>(window, "DecisionsList").Items.Count > 0, "Date-filtered session did not expose its real saved decisions.");
            Control<ListBox>(window, "DecisionsList").SelectedIndex = 0;
            WaitUntil(() => Field(window, "_historyContext") is HistoricalContext c && c.DeckVersionId == deck.Id && !(bool)Field(window, "_historyLoading")!,
                "Date-filtered saved decision did not publish a fixed historical frame.");
            pinned = (HistoricalContext)Field(window, "_historyContext")!; pinnedAt = (DateTimeOffset?)Field(window, "_historyObservedAt");
            Check(pinnedAt.HasValue, "The selected real decision had no historical knowledge cutoff.");
            adviceBox.Text = "DATE_FILTER_REPLAY_KEPT";
            opponent.Text = "MAGE"; WaitPage(window, 1, 70);
            Check(since.DateValue == firstDay && until.DateValue == lastDay && Paging(window).Scope?.Since == query.Since && Paging(window).Scope?.Until == query.Until &&
                groups.Items.Cast<HistoryMatchupRow>().Single().OpponentClass == "MAGE", "An unrelated filter change reset or recomputed the selected dates.");
            AssertPinned();
            opponent.Text = ""; WaitPage(window, 1, 100);

            foreach (var lateFailure in new[] { false, true })
            {
                var completion = new TaskCompletionSource<HistoryStatsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var started = 0;
                var real = new HistoryStatsQueryResult(Await(store.ListSessionPageAsync(Paging(window).Scope!, 1, 100)), actual);
                Set(window, "_historyQueryOverride", (Func<SessionFilter, int, CancellationToken, Task<HistoryStatsQueryResult>>)((_, _, _) =>
                { Interlocked.Increment(ref started); return completion.Task; }));
                var delayedRead = (Task)typeof(MainWindow).GetMethod("LoadHistoryPageAsync", Private)!.Invoke(window, new object[] { 1, true })!;
                WaitUntil(() => Volatile.Read(ref started) == 1, "Delayed real-result query did not start.");
                if (lateFailure) since.Text = "invalid-direct-date-property"; else TypeDate(since, "definitely-invalid-date");
                WaitUntil(() => since.HasInputError && error.Text.Length > 0 && !Paging(window).IsLoading, "Typed invalid date silently reused SelectedDate.");
                var dateError = error.Text;
                Check(grid.Items.Count == 0 && groups.Items.Count == 0 && !next.IsEnabled && !previous.IsEnabled &&
                    !Control<TextBlock>(window, "StatsText").Text.Contains("207", StringComparison.Ordinal), "Typed invalid input retained old rows/groups/stats or allowed paging.");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(started == 1, "Disabled invalid-date paging still started a query.");
                if (lateFailure) completion.SetException(new IOException("STALE_DATE_FAILURE")); else completion.SetResult(real);
                Await(delayedRead); Pump();
                Check(grid.Items.Count == 0 && groups.Items.Count == 0 && error.Text == dateError && !next.IsEnabled &&
                    !Control<TextBlock>(window, "StatusText").Text.Contains("STALE_DATE_FAILURE", StringComparison.Ordinal), "A stale result/error replaced the invalid-date state.");
                AssertPinned();
                Set(window, "_historyQueryOverride", null); since.SetDate(firstDay); WaitPage(window, 1, 100);
            }
            since.SetDate(lastDay.AddDays(1));
            WaitUntil(() => error.Text.Length > 0 && grid.Items.Count == 0 && groups.Items.Count == 0, "Inverted dates did not clear stale results.");
            since.SetDate(firstDay); WaitPage(window, 1, 100);
            Click(window, "StatsLast7DaysButton"); WaitPage(window, 1, 0);
            Check(since.DateValue == new DateTime(2026, 10, 25) && until.DateValue == new DateTime(2026, 10, 31), "Seven-day preset did not select seven local dates including today.");
            var frozenDates = (since.DateValue, until.DateValue);
            fixedNow = fixedNow.AddDays(10); buildFilter.Text = "255791"; WaitPage(window, 1, 0);
            Click(window, "HistoryRefreshButton"); WaitPage(window, 1, 0);
            Check((since.DateValue, until.DateValue) == frozenDates, "Paging/filter refresh recomputed a previously selected recent-days preset.");
            Click(window, "StatsLast30DaysButton"); WaitPage(window, 1, 0);
            Check(since.DateValue == new DateTime(2026, 10, 12) && until.DateValue == new DateTime(2026, 11, 10), "Thirty-day preset was a rolling duration or did not include its click-time local today.");
            buildFilter.Text = "255790"; Click(window, "StatsAllDatesButton"); WaitPage(window, 1, 100);
            Check(since.DateValue is null && until.DateValue is null && Paging(window).Scope?.Since is null && Paging(window).Scope?.Until is null &&
                error.Text.Length == 0 && Control<TextBlock>(window, "StatsText").Text.Contains("209", StringComparison.Ordinal), "All-dates did not clear both explicit dates and include both outside-range sentinels.");
            AssertPinned();
            since.SetDate(firstDay); until.SetDate(lastDay); WaitPage(window, 1, 100);
            LayoutRemainsReachable(window);
        }
        finally
        {
            Set(window, "_historyQueryOverride", oldQuery); Set(window, "_historyNow", oldClock); Set(window, "_statsTimeZone", oldZone);
            buildFilter.Text = oldBuild; opponent.Text = oldOpponent; first.SelectedIndex = oldFirst; ranked.IsChecked = oldRanked;
            deckFilter.Text = oldDeck; since.SetDate(oldSince); until.SetDate(oldUntil);
            WaitUntil(() => !Paging(window).IsLoading, "Restoring date filters did not settle.");
            Set(window, "_historyContext", oldContext); Set(window, "_historyObservedAt", oldObserved); adviceBox.Text = oldAdvice;
            Invoke(window, "RenderSnapshot");
        }
        ClosingRejectsLateQuery(store, deck.Id, firstDay, lastDay);
        Passive(window);

        void AssertPinned() => Check(ReferenceEquals(pinned, Field(window, "_historyContext")) && Equals(pinnedAt, Field(window, "_historyObservedAt")) &&
            adviceBox.Text == "DATE_FILTER_REPLAY_KEPT", "Changing stats dates/results replaced the fixed review, historical knowledge cutoff or current advice.");
    }

    private static void AssertStats(PersonalStats stats)
    {
        Check(stats is { Games: 207, Wins: 42, Losses: 83, Ties: 41, Unknown: 41 } && Math.Abs(stats.WinRate!.Value - 42d / 125) < 1e-12 &&
            stats.Matchups.Sum(s => s.Games) == 207 && stats.Matchups.Sum(s => s.Wins) == 42 && stats.Matchups.Sum(s => s.Losses) == 83 &&
            stats.Matchups.Sum(s => s.Ties) == 41 && stats.Matchups.Sum(s => s.Unknown) == 41,
            "Statistics ignored the full date range, treated Conceded as unknown or included ties/unknowns in the win-rate denominator.");
    }
    private static void AssertGroups(DataGrid grid)
    {
        var rows = grid.Items.Cast<HistoryMatchupRow>().ToDictionary(r => r.OpponentClass);
        foreach (var (name, games, wins, losses, ties, unknown) in new[] { ("MAGE", 70, 15, 27, 14, 14), ("WARRIOR", 69, 13, 29, 13, 14), ("PRIEST", 68, 14, 27, 14, 13) })
        {
            Check(rows.TryGetValue(name, out var row) && row.Games == games && row.Wins == wins && row.Losses == losses && row.Ties == ties && row.Unknown == unknown &&
                row.EffectiveGames == wins + losses && row.WinRateText == ((double)wins / (wins + losses)).ToString("P1", CultureInfo.CurrentCulture),
                "Displayed class statistics lost raw counts, effective games or its own win-rate denominator: " + name);
        }
    }
    private static void LayoutRemainsReachable(MainWindow window)
    {
        var tabs = Control<TabControl>(window, "MainTabs"); var oldTab = tabs.SelectedIndex;
        var expander = Control<Expander>(window, "StatsMatchupsExpander"); var expanded = expander.IsExpanded;
        var root = window.Content as FrameworkElement ?? throw new Exception("Missing actual window content root.");
        // The existing smoke owns a base Application, so import the actual product's resource
        // definitions locally for visual measurement without constructing another Application.
        var workspace = new DirectoryInfo((string)Field(window, "_dataRoot")!);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var appXaml = XDocument.Load(Path.Combine(workspace?.FullName ?? throw new Exception("Missing layout style source."), "src", "HsHelper.App", "App.xaml"));
        var resources = appXaml.Root!.Elements().Single(e => e.Name.LocalName == "Application.Resources");
        var resourceXaml = new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(e => new XElement(e)));
        var styles = (ResourceDictionary)XamlReader.Parse(resourceXaml.ToString()); root.Resources.MergedDictionaries.Add(styles);
        try
        {
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => Equals(t.Header, "对局记录")); expander.IsExpanded = true;
            var scroll = Control<ScrollViewer>(window, "HistoryScrollViewer");
            var layoutErrors = new List<string>();
            foreach (var size in new[] { new Size(760, 480), new Size(800, 550), new Size(1180, 800) })
            {
                // Measure the actual window child root, so dirty hidden ancestors cannot replace
                // a leaf-only constraint with an infinite measure during the dispatcher pump.
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                scroll.ScrollToTop(); scroll.UpdateLayout(); Pump(); scroll.UpdateLayout(); SaveLayout("top");
                scroll.ScrollToEnd(); scroll.UpdateLayout(); Pump(); scroll.UpdateLayout();
                var metrics = $"stats layout root constraint={size}, root actual={root.ActualWidth}/{root.ActualHeight}: viewport={scroll.ViewportWidth}/{scroll.ViewportHeight}, extent={scroll.ExtentWidth}/{scroll.ExtentHeight}, offset={scroll.VerticalOffset}, scrollable={scroll.ScrollableHeight}, actual={scroll.ActualWidth}/{scroll.ActualHeight}, desired={scroll.DesiredSize}.";
                Console.WriteLine(metrics);
                if (!(scroll.ViewportHeight > 0 && scroll.ViewportHeight <= size.Height && scroll.ActualWidth <= size.Width && scroll.ExtentHeight >= scroll.ViewportHeight &&
                    scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1)) layoutErrors.Add(metrics);
                SaveLayout("bottom");

                void SaveLayout(string position)
                {
                    var directory = Path.GetFullPath(Environment.CurrentDirectory);
                    if (!directory.Contains(Path.DirectorySeparatorChar + ".local" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        directory = (string)Field(window, "_dataRoot")!;
                    Check(Path.GetFullPath(directory).Contains(Path.DirectorySeparatorChar + ".local" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                        "Stats layout artifacts must remain in the isolated .local test directory.");
                    var viewport = new Size(Math.Ceiling(scroll.ActualWidth), Math.Ceiling(scroll.ActualHeight));
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        drawing.DrawRectangle(Brushes.White, null, new Rect(viewport));
                    var bitmap = new RenderTargetBitmap((int)viewport.Width, (int)viewport.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual); bitmap.Render(scroll);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, $"stats-layout-{(int)size.Width}x{(int)size.Height}-{position}.png")); encoder.Save(output);
                }
            }
            Check(layoutErrors.Count == 0, "Date controls or expanded class rows made bottom details unreachable. " + string.Join(" ", layoutErrors));
            Passive(window);
        }
        finally { expander.IsExpanded = expanded; tabs.SelectedIndex = oldTab; root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static void ClosingRejectsLateQuery(SessionStore store, string deck, DateTime firstDay, DateTime lastDay)
    {
        var window = new MainWindow(); var closed = false; window.Closed += (_, _) => closed = true;
        var completion = new TaskCompletionSource<HistoryStatsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionFilter? captured = null; var returned = false;
        try
        {
            Control<TextBox>(window, "StatsDeckBox").Text = deck;
            Control<StatsDatePicker>(window, "StatsSincePicker").SetDate(firstDay); Control<StatsDatePicker>(window, "StatsUntilPicker").SetDate(lastDay);
            Set(window, "_statsTimeZone", Fixed(8)); Set(window, "_store", store);
            Set(window, "_historyQueryOverride", (Func<SessionFilter, int, CancellationToken, Task<HistoryStatsQueryResult>>)(async (filter, _, _) =>
            { captured = filter; try { return await completion.Task; } finally { returned = true; } }));
            var delayedRead = (Task)typeof(MainWindow).GetMethod("LoadHistoryPageAsync", Private)!.Invoke(window, new object[] { 1, true })!;
            WaitUntil(() => captured is not null, "Closing stats fixture never entered its delayed query.");
            window.Close(); WaitUntil(() => closed, "Closing did not cancel the delayed date/stats browser read.");
            var grid = Control<DataGrid>(window, "SessionsGrid"); var groups = Control<DataGrid>(window, "StatsMatchupsGrid");
            var oldRows = grid.ItemsSource; var oldGroups = groups.ItemsSource; var oldText = Control<TextBlock>(window, "StatsText").Text;
            completion.SetResult(new(Await(store.ListSessionPageAsync(captured!, 1, 100)), Await(store.GetStatsAsync(captured!))));
            Await(delayedRead); Check(returned, "Delayed date query did not return after its window closed."); Pump();
            Check(ReferenceEquals(oldRows, grid.ItemsSource) && ReferenceEquals(oldGroups, groups.ItemsSource) && oldText == Control<TextBlock>(window, "StatsText").Text &&
                Field(window, "_historyListCancellation") is null && !Control<Button>(window, "SessionNextPageButton").IsEnabled,
                "A late query updated the closed window or retained an active paging cancellation source.");
            Passive(window);
        }
        finally
        {
            completion.TrySetCanceled();
            if (!closed) { window.Close(); WaitUntil(() => closed, "Closing stats fixture cleanup failed."); }
        }
    }
    private static DateTimeOffset LocalUtc(DateTime date) => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime();
    private static void Seed(SessionStore store, string database, string deck, DateTime firstDay, DateTime lastDay)
    {
        var recorder = new SessionRecorder(store, new(deck, "255790", 1, PlayerClass: "HUNTER"));
        var times = new Dictionary<string, DateTimeOffset>();
        for (var i = 0; i < 205; i++)
        {
            var id = "stats-date-" + i.ToString("D3"); var opponent = new[] { "MAGE", "WARRIOR", "PRIEST" }[i % 3];
            var outcome = new[] { "WON", "LOST", "CONCEDED", "TIED", "PLAYING" }[i % 5];
            recorder.Observe(Frame(id, opponent, outcome), i + 1); times.Add(id, LocalUtc(firstDay.AddHours(12 + i)));
        }
        foreach (var (suffix, at, opponent, outcome) in new[] { ("start", firstDay, "MAGE", "WON"), ("end", lastDay.AddDays(1).AddTicks(-1), "WARRIOR", "CONCEDED"),
            ("before", firstDay.AddTicks(-1), "MAGE", "WON"), ("next", lastDay.AddDays(1), "MAGE", "WON") })
        { var id = "stats-date-" + suffix; recorder.Observe(Frame(id, opponent, outcome), 300 + times.Count); times.Add(id, LocalUtc(at)); }
        Await(recorder.CompleteAsync());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); connection.Open();
        using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE game_sessions SET started_at=$at WHERE id=$id"; var atParameter = command.Parameters.Add("$at", SqliteType.Text); var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var (id, at) in times) { atParameter.Value = at.ToString("O", CultureInfo.InvariantCulture); idParameter.Value = id; Check(command.ExecuteNonQuery() == 1, "A synthetic date fixture was not saved through the real recorder."); }
        transaction.Commit();
    }
    private static GameSnapshot Frame(string id, string opponent, string outcome)
    {
        EntitySnapshot Entity(int entity, string? card, EntityKind kind, params (string Key, string Value)[] tags) => new(entity, card, kind, tags.ToFrozenDictionary(p => p.Key, p => p.Value));
        return new(id, 10, 10, 1, 1, 1, GamePhase.Complete, new[]
        {
            Entity(1, null, EntityKind.Game, ("STATE", "COMPLETE")),
            Entity(2, null, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("FIRST_PLAYER", "1"), ("PLAYSTATE", outcome)),
            Entity(3, null, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "20")),
            Entity(10, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CLASS", "HUNTER")),
            Entity(20, opponent == "MAGE" ? "HERO_08" : opponent == "WARRIOR" ? "HERO_01" : "HERO_09", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CLASS", opponent))
        }.ToFrozenDictionary(e => e.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790", Format = "STANDARD", GameType = "GT_RANKED" };
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null);
    private static HistoryPagingState Paging(MainWindow window) => (HistoryPagingState)Field(window, "_historyPaging")!;
    private static GameSession Session(object item) => (GameSession)item.GetType().GetProperty("Value")!.GetValue(item)!;
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static void Click(MainWindow window, string name)
    { var button = Control<Button>(window, name); Check(button.IsEnabled, "Stats action was disabled: " + name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void TypeDate(StatsDatePicker picker, string text)
    {
        picker.ApplyTemplate();
        var editor = picker.Template.FindName("PART_TextBox", picker) as TextBox ?? throw new Exception("Missing actual date editor.");
        editor.Text = text;
    }
    private static void WaitPage(MainWindow window, int page, int count) => WaitUntil(() => !Paging(window).IsLoading && Paging(window).PageNumber == page &&
        Control<DataGrid>(window, "SessionsGrid").Items.Count == count, $"Stats date page {page}/{count} did not load.");
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Stats fixture operation timed out."); task.GetAwaiter().GetResult(); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Stats regression created or displayed a native window.");
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; var clock = Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (done() || clock.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }

    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
