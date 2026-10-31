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

internal static class StaticGiftExpansionUiChecks
{
    private const string Build = "255790";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Step(string Kind, int Source, int? Target = null);
    private sealed record StatsCase(string Gift, int Attack, int Health, int Cost, string Coverage);
    private static readonly Step[] DoubleAttack = [new("attack", 30, 40), new("attack", 30, 40)];

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "static-gift-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new AdviceHandler(); using var http = new HttpClient(handler);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            var cards = FixtureCards(); var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_ACTIVE_STATIC_GIFT"; Import(catalog, decoy, "999999");
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;
            C<TextBox>(window, "EndpointBox").Text = "https://static-gift-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "static-gift-ui"; C<PasswordBox>(window, "KeyBox").Password = "fixture-static-gift-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://static-gift-ui.invalid/v1/chat/completions"), "static-gift-ui", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });

            var shield = Frame("shield"); Pin(window, shield); var shieldTools = Tools(window); Author(shieldTools, DoubleAttack); Evaluate(shieldTools);
            Verified(shieldTools, 2);
            Check(shieldTools.ResultText.Contains("生命 10", StringComparison.Ordinal) && shieldTools.ResultText.Contains("生命 14", StringComparison.Ordinal) &&
                shieldTools.ResultText.Contains("dark-gift-observed-shield-windfury:EDR_100t13e", StringComparison.Ordinal) &&
                shieldTools.ResultText.Contains("dark-gift-observed-taunt:EDR_100t3e", StringComparison.Ordinal), "Multiple audited static gifts reapplied health or shield during the second Windfury attack: " + shieldTools.ResultText);
            var stats = new[]
            {
                new StatsCase("EDR_100t1e", 7, 4, 4, "elusive"), new("EDR_100t2e", 3, 2, 2, "short-claws"),
                new("EDR_100t3e", 5, 6, 4, "taunt"), new("EDR_100t8e1", 9, 7, 4, "sweet-dreams"),
                new("EDR_100t8e", 7, 6, 4, "shuffled-stats"), new("EDR_100t5e2", 1, 1, 4, "copy-stats"), new("EDR_100t5e5", 2, 2, 4, "copy-stats")
            };
            foreach (var current in stats)
            {
                var frame = Frame("stats", current: current); var before = SnapshotCodec.Serialize(frame); Pin(window, frame);
                var tools = Tools(window); Author(tools, [new("play", 30), new("attack", 30, 40)]); Evaluate(tools); Verified(tools, 2);
                Check(tools.ResultText.Contains($"攻击 {current.Attack} / 生命 {current.Health}", StringComparison.Ordinal) &&
                    tools.ResultText.Contains("模拟后可用法力：" + (10 - current.Cost), StringComparison.Ordinal) &&
                    tools.ResultText.Contains("dark-gift-observed-" + current.Coverage + ":" + current.Gift, StringComparison.Ordinal) &&
                    before == SnapshotCodec.Serialize(frame), "The actual play/attack path recomputed a settled stat, discount, shuffle or copy gift: " + current.Gift + "\n" + tools.ResultText);
            }
            Pass("actual candidate controls preserve all eight newly audited gifts; multiple static gifts share current values and a consumed shield is not restored for the second Windfury attack");

