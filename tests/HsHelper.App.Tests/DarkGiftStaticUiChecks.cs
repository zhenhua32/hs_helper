using System.Diagnostics;
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
using HsHelper.Agent;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;

/// <summary>Observed current enchantments through the actual candidate editor and final advice pipeline.</summary>
internal static class DarkGiftStaticUiChecks
{
    private const string Build = "255790";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "dark-gift-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new AdviceHandler(); using var http = new HttpClient(handler);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            var cards = FixtureCards(); var catalog = (CardCatalog)Field(window, "_catalog")!;
            Await(catalog.InitializeAsync()); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_ACTIVE_BUILD";
            Import(catalog, decoy, "999999");
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "EndpointBox").Text = "https://dark-gift-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "dark-gift-ui"; C<PasswordBox>(window, "KeyBox").Password = "fixture-dark-gift-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://dark-gift-ui.invalid/v1/chat/completions"), "dark-gift-ui", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;

            foreach (var charge in new[] { true, false })
            {
                var frame = Frame(cards, charge ? "charge" : "lifesteal", charge); var original = SnapshotCodec.Serialize(frame);
                var cutoff = PinSaved(window, store, frame);
                Await(Invoke(window, "RenderBattleAsync"));
                var battle = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
                var hand = battle.Local.Hand.Single(card => card.EntityId == 30);
                Check(hand.Attack == (charge ? 5 : 8) && hand.Status.Contains(charge ? "冲锋" : "吸血", StringComparison.Ordinal) &&
                    !hand.Name.Contains("WRONG_ACTIVE_BUILD", StringComparison.Ordinal), "Battle display changed an observed value, keyword or pinned card build.");
                var tools = Tools(window); var loaded = (IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!;
                var enchantId = charge ? "EDR_100t6e" : "EDR_100te";
                Check(loaded[enchantId].DbfId == (charge ? 113809 : 113803) && loaded[enchantId].Text.Contains(charge ? "冲锋" : "吸血", StringComparison.Ordinal) &&
                    !loaded[enchantId].Name.Contains("WRONG_ACTIVE_BUILD", StringComparison.Ordinal), "The actual local binding lost the fixed-build enchantment definition.");
                AuthorLine(tools, charge ? 21 : 40); Evaluate(tools);
                Check(tools.ResultText.Contains("验证级别：已验证", StringComparison.Ordinal) && tools.ResultText.Contains("已应用动作：2", StringComparison.Ordinal) &&
                    tools.ResultText.Contains(charge ? "已验证斩杀：是" : "生命 28", StringComparison.Ordinal) &&
                    (charge || tools.ResultText.Contains("攻击 8", StringComparison.Ordinal)), "The actual two-button-authored line was not fully verified: " + tools.ResultText);
                handler.Prepare(charge ? 21 : 40, charge ? "DARK_GIFT_UI_CHARGE" : "DARK_GIFT_UI_LIFESTEAL");
                Await(Invoke(window, "AnalyzeCurrentAsync"));
                var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == frame.MatchId);
                var main = C<TextBox>(window, "AdviceBox").Text;
                Check(saved.Advice.Verification == "verified" && saved.Advice.Actions.Count == 2 && saved.Advice.VerifiedLethal == charge &&
                    saved.Advice.CardBuild == Build && saved.Advice.KnowledgeAsOf == cutoff && saved.Advice.StateVersion == frame.StateVersion &&
                    saved.Advice.EventSequence == frame.EventSequence && main.Contains("动作已通过本地规则验证", StringComparison.Ordinal) &&
                    main.Contains(handler.Summary, StringComparison.Ordinal) && (!charge || main.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal)),
                    "Actual final host verification, main advice or durable archive lost the observed effect or provenance: " + main);
                var result = handler.LastEvaluation!["Result"]!["Entities"]!.AsArray();
                Check(result.Single(entity => entity!["EntityId"]!.GetValue<int>() == 11)!["RemainingHealth"]!.GetValue<int>() == (charge ? 20 : 28) &&
                    result.Single(entity => entity!["EntityId"]!.GetValue<int>() == 30)!["Attack"]!.GetValue<int>() == (charge ? 5 : 8),
                    "The final real evaluate_line reapplied the static gift instead of preserving the observed attack/healing.");
                AssertArchiveRender(archive, saved, window);
                Check(original == SnapshotCodec.Serialize(frame), "Candidate editing or final verification mutated the observed frame.");
            }
            Pass("actual play/attack controls, final fake-HTTP host validation and saved main/archive rendering retain observed Charge and Lifesteal without reapplying stats");

            var candidateFrame = Frame(cards, "candidate-lifecycle", true); PinSaved(window, store, candidateFrame);
            var candidateTools = Tools(window); var kind = C<ComboBox>(candidateTools, "LocalKindBox");
            kind.SelectedIndex = 1; Check(!HasSource(candidateTools, 30), "An unplayed hand minion appeared as an attack source.");
            AddPlay(candidateTools); kind.SelectedIndex = 1;
            var future = SelectId(C<ComboBox>(candidateTools, "LocalSourceBox"), 30);
            Check(future.GetType().GetProperty("Text")!.GetValue(future)!.ToString()!.Contains("攻击资格仍由规则验证", StringComparison.Ordinal), "The candidate source was presented as an already legal attacker.");
            SelectId(C<ComboBox>(candidateTools, "LocalTargetBox"), 21); Click(C<Button>(candidateTools, "LocalAddActionButton")); Evaluate(candidateTools);
            Check(candidateTools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal), "The lifecycle control never established a verified old result.");
            C<ListBox>(candidateTools, "LocalActionsList").SelectedIndex = 0;
            Click((Button)Field(candidateTools, "_remove")!);
            Check(!HasSource(candidateTools, 30) && candidateTools.ResultText == "候选序列已变更，请重新验证。", "Removing its play retained a future hand attacker or its stale verified result.");
            AddPlay(candidateTools); kind.SelectedIndex = 1; Check(HasSource(candidateTools, 30), "Re-adding play did not refresh attack candidates.");
            candidateTools.UpdateBinding(Frame(cards, "rebound", true), Build, true); Await(candidateTools.BindingReady);
            Check(!HasSource(candidateTools, 30) && C<ListBox>(candidateTools, "LocalActionsList").Items.Count == 0, "Rebinding retained an old candidate play/attacker.");
            var spectator = candidateFrame with { IsSpectating = true };
            candidateTools.UpdateBinding(spectator, Build, true); Await(candidateTools.BindingReady); kind.SelectedIndex = 0;
            Check(!HasSource(candidateTools, 30), "Spectator hand identities were added by the candidate-source extension.");
            Pass("attack candidates follow actual add/remove/rebind controls and never promote an unplayed or spectator hand entity");

            var ordinary = Frame(cards, "ordinary-rush", true, gift: null, observedGift: false); PinSaved(window, store, ordinary);
            var ordinaryTools = Tools(window); AuthorLine(ordinaryTools, 21); Evaluate(ordinaryTools);
            Check(ordinaryTools.ResultText.Contains("验证级别：无效", StringComparison.Ordinal) && !ordinaryTools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                "The candidate editor granted a new non-Charge minion permission to attack a hero: " + ordinaryTools.ResultText);
            var unknown = Frame(cards, "unknown-gift", true, gift: "EDR_100t5e"); PinSaved(window, store, unknown);
            var unknownTools = Tools(window); AuthorLine(unknownTools, 21); Evaluate(unknownTools);
            Check(unknownTools.ResultText.Contains("条件成立", StringComparison.Ordinal) && !unknownTools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                "An unaudited gift was promoted to verified by the UI candidate path: " + unknownTools.ResultText);
            handler.Prepare(21, "DARK_GIFT_UI_UNKNOWN"); Await(Invoke(window, "AnalyzeCurrentAsync"));
            var unknownSaved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == unknown.MatchId);
            Check(unknownSaved.Advice.Verification == "conditional" && unknownSaved.Advice.Actions.Count == 0 && !unknownSaved.Advice.VerifiedLethal &&
                C<TextBox>(window, "AdviceBox").Text.Contains("有条件建议（具体动作已移除）", StringComparison.Ordinal), "An unknown gift escaped final advice action/lethal gating or its archive projection.");
            AssertArchiveRender(archive, unknownSaved, window);
            var wrong = new CardCatalog(Path.Combine(directory, "wrong-definition.db")); Await(wrong.InitializeAsync());
            var tampered = (JsonArray)cards.DeepClone(); tampered.Single(card => card!["id"]!.ToString() == "EDR_100t6e")!["dbfId"] = 1;
            Import(wrong, tampered, Build);
            var wrongTools = new LocalToolsWindow(Frame(cards, "wrong-source", true), Build, wrong, true);
            try
            {
                Await(wrongTools.BindingReady); AuthorLine(wrongTools, 21); Evaluate(wrongTools);
                Check(wrongTools.ResultText.Contains("条件成立", StringComparison.Ordinal) && !wrongTools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                    "The same enchantment ID with a wrong fixed definition bypassed source auditing: " + wrongTools.ResultText);
            }
            finally { wrongTools.Close(); }
            Pass("ordinary new minions remain illegal hero attackers, while unknown or wrong-definition gifts retain conditional results and remove final actions/lethal claims");

            var staleFrame = Frame(cards, "stale-advice", true); PinSaved(window, store, staleFrame);
            handler.Prepare(21, "DARK_GIFT_UI_STALE", delayed: true);
            var analysis = Invoke(window, "AnalyzeCurrentAsync"); Await(handler.FinalEntered.Task);
            var countBefore = Await(archive.ListAsync()).Count;
            var newer = Frame(cards, "newer-frame", false); PinSaved(window, store, newer);
            InvokeObject(window, "RenderSnapshot"); C<TextBox>(window, "AdviceBox").Text = "NEWER_GIFT_FRAME";
            handler.ReleaseFinal.SetResult(); Await(analysis);
            Check(C<TextBox>(window, "AdviceBox").Text == "NEWER_GIFT_FRAME" && Await(archive.ListAsync()).Count == countBefore &&
                !Await(archive.ListAsync()).Any(record => record.Advice.MatchId == staleFrame.MatchId), "A model answer for an old static-gift frame was displayed or archived after rebinding.");
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero && (Field(window, "_localTools") is not LocalToolsWindow open || new WindowInteropHelper(open).Handle == IntPtr.Zero),
                "The regression created a native window handle.");
            Pass("a final model answer delayed after actual tool evaluation cannot publish or archive after the original frame changes");
        }
        finally { window.Close(); launch.SetValue(null, previous); }
    }

    private static JsonArray FixtureCards()
    {
        var file = Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", "dark-gift-static-255790.json");
        return JsonNode.Parse(File.ReadAllText(file))!["cards"]!.AsArray();
    }
    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new FileNotFoundException("The fixed Dark Gift fixture was not found.");
    }
    private static void Import(CardCatalog catalog, JsonArray cards, string build)
    { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://dark-gift-ui")); }
    private static GameSnapshot Frame(JsonArray cards, string suffix, bool charge, string? gift = "native", bool observedGift = true)
    {
        var body = cards.Single(card => card!["id"]!.ToString() == "EDR_227")!;
        var engine = new PowerLogStateEngine(1, "dark-gift-ui-" + suffix);
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=NUM_CARDS_IN_HAND value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Feed("FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER", "tag=HEALTH value=30", "tag=DAMAGE value=10", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE", $"tag=HEALTH value={(charge ? 3 : 30)}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=30 CardID=EDR_227", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", $"tag=COST value={body["cost"]}", $"tag=ATK value={body["attack"]!.GetValue<int>() + (charge ? 0 : 3)}", $"tag=HEALTH value={body["health"]}", "tag=DAMAGE value=0", "tag=RUSH value=1", "tag=DEATHRATTLE value=1");
        if (observedGift) Feed(charge ? "tag=CHARGE value=1" : "tag=LIFESTEAL value=1");
        if (!charge) Feed("FULL_ENTITY - Creating ID=40 CardID=EDR_227", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=0", "tag=HEALTH value=40", "tag=DAMAGE value=0", "tag=DEATHRATTLE value=1", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1");
        if (gift is not null) Feed($"FULL_ENTITY - Creating ID=50 CardID={(gift == "native" ? charge ? "EDR_100t6e" : "EDR_100te" : gift)}", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var result = engine.Snapshot(); Check(result.Quality.IsStable && result.Quality.IsComplete && result.Quality.HasLocalPerspective, "The replay fixture did not establish a complete own frame."); return result;
    }
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame)
    {
        var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync());
        var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, []));
        Set(window, "_historyObservedAt", point.RecordedAt); Set(window, "_liveFrame", frame); return point.RecordedAt;
    }
    private static LocalToolsWindow Tools(MainWindow window)
    { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void AddPlay(LocalToolsWindow tools)
    {
        C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 0; SelectId(C<ComboBox>(tools, "LocalSourceBox"), 30);
        SelectId(C<ComboBox>(tools, "LocalTargetBox"), 0); Click(C<Button>(tools, "LocalAddActionButton"));
    }
    private static void AuthorLine(LocalToolsWindow tools, int target)
    {
        AddPlay(tools); C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1;
        SelectId(C<ComboBox>(tools, "LocalSourceBox"), 30); SelectId(C<ComboBox>(tools, "LocalTargetBox"), target);
        Click(C<Button>(tools, "LocalAddActionButton"));
        Check(C<ListBox>(tools, "LocalActionsList").Items.Count == 2, "Actual add-action controls failed to author both steps.");
    }
    private static void Evaluate(LocalToolsWindow tools)
    { Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => !tools.HasPendingCalculation, "The actual local evaluation did not settle."); }
    private static bool HasSource(LocalToolsWindow tools, int id) => C<ComboBox>(tools, "LocalSourceBox").Items.Cast<object>().Any(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static object SelectId(ComboBox box, int id)
    { var selected = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id); box.SelectedItem = selected; return selected; }
    private static void AssertArchiveRender(AdviceArchiveStore store, ArchivedAdviceRecord saved, MainWindow main)
    {
        var browser = new AdviceArchiveWindow(store, new(saved.Advice.MatchId, "合成附魔局面", AdviceArchiveScopeKind.Session));
        try
        {
            Await(browser.InitializeAsync()); var list = C<ListBox>(browser, "ArchiveRecordsList");
            list.SelectedItem = list.Items.Cast<object>().Single(item => ((ArchivedAdviceRecord)item.GetType().GetProperty("Value")!.GetValue(item)!).Id == saved.Id);
            var text = C<TextBox>(browser, "ArchiveDetailText").Text;
            Check(browser.SelectedRecord?.Id == saved.Id && text.Contains(saved.Advice.Summary, StringComparison.Ordinal) && text.Contains(Build, StringComparison.Ordinal) &&
                text.Contains("原验证状态：" + saved.Advice.Verification, StringComparison.Ordinal) &&
                C<TextBox>(main, "AdviceBox").Text.Contains(saved.Advice.Summary, StringComparison.Ordinal) && new WindowInteropHelper(browser).Handle == IntPtr.Zero,
                "Reopening the actual archive browser did not render the same saved result/provenance: " + text);
        }
        finally { browser.Close(); }
    }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An actual test button is disabled: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Dark Gift UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string error)
    {
        if (ready()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), error);
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Pass(string message) => Console.WriteLine("PASS observed Dark Gift UI: " + message);

    private sealed class AdviceHandler : HttpMessageHandler
    {
        public string Summary { get; private set; } = "";
        public JsonObject? LastEvaluation { get; private set; }
        private int _target; private bool _delayed;
        public TaskCompletionSource FinalEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinal { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Prepare(int target, string summary, bool delayed = false)
        { _target = target; Summary = summary; _delayed = delayed; LastEvaluation = null; FinalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); ReleaseFinal = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            var tool = json["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool");
            var actions = new[] { new { kind = "play", source_entity_id = 30, target_entity_id = (int?)null }, new { kind = "attack", source_entity_id = 30, target_entity_id = (int?)_target } };
            if (tool is null)
            {
                return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id = "dark_gift_ui_evaluate", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } },
                    usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            }
            LastEvaluation = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject(); FinalEntered.TrySetResult();
            if (_delayed) await ReleaseFinal.Task;
            var content = JsonSerializer.Serialize(new { summary = Summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions });
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
