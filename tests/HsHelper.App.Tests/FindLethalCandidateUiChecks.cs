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
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

/// <summary>Actual bounded-search controls and final advice use the same observed card candidates.</summary>
internal static class FindLethalCandidateUiChecks
{
    private const string Build = "255790";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "find-lethal-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new SearchAdviceHandler(); using var http = new HttpClient(handler);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            var definitions = FixtureCards(); var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(definitions.ToJsonString()))) Await(catalog.ImportAsync(input, Build, source: "fixture://find-lethal-ui"));
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;

            foreach (var kind in new[] { "arcane", "hero_power", "charge", "combo" })
            {
                var frame = Frame(kind); var before = SnapshotCodec.Serialize(frame); Pin(window, frame);
                var tools = Tools(window); Search(tools);
                var rows = tools.ResultText.Split('\n').Where(line => line.StartsWith("· 使用手牌：", StringComparison.Ordinal) ||
                    line.StartsWith("· 英雄技能：", StringComparison.Ordinal) || line.StartsWith("· 攻击：", StringComparison.Ordinal)).ToArray();
                var expected = kind switch
                {
                    "arcane" => new[] { "使用手牌：幸运币", "使用手牌：奥术射击" },
                    "hero_power" => ["使用手牌：幸运币", "英雄技能：稳固射击"],
                    "charge" => ["使用手牌：震地雷龙", "攻击：震地雷龙"],
                    _ => ["使用手牌：幸运币", "使用手牌：震地雷龙", "攻击：震地雷龙"]
                };
                Check(tools.ResultText.Contains("找到已验证的斩杀路线。", StringComparison.Ordinal) && tools.ResultText.Contains("验证级别：已验证", StringComparison.Ordinal) &&
                    tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal) && rows.Length == expected.Length &&
                    rows.Zip(expected).All(pair => pair.First.StartsWith("· " + pair.Second, StringComparison.Ordinal)),
                    "The actual search button omitted or reordered the required observed candidates for " + kind + ": " + tools.ResultText);
                Check(before == SnapshotCodec.Serialize(frame), "The actual search mutated its input frame.");
                if (kind is "charge" or "combo") Check(frame.Entities[30].GetIntTag("CHARGE") == 1 && !frame.Entities[30].Tags.ContainsKey("RUSH") &&
                    tools.ResultText.Contains("dark-gift-observed-charge:EDR_100t6e", StringComparison.Ordinal), "The positive case depended on native Rush or skipped the attached-gift audit.");
                Passive(tools);
            }
            Pass("actual bounded-search button displays Coin then direct damage or hero power and non-Rush observed Charge, including the three-step combined route");

            foreach (var negative in new[] { "missing-keyword", "missing-cost", "unknown-gift", "secret", "raptor", "cant-attack-heroes" })
            {
                var frame = Frame("charge", negative); Pin(window, frame); var tools = Tools(window); Search(tools);
                Check(tools.ResultText.Contains("未找到已验证的斩杀；不代表没有斩杀", StringComparison.Ordinal) &&
                    !tools.ResultText.Contains("找到已验证的斩杀路线。", StringComparison.Ordinal) && !tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                    "An incomplete or unresolved candidate became a verified UI lethal: " + negative + "\n" + tools.ResultText);
            }
            Pass("missing current Charge or cost, unknown gifts/secrets/Raptor and a current cannot-attack-heroes restriction remain unproved searches");

            foreach (var close in new[] { false, true })
            {
                var frame = Frame("combo", close ? "late-close" : "late-rebind");
                var tools = new LocalToolsWindow(frame, Build, catalog, true); var completed = false; string? replacementText = null;
                try
                {
                    Await(tools.BindingReady);
                    // The existing completion observer runs only after the actual immutable-frame search has returned.
                    var coach = Field(tools, "_coach")!;
                    Set(coach, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
                    {
                        Check(observation.Name == "find_lethal" && observation.Success && JsonNode.Parse(observation.ResultJson!)!["VerifiedLethalFound"]!.GetValue<bool>(),
                            "The late-result gate did not follow a real successful search.");
                        if (close) tools.Close();
                        else tools.UpdateBinding(Frame("charge", "missing-keyword"), Build, true);
                        replacementText = tools.ResultText; completed = true;
                    }));
                    Click(C<Button>(tools, "LocalLethalButton")); WaitUntil(() => completed && !tools.HasPendingCalculation, "The post-search binding/close gate was not exercised.");
                    Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
                    if (!close) Await(tools.BindingReady);
                    Check(tools.ResultText == replacementText && !tools.ResultText.Contains("找到已验证的斩杀路线。", StringComparison.Ordinal) &&
                        (close ? !tools.IsBindingValid : tools.BoundMatchId != frame.MatchId), "A completed old search overwrote a replacement binding or closed calculator.");
                    Passive(tools);
                }
                finally { if (!close || !completed) tools.Close(); }
            }
            Pass("after a real successful search the original result cannot publish across a binding replacement or hidden calculator close");

            var combined = Frame("combo", "advice"); var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER"));
            recorder.Observe(combined, 1); Await(recorder.CompleteAsync());
            var point = Await(store.ListDecisionPointsAsync(combined.MatchId, true)).Single(); Pin(window, combined); Set(window, "_historyObservedAt", point.RecordedAt);
            C<TextBox>(window, "EndpointBox").Text = "https://find-lethal-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "find-lethal-ui"; C<PasswordBox>(window, "KeyBox").Password = "fixture-find-lethal-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://find-lethal-ui.invalid/v1/chat/completions"), "find-lethal-ui", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });
            Await(Invoke(window, "AnalyzeCurrentAsync"));
            var search = handler.Search!; var saved = Await(archive.ListAsync()).Single();
            Check(handler.RequestCount == 2 && search["VerifiedLethalFound"]!.GetValue<bool>() && search["Evaluation"]!["MatchId"]!.ToString() == combined.MatchId &&
                saved.Advice.Verification == "verified" && saved.Advice.VerifiedLethal && saved.Advice.Actions.Select(action => (action.Kind, action.SourceEntityId, action.TargetEntityId))
                    .SequenceEqual(new[] { ("play", (int?)31, (int?)null), ("play", (int?)30, (int?)null), ("attack", (int?)30, (int?)21) }) &&
                saved.Advice.MatchId == combined.MatchId && saved.Advice.StateVersion == combined.StateVersion && saved.Advice.EventSequence == combined.EventSequence &&
                saved.Advice.CardBuild == Build && saved.Advice.KnowledgeAsOf == point.RecordedAt && saved.Advice.Evidence.Any(evidence => evidence.ToolName == "find_lethal"),
                "The actual model-tool proposal/final host validator/archive did not retain the real search route and exact decision provenance.");
            var mainText = C<TextBox>(window, "AdviceBox").Text;
            Check(mainText.Contains(SearchAdviceHandler.Summary, StringComparison.Ordinal) && mainText.Contains("动作已通过本地规则验证", StringComparison.Ordinal) &&
                mainText.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal), "Main advice did not show the final independently verified search proposal.");
            var browser = new AdviceArchiveWindow(archive, new(combined.MatchId, "合成斩杀搜索", AdviceArchiveScopeKind.Session));
            try
            {
                Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0;
                var text = C<TextBox>(browser, "ArchiveDetailText").Text;
                Check(browser.SelectedRecord?.Id == saved.Id && text.Contains(SearchAdviceHandler.Summary, StringComparison.Ordinal) &&
                    text.Contains("原验证状态：verified", StringComparison.Ordinal) && text.Contains("斩杀标记：是", StringComparison.Ordinal) &&
                    text.IndexOf("来源 #31", StringComparison.Ordinal) < text.IndexOf("来源 #30", StringComparison.Ordinal) && text.Contains("find_lethal", StringComparison.Ordinal),
                    "Reopening the real archive browser lost the route order, tool evidence or final verification.");
                Passive(browser);
            }
            finally { browser.Close(); }

            var blocked = Frame("charge", "cant-attack-heroes"); var blockedRecorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER"));
            blockedRecorder.Observe(blocked, 1); Await(blockedRecorder.CompleteAsync());
            var blockedPoint = Await(store.ListDecisionPointsAsync(blocked.MatchId, true)).Single(); Pin(window, blocked); Set(window, "_historyObservedAt", blockedPoint.RecordedAt);
            handler.ForceBlockedProposal = true; Click(C<Button>(window, "AnalyzeButton"));
            WaitUntil(() => Field(window, "_analysis") is null && C<TextBlock>(window, "StatusText").Text.Contains("未通过本地合法性校验", StringComparison.Ordinal),
                "The actual Analyze button did not surface the final host's invalid-action rejection.");
            var remaining = Await(archive.ListAsync()); var budget = ((AnalysisBudget)Field(window, "_analysisBudget")!).Snapshot(blocked.MatchId);
            var reopenedTraces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(reopenedTraces.InitializeAsync());
            var totals = Await(reopenedTraces.GetBudgetTotalsAsync(blocked.MatchId)); var requests = Await(reopenedTraces.ListRequestsAsync(blocked.MatchId));
            Check(handler.Search!["VerifiedLethalFound"]!.GetValue<bool>() == false && handler.RequestCount == 4 && remaining.Count == 1 && remaining[0].Id == saved.Id &&
                !C<TextBox>(window, "AdviceBox").Text.Contains("FIND_LETHAL_UI_BLOCKED_PROPOSAL", StringComparison.Ordinal) &&
                !C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal) &&
                budget.RequestsStarted == 2 && budget.InFlightRequests == 0 && budget.ChargedTokens == 20 && totals.RequestCount == 2 && totals.ChargedTokens == 20 &&
                requests.Count == 2 && requests.All(request => request.Status == AnalysisTraceStatus.Completed && request.ChargedTokens > 0),
                "A forced illegal hero attack published forged verification/archive data or lost already-consumed fake HTTP usage and durable traces.");
            Passive(window);
            Pass("actual Analyze search proposals pass final host/main/archive validation, while a forced hero attack cannot override an observed restriction or forge verified lethal");
        }
        finally { window.Close(); launch.SetValue(null, previous); }
    }

    private static JsonArray FixtureCards()
    {
        var wanted = new HashSet<string>(["GAME_005", "CORE_DS1_185", "HERO_05bp", "DINO_421", "EDR_849", "EDR_100t6e", "EDR_100t5e", "HERO_05", "HERO_08"], StringComparer.Ordinal);
        var definitions = new Dictionary<string, JsonNode>();
        foreach (var fixture in new[] { "beast-hunter-255790.json", "dark-gift-static-255790.json", "play-trigger-line-255790.json", "quest-progress-255790.json", "terror-end-turn-255790.json" })
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", fixture)))!;
            Check(root["build"]!.ToString() == Build, "A search fixture was not fixed to the audited build.");
            foreach (var card in root["cards"]!.AsArray()) if (wanted.Contains(card!["id"]!.ToString())) definitions.TryAdd(card["id"]!.ToString(), card.DeepClone());
        }
        Check(wanted.SetEquals(definitions.Keys), "The fixed search fixtures omitted a required source identity.");
        return new JsonArray(definitions.Values.ToArray());
    }
    private static GameSnapshot Frame(string kind, string? variant = null)
    {
        var coin = kind is "arcane" or "hero_power" or "combo"; var body = kind is "charge" or "combo";
        var engine = new PowerLogStateEngine(1, "find-lethal-ui-" + kind + "-" + (variant ?? "positive"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            $"tag=RESOURCES value={(kind == "arcane" ? 0 : kind == "hero_power" ? 1 : coin ? 7 : 8)}", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            $"tag=NUM_CARDS_IN_HAND value={(coin ? 1 : 0) + (body || kind == "arcane" ? 1 : 0)}",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Feed("FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE", $"tag=HEALTH value={(body ? 8 : 2)}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        if (coin) Feed("FULL_ENTITY - Creating ID=31 CardID=GAME_005", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=SPELL", "tag=COST value=0");
        if (kind == "arcane") Feed("FULL_ENTITY - Creating ID=32 CardID=CORE_DS1_185", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=SPELL", "tag=COST value=1");
        if (kind == "hero_power") Feed("FULL_ENTITY - Creating ID=12 CardID=HERO_05bp", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER", "tag=COST value=2", "tag=EXHAUSTED value=0");
        if (body)
        {
            Feed("FULL_ENTITY - Creating ID=30 CardID=DINO_421", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=8", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=TAUNT value=1", "tag=ELUSIVE value=1", "tag=DEATHRATTLE value=1");
            if (variant != "missing-cost") Feed("tag=COST value=8");
            if (variant != "missing-keyword") Feed("tag=CHARGE value=1");
            if (variant == "cant-attack-heroes") Feed("tag=CANT_ATTACK_HEROES value=1");
            Feed($"FULL_ENTITY - Creating ID=50 CardID={(variant == "unknown-gift" ? "EDR_100t5e" : "EDR_100t6e")}", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
        }
        if (variant == "secret") Feed("FULL_ENTITY - Creating ID=70 CardID=", "tag=CONTROLLER value=2", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1");
        if (variant == "raptor") Feed("FULL_ENTITY - Creating ID=60 CardID=EDR_849", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=0", "tag=HEALTH value=3", "tag=DAMAGE value=0", "tag=NUM_TURNS_IN_PLAY value=1");
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var frame = engine.Snapshot(); Check(frame.Quality.IsComplete && frame.Quality.IsStable && frame.Quality.HasLocalPerspective, "The real parser fixture was not a complete stable own frame."); return frame;
    }
    private static void Pin(MainWindow window, GameSnapshot frame)
    { Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", DateTimeOffset.UtcNow.AddMinutes(-1)); Set(window, "_liveFrame", frame); }
    private static LocalToolsWindow Tools(MainWindow window)
    { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Search(LocalToolsWindow tools)
    { Click(C<Button>(tools, "LocalLethalButton")); WaitUntil(() => !tools.HasPendingCalculation, "The actual bounded-search button did not settle."); }
    private static string Workspace()
    { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName; throw new DirectoryNotFoundException("Missing search-fixture workspace."); }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "The actual search button was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Search UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string error)
    {
        if (ready()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), error);
    }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "The hidden search check created a visible/native window.");
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Pass(string message) => Console.WriteLine("PASS lethal candidate UI: " + message);

    private sealed class SearchAdviceHandler : HttpMessageHandler
    {
        public const string Summary = "FIND_LETHAL_UI_COMBINED_VERIFIED";
        public int RequestCount { get; private set; }
        public JsonObject? Search { get; private set; }
        public bool ForceBlockedProposal { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++; var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var tool = json["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool");
            if (tool is null)
                return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id = "find_lethal_ui_search", type = "function", function = new { name = "find_lethal", arguments = "{\"maximum_depth\":4,\"maximum_nodes\":1500}" } } } } } },
                    usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            Search = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject();
            Check(Search["VerifiedLethalFound"]?.GetValue<bool>() == !ForceBlockedProposal, "The real model search returned an unexpected verification result.");
            var actions = ForceBlockedProposal
                ? new[] { new { kind = "play", source_entity_id = 30, target_entity_id = (int?)null }, new { kind = "attack", source_entity_id = 30, target_entity_id = (int?)21 } }
                : Search["Actions"]!.AsArray().Select(action => new { kind = action!["Kind"]!.ToString() switch { "PlayCard" => "play", "Attack" => "attack", "HeroPower" => "hero_power", _ => throw new InvalidOperationException("Unexpected search action.") },
                    source_entity_id = action["SourceEntityId"]!.GetValue<int>(), target_entity_id = action["TargetEntityId"]?.GetValue<int>() }).ToArray();
            var content = JsonSerializer.Serialize(new { summary = ForceBlockedProposal ? "FIND_LETHAL_UI_BLOCKED_PROPOSAL" : Summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions, verification = "verified", verified_lethal = true });
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
