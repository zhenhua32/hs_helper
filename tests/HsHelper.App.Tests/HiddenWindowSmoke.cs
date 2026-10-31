using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;

/// <summary>Real WPF routed events on an isolated STA dispatcher, without showing or creating a window handle.</summary>
internal static class HiddenWindowSmoke
{
    private const string FakeKey = "ui-smoke-fixture-key";
    private const string ProposedSummary = "UI_SMOKE_DECK_PLAN";
    private static readonly BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var workspace = FindWorkspace();
        var testRoot = Path.Combine(workspace, ".local", "ui-tests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(testRoot, "data");
        var samples = Path.Combine(testRoot, "samples", "decks");
        Directory.CreateDirectory(samples); Directory.CreateDirectory(data);
        File.Copy(Path.Combine(workspace, "samples", "decks", "beast-hunter.txt"), Path.Combine(samples, "beast-hunter.txt"));
        File.WriteAllText(Path.Combine(testRoot, ".env"), "api_url=https://smoke.invalid/v1\nmodel_name=ui-fixture\napi_key=" + FakeKey + "\nMODEL_MAX_TOOL_ROUNDS=0\n", new UTF8Encoding(false));
        var oldDirectory = Environment.CurrentDirectory;
        var oldDataRoot = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR");
        Exception? failure = null;
        try
        {
            Environment.CurrentDirectory = testRoot;
            Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", data);
            var thread = new Thread(() =>
            {
                try { RunOnDispatcher(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "HsHelper hidden WPF regression" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(45)), "Hidden WPF smoke did not finish within its bounded timeout.");
            if (failure is not null) throw new Exception("Isolated hidden UI check failed: " + failure.Message, failure);
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", oldDataRoot);
        }
    }

    private static void RunOnDispatcher()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Exception? unhandled = null;
        application.DispatcherUnhandledException += (_, e) => { unhandled = e.Exception; e.Handled = true; };
        MainWindow? window = null;
        var windowClosed = false;
        try
        {
            window = new MainWindow();
            window.Closed += (_, _) => windowClosed = true;
            using var fakeModel = new PlanningHandler();
            using var rejectCards = new RejectHandler();
            ReplaceClient(window, "_http", new HttpClient(fakeModel));
            ReplaceClient(window, "_cardHttp", new HttpClient(rejectCards));
            AssertPassive(window);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
            WaitUntil(() => Field(window, "_loadedModelSettings") is not null && Field(window, "_knowledge") is not null,
                "Loaded did not finish local configuration and knowledge initialization.", () => unhandled);
            Check(Control<PasswordBox>(window, "KeyBox").Password == FakeKey, "Initialization did not load the isolated fixture configuration.");
            Check(Control<FrameworkElement>(window, "BattleViewRoot").DataContext is BattleViewState { HasLocalPerspective: false, HasDeckTracking: false } &&
                Control<TextBlock>(window, "DeckCompositionStatusText").Text.Contains("尚无匹配", StringComparison.Ordinal) &&
                Control<TextBlock>(window, "BattleQualityText").Text.Contains("尚未确认", StringComparison.Ordinal),
                "A fresh startup left the battle panel blank instead of showing explicit unknown state.");
            var planDeck = Control<ComboBox>(window, "PlanDeckBox");
            var deck = planDeck.SelectedItem?.GetType().GetProperty("Value")?.GetValue(planDeck.SelectedItem) as DeckVersion;
            Check(deck?.Name == "野兽快" && Control<ComboBox>(window, "SavedDeckBox").SelectedItem is not null,
                "Loaded did not select the seeded deck in both deck selectors.");
            AssertPassive(window);
            CheckConfigurationIo(window, () => unhandled);
            AssertPassive(window);

            var title = Control<TextBox>(window, "KnowledgeTitleBox");
            var content = Control<TextBox>(window, "KnowledgeContentBox");
            var save = Control<Button>(window, "SaveKnowledgeButton");
            title.Text = "hidden fixture plan"; content.Text = "UI_PLAN_VERSION_ONE";
            Click(save);
            WaitUntil(() => !Saving(window) && Editing(window) is { Version: 1 }, "Save button did not create and select plan v1.", () => unhandled);
            var first = Editing(window)!;
            Check(first.Content == "UI_PLAN_VERSION_ONE" && first.DeckVersionId == deck!.Id && Control<ListBox>(window, "KnowledgeList").Items.Count == 1,
                "First routed save lost editor values or deck binding.");
            content.Text = "UI_PLAN_VERSION_TWO";
            Click(save);
            WaitUntil(() => !Saving(window) && Editing(window) is { Version: 2 }, "Save button did not revise the selected plan to v2.", () => unhandled);
            var second = Editing(window)!;
            Check(second.EntryId == first.EntryId && second.Content == "UI_PLAN_VERSION_TWO" &&
                Control<TextBlock>(window, "KnowledgeVersionText").Text.Contains("v2", StringComparison.Ordinal), "Editor did not display the actual saved second version.");
            AssertPassive(window);

            Click(FindButton(window, "删除当前条目"));
            WaitUntil(() => Editing(window) is null && Control<ListBox>(window, "KnowledgeList").Items.Count == 0,
                "Delete button did not clear the editor and current list.", () => unhandled);
            var knowledge = (CoachKnowledgeStore)Field(window, "_knowledge")!;
            var tombstone = Await(knowledge.GetAsOfAsync(first.EntryId, DateTimeOffset.MaxValue, includeDeleted: true), () => unhandled);
            var oldFirst = Await(knowledge.GetAsOfAsync(first.EntryId, first.RecordedAt), () => unhandled);
            var oldSecond = Await(knowledge.GetAsOfAsync(first.EntryId, second.RecordedAt), () => unhandled);
            Check(tombstone is { IsDeleted: true, Version: 3 } && oldFirst?.Content == "UI_PLAN_VERSION_ONE" && oldSecond?.Content == "UI_PLAN_VERSION_TWO",
                "UI delete removed earlier versions instead of writing a tombstone.");
            AssertPassive(window);

            Click(FindButton(window, "AI 分析套牌计划"));
            WaitUntil(() => fakeModel.Calls == 1 && Field(window, "_analysis") is null &&
                Control<TextBox>(window, "AdviceBox").Text.Contains(ProposedSummary, StringComparison.Ordinal),
                "Planning button did not complete, archive and display the fake model advice.", () => unhandled);
            var archive = (AdviceArchiveStore)Field(window, "_adviceArchive")!;
            var records = Await(archive.ListAsync(new AdviceArchiveFilter(MatchId: "deck-" + deck!.Id)), () => unhandled);
            Check(records.Count == 1 && records[0].Advice is { StateVersion: 0, EventSequence: 0, Verification: "strategy" } &&
                records[0].Advice.DeckVersionId == deck.Id && records[0].Advice.PromptTemplateVersion == "hunter-v5/deckplan" &&
                records[0].Advice.Summary.Contains(ProposedSummary, StringComparison.Ordinal), "Planning UI archived the wrong state, deck or template.");
            var adviceText = Control<TextBox>(window, "AdviceBox").Text;
            Check(adviceText.Contains("已保存建议记录", StringComparison.Ordinal) && !adviceText.Contains(FakeKey, StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(records).Contains(FakeKey, StringComparison.Ordinal), "Displayed/archive advice did not use the sanitized saved record.");
            Check(fakeModel.Calls == 1 && !fakeModel.UnexpectedRequest && rejectCards.Calls == 0, "Hidden UI initialization made an unexpected request.");
            Check(!fakeModel.RequestBody.Contains(FakeKey, StringComparison.Ordinal), "Fixture credential entered the model payload.");
            AssertPassive(window);
            CheckCurrentBuildSync(window, () => unhandled);
            HistoryPagingChecks.RunOnDispatcher(window);
            KnowledgePagingChecks.RunOnDispatcher(window);
            AdviceArchiveBrowserChecks.RunOnDispatcher(window);
            DeckExportChecks.RunOnDispatcher(window);
            KnowledgeNavigationChecks.RunOnDispatcher(window);
            StatsDateChecks.RunOnDispatcher(window);
            AdviceLimitationsChecks.RunOnDispatcher(window);
            StartingDeckUiChecks.RunOnDispatcher(window);
            LocalToolsHiddenSmoke.RunOnDispatcher(Path.Combine(Environment.CurrentDirectory, "local-tools"));
            WindowClosingRegression.RunOnDispatcher(Path.Combine(Environment.CurrentDirectory, "closing"));
            CardBrowsingChecks.RunOnDispatcher(window);
            ModelCapabilitiesUiChecks.RunOnDispatcher(window);
            LastObservedReviewChecks.RunOnDispatcher(window);
            LargeCardUiChecks.RunOnDispatcher(window);
            DarkGiftStaticUiChecks.RunOnDispatcher(window);
            FindLethalCandidateUiChecks.RunOnDispatcher(window);
            AssertPassive(window);
            Check(unhandled is null, "Dispatcher initialization or a routed action raised an unhandled exception.");
        }
        finally
        {
            if (window is not null)
            {
                window.Close();
                WaitUntil(() => windowClosed, "Initialized main window did not close through its persistence workflow.", () => unhandled);
            }
            application.Shutdown();
        }
    }

    private static void AssertPassive(MainWindow window)
    {
        Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A hidden regression created or displayed the main window handle.");
        Check(Field(window, "_hotkeys") is GlobalHotkeys hotkeys && hotkeys.Bindings.Count == 0, "Hidden regression registered a hotkey.");
        Check(Field(window, "_overlay") is null && Field(window, "_timer") is DispatcherTimer live && !live.IsEnabled &&
            Field(window, "_decisionTimer") is DispatcherTimer automatic && !automatic.IsEnabled, "Hidden regression started an overlay, live watcher or automatic suggestion timer.");
    }
    private static void CheckCurrentBuildSync(MainWindow window, Func<Exception?> unhandled)
    {
        using var cards = new PinnedCardsHandler();
        ReplaceClient(window, "_cardHttp", new HttpClient(cards));
        var frame = BattleViewRegression.Frame();
        var historical = new HistoricalContext("old-ui-match", "111111", null, null,
            frame with { MatchId = "old-ui-match", ClientBuild = "111111" }, []);
        typeof(MainWindow).GetField("_liveFrame", PrivateFields)!.SetValue(window, frame);
        typeof(MainWindow).GetField("_historyContext", PrivateFields)!.SetValue(window, historical);
        var selectedBuild = Control<TextBox>(window, "SessionBuildBox");
        selectedBuild.Text = historical.CardBuild;
        Click(FindButton(window, "同步客户端版本卡库"));
        WaitUntil(() => !(bool)Field(window, "_catalogBusy")!, "Client-build sync did not finish.", unhandled);
        var catalog = (CardCatalog)Field(window, "_catalog")!;
        var active = Await(catalog.GetActiveManifestAsync(), unhandled);
        Check(cards.Calls == 1 && active?.Build == BattleViewRegression.Build && active.Locale == "zhCN",
            "Client-build button did not fetch and activate the observed fixed Chinese build.");
        Check(ReferenceEquals(Field(window, "_historyContext"), historical) && selectedBuild.Text == historical.CardBuild,
            "Syncing the live client build rebound the selected historical analysis.");
        Check(Control<TextBlock>(window, "StatusText").Text.Contains("已缓存客户端版本 " + BattleViewRegression.Build, StringComparison.Ordinal),
            "Client-build sync did not report the actual cached version.");

        typeof(MainWindow).GetField("_liveFrame", PrivateFields)!.SetValue(window, frame with { ClientBuild = "unknown" });
        Click(FindButton(window, "同步客户端版本卡库"));
        WaitUntil(() => !(bool)Field(window, "_catalogBusy")!, "Unknown build rejection did not finish.", unhandled);
        Check(cards.Calls == 1 && Control<TextBlock>(window, "StatusText").Text.Contains("尚未确认客户端 build", StringComparison.Ordinal),
            "An unconfirmed client build sent a catalog request or silently used latest.");
    }
    private static void CheckConfigurationIo(MainWindow window, Func<Exception?> unhandled)
    {
        var gate = (SemaphoreSlim)Field(window, "_configurationIoGate")!;
        var model = Control<TextBox>(window, "ModelBox");
        var configStatus = Control<TextBlock>(window, "ModelConfigText");
        var originalModel = model.Text;
        Check(gate.Wait(0), "Configuration gate was unexpectedly busy before reload race.");
        try
        {
            var request = (long)Field(window, "_configurationReadRequest")!;
            Click(FindButton(window, "从 .env 重新加载"));
            Check((long)Field(window, "_configurationReadRequest")! > request, "Explicit reload did not reach the held I/O gate.");
            model.Text = "typed-while-reload-waits";
        }
        finally { gate.Release(); }
        WaitUntil(() => gate.CurrentCount == 1, "Queued stale reload did not finish.", unhandled);
        DrainDispatcher(unhandled);
        Check(model.Text == "typed-while-reload-waits" && configStatus.Text.Contains("已修改", StringComparison.Ordinal),
            "A delayed environment reload overwrote newer input or claimed it had loaded.");
        Check(Control<PasswordBox>(window, "KeyBox").Password == FakeKey, "Reload race changed the isolated fake credential.");

        Check(gate.Wait(0), "Configuration gate was unexpectedly busy before queued save race.");
        try
        {
            model.Text = "config-save-A";
            var staleRevision = (long)Field(window, "_configurationRevision")!;
            var staleConfiguration = (ModelConfiguration)typeof(MainWindow).GetMethod("ConfigurationFromFields", PrivateFields)!.Invoke(window, null)!;
            Click(FindButton(window, "加密保存配置"));
            Check(Field(window, "_configurationWrite") is CancellationTokenSource, "Save A did not wait at the I/O gate.");
            model.Text = "config-save-B";
            Click(FindButton(window, "加密保存配置"));
            var owner = (CancellationTokenSource)Field(window, "_configurationWrite")!;
            var readRequest = (long)Field(window, "_configurationReadRequest")!;
            var staleSave = (Task<bool>)typeof(MainWindow).GetMethod("SaveModelConfigurationAsync", PrivateFields)!
                .Invoke(window, new object?[] { staleConfiguration, staleRevision })!;
            var staleLoad = (Task<bool>)typeof(MainWindow).GetMethod("LoadModelConfigurationAsync", PrivateFields)!
                .Invoke(window, new object?[] { true, staleRevision })!;
            Check(staleSave.IsCompletedSuccessfully && !staleSave.Result && staleLoad.IsCompletedSuccessfully && !staleLoad.Result,
                "A stale configuration request waited for the I/O gate instead of rejecting immediately.");
            Check(ReferenceEquals(owner, Field(window, "_configurationWrite")) && !owner.IsCancellationRequested &&
                (long)Field(window, "_configurationReadRequest")! == readRequest,
                "A stale load/save cancelled or superseded the newer queued Save B.");
            DrainDispatcher(unhandled); // lets cancelled Save A unwind while Save B still cannot write
            Check(model.Text == "config-save-B" && configStatus.Text.Contains("已修改", StringComparison.Ordinal),
                "A cancelled older save reported success for newer unsaved input.");
            Check(Field(window, "_configurationWrite") is CancellationTokenSource, "Cancelling Save A also removed pending Save B ownership.");
        }
        finally { gate.Release(); }
        WaitUntil(() => gate.CurrentCount == 1 && Field(window, "_configurationWrite") is null &&
            configStatus.Text.Contains("已使用 Windows", StringComparison.Ordinal), "Newest queued save did not finish.", unhandled);
        var protectedStore = (ProtectedModelConfigurationStore)Field(window, "_modelStore")!;
        var saved = Await(protectedStore.LoadAsync(), unhandled);
        Check(saved is not null && saved.Settings.Model == "config-save-B" && saved.ApiKey == FakeKey && model.Text == "config-save-B",
            "DPAPI configuration retained the older queued snapshot instead of Save B.");
        model.Text = originalModel;
    }
    private static void DrainDispatcher(Func<Exception?> unhandled)
    {
        var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        WaitUntil(() => operation.Task.IsCompleted, "Dispatcher did not drain queued UI completions.", unhandled);
        operation.Task.GetAwaiter().GetResult();
    }
    private static void ReplaceClient(MainWindow window, string name, HttpClient replacement)
    {
        var field = typeof(MainWindow).GetField(name, PrivateFields)!;
        var original = (HttpClient)field.GetValue(window)!;
        field.SetValue(window, replacement); original.Dispose();
    }
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, PrivateFields)!.GetValue(window);
    private static CoachKnowledgeVersion? Editing(MainWindow window) => Field(window, "_editingKnowledge") as CoachKnowledgeVersion;
    private static bool Saving(MainWindow window) => (bool)Field(window, "_knowledgeSaving")!;
    private static T Control<T>(MainWindow window, string name) where T : class
        => window.FindName(name) as T ?? throw new Exception("Missing named UI control: " + name);
    private static Button FindButton(DependencyObject root, string label) => Walk(root).OfType<Button>().Single(button => Equals(button.Content, label));
    private static void Click(Button button) { Check(button.IsEnabled, "Routed UI action was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
    private static void WaitUntil(Func<bool> complete, string message, Func<Exception?> unhandled)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { if (complete() || unhandled() is not null || DateTime.UtcNow - start > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Check(unhandled() is null, "Hidden dispatcher action failed (" + unhandled()?.GetType().Name + ").");
        Check(complete(), message);
    }
    private static T Await<T>(Task<T> operation, Func<Exception?> unhandled)
    { WaitUntil(() => operation.IsCompleted, "Local UI storage operation did not complete.", unhandled); return operation.GetAwaiter().GetResult(); }
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("App tests must run inside the hs_helper workspace.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class RejectHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Hidden smoke rejected an unexpected request.")); }
    }
    private sealed class PinnedCardsHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(Calls == 1 && request.RequestUri?.AbsoluteUri ==
                "https://api.hearthstonejson.com/v1/" + BattleViewRegression.Build + "/zhCN/cards.json",
                "Client-build sync requested latest, another build or another locale.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(BattleViewRegression.CardsJson(), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class PlanningHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool UnexpectedRequest { get; private set; }
        public string RequestBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; RequestBody = await request.Content!.ReadAsStringAsync(token);
            UnexpectedRequest = Calls != 1 || request.RequestUri?.Host != "smoke.invalid" || request.Headers.Authorization?.ToString() != "Bearer " + FakeKey;
            if (UnexpectedRequest) throw new InvalidOperationException("Hidden smoke rejected an unexpected model request.");
            var payload = JsonNode.Parse(RequestBody)!;
            var context = JsonNode.Parse(payload["messages"]![1]!["content"]!.GetValue<string>())!;
            Check(context["TaskKind"]!.GetValue<string>() == "DeckPlan" && context["Snapshot"]!["schema"]!.GetValue<string>() == "compact-v1",
                "Planning button did not use the versioned compact task context.");
            var content = JsonSerializer.Serialize(new { summary = ProposedSummary + " " + FakeKey, reasons = new[] { "仅使用导入的套牌背景" }, alternative = (string?)null,
                risks = new[] { "这是假响应；卡库和实时局面未知" } });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = 8, completion_tokens = 2, total_tokens = 10 } }), Encoding.UTF8, "application/json") };
        }
    }
}
