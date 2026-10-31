using System.Collections.Frozen;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Core;
using HsHelper.Storage;

public static class HistoryPagingChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("history paging rejects delayed older-page results", OutOfOrder);
        yield return ("history filter changes reset the first page and reject stale scope", FilterReset);
        yield return ("history browsing freezes its first cutoff and refresh renews it", FrozenCutoff);
    }
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static SessionPage Page(int number, bool more = false) => new(Array.AsReadOnly(Array.Empty<GameSession>()), number, 100, more);

    private static void OutOfOrder()
    {
        var state = new HistoryPagingState(); var scope = new SessionFilter(CardBuild: "255790");
        var first = state.Begin(scope, 1, Now); Check(state.TryApply(first, Page(1, true)), "First page failed to publish.");
        var slow = state.Begin(scope, 2, Now.AddSeconds(1)); var latest = state.Begin(scope, 1, Now.AddSeconds(2));
        Check(!state.TryApply(slow, Page(2)) && !state.TryFail(slow) && state.IsLoading && state.TryApply(latest, Page(1, true)) &&
            state.PageNumber == 1 && state.HasMore && !state.IsLoading, "A stale page completion or failure overwrote the latest page state.");
    }
    private static void FilterReset()
    {
        var state = new HistoryPagingState(); var original = new SessionFilter(CardBuild: "255790");
        var first = state.Begin(original, 1, Now); state.TryApply(first, Page(1, true));
        var next = state.Begin(original, 2, Now.AddSeconds(1)); state.TryApply(next, Page(2, true));
        var delayed = state.Begin(original, 3, Now.AddSeconds(2));
        var changed = state.Begin(original with { OpponentClass = "MAGE" }, 3, Now.AddSeconds(3));
        Check(changed.PageNumber == 1 && !state.TryApply(delayed, Page(3)) && state.TryApply(changed, Page(1)) && state.PageNumber == 1 && !state.HasMore,
            "A changed filter began on a later page or accepted a response from its old scope.");
    }
    private static void FrozenCutoff()
    {
        var state = new HistoryPagingState(); var filter = new SessionFilter();
        var first = state.Begin(filter, 1, Now); state.TryApply(first, Page(1, true));
        var second = state.Begin(filter, 2, Now.AddMinutes(1));
        Check(second.Query.Until == Now, "A page transition moved the browsing cutoff and allowed insertions to shift its offsets.");
        state.TryApply(second, Page(2));
        var refresh = state.Begin(filter, 2, Now.AddMinutes(2), refresh: true);
        Check(refresh.PageNumber == 1 && refresh.Query.Until == Now.AddMinutes(2) && !state.TryApply(second, Page(2)),
            "Refresh retained an old cutoff/page or accepted the prior in-flight request.");
        var earlier = state.Begin(filter with { Until = Now.AddDays(-1) }, 1, Now.AddMinutes(3));
        Check(earlier.Query.Until == Now.AddDays(-1), "Browsing widened an explicitly earlier caller cutoff.");
    }

    /// <summary>Invoke inside the existing hidden WPF smoke; no new Application or visible window.</summary>
    public static void RunOnDispatcher(MainWindow window)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "History paging smoke requires the existing STA dispatcher.");
        var store = (SessionStore)Field(window, "_store")!;
        var sourceDeck = Await(store.ListDeckVersionsAsync()).First();
        var deck = Await(store.SaveDeckVersionAsync("history-paging-ui", sourceDeck.Definition, "255790"));
        var recorder = new SessionRecorder(store, new(deck.Id, "255790", 1, PlayerClass: "HUNTER"));
        for (var index = 0; index < 105; index++) recorder.Observe(Frame(index), index + 1);
        Await(recorder.CompleteAsync());
        var deckFilter = Control<TextBox>(window, "StatsDeckBox"); var buildFilter = Control<TextBox>(window, "StatsBuildBox");
        var opponentFilter = Control<TextBox>(window, "StatsOpponentBox"); var firstFilter = Control<ComboBox>(window, "StatsFirstBox");
        var ranked = Control<CheckBox>(window, "RankedOnlyBox");
        var oldDeck = deckFilter.Text; var oldBuild = buildFilter.Text; var oldOpponent = opponentFilter.Text;
        var oldFirst = firstFilter.SelectedIndex; var oldRanked = ranked.IsChecked;
        var grid = Control<DataGrid>(window, "SessionsGrid");
        var previous = Control<Button>(window, "SessionPrevPageButton"); var next = Control<Button>(window, "SessionNextPageButton");
        var label = Control<TextBlock>(window, "HistoryPageText");
        try
        {
            buildFilter.Text = ""; opponentFilter.Text = ""; firstFilter.SelectedIndex = 0; ranked.IsChecked = true;
            deckFilter.Text = deck.Id;
            WaitUntil(() => !Paging(window).IsLoading && grid.Items.Count == 100 && Paging(window).Scope?.DeckVersionId == deck.Id,
                "The real filtered history UI did not show the first hundred sessions.");
            Check(!previous.IsEnabled && next.IsEnabled && label.Text.Contains("第 1 页", StringComparison.Ordinal) &&
                label.Text.Contains("本页 100 条", StringComparison.Ordinal), "Page-one controls/counts were not bound.");
            Click(next);
            WaitUntil(() => !Paging(window).IsLoading && Paging(window).PageNumber == 2 && grid.Items.Count == 5,
                "The actual next-page button did not make all 105 saved sessions reachable.");
            Check(previous.IsEnabled && !next.IsEnabled && label.Text.Contains("已到末页", StringComparison.Ordinal), "The last-page controls were wrong.");
            grid.SelectedIndex = 0;
            var decisions = Control<ListBox>(window, "DecisionsList");
            WaitUntil(() => decisions.Items.Count > 0, "An older-page session did not load its own saved decisions.");
            decisions.SelectedIndex = 0;
            WaitUntil(() => Field(window, "_historyContext") is HistoricalContext context && context.DeckVersionId == deck.Id,
                "Selecting an older-page decision did not bind its historical frame.");
            var pinned = (HistoricalContext)Field(window, "_historyContext")!;
            Click(previous);
            WaitUntil(() => !Paging(window).IsLoading && Paging(window).PageNumber == 1 && grid.Items.Count == 100,
                "The actual previous-page button failed.");
            Check(ReferenceEquals(pinned, Field(window, "_historyContext")) && grid.SelectedItem is null && decisions.Items.Count == 0 &&
                Control<TextBlock>(window, "HistoryStatusText").Text.Contains("仍固定复盘", StringComparison.Ordinal),
                "Changing the list mixed a new selection with old decisions or unexpectedly removed the fixed historical frame.");
            opponentFilter.Text = "WARRIOR"; opponentFilter.Text = "MAGE";
            WaitUntil(() => !Paging(window).IsLoading && Paging(window).PageNumber == 1 && grid.Items.Count == 60,
                "Changing filters did not automatically return to the first result page.");
            Check(!previous.IsEnabled && !next.IsEnabled && grid.Items.OfType<object>().All(item => Session(item).OpponentClass == "MAGE") &&
                ReferenceEquals(pinned, Field(window, "_historyContext")), "A stale filter query overwrote the latest results or unpinned the fixed review.");
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "History paging created a visible test window.");
        }
        finally
        {
            buildFilter.Text = oldBuild; deckFilter.Text = oldDeck; opponentFilter.Text = oldOpponent;
            firstFilter.SelectedIndex = oldFirst; ranked.IsChecked = oldRanked;
            WaitUntil(() => !Paging(window).IsLoading, "Restoring the isolated history filters did not complete.");
            typeof(MainWindow).GetMethod("ReturnLiveClicked", Private)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
        }
    }

    private static GameSnapshot Frame(int index)
    {
        EntitySnapshot Entity(int id, string? card, EntityKind kind, params (string Key, string Value)[] tags) =>
            new(id, card, kind, tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value));
        var opponent = index < 60 ? "MAGE" : "WARRIOR";
        return new("history-paging-ui-" + index.ToString("D3"), 10, 10, 1, 1, 1, GamePhase.Complete,
            new[] { Entity(1, null, EntityKind.Game, ("STATE", "COMPLETE")),
                Entity(2, null, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("FIRST_PLAYER", "1"), ("PLAYSTATE", "WON")),
                Entity(3, null, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "20"), ("PLAYSTATE", "LOST")),
                Entity(10, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CLASS", "HUNTER")),
                Entity(20, opponent == "MAGE" ? "HERO_08" : "HERO_01", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CLASS", opponent)) }
                .ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
            { ClientBuild = "255790", Format = "STANDARD", GameType = "GT_RANKED" };
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static HistoryPagingState Paging(MainWindow window) => (HistoryPagingState)Field(window, "_historyPaging")!;
    private static GameSession Session(object item) => (GameSession)item.GetType().GetProperty("Value")!.GetValue(item)!;
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static void Click(Button button) { Check(button.IsEnabled, "History page button was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "History fixture setup timed out."); task.GetAwaiter().GetResult(); }
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
