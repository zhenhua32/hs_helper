using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;
using HsHelper.Storage;

public static class StartingDeckUiChecks
{
    private const string Build = "255790";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("starting sideboard display identifies original deck and initial copies without computing a remaining pool", () =>
        {
            var result = JsonNode.Parse("""
                {"Verification":"Conditional","SideboardEvidence":{"InitialReference":{"IsValid":true,"IsSessionBound":true,
                "DeckVersionId":"original-A","CardBuild":"255790","TotalCards":3,
                "Cards":[{"DbfId":1,"CardId":"A","InitialCopies":2},{"DbfId":2,"CardId":null,"InitialCopies":1}]}}}
                """)!;
            var text = LocalToolResultFormatter.Format("evaluate_choice", result, cardName: id => id == "A" ? "原始野兽" : "OTHER");
            Check(text.Contains("original-A") && text.Contains("255790") && text.Contains("初始参考 2 张：原始野兽") &&
                text.Contains("初始参考 1 张：身份未知（DBF 2）") && text.Contains("完整剩余边栏池未知") && !text.Contains("剩余 1"),
                "Starting references lost provenance, names/counts or their incomplete-pool boundary.");
        });
        yield return ("missing original sideboard binding is explicit instead of borrowing the editor", () =>
        {
            var text = LocalToolResultFormatter.Format("evaluate_choice", JsonNode.Parse("""
                {"Verification":"Unsupported","SideboardEvidence":{"InitialReference":null}}
                """)!);
            Check(text.Contains("原对局起始牌码未确认") && text.Contains("不借用当前套牌或编辑草稿") && !text.Contains("原对局套牌版本："),
                "An absent original deck was rendered as a confirmed empty/selected reference.");
        });
    }

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "starting-deck-ui"); Directory.CreateDirectory(directory);
        var oldData = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR");
        Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", directory);
        var window = new MainWindow();
        try
        {
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
            var workspace = new DirectoryInfo(AppContext.BaseDirectory);
            while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
                Await(catalog.ImportAsync(input, Build, source: "fixture://starting-deck-ui"));
            var definitions = Await(catalog.GetByIdsAsync(["JAIL_831", "EDR_489", "DINO_421", "EDR_227"], Build)).ToDictionary(card => card.Id);
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            DeckDefinition Deck(params (string Id, int Count)[] cards) => new(2, [31], [new(125998, 1)],
                [new(125998, cards.Select(card => new DeckCard(definitions[card.Id].DbfId!.Value, card.Count)).ToArray())]);
            var original = Await(store.SaveDeckVersionAsync("original-A", Deck(("EDR_489", 1), ("DINO_421", 1), ("EDR_227", 1)), Build));
            var selected = Await(store.SaveDeckVersionAsync("selected-B", Deck(("DINO_421", 3)), Build));
            Await((Task)Invoke(window, "LoadDeckLibraryAsync", (object?)null)!);
            var frame = ChoiceFrame(); var other = ChoiceFrame("starting-deck-ui-live-B");
            var recorder = new SessionRecorder(store, new(original.Id, Build, 1, PlayerClass: "HUNTER"));
            recorder.Observe(frame, 1); recorder.Observe(Completed(frame), 2); Await(recorder.CompleteAsync());
            var otherRecorder = new SessionRecorder(store, new(selected.Id, Build, 1, PlayerClass: "HUNTER"));
            otherRecorder.Observe(other, 1); Await(otherRecorder.CompleteAsync());
            var point = Await(store.ListDecisionPointsAsync(frame.MatchId, includeUnstable: true)).First(p => p.StateVersion == frame.StateVersion);
            var pinned = Await(store.GetHistoricalDecisionAsync(point.Id))!.Context;
            Set(window, "_liveFrame", other); Set(window, "_sourceDeckVersion", selected); Set(window, "_sourceBindingEstablished", true);
            Set(window, "_historyContext", pinned); Set(window, "_historyObservedAt", point.RecordedAt);
            Control<TextBox>(window, "SessionBuildBox").Text = Build;
            Invoke(window, "RenderSnapshot"); Await((Task)Invoke(window, "RenderBattleAsync")!);
            var tools = (LocalToolsWindow)Invoke(window, "BindLocalToolsWindow")!; Await(tools.BindingReady);
            Evaluate(tools);
            Check(tools.ResultText.Contains(original.Id) && !tools.ResultText.Contains(selected.Id) &&
                definitions.Values.Where(card => card.Id != "JAIL_831").All(card => tools.ResultText.Contains(card.Name)) &&
                tools.ResultText.Contains("起始牌码参考数量：3") && tools.ResultText.Contains("不代表当前余量"),
                "The real local evaluate-choice route did not show all three original-session references.");
            // Use the actual picker event; the pinned historical source must remain A.
            var picker = Control<ComboBox>(window, "SavedDeckBox");
            picker.SelectedItem = picker.Items.OfType<object>().Single(item => DeckItem(item).Id == selected.Id);
            WaitUntil(() => ((DeckVersion?)Field(window, "_selectedDeckVersion"))?.Id == selected.Id, "The saved-deck picker did not change.");
            var rebound = (LocalToolsWindow)Invoke(window, "BindLocalToolsWindow")!; Await(rebound.BindingReady); Evaluate(rebound);
            Check(rebound.ResultText.Contains(original.Id) && !rebound.ResultText.Contains(selected.Id), "Changing the picker replaced the pinned starting deck.");

            var emptyCleanup = Await((Task<CleanupResult>)Invoke(window, "CleanupHistoryBeforeAsync", DateTimeOffset.UtcNow.AddYears(-1))!);
            Check(emptyCleanup.SessionsDeleted == 0 && ReferenceEquals(pinned, Field(window, "_historyContext")) && rebound.IsBindingValid,
                "A no-op cleanup unnecessarily unpinned or invalidated the original historical frame.");
            var nondeletedCleanup = Await((Task<CleanupResult>)Invoke(window, "CleanupHistoryBeforeAsync", DateTimeOffset.UtcNow.AddMinutes(1))!);
            Check(nondeletedCleanup.SessionsDeleted == 1 && Field(window, "_historyContext") is null &&
                !rebound.IsBindingValid && Field(rebound, "_startingDeck") is null &&
                ((GameSnapshot)Property(window, "DisplaySnapshot")!).MatchId == other.MatchId &&
                !Control<TextBlock>(window, "StateText").Text.StartsWith("历史局面") &&
                Control<DataGrid>(window, "EntityGrid").Items.Count == other.Entities.Count && Field(window, "_historyObservedAt") is null,
                "Deleting the pinned session left its frame or local tool source active.");
            Await((Task)Invoke(window, "RenderBattleAsync")!);
            Check(Control<FrameworkElement>(window, "BattleViewRoot").DataContext is BattleViewState battle && battle.MatchId == other.MatchId,
                "Cleanup did not publish the replacement battle frame.");

            // Rebind to B, then clear its saved source: no previous reference may survive.
            var next = (LocalToolsWindow)Invoke(window, "BindLocalToolsWindow")!; Await(next.BindingReady); Evaluate(next);
            Check(next.ResultText.Contains(selected.Id) && !next.ResultText.Contains(original.Id), "A frame switch retained the previous session reference.");
            Set(window, "_sourceDeckVersion", null); Invoke(window, "BindLocalToolsWindow"); Await(next.BindingReady); Evaluate(next);
            Check(next.ResultText.Contains("原对局起始牌码未确认") && !next.ResultText.Contains(original.Id) && !next.ResultText.Contains(selected.Id),
                "A missing original saved version borrowed an earlier reference.");
            Set(window, "_historyContext", pinned with { Deck = null }); Invoke(window, "BindLocalToolsWindow"); Await(next.BindingReady); Evaluate(next);
            Check(next.ResultText.Contains("原对局起始牌码未确认") && !next.ResultText.Contains(original.Id), "A missing historical deck was filled from the editor/source.");
            Set(window, "_historyContext", pinned with { Snapshot = frame with { StateVersion = frame.StateVersion - 1 } });
            var unrelated = (StartingDeckBinding?)Invoke(window, "StartingDeckForSnapshot", frame, Build);
            Check(unrelated is null, "Historical metadata was used for a different frame.");

            next.UpdateBinding(frame, Build, true, new(frame.MatchId, frame.StateVersion, frame.EventSequence, Build, original.Id, original.Definition));
            var oldNames = next.BindingReady;
            next.UpdateBinding(other, Build); Await(oldNames); Await(next.BindingReady); Evaluate(next);
            Check(Field(next, "_startingDeck") is null && next.ResultText.Contains("原对局起始牌码未确认") && !next.ResultText.Contains(original.Id),
                "A delayed previous-frame name load revived the original reference after an unbound update.");

            var current = Await(store.GetHistoricalContextAtAsync(other.MatchId, other.EventSequence))!;
            Set(window, "_historyContext", current); Set(window, "_historyObservedAt", point.RecordedAt);
            Invoke(window, "BindLocalToolsWindow"); Await(next.BindingReady); Evaluate(next);
            var third = ChoiceFrame("starting-deck-ui-clean-other");
            var thirdRecorder = new SessionRecorder(store, new(selected.Id, Build, 1, PlayerClass: "HUNTER"));
            thirdRecorder.Observe(Completed(third), 1); Await(thirdRecorder.CompleteAsync());
            var otherCleanup = Await((Task<CleanupResult>)Invoke(window, "CleanupHistoryBeforeAsync", DateTimeOffset.UtcNow.AddMinutes(1))!);
            Check(otherCleanup.SessionsDeleted == 1 && ReferenceEquals(current, Field(window, "_historyContext")) && next.IsBindingValid &&
                next.ResultText.Contains(selected.Id), "Deleting another completed session removed the surviving pinned source or its tool reference.");

            // Delay a real missing-session lookup and change the pin while it is awaiting.
            Set(window, "_historyContext", pinned);
            var read = new TaskCompletionSource<GameSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            Func<string, Task<GameSession?>> lookup = id => ++calls == 1 ? read.Task : store.GetSessionAsync(id);
            var transition = (Task)Invoke(window, "ApplyPostCleanupAsync", lookup)!;
            Set(window, "_historyContext", current); Set(window, "_historyObservedAt", point.RecordedAt);
            read.SetResult(null); Await(transition);
            Check(calls == 2 && ReferenceEquals(current, Field(window, "_historyContext")), "A delayed cleanup result removed a newer existing pin.");
            var closeRead = new TaskCompletionSource<GameSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeTransition = (Task)Invoke(window, "ApplyPostCleanupAsync", (Func<string, Task<GameSession?>>)(_ => closeRead.Task))!;
            Set(window, "_closing", true); closeRead.SetResult(null); Await(closeTransition);
            Check(ReferenceEquals(current, Field(window, "_historyContext")), "Cleanup updated a closing window after its awaited lookup.");
            Set(window, "_closing", false);
            LateReadCleanup(window, store, selected, other, directory, fromNote: false);
            LateReadCleanup(window, store, selected, other, directory, fromNote: true);
            Passive(window); Passive(next); next.Close();
            Check(Field(next, "_startingDeck") is null, "Closing the local window retained a starting reference.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            Set(window, "_allowClose", true); window.Close(); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", oldData);
        }
    }

    private static void LateReadCleanup(MainWindow window, SessionStore store, DeckVersion deck, GameSnapshot live, string directory, bool fromNote)
    {
        var frame = ChoiceFrame(fromNote ? "cleanup-pending-note" : "cleanup-pending-decision");
        var recorder = new SessionRecorder(store, new(deck.Id, Build, 1, PlayerClass: "HUNTER"));
        recorder.Observe(frame, 1); recorder.Observe(Completed(frame), 2); Await(recorder.CompleteAsync());
        var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(p => p.StateVersion == frame.StateVersion);
        var survivor = Await(store.GetHistoricalContextAtAsync(live.MatchId, live.EventSequence))!;
        Set(window, "_historyContext", survivor); Set(window, "_historyObservedAt", point.RecordedAt);
        Invoke(window, "RenderSnapshot");
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cleanup = null;
        Set(window, "_historicalReadCompleted", (Func<DecisionPoint, CancellationToken, Task>)((_, _) => { readEntered.SetResult(); return releaseRead.Task; }));
        Set(window, "_historyQueryOverride", (Func<SessionFilter, int, CancellationToken, Task<HistoryStatsQueryResult>>)(async (filter, page, token) =>
        {
            var actual = new HistoryStatsQueryResult(await store.ListSessionPageAsync(filter, page, 100, token), await store.GetStatsAsync(filter, token));
            pageEntered.SetResult(); await releasePage.Task; return actual;
        }));
        try
        {
            if (fromNote)
            {
                var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "pending-read-notes.db")); Await(knowledge.InitializeAsync());
                var note = Await(knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "cleanup-note", "actual saved note", MatchId: frame.MatchId)
                { EventSequence = point.EventSequence }));
                Set(window, "_knowledge", knowledge); Invoke(window, "DisplayKnowledgeEntry", note);
                var button = Control<Button>(window, "OpenKnowledgeSnapshotButton"); Check(button.IsEnabled, "The saved anchored note was not navigable.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
            else
            {
                var itemType = typeof(MainWindow).GetNestedType("DecisionItem", BindingFlags.NonPublic)!;
                var item = Activator.CreateInstance(itemType, point)!;
                var decisions = Control<ListBox>(window, "DecisionsList"); decisions.ItemsSource = new[] { item }; decisions.SelectedIndex = 0;
            }
            WaitUntil(() => readEntered.Task.IsCompleted, "The real historical reader did not reach its post-catalog barrier.");
            Check((bool)Field(window, "_historyLoading")!, "The historical read was not pending before cleanup.");
            cleanup = (Task<CleanupResult>)Invoke(window, "CleanupHistoryBeforeAsync", DateTimeOffset.UtcNow.AddMinutes(1))!;
            WaitUntil(() => pageEntered.Task.IsCompleted, "Cleanup did not reach the delayed real history page read.");
            Check(Await(store.GetSessionAsync(frame.MatchId)) is null, "The pending read's original session was not actually deleted.");
            releaseRead.SetResult(); Pump(); Pump();
            Check(Field(window, "_historyContext") is not HistoricalContext late || late.SessionId != frame.MatchId,
                (fromNote ? "NOTE" : "DECISION") + ": a completed old historical read republished its deleted session before page refresh completed.");
            releasePage.SetResult(); Await(cleanup);
            Check(Field(window, "_historyContext") is not HistoricalContext stale || stale.SessionId != frame.MatchId,
                "Refreshing the history list retained a deleted session published by an old read.");
            Check(!(bool)Field(window, "_historyLoading")! && !(bool)Field(window, "_knowledgeNavigationLoading")! &&
                Field(window, "_analysis") is not CancellationTokenSource { IsCancellationRequested: false } &&
                Field(window, "_localTools") is LocalToolsWindow { IsBindingValid: false },
                "Cleanup left the old read, calculation or analysis actionable.");
            if (fromNote) Check(ReferenceEquals(survivor, Field(window, "_historyContext")), "Cancelling an old note read removed the surviving completed pin.");
            Await((Task)Invoke(window, "RenderBattleAsync")!);
            Check(Control<FrameworkElement>(window, "BattleViewRoot").DataContext is BattleViewState view && view.MatchId == live.MatchId,
                "Late-read cleanup left the battle view bound to its deleted source.");
        }
        finally
        {
            releaseRead.TrySetResult(); releasePage.TrySetResult(); if (cleanup is not null) Await(cleanup);
            Set(window, "_historicalReadCompleted", null); Set(window, "_historyQueryOverride", null);
            Invoke(window, "CancelKnowledgeNavigation", (object?)null);
        }
    }

    private static void Evaluate(LocalToolsWindow tools)
    {
        var choices = Control<ListBox>(tools, "LocalChoicesList"); choices.SelectedIndex = 0;
        var button = Control<Button>(tools, "LocalEvaluateChoiceButton"); Check(button.IsEnabled, "Choice effects button was disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        WaitUntil(() => !tools.HasPendingCalculation && tools.ResultText.Contains("下水道之王边栏选择证据"), "Local choice evaluation did not finish.");
    }
    private static GameSnapshot ChoiceFrame(string identity = "starting-deck-ui-original")
    {
        var engine = new PowerLogStateEngine(1, identity);
        foreach (var line in new[] { $"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=8", "tag=STEP value=MAIN_ACTION", "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1",
            "tag=NUM_CARDS_IN_HAND value=0", "tag=MAXHANDSIZE value=10", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0" }) engine.ProcessLine(line);
        foreach (var (id, card, zone) in new[] { (30, "JAIL_831", "PLAY"), (80, "DINO_421", "SETASIDE"), (81, "EDR_227", "SETASIDE") })
            foreach (var line in new[] { $"FULL_ENTITY - Creating ID={id} CardID={card}", "tag=CONTROLLER value=1", $"tag=ZONE value={zone}", "tag=CARDTYPE value=MINION" }) engine.ProcessLine(line);
        foreach (var line in new[] { "PowerProcessor.EndCurrentTaskList() - complete",
            "GameState.DebugPrintEntityChoices() - id=9 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1",
            "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 cardId=JAIL_831 player=1]",
            "GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=80 cardId=DINO_421 player=1]",
            "GameState.DebugPrintEntityChoices() - Entities[1]=[entityName=Anonymous id=81 cardId=EDR_227 player=1]", "ChoiceCardMgr.OnChoicesShown() - shown" }) engine.ProcessLine(line);
        return engine.Snapshot();
    }
    private static GameSnapshot Completed(GameSnapshot frame)
    {
        var entities = frame.Entities.ToDictionary();
        var gameTags = entities[1].Tags.ToDictionary(); gameTags["STATE"] = "COMPLETE";
        var playerTags = entities[2].Tags.ToDictionary(); playerTags["PLAYSTATE"] = "WON";
        entities[1] = entities[1] with { Tags = gameTags.ToFrozenDictionary() };
        entities[2] = entities[2] with { Tags = playerTags.ToFrozenDictionary() };
        return frame with { StateVersion = frame.StateVersion + 1, EventSequence = frame.EventSequence + 1,
            Phase = GamePhase.Complete, Entities = entities.ToFrozenDictionary(), Choice = null, ChoiceHistory = null,
            TurnHistory = null, PublicPlays = null };
    }
    private static DeckVersion DeckItem(object item) => (DeckVersion)item.GetType().GetProperty("Value")!.GetValue(item)!;
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? Property(object instance, string name) => instance.GetType().GetProperty(name, Private)!.GetValue(instance);
    private static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A starting-deck check created a window handle.");
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Starting deck operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return;
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        var elapsed = Stopwatch.StartNew(); timer.Tick += (_, _) => { if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