            var elusive = Frame("elusive"); Check(elusive.Entities[40].GetIntTag("ELUSIVE") == 1, "The actual numeric1211 parser path did not establish current Elusive.");
            Pin(window, elusive); Await(Invoke(window, "RenderBattleAsync"));
            var actualView = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
            Check(actualView.Opponent.Board.Single(card => card.EntityId == 40).Status.Split('、').Contains("扰魔"), "The actual battle binding omitted observed numeric1211 Elusive.");
            foreach (var restriction in new[] { "spell-only", "power-only", "both-only" })
            {
                Pin(window, Frame("elusive", restriction)); Await(Invoke(window, "RenderBattleAsync"));
                actualView = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
                var labels = actualView.Opponent.Board.Single(card => card.EntityId == 40).Status.Split('、');
                Check(!labels.Contains("扰魔") && labels.Count(label => label == "法术无法指定") == (restriction == "power-only" ? 0 : 1) &&
                    labels.Count(label => label == "英雄技能无法指定") == (restriction == "spell-only" ? 0 : 1) &&
                    !labels.Any(label => label.Contains("CANT_", StringComparison.Ordinal) || label.Contains("ELUSIVE", StringComparison.Ordinal)) &&
                    actualView.Opponent.Hand.Count == 0 && !actualView.Opponent.Board.Any(card => card.EntityId == 70),
                    "Separate target restrictions were conflated with Elusive, duplicated, rendered as technical tags or exposed from private hand data.");
            }
            Pin(window, Frame("elusive", "unconfirmed")); Await(Invoke(window, "RenderBattleAsync"));
            actualView = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
            Check(!actualView.HasLocalPerspective && actualView.Local.Hand.Count == 0 && actualView.Opponent.Hand.Count == 0, "Unknown perspective exposed private status-bearing hand entries.");
            Pin(window, elusive); var elusiveTools = Tools(window); Author(elusiveTools, [new("play", 32, 40)]); Evaluate(elusiveTools);
            Check(elusiveTools.ResultText.Contains("验证级别：无效", StringComparison.Ordinal), "An actual targeted spell bypassed observed Elusive: " + elusiveTools.ResultText);
            foreach (var action in new[] { new Step("attack", 30, 40), new("play", 35, 40) })
            {
                Pin(window, elusive); elusiveTools = Tools(window); Author(elusiveTools, [action]); Evaluate(elusiveTools); Verified(elusiveTools, 1);
                Check(!elusiveTools.ResultText.Contains("WRONG_ACTIVE_STATIC_GIFT", StringComparison.Ordinal), "The fixed-build target/source display used the active unrelated catalog.");
            }
            var taunt = Frame("taunt"); Pin(window, taunt); var tauntTools = Tools(window); Author(tauntTools, [new("attack", 30, 21)]); Evaluate(tauntTools);
            Check(tauntTools.ResultText.Contains("验证级别：无效", StringComparison.Ordinal), "The observed gifted Taunt did not block attacking another target.");
            Pin(window, taunt); tauntTools = Tools(window); Author(tauntTools, [new("attack", 30, 40)]); Evaluate(tauntTools); Verified(tauntTools, 1);
            Check(tauntTools.ResultText.Contains("dark-gift-observed-taunt:EDR_100t3e", StringComparison.Ordinal) &&
                !tauntTools.ResultText.Contains("WRONG_ACTIVE_STATIC_GIFT", StringComparison.Ordinal), "The actual Taunt target was not audited at the fixed source build.");
            Pin(window, Frame("taunt", "taunt-death")); tauntTools = Tools(window);
            Author(tauntTools, [new("attack", 30, 40), new("attack", 31, 21)]); Evaluate(tauntTools); Verified(tauntTools, 2);
            Check(tauntTools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal), "A successfully audited gifted Taunt's death left a stale enchantment boundary blocking the second actual attacker: " + tauntTools.ResultText);
            Pass("numeric1211 Elusive blocks the real spell route while attacks and the supported archer battlecry remain legal; gifted Taunt restricts actual attack targets at the pinned build");

            var point = PinSaved(window, store, shield); var beforeShield = SnapshotCodec.Serialize(shield); handler.Prepare(DoubleAttack, "STATIC_GIFT_UI_SHIELD");
            Await(Invoke(window, "AnalyzeCurrentAsync")); var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == shield.MatchId);
            var entities = handler.LastEvaluation!["Result"]!["Entities"]!.AsArray(); var actor = entities.Single(entity => entity!["EntityId"]!.GetValue<int>() == 30)!;
            Check(handler.LastEvaluation["Verification"]!.ToString() == "Verified" && actor["Attack"]!.GetValue<int>() == 8 && actor["MaximumHealth"]!.GetValue<int>() == 12 &&
                actor["RemainingHealth"]!.GetValue<int>() == 10 && actor["AttacksUsed"]!.GetValue<int>() == 2 && actor["Flags"]!["DIVINE_SHIELD"]!.GetValue<int>() == 0 &&
                entities.Single(entity => entity!["EntityId"]!.GetValue<int>() == 40)!["RemainingHealth"]!.GetValue<int>() == 14 &&
                saved.Advice.Verification == "verified" && !saved.Advice.VerifiedLethal && saved.Advice.Actions.Count == 2 && saved.Advice.CardBuild == Build &&
                saved.Advice.KnowledgeAsOf == point && saved.Advice.StateVersion == shield.StateVersion && saved.Advice.EventSequence == shield.EventSequence &&
                C<TextBox>(window, "AdviceBox").Text.Contains("动作已通过本地规则验证", StringComparison.Ordinal) && beforeShield == SnapshotCodec.Serialize(shield),
                "Final real host validation or archive projection restored a shield, reapplied health, lost the exact frame or trusted the model's forged lethal claim.");
            ArchiveRender(archive, saved);
            foreach (var variant in new[] { "ongoing", "missing-cost", "bad-definition" })
            {
                var frame = variant == "missing-cost" ? Frame("stats", variant, new("EDR_100t2e", 3, 2, 2, "short-claws")) : Frame("shield", variant);
                if (variant == "bad-definition")
                {
                    var tampered = (JsonArray)cards.DeepClone(); tampered.Single(card => card!["id"]!.ToString() == "EDR_100t13e")!["dbfId"] = 1;
                    var badCatalog = new CardCatalog(Path.Combine(directory, "bad-source-cards.db")); Await(badCatalog.InitializeAsync()); Import(badCatalog, tampered, Build);
                    (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", badCatalog);
                }
                PinSaved(window, store, frame); var steps = variant == "missing-cost" ? new[] { new Step("play", 30) } : DoubleAttack;
                var tools = Tools(window); Author(tools, steps); Evaluate(tools);
                Check(!tools.ResultText.Contains("验证级别：已验证", StringComparison.Ordinal) && !tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                    "An ongoing gift, missing current cost or wrong fixed definition was upgraded by the actual calculator: " + variant);
                handler.Prepare(steps, "STATIC_GIFT_UI_DENIED_" + variant); Await(Invoke(window, "AnalyzeCurrentAsync"));
                var denied = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == frame.MatchId);
                Check(denied.Advice.Verification != "verified" && denied.Advice.Actions.Count == 0 && !denied.Advice.VerifiedLethal &&
                    C<TextBox>(window, "AdviceBox").Text.Contains("具体动作已移除", StringComparison.Ordinal) &&
                    !C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal), "A provider's forged verified/lethal fields bypassed final host limitations: " + variant);
                ArchiveRender(archive, denied);
            }
            (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", catalog);
            Pass("real Analyze fake HTTP passes through final host/main/archive; exact consumed shield and aggregate health are retained, and ongoing/missing/bad-definition cases cannot forge verified actions or lethal");

            foreach (var close in new[] { false, true })
            {
                var frame = Frame("shield", close ? "late-close" : "late-rebind"); var tools = new LocalToolsWindow(frame, Build, catalog, true);
                var completed = false; string? replacement = null;
                try
                {
                    Await(tools.BindingReady); Author(tools, DoubleAttack); var coach = Field(tools, "_coach")!;
                    Set(coach, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
                    {
                        Check(observation.Success && JsonNode.Parse(observation.ResultJson!)!["Verification"]!.ToString() == "Verified", "The late gate did not follow real static-gift verification.");
                        if (close) tools.Close(); else tools.UpdateBinding(Frame("taunt", "replacement"), Build, true);
                        replacement = tools.ResultText; completed = true;
                    }));
                    Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => completed && !tools.HasPendingCalculation, "The post-real-evaluation lifecycle gate did not complete.");
                    Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task); if (!close) Await(tools.BindingReady);
                    Check(tools.ResultText == replacement && !tools.ResultText.Contains("验证级别：已验证", StringComparison.Ordinal) &&
                        (close ? !tools.IsBindingValid : tools.BoundMatchId != frame.MatchId), "An old verified static-gift result crossed a rebind/close boundary."); Passive(tools);
                }
                finally { if (!close || !completed) tools.Close(); }
            }
            Passive(window);
            Pass("a completed real static-gift evaluation cannot publish over a replacement frame or closed hidden calculator");
        }
        finally { window.Close(); launch.SetValue(null, previous); }
    }

    private static JsonArray FixtureCards()
    {
        var wanted = new HashSet<string>(["DINO_421", "EDR_227", "CS2_172", "CS2_189", "CORE_DS1_185", "HERO_05", "HERO_08", "EDR_100t1e", "EDR_100t2e", "EDR_100t3e", "EDR_100t8e", "EDR_100t8e1", "EDR_100t13e", "EDR_100t5e", "EDR_100t5e2", "EDR_100t5e5"], StringComparer.Ordinal);
        var cards = new Dictionary<string, JsonNode>();
        foreach (var fixture in new[] { "dark-gift-static-255790.json", "beast-hunter-255790.json", "quest-progress-255790.json", "terror-end-turn-255790.json" })
        {
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", fixture)))!;
            Check(source["build"]!.ToString() == Build, "The UI fixture was not bound to fixed 255790.");
            foreach (var card in source["cards"]!.AsArray()) if (wanted.Contains(card!["id"]!.ToString())) cards.TryAdd(card["id"]!.ToString(), card.DeepClone());
        }
        Check(wanted.SetEquals(cards.Keys), "Expanded fixed static-gift source fixture is incomplete."); return new(cards.Values.ToArray());
    }
    private static GameSnapshot Frame(string kind, string? variant = null, StatsCase? current = null)
    {
        var engine = new PowerLogStateEngine(variant == "unconfirmed" ? null : 1, "static-gift-ui-" + kind + "-" + (current?.Gift ?? "board") + "-" + (variant ?? "positive"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", $"tag=NUM_CARDS_IN_HAND value={(kind == "stats" ? 1 : kind == "elusive" ? 2 : 0)}",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Feed("FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE", $"tag=HEALTH value={(variant == "taunt-death" ? 3 : 30)}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        if (kind == "stats")
        {
            Feed("FULL_ENTITY - Creating ID=30 CardID=EDR_227", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", $"tag=ATK value={current!.Attack}", $"tag=HEALTH value={current.Health}", "tag=DAMAGE value=0", "tag=RUSH value=1", "tag=DEATHRATTLE value=1");
            if (variant != "missing-cost") Feed($"tag=COST value={current.Cost}");
            if (current.Gift == "EDR_100t1e") Feed("tag=1211 value=1");
            if (current.Gift == "EDR_100t3e") Feed("tag=TAUNT value=1");
        }
        else
        {
            Feed($"FULL_ENTITY - Creating ID=30 CardID={(kind == "shield" ? "DINO_421" : "CS2_172")}", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", $"tag=ATK value={(kind == "shield" ? 8 : variant == "taunt-death" ? 6 : 3)}", $"tag=HEALTH value={(kind == "shield" ? 12 : 8)}", "tag=DAMAGE value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=EXHAUSTED value=0", "tag=NUM_ATTACKS_THIS_TURN value=0");
            if (kind == "shield") Feed("tag=194 value=1", "tag=189 value=1", "tag=TAUNT value=1", "tag=ELUSIVE value=1", "tag=DEATHRATTLE value=1");
        }
        if (variant == "taunt-death") Feed("FULL_ENTITY - Creating ID=31 CardID=CS2_172", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=3", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=EXHAUSTED value=0", "tag=NUM_ATTACKS_THIS_TURN value=0");
        Feed("FULL_ENTITY - Creating ID=40 CardID=CS2_172", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", $"tag=ATK value={(kind == "shield" ? 2 : kind == "elusive" ? 5 : 0)}", $"tag=HEALTH value={(kind == "elusive" ? 4 : variant == "taunt-death" ? 6 : 30)}", "tag=DAMAGE value=0");
        if (kind == "elusive")
        {
            if (variant is "spell-only" or "both-only") Feed("tag=311 value=1", "tag=CANT_BE_TARGETED_BY_ABILITIES value=1");
            if (variant is "power-only" or "both-only") Feed("tag=332 value=1");
            if (variant is not ("spell-only" or "power-only" or "both-only")) Feed("tag=1211 value=1");
            Feed("FULL_ENTITY - Creating ID=32 CardID=CORE_DS1_185", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=SPELL", "tag=COST value=1",
                "FULL_ENTITY - Creating ID=35 CardID=CS2_189", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=COST value=1", "tag=ATK value=1", "tag=HEALTH value=1", "tag=DAMAGE value=0", "tag=BATTLECRY value=1");
            Feed("FULL_ENTITY - Creating ID=70 CardID=CS2_172", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=1211 value=1", "tag=311 value=1", "tag=332 value=1");
        }
        if (kind == "taunt") Feed("tag=TAUNT value=1");
        void Enchant(int id, string gift, int owner, int target) => Feed($"FULL_ENTITY - Creating ID={id} CardID={gift}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", $"tag=ATTACHED value={target}");
        if (kind == "shield") { Enchant(50, "EDR_100t13e", 1, 30); Enchant(51, "EDR_100t3e", 1, 30); if (variant == "ongoing") Enchant(52, "EDR_100t5e", 1, 30); }
        if (kind == "stats") Enchant(50, current!.Gift, 1, 30);
        if (kind is "elusive" or "taunt") Enchant(50, kind == "elusive" ? "EDR_100t1e" : "EDR_100t3e", 2, 40);
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var frame = engine.Snapshot();
        Check(variant == "unconfirmed" ? !frame.Quality.HasLocalPerspective && !frame.Quality.IsComplete && !frame.Quality.IsStable :
            frame.Quality.IsComplete && frame.Quality.IsStable && frame.Quality.HasLocalPerspective, "The actual parser did not produce the expected perspective quality.");
        return frame;
    }
    private static void Import(CardCatalog catalog, JsonArray cards, string build) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://static-gift-ui")); }
    private static void Pin(MainWindow window, GameSnapshot frame) { Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", DateTimeOffset.UtcNow.AddMinutes(-1)); Set(window, "_liveFrame", frame); }
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame) { var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync()); var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Pin(window, frame); Set(window, "_historyObservedAt", point.RecordedAt); return point.RecordedAt; }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Author(LocalToolsWindow tools, IReadOnlyList<Step> steps)
    {
        foreach (var step in steps)
        {
            C<ComboBox>(tools, "LocalKindBox").SelectedIndex = step.Kind == "play" ? 0 : 1;
            Select(C<ComboBox>(tools, "LocalSourceBox"), step.Source); Select(C<ComboBox>(tools, "LocalTargetBox"), step.Target ?? 0); Click(C<Button>(tools, "LocalAddActionButton"));
        }
        Check(C<ListBox>(tools, "LocalActionsList").Items.Count == steps.Count, "Actual controls failed to author the complete candidate line.");
    }
    private static void Select(ComboBox box, int id) => box.SelectedItem = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static void Evaluate(LocalToolsWindow tools) { Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => !tools.HasPendingCalculation, "Actual local evaluation did not settle."); }
    private static void Verified(LocalToolsWindow tools, int count) => Check(tools.ResultText.Contains("验证级别：已验证", StringComparison.Ordinal) && tools.ResultText.Contains("已应用动作：" + count, StringComparison.Ordinal), "Actual static-gift candidate line was not verified: " + tools.ResultText);
    private static void ArchiveRender(AdviceArchiveStore store, ArchivedAdviceRecord record)
    {
        var browser = new AdviceArchiveWindow(store, new(record.Advice.MatchId, "合成静态附魔", AdviceArchiveScopeKind.Session));
        try { Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0; var text = C<TextBox>(browser, "ArchiveDetailText").Text;
            Check(browser.SelectedRecord?.Id == record.Id && text.Contains(record.Advice.Summary, StringComparison.Ordinal) && text.Contains("原验证状态：" + record.Advice.Verification, StringComparison.Ordinal) && text.Contains(Build, StringComparison.Ordinal), "Actual archive rendering lost final host fields or source build."); Passive(browser); }
        finally { browser.Close(); }
    }
    private static string Workspace() { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "AGENTS.md"))) return path.FullName; throw new DirectoryNotFoundException("Missing static-gift fixture workspace."); }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An actual static-gift UI action was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Static-gift UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string message) { if (ready()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), message); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A static-gift check displayed or created a native window.");
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Pass(string message) => Console.WriteLine("PASS expanded static gift UI: " + message);

    private sealed class AdviceHandler : HttpMessageHandler
    {
        private Step[] _steps = []; private string _summary = "";
        public JsonObject? LastEvaluation { get; private set; }
        public void Prepare(IReadOnlyList<Step> steps, string summary) { _steps = steps.ToArray(); _summary = summary; LastEvaluation = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var tool = body["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool");
            var actions = _steps.Select(step => new { kind = step.Kind, source_entity_id = step.Source, target_entity_id = step.Target }).ToArray();
            if (tool is null) return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                tool_calls = new[] { new { id = "static_gift_ui_evaluate", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            LastEvaluation = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject();
            var content = JsonSerializer.Serialize(new { summary = _summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions, verification = "verified", verified_lethal = true });
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
