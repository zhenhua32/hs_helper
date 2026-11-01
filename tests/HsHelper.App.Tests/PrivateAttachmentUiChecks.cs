using System.Diagnostics;
using System.Collections.Frozen;
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

internal static class PrivateAttachmentUiChecks
{
    private const string Build = "255790", Gift = "EDR_100t13e";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "private-attachment-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new Handler(); using var http = new HttpClient(handler);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); var definitions = FixtureCards(); Import(catalog, definitions);
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;
            C<TextBox>(window, "EndpointBox").Text = "https://private-attachment-ui.invalid/v1/chat/completions"; C<TextBox>(window, "ModelBox").Text = "private-attachment-ui";
            C<PasswordBox>(window, "KeyBox").Password = "fixture-private-attachment-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://private-attachment-ui.invalid/v1/chat/completions"), "private-attachment-ui", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });

            foreach (var zone in new[] { "HAND", "DECK", "SETASIDE", "LEGACY_HAND" })
            {
                var frame = Frame(zone == "LEGACY_HAND" ? "HAND" : zone, zone == "LEGACY_HAND" ? "legacy-raw" : null);
                if (zone == "LEGACY_HAND")
                {
                    var entities = frame.Entities.ToDictionary(); entities[50] = new(50, Gift, EntityKind.Card,
                        new Dictionary<string, string> { ["CONTROLLER"] = "2", ["ZONE"] = "PLAY", ["CARDTYPE"] = "ENCHANTMENT", ["ATTACHED"] = "70" }.ToFrozenDictionary());
                    frame = frame with { Entities = entities.ToFrozenDictionary(), RecentEvents = [.. frame.RecentEvents,
                        new(frame.EventSequence, "ZoneChanged", 50, 2, Gift, "SETASIDE", "PLAY", "Synthetic legacy attachment event without identity proof")] };
                }
                else
                {
                    Check(frame.Entities[70].CardId is null && !frame.Entities.Values.Any(entity => entity.CardId == Gift) &&
                        !frame.RecentEvents.Any(entry => entry.CardId == Gift), "A private attachment target leaked its enchantment identity or source event before reaching the UI: " + zone);
                    if (frame.Entities.TryGetValue(50, out var source)) Check(!source.Tags.ContainsKey("ATTACHED") && !source.Tags.ContainsKey("40"), "A private source retained its target relation.");
                }
                var original = SnapshotCodec.Serialize(frame);
                Pin(window, frame); InvokeObject(window, "RenderSnapshot"); Await(Invoke(window, "RenderBattleAsync"));
                Check(!C<DataGrid>(window, "EntityGrid").Items.OfType<EntitySnapshot>().Any(entity => entity.CardId == Gift) &&
                    !BattleViewBuilder.VisibleCardIds(frame, Build).Contains(Gift), "Actual entity rendering or catalog visibility retained a private attachment.");
                var tools = Tools(window); Check(!((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(Gift), "Local tools preloaded private gift metadata.");
                handler.Prepare(false); Await(Invoke(window, "AnalyzeCurrentAsync"));
                Check(handler.Snapshots.Count == 2 && handler.Snapshots.All(snapshot => !snapshot.ToJsonString().Contains(Gift, StringComparison.Ordinal)) &&
                    handler.ToolResult is { } tool && !tool.ToJsonString().Contains(Gift, StringComparison.Ordinal) && tool["error"] is not null,
                    "Actual model snapshot/cards/events or get_entity_effect exposed a source attached to an opposing private target: " + zone);
                Check(original == SnapshotCodec.Serialize(frame), "Derived UI/host privacy projection mutated the original legacy/current source frame.");
            }
            Pass("actual parser/entity grid/card preload/LocalTools and model snapshots plus effect tools suppress sources attached to opposing HAND DECK and SETASIDE");

            foreach (var scope in new[] { "OWN_HAND", "PUBLIC_PLAY" })
            {
                var frame = Frame(scope); Pin(window, frame); InvokeObject(window, "RenderSnapshot"); Await(Invoke(window, "RenderBattleAsync"));
                var tools = Tools(window); Check(frame.Entities[50].CardId == Gift && BattleViewBuilder.VisibleCardIds(frame, Build).Contains(Gift) &&
                    C<DataGrid>(window, "EntityGrid").Items.OfType<EntitySnapshot>().Any(entity => entity.Id == 50 && entity.CardId == Gift) &&
                    ((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(Gift), "The privacy fix removed a confirmed own/public attachment source.");
                handler.Prepare(false); Await(Invoke(window, "AnalyzeCurrentAsync"));
                Check(handler.Snapshots.All(snapshot => snapshot.ToJsonString().Contains(Gift, StringComparison.Ordinal)) &&
                    handler.ToolResult!.ToJsonString().Contains(Gift, StringComparison.Ordinal) && handler.ToolResult["error"] is null,
                    "A confirmed own hand or public-board source was no longer queryable through actual host tools.");
            }
            var past = Frame("PAST_OWN"); Pin(window, past); InvokeObject(window, "RenderSnapshot"); Await(Invoke(window, "RenderBattleAsync"));
            var pastTools = Tools(window);
            Check(!past.Entities.ContainsKey(50) && past.RecentEvents.Any(entry => entry.CardId == Gift) &&
                !((IReadOnlyDictionary<string, CardRecord>)Field(pastTools, "_cards")!).ContainsKey(Gift),
                "An actually observed past own/public source was erased from history or reused as current source metadata after its target became private.");
            handler.Prepare(false); Await(Invoke(window, "AnalyzeCurrentAsync"));
            Check(handler.Snapshots.All(snapshot => !snapshot["entities"]!.AsArray().Any(entity => entity?["card_id"]?.ToString() == Gift) &&
                    snapshot["recent_events"]!.AsArray().Any(entry => entry?["card_id"]?.ToString() == Gift)) && handler.ToolResult!["error"] is not null,
                "True seen history was mistaken for a currently inspectable private source, or was retroactively erased from the model's historical facts.");
            Pass("confirmed own HAND and public PLAY sources remain visible in actual render preload and model effect queries");

            foreach (var mutation in new[] { "native", "dbf", "fulltext" })
            {
                var chosen = catalog;
                if (mutation != "native")
                {
                    var changed = (JsonArray)definitions.DeepClone(); var aura = changed.Single(card => card!["id"]!.ToString() == "EDR_480")!;
                    if (mutation == "dbf") aura["dbfId"] = 1; else aura["text"] = "友方野兽不再造成额外伤害。";
                    chosen = new CardCatalog(Path.Combine(directory, "native-" + mutation + ".db")); Await(chosen.InitializeAsync()); Import(chosen, changed);
                }
                (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", chosen);
                var frame = Frame("AURA", mutation); var cutoff = PinSaved(window, store, frame); var tools = Tools(window);
                C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1; Select(C<ComboBox>(tools, "LocalSourceBox"), 30); Select(C<ComboBox>(tools, "LocalTargetBox"), 21);
                Click(C<Button>(tools, "LocalAddActionButton")); Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => !tools.HasPendingCalculation, "Native aura local evaluation did not settle.");
                Check(tools.ResultText.Contains("验证级别：已验证", StringComparison.Ordinal) == (mutation == "native") &&
                    tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal) == (mutation == "native"), "Native aura metadata was ignored by the actual local action route: " + mutation);
                handler.Prepare(true); Await(Invoke(window, "AnalyzeCurrentAsync"));
                var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == frame.MatchId);
                Check(saved.Advice.KnowledgeAsOf == cutoff && saved.Advice.CardBuild == Build && saved.Advice.StateVersion == frame.StateVersion &&
                    saved.Advice.EventSequence == frame.EventSequence && (mutation == "native"
                        ? saved.Advice.Verification == "verified" && saved.Advice.VerifiedLethal && saved.Advice.Actions.Count == 1
                        : saved.Advice.Verification != "verified" && !saved.Advice.VerifiedLethal && saved.Advice.Actions.Count == 0),
                    "Final host verification or archive trusted a model's forged native-aura verification: " + mutation);
                var text = C<TextBox>(window, "AdviceBox").Text;
                Check(mutation == "native" ? text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal) :
                    text.Contains("具体动作已移除", StringComparison.Ordinal) && !text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal), "Main advice disagreed with final native-aura validation.");
                var browser = new AdviceArchiveWindow(archive, new(frame.MatchId, "合成原生定义", AdviceArchiveScopeKind.Session));
                try { Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0; Check(browser.SelectedRecord?.Id == saved.Id &&
                    C<TextBox>(browser, "ArchiveDetailText").Text.Contains("原验证状态：" + saved.Advice.Verification, StringComparison.Ordinal), "Actual archive rendering lost host source validation."); Passive(browser); }
                finally { browser.Close(); }
            }
            (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", catalog);
            Pass("native Goldrinn doubles the real attack; wrong DBF or coherent altered fulltext cannot forge local final-host main or archived verified lethal");

            foreach (var fail in new[] { false, true })
            {
                var old = Frame("OWN_HAND", "old-" + fail); Pin(window, old);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (kind, observed, build, token) =>
                {
                    Check(kind == "battle" && observed.MatchId == old.MatchId && build == Build, "The old-source gate did not follow actual fixed-build catalog IO.");
                    entered.SetResult(); await release.Task; if (fail) throw new IOException("OLD_ATTACHMENT_SOURCE_READ");
                }));
                var pending = Invoke(window, "RenderBattleAsync"); Await(entered.Task); Set(window, "_aggregatedCardReadCompleted", null);
                Pin(window, Frame("HAND", "new-" + fail)); Await(Invoke(window, "RenderBattleAsync")); var current = C<FrameworkElement>(window, "BattleViewRoot").DataContext;
                var tools = Tools(window); tools.UpdateBinding(old, Build, true); var oldNames = tools.BindingReady;
                tools.UpdateBinding(Frame("HAND", "new-tools-" + fail), Build, true); Await(tools.BindingReady); Await(oldNames);
                release.SetResult(); Await(pending);
                Check(ReferenceEquals(current, C<FrameworkElement>(window, "BattleViewRoot").DataContext) &&
                    !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(Gift), "An old public-source success/failure or preload overwrote a private replacement frame.");
                tools.UpdateBinding(old, Build, true); var closingNames = tools.BindingReady; tools.Close(); Await(closingNames);
                Check(!tools.IsBindingValid && ((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).Count == 0, "Closing retained or republished old source metadata."); Passive(tools);
            }
            Passive(window);
            Pass("after-real-IO source successes failures and preload completions cannot cross a new frame or closed hidden calculator");
        }
        finally { window.Close(); launch.SetValue(null, previous); }
    }

    private static JsonArray FixtureCards()
    {
        var wanted = new HashSet<string>(["HERO_05", "HERO_08", "CS2_172", "EDR_227", "EDR_480", Gift], StringComparer.Ordinal); var cards = new Dictionary<string, JsonNode>();
        foreach (var fixture in new[] { "dark-gift-static-255790.json", "beast-hunter-255790.json", "terror-end-turn-255790.json" })
        {
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", fixture)))!;
            foreach (var card in source["cards"]!.AsArray()) if (wanted.Contains(card!["id"]!.ToString())) cards.TryAdd(card["id"]!.ToString(), card.DeepClone());
        }
        Check(wanted.SetEquals(cards.Keys), "Missing fixed-source private-attachment/native-aura fixture."); return new(cards.Values.ToArray());
    }
    private static GameSnapshot Frame(string scope, string? variant = null)
    {
        var engine = new PowerLogStateEngine(1, "private-attachment-ui-" + scope + "-" + (variant ?? "main"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", $"tag=NUM_CARDS_IN_HAND value={(scope == "OWN_HAND" ? 1 : 0)}",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        foreach (var (id, card, owner, cls) in new[] { (11, "HERO_05", 1, "HUNTER"), (21, "HERO_08", 2, "MAGE") })
            Feed($"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", $"tag=CLASS value={cls}", $"tag=HEALTH value={(scope == "AURA" && id == 21 ? 6 : 30)}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=30 CardID=CS2_172", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=3", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=CARDRACE value=20", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=NUM_ATTACKS_THIS_TURN value=0");
        if (scope == "AURA") Feed("FULL_ENTITY - Creating ID=60 CardID=EDR_480", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=0", "tag=HEALTH value=9", "tag=DAMAGE value=0", "tag=RUSH value=1", "tag=AURA value=1");
        else
        {
            var owner = scope is "OWN_HAND" or "PAST_OWN" ? 1 : 2; var zone = scope == "PUBLIC_PLAY" ? "PLAY" : scope is "OWN_HAND" or "PAST_OWN" ? "HAND" : scope;
            Feed("FULL_ENTITY - Creating ID=70 CardID=EDR_227", $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}", "tag=CARDTYPE value=MINION", "tag=ATK value=5", "tag=HEALTH value=2", "tag=DAMAGE value=0", "tag=COST value=4", "tag=DIVINE_SHIELD value=1", "tag=WINDFURY value=1", "tag=RUSH value=1", "tag=DEATHRATTLE value=1",
                $"FULL_ENTITY - Creating ID=50 CardID={Gift}", $"tag=CONTROLLER value={(scope == "PAST_OWN" ? 2 : owner)}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=70");
            if (scope == "PAST_OWN") Feed("TAG_CHANGE Entity=50 tag=ZONE value=SETASIDE", "TAG_CHANGE Entity=50 tag=ZONE value=PLAY", "TAG_CHANGE Entity=70 tag=ZONE value=DECK");
        }
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var frame = engine.Snapshot(); Check(frame.Quality.IsStable && frame.Quality.IsComplete && frame.Quality.HasLocalPerspective, "The real source fixture was not stable complete own perspective."); return frame;
    }
    private static void Import(CardCatalog catalog, JsonArray cards) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, Build, source: "fixture://private-attachment-ui")); }
    private static void Pin(MainWindow window, GameSnapshot frame) { Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", DateTimeOffset.UtcNow.AddMinutes(-1)); Set(window, "_liveFrame", frame); }
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame) { var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync()); var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Pin(window, frame); Set(window, "_historyObservedAt", point.RecordedAt); return point.RecordedAt; }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Select(ComboBox box, int id) => box.SelectedItem = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static string Workspace() { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "AGENTS.md"))) return path.FullName; throw new DirectoryNotFoundException("Missing private-attachment fixture root."); }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An actual private-attachment UI action was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Private-attachment UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string error) { if (ready()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), error); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "The source check created a visible/native window.");
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Pass(string message) => Console.WriteLine("PASS private attachment UI: " + message);

    private sealed class Handler : HttpMessageHandler
    {
        private bool _aura;
        public List<JsonObject> Snapshots { get; } = [];
        public JsonObject? ToolResult { get; private set; }
        public void Prepare(bool aura) { _aura = aura; Snapshots.Clear(); ToolResult = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; var messages = body["messages"]!.AsArray();
            var content = messages.Last(message => message?["role"]?.ToString() == "user")!["content"]!.GetValue<string>();
            Snapshots.Add(JsonNode.Parse(content)!["Snapshot"]!.DeepClone().AsObject());
            var tool = messages.LastOrDefault(message => message?["role"]?.ToString() == "tool");
            if (tool is null) return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                tool_calls = new[] { new { id = "private_attachment_ui_tool", type = "function", function = new { name = _aura ? "evaluate_line" : "get_entity_effect", arguments = _aura ? "{\"actions\":[{\"kind\":\"attack\",\"source_entity_id\":30,\"target_entity_id\":21}]}" : "{\"entity_id\":50}" } } } } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            ToolResult = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject();
            var actions = _aura ? new[] { new { kind = "attack", source_entity_id = 30, target_entity_id = 21 } } : [];
            var final = JsonSerializer.Serialize(new { summary = _aura ? "NATIVE_AURA_FINAL" : "PRIVATE_ATTACHMENT_SAFE", reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions, verification = "verified", verified_lethal = true });
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = final } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
