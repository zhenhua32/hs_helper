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
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using HsHelper.Agent;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

internal static class BattlecryExecutionUiChecks
{
    private const string Build = "255790", Gift = "EDR_100t7e", Key = "fixture-battlecry-ui-key";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly (string Kind, int Source, int? Target)[] NativeSteps =
        [("play", 30, null), ("hero_power", 12, null), ("play", 31, null), ("attack", 31, 21)];
    private static readonly (string Kind, int Source, int? Target)[] ScriptSteps =
        [("play", 30, null), ("play", 31, null), ("attack", 31, 21)];

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "battlecry-execution-app", "data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var rejected = new RejectNetwork(); using var handler = new AdviceHandler();
        try
        {
            ReplaceClient(window, "_http", handler); ReplaceClient(window, "_cardHttp", rejected);
            var cards = Cards(); var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_BATTLECRY_BUILD"; Import(catalog, decoy, "999999");
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "EndpointBox").Text = "https://battlecry-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "battlecry-ui-fixture"; C<PasswordBox>(window, "KeyBox").Password = Key;
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://battlecry-ui.invalid/v1/chat/completions"), "battlecry-ui-fixture", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;

            var frame = Frame("native"); var original = SnapshotCodec.Serialize(frame); var cutoff = PinSaved(window, store, frame);
            Await(Invoke(window, "RenderBattleAsync")); var tools = Tools(window); Author(tools, NativeSteps); var native = Evaluate(tools);
            Exact(native, 30, 2); var state = native["Result"]!;
            Check(native["Verification"]!.ToString() == "Verified" && native["IsLethal"]!.GetValue<bool>() && native["AppliedActions"]!.GetValue<int>() == 4 &&
                state["AvailableMana"]!.GetValue<int>() == 0 && state["ImbueCountAfter"]!.GetValue<int>() == 4 &&
                state["HeroPowerChanges"]!.AsArray().Count == 2 && Entity(state, 31)["Attack"]!.GetValue<int>() == 5 &&
                Entity(state, 31)["Cost"]!.GetValue<int>() == 1 && Entity(state, 30)["Attack"]!.GetValue<int>() == 2 &&
                Entity(state, 30)["MaximumHealth"]!.GetValue<int>() == 3 && SnapshotCodec.Serialize(frame) == original,
                "The actual single-play native route did not preserve two Imbues, one payment and the current Wolf/Boar values.");
            Check(C<ListBox>(tools, "LocalActionsList").Items.Count == 4 && tools.ResultText.Contains("计划次数 2；已开始 2；已完成 2；尚未完成 0", StringComparison.Ordinal) &&
                tools.ResultText.Contains("不会再次出牌或支付", StringComparison.Ordinal) && tools.ResultText.Contains("固定卡库 255790", StringComparison.Ordinal) &&
                tools.ResultText.Contains("粗暴唤醒 #50", StringComparison.Ordinal) &&
                !tools.ResultText.Contains("WRONG_BATTLECRY_BUILD", StringComparison.Ordinal), "The desktop conflated internal activations with submitted actions or an active-build card record.");
            var search = Run(tools, "LocalLethalButton");
            File.WriteAllText(Path.Combine(directory, "native-search.json"), search.ToJsonString());
            Check(search["VerifiedLethalFound"]!.GetValue<bool>() && search["Actions"]!.AsArray().Count == 4 &&
                search["Actions"]!.AsArray().Count(action => action!["Kind"]!.ToString() == "PlayCard" && action["SourceEntityId"]!.GetValue<int>() == 30) == 1,
                "Actual lethal search duplicated the native play or missed the complete five-mana route: " + search.ToJsonString());
            Exact(search["Evaluation"]!, 30, 2);
            Check(!HasOption(C<ComboBox>(tools, "LocalTargetBox"), 31), "A planned hand minion was generally exposed as an attack target.");
            foreach (var scenario in new[] { "archer-shield", "archer-fizzle", "archer-self", "archer-game-end" })
            {
                var archer = Frame(scenario); PinSaved(window, store, archer); tools = Tools(window);
                var target = scenario == "archer-fizzle" ? 40 : scenario == "archer-self" ? 30 : scenario == "archer-game-end" ? 11 : 21;
                Author(tools, [("play", 30, target)]); var result = Evaluate(tools); var execution = Execution(result, 30);
                Check(result["AppliedActions"]!.GetValue<int>() == 1 && result["Result"]!["AvailableMana"]!.GetValue<int>() == 0,
                    "The Archer's internal repeat paid or played the source twice.");
                if (scenario == "archer-shield")
                { Exact(result, 30, 2); Check(result["IsLethal"]!.GetValue<bool>(), "Two separate fixed-target Archer damage packets did not consume shield before the lethal packet."); }
                else if (scenario is "archer-fizzle" or "archer-self")
                    Check(execution["StartedActivations"]!.GetValue<int>() == 2 && execution["CompletedActivations"]!.GetValue<int>() == 2 &&
                        execution["Activations"]!.AsArray()[1]!["Status"]!.ToString() == "Fizzled" && !result["IsLethal"]!.GetValue<bool>() &&
                        Entity(result["Result"]!, 21)["RemainingHealth"]!.GetValue<int>() == 30 && tools.ResultText.Contains("目标已失效", StringComparison.Ordinal),
                        "The second Archer activation retargeted after its original minion died.");
                else Check(execution["StartedActivations"]!.GetValue<int>() == 1 && execution["Activations"]!.AsArray().Count == 1 &&
                        !result["IsLethal"]!.GetValue<bool>(), "Own hero death started a second activation or became a verified enemy lethal.");
                if (scenario == "archer-self")
                {
                    Check(!result["Result"]!["Entities"]!.AsArray().Any(entity => entity!["EntityId"]!.GetValue<int>() == 30), "The one-health self-target Archer survived its actual first damage packet.");
                    C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1;
                    Check(!HasOption(C<ComboBox>(tools, "LocalTargetBox"), 30), "The prospective self Battlecry target remained an attack target after a kind change.");
                    tools.UpdateBinding(frame, Build, true); Await(tools.BindingReady);
                    Check(!HasOption(C<ComboBox>(tools, "LocalTargetBox"), 30), "The prospective Archer self target leaked into another binding.");
                }
            }
            Pass("real native play, Wolf power, Boar play and face attack resolve as four paid actions while fixed Archer targets consume separate shield packets, fizzle or stop at game end");

            var assembled = Frame("assembled"); PinSaved(window, store, assembled); tools = Tools(window); Author(tools, ScriptSteps); var script = Evaluate(tools);
            Exact(script, 30, 2); var effect = script["Result"]!["AssembledEffects"]!.AsArray().Single()!; var change = effect["Targets"]!.AsArray().Single()!;
            Check(script["Verification"]!.ToString() == "Verified" && script["IsLethal"]!.GetValue<bool>() && script["AppliedActions"]!.GetValue<int>() == 3 &&
                effect["ExecutedCount"]!.GetValue<int>() == 2 && change["EntityId"]!.GetValue<int>() == 31 && change["AttackBefore"]!.GetValue<int>() == 1 &&
                change["AttackAfter"]!.GetValue<int>() == 3 && change["MaximumHealthAfter"]!.GetValue<int>() == 3 &&
                Entity(script["Result"]!, 30)["Attack"]!.GetValue<int>() == 5 && Entity(script["Result"]!, 30)["MaximumHealth"]!.GetValue<int>() == 4 &&
                script["Result"]!["DiscoveryBoundaryReached"]?.GetValue<bool>() != true && tools.ResultText.Contains("本次模拟执行次数 2", StringComparison.Ordinal),
                "The actual assembled play did not cumulatively buff the other hand minion twice without self-buff or native Discover.");
            var pair = Frame("assembled-pair"); PinSaved(window, store, pair); tools = Tools(window);
            Author(tools, [("play", 30, null), ("play", 32, null), ("play", 31, null), ("attack", 31, 21)]); var paired = Evaluate(tools);
            Exact(paired, 30, 2); Exact(paired, 32, 1);
            Check(paired["Verification"]!.ToString() == "Verified" && paired["IsLethal"]!.GetValue<bool>() && Entity(paired["Result"]!, 31)["Attack"]!.GetValue<int>() == 4 &&
                Entity(paired["Result"]!, 32)["Attack"]!.GetValue<int>() == 7 && Entity(paired["Result"]!, 30)["Attack"]!.GetValue<int>() == 5 &&
                paired["Result"]!["AssembledEffects"]!.AsArray().Select(item => item!["EffectCardId"]!.ToString()).SequenceEqual(["TIME_100", "CS3_025"]),
                "Two same-base sources shared their repeat attachment or per-entity donor identity.");
            Pass("one actual assembled play executes two cumulative hand buffs and preserves source exclusion and distinct same-CardId donor and gift bindings");

            foreach (var scenario in new[] { "global", "unsupported-gift", "mixed-gift", "duplicate-gift", "missing-cost" })
            {
                PinSaved(window, store, Frame(scenario)); tools = Tools(window); Author(tools, NativeSteps); var denied = Evaluate(tools);
                Check(denied["Verification"]!.ToString() != "Verified" && !denied["IsLethal"]!.GetValue<bool>() && !tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                    "An unproved repeat or current parameter became an exact lethal: " + scenario);
            }
            foreach (var scenario in new[] { "inactive", "silenced", "ordinary" })
            {
                PinSaved(window, store, Frame(scenario)); tools = Tools(window); Author(tools, [("play", 30, null)]); var result = Evaluate(tools);
                Exact(result, 30, scenario == "ordinary" ? 1 : 0);
                Check(result["AppliedActions"]!.GetValue<int>() == 1 && result["Result"]!["AvailableMana"]!.GetValue<int>() == 3 &&
                    result["Result"]!["ImbueCountAfter"]!.GetValue<int>() == (scenario == "ordinary" ? 3 : 2),
                    "A zero or ordinary activation count changed the single source payment or fabricated an extra Imbue.");
            }
            PinSaved(window, store, Frame("quest")); tools = Tools(window); Author(tools, [("play", 30, null)]); var quest = Evaluate(tools); Exact(quest, 30, 2);
            Check(quest["Result"]!["QuestProgressPreviews"]!.AsArray().Single()!["Increment"]!.GetValue<int>() == 1 &&
                quest["Result"]!["QuestProgressPreviews"]!.AsArray().Single()!["ResultingProgress"]!.GetValue<int>() == 1,
                "Two Battlecries were recorded as two successful Beast plays for the side quest.");
            PinSaved(window, store, Frame("raptor")); tools = Tools(window); Author(tools, NativeSteps); var raptor = Evaluate(tools); Exact(raptor, 30, 2);
            Check(raptor["AppliedActions"]!.GetValue<int>() == 1 && raptor["Result"]!["RaptorBoundaryReached"]!.GetValue<bool>() &&
                raptor["Result"]!["RaptorPreviews"]!.AsArray().Single()!["TriggerCount"]!["Maximum"]!.GetValue<int>() == 1 && !raptor["IsLethal"]!.GetValue<bool>(),
                "Internal Battlecries doubled an after-play Raptor trigger or bypassed its random boundary.");
            foreach (var direct in new[] { (Scenario: "direct-draw", Button: "LocalPreviewDrawButton"),
                (Scenario: "direct-generation", Button: "LocalPreviewGenerationButton"), (Scenario: "direct-discovery", Button: "LocalPreviewPlayTriggersButton") })
            {
                PinSaved(window, store, Frame(direct.Scenario)); tools = Tools(window); C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 0; Select(C<ComboBox>(tools, "LocalSourceBox"), 30);
                var result = Run(tools, direct.Button); var preview = direct.Scenario == "direct-discovery" ? result["Discovery"]! : result;
                Check(preview["IsSingleBattlecryActivationPreview"]!.GetValue<bool>() && preview["BattlecryMultiplicity"]!["Activations"]!.GetValue<int>() == 2 &&
                    tools.ResultText.Contains("以下仅预览一轮战吼，不是整张牌多次战吼的合计结果", StringComparison.Ordinal) &&
                    tools.ResultText.Contains("战吼计划次数：2", StringComparison.Ordinal), "A direct draw, generation or discovery preview was mislabeled as the total repeated result.");
            }
            var missing = (JsonObject)script.DeepClone(); var missingExecution = (JsonObject)Execution(missing, 30);
            foreach (var key in new[] { "PlannedActivations", "StartedActivations", "CompletedActivations", "RemainingActivations" }) missingExecution.Remove(key);
            Check(LocalToolResultFormatter.Format("evaluate_line", missing).Contains("计划次数 未知；已开始 未知；已完成 未知；尚未完成 未知", StringComparison.Ordinal),
                "Missing legacy execution fields became observed zero counts.");
            Pass("unknown global or gift modifiers and missing observations remain boundaries while disabled and ordinary Battlecries pay once and independent Quest and Raptor triggers count one play");

            PinSaved(window, store, frame); Set(window, "_historyObservedAt", cutoff); tools = Tools(window); Author(tools, NativeSteps); Evaluate(tools);
            handler.Prepare("ATOMIC_BATTLECRY_UI_COMPLETE"); Await(Invoke(window, "AnalyzeCurrentAsync"));
            var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == frame.MatchId);
            Check(saved.Advice.Verification == "verified" && saved.Advice.VerifiedLethal && saved.Advice.Actions.Count == 4 &&
                saved.Advice.KnowledgeAsOf == cutoff && saved.Advice.EventSequence == frame.EventSequence && saved.Advice.CardBuild == Build &&
                saved.Advice.Evidence.Any(item => item.ToolName == "evaluate_line" && item.Summary.Contains("计划/开始/完成 2/2/2", StringComparison.Ordinal)) &&
                C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal),
                "Final host, main advice or the durable archive lost atomic counts, actions or the original saved-decision cutoff.");
            Exact(handler.LastEvaluation!, 30, 2);
            var requests = Await(traces.ListRequestsAsync(frame.MatchId)); var toolTraces = Await(traces.ListToolsAsync(frame.MatchId));
            Check(requests.Count == 2 && requests.Sum(request => request.ChargedTokens) == 20 &&
                requests.All(request => request.StateVersion == frame.StateVersion && request.EventSequence == frame.EventSequence) &&
                toolTraces.Any(trace => trace.ToolName == "evaluate_line" && saved.Advice.Evidence.Any(evidence => evidence.ResultHash == trace.ResultHash)),
                "The real model usage or result-hash trace was not retained alongside the atomic host result.");
            var reopened = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(reopened.InitializeAsync()); var same = Await(reopened.GetAsync(saved.Id))!;
            Check(same.Advice.Actions.SequenceEqual(saved.Advice.Actions) && same.Advice.Evidence.SequenceEqual(saved.Advice.Evidence), "Archive reopening changed the actual atomic result evidence.");
            var browser = new AdviceArchiveWindow(reopened, new(frame.MatchId, "合成双战吼", AdviceArchiveScopeKind.Session));
            try { Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0; Check(C<TextBox>(browser, "ArchiveDetailText").Text.Contains("计划/开始/完成 2/2/2", StringComparison.Ordinal), "Actual archive rendering omitted the retained execution-count evidence."); Passive(browser); }
            finally { browser.Close(); }
            var bad = new CardCatalog(Path.Combine(directory, "bad-cards.db")); Await(bad.InitializeAsync()); var badCards = (JsonArray)cards.DeepClone(); badCards.Single(card => card!["id"]!.ToString() == Gift)!["dbfId"] = 1; Import(bad, badCards, Build);
            (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", bad);
            try
            {
                var denied = Frame("bad-definition"); PinSaved(window, store, denied); tools = Tools(window); Author(tools, NativeSteps); var badLine = Evaluate(tools);
                Check(badLine["Verification"]!.ToString() == "Unsupported" && badLine["AppliedActions"]!.GetValue<int>() == 0 && !badLine["IsLethal"]!.GetValue<bool>(), "Bad gift metadata passed the actual payment boundary.");
                handler.Prepare("ATOMIC_BATTLECRY_UI_BAD_DEFINITION"); Await(Invoke(window, "AnalyzeCurrentAsync"));
                var badSaved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == denied.MatchId);
                Check(badSaved.Advice.Verification == "unsupported" && badSaved.Advice.Actions.Count == 0 && !badSaved.Advice.VerifiedLethal &&
                    !C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal), "The model forged a verified archived lethal from unaudited multiplicity metadata.");
            }
            finally { (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", catalog); }
            foreach (var close in new[] { false, true }) foreach (var fail in new[] { false, true })
            {
                var old = Frame("late-" + close + fail); var pending = new LocalToolsWindow(old, Build, catalog, true); var completed = false; string? replacement = null;
                try
                {
                    Await(pending.BindingReady); Author(pending, NativeSteps);
                    Set(Field(pending, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
                    {
                        Check(observation.Success && Execution(JsonNode.Parse(observation.ResultJson!)!, 30)["CompletedActivations"]!.GetValue<int>() == 2, "The stale gate did not follow a completed real atomic calculation.");
                        if (close) pending.Close(); else pending.UpdateBinding(old with { Entities = old.Entities.ToFrozenDictionary(pair => pair.Key,
                            pair => pair.Key == 50 ? pair.Value with { CardId = "EDR_100t7e1" } : pair.Value) }, Build, true);
                        replacement = pending.ResultText; completed = true; if (fail) throw new IOException("OLD_BATTLECRY_EXECUTION");
                    }));
                    Click(C<Button>(pending, "LocalEvaluateButton")); WaitUntil(() => completed && !pending.HasPendingCalculation, "An old atomic result did not settle across lifecycle change.");
                    if (!close) Await(pending.BindingReady); Barrier();
                    Check(pending.ResultText == replacement && !pending.ResultText.Contains("计划次数 2", StringComparison.Ordinal) &&
                        !pending.ResultText.Contains("OLD_BATTLECRY_EXECUTION", StringComparison.Ordinal), "An old calculation or failure replaced different gift evidence under the same keys or a closed calculator."); Passive(pending);
                }
                finally { if (!close || !completed) pending.Close(); }
            }
            PinSaved(window, store, frame); tools = Tools(window); Author(tools, NativeSteps); Evaluate(tools); Layout(tools, directory);
            Check(rejected.Calls == 0, "A fixed atomic UI test downloaded card data."); Passive(window);
            Pass("real fake-HTTP analysis final host trace and reopened archive retain one-play atomic evidence while same-key gift changes closing and late failures cannot publish stale results and finite controls remain reachable");
        }
        finally { Set(window, "_allowClose", true); window.Close(); launch.SetValue(null, previous); }
    }

    private static GameSnapshot Frame(string scenario)
    {
        var script = scenario.StartsWith("assembled", StringComparison.Ordinal) || scenario == "quest"; var pair = scenario == "assembled-pair";
        var archer = scenario.StartsWith("archer", StringComparison.Ordinal); var includeBoar = !archer;
        var engine = new PowerLogStateEngine(1, "battlecry-ui-" + scenario);
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=HERO_POWER_ENTITY value=12",
            $"tag=RESOURCES value={(archer ? 1 : pair ? 9 : 5)}", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=3527 value=2",
            $"tag=NUM_CARDS_IN_HAND value={(includeBoar ? 2 : 1) + (pair ? 1 : 0)}",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Body(11, "HERO_05", 1, "HERO", "PLAY", 0, scenario == "archer-game-end" ? 1 : 30); Feed("tag=CLASS value=HUNTER");
        Body(21, "HERO_08", 2, "HERO", "PLAY", 0, scenario == "archer-shield" ? 1 : archer ? 30 : pair ? 4 : script ? 3 : 5); Feed("tag=CLASS value=MAGE");
        if (scenario == "archer-shield") Feed("tag=DIVINE_SHIELD value=1");
        Feed("FULL_ENTITY - Creating ID=12 CardID=EDR_850p", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER",
            "tag=COST value=2", "tag=EXHAUSTED value=0", "tag=TAG_SCRIPT_DATA_NUM_1 value=2", "tag=TAG_SCRIPT_DATA_NUM_2 value=2");
        var sourceCard = archer ? "CS2_189" : scenario switch { "quest" => "FIR_953", "direct-draw" => "FIR_921", "direct-generation" => "CORE_UNG_912", "direct-discovery" => "JAIL_831", _ => script ? "EDR_102" : "EDR_852" };
        Body(30, sourceCard, 1, "MINION", "HAND", archer ? 1 : script ? 5 : 2, archer ? 1 : script ? 4 : 3);
        if (scenario != "missing-cost") Feed($"tag=COST value={(archer ? 1 : script ? 4 : 2)}"); Feed($"tag=BATTLECRY value={(scenario == "inactive" ? 0 : 1)}");
        if (scenario == "silenced") Feed("tag=SILENCED value=1");
        if (scenario == "quest") Feed("tag=CARDRACE value=BEAST");
        if (script) Feed($"tag=4572 value={(scenario == "quest" ? 113822 : 0)}", "tag=4573 value=121051");
        if (includeBoar) { Body(31, "CS2_171", 1, "MINION", "HAND", 1, 1); Feed($"tag=COST value={(script ? 1 : 5)}", "tag=CHARGE value=1", "tag=CARDRACE value=BEAST"); }
        if (pair) { Body(32, "EDR_102", 1, "MINION", "HAND", 5, 4); Feed("tag=COST value=4", "tag=BATTLECRY value=1", "tag=4572 value=0", "tag=4573 value=66858"); }
        if (scenario != "ordinary") Enchantment(50, scenario == "unsupported-gift" ? "EDR_100t7e1" : Gift);
        if (scenario == "mixed-gift") Enchantment(51, "EDR_100t5e");
        if (scenario == "duplicate-gift") Enchantment(51, Gift);
        if (scenario == "global") Feed("TAG_CHANGE Entity=2 tag=1112 value=1");
        if (scenario == "archer-fizzle") Body(40, "CS2_172", 2, "MINION", "PLAY", 1, 1);
        if (scenario == "quest") Feed("FULL_ENTITY - Creating ID=70 CardID=TLC_EVENT_400", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SIDE_QUEST value=1", "tag=QUEST_PROGRESS value=0", "tag=QUEST_PROGRESS_TOTAL value=3");
        if (scenario == "raptor") { Body(60, "EDR_849", 1, "MINION", "PLAY", 1, 5); Feed("tag=CARDRACE value=BEAST", "tag=TRIGGER_VISUAL value=1"); }
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var result = engine.Snapshot(); Check(result.Quality.IsStable && result.Quality.IsComplete, "The real atomic Battlecry fixture did not settle."); return result;
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        void Body(int id, string card, int owner, string type, string zone, int attack, int health) => Feed($"FULL_ENTITY - Creating ID={id} CardID={card}",
            $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}", $"tag=CARDTYPE value={type}", $"tag=ATK value={attack}", $"tag=HEALTH value={health}",
            "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=FROZEN value=0", "tag=EXHAUSTED value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=DIVINE_SHIELD value=0", "tag=DEATHRATTLE value=0", "tag=REBORN value=0");
        void Enchantment(int id, string card) => Feed($"FULL_ENTITY - Creating ID={id} CardID={card}", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
    }

    private static JsonArray Cards()
    {
        var result = new Dictionary<string, JsonNode>();
        foreach (var name in new[] { "battlecry-repeat", "assembled-scripts", "native-definitions", "lethal-search", "dark-gift-static", "terror-end-turn", "quest-progress" })
            foreach (var card in JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")))!["cards"]!.AsArray())
                result.TryAdd(card!["id"]!.ToString(), card.DeepClone());
        return new JsonArray(result.Values.OrderBy(card => card["id"]!.ToString(), StringComparer.Ordinal).ToArray());
    }
    private static void Exact(JsonNode line, int source, int count)
    {
        var execution = Execution(line, source);
        Check(execution["CardBuild"]!.ToString() == Build && execution["ActionOrdinal"]!.GetValue<int>() >= 1 && execution["IsMultiplicityResolved"]!.GetValue<bool>() &&
            execution["PlannedActivations"]!.GetValue<int>() == count && execution["StartedActivations"]!.GetValue<int>() == count && execution["CompletedActivations"]!.GetValue<int>() == count &&
            execution["RemainingActivations"]!.GetValue<int>() == 0 && execution["IsComplete"]!.GetValue<bool>() && execution["Activations"]!.AsArray().Count == count,
            "The actual atomic execution lost its exact planned/started/completed count and one-based action binding: " + execution.ToJsonString());
    }
    private static JsonNode Execution(JsonNode line, int id) => line["Result"]!["BattlecryExecutions"]!.AsArray().Single(item => item!["SourceEntityId"]!.GetValue<int>() == id)!;
    private static JsonNode Entity(JsonNode state, int id) => state["Entities"]!.AsArray().Single(item => item!["EntityId"]!.GetValue<int>() == id)!;
    private static void Author(LocalToolsWindow tools, IEnumerable<(string Kind, int Source, int? Target)> steps)
    { foreach (var step in steps) { C<ComboBox>(tools, "LocalKindBox").SelectedIndex = step.Kind switch { "attack" => 1, "hero_power" => 2, _ => 0 }; Select(C<ComboBox>(tools, "LocalSourceBox"), step.Source); Select(C<ComboBox>(tools, "LocalTargetBox"), step.Target ?? 0); Click(C<Button>(tools, "LocalAddActionButton")); } }
    private static JsonObject Evaluate(LocalToolsWindow tools) => Run(tools, "LocalEvaluateButton");
    private static JsonObject Run(LocalToolsWindow tools, string button)
    { JsonObject? result = null; Set(Field(tools, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation => result = JsonNode.Parse(observation.ResultJson!)!.AsObject())); Click(C<Button>(tools, button)); WaitUntil(() => result is not null && !tools.HasPendingCalculation, "An actual atomic calculation did not settle."); return result!; }
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame)
    { var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync()); var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", point.RecordedAt); Set(window, "_liveFrame", frame); return point.RecordedAt; }
    private static void Layout(LocalToolsWindow tools, string directory)
    {
        var root = (FrameworkElement)tools.Content; var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml")); var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString()); root.Resources.MergedDictionaries.Add(styles);
        try
        {
            var size = new Size(640, 460); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); Barrier(); root.UpdateLayout(); var outer = (ScrollViewer)root; var presenter = VisualWalk(outer).OfType<ScrollContentPresenter>().First();
            foreach (var name in new[] { "LocalSourceBox", "LocalAddActionButton", "LocalEvaluateButton" }) { var control = C<FrameworkElement>(tools, name); control.BringIntoView(); Barrier(); root.UpdateLayout(); var rect = control.TransformToAncestor(presenter).TransformBounds(new Rect(control.RenderSize)); Check(rect.Top >= -1 && rect.Bottom <= presenter.ActualHeight + 1, "An atomic action control is unreachable."); }
            var result = C<TextBox>(tools, "LocalResultBox"); var index = result.Text.IndexOf("战吼逐次结算", StringComparison.Ordinal); Check(index >= 0, "No actual execution proof was present.");
            var evidence = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(index)); outer.ScrollToVerticalOffset(outer.VerticalOffset + evidence.Top - 12); Barrier(); root.UpdateLayout();
            evidence = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(index)); Check(!evidence.IsEmpty && evidence.Top >= -1 && evidence.Bottom <= presenter.ActualHeight + 1, "Atomic evidence is not reachable."); Save("provenance");
            outer.ScrollToEnd(); result.ScrollToEnd(); Barrier(); root.UpdateLayout(); var last = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(result.Text.Length - 1)); Save("end");
            Check(!last.IsEmpty && last.Height > 0 && last.Top >= -1 && last.Bottom <= presenter.ActualHeight + 1, "The last atomic result line cannot be reached.");
            Console.WriteLine($"Atomic battlecry layout: viewport={outer.ViewportHeight}; extent={outer.ExtentHeight}; offset={outer.VerticalOffset}; lastLine={last}; PNG={directory}");
            void Save(string suffix) { var bitmap = new RenderTargetBitmap(640, 460, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual(); using (var draw = background.RenderOpen()) draw.DrawRectangle(Brushes.White, null, new Rect(size)); bitmap.Render(background); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, "battlecry-execution-640x460-" + suffix + ".png")); png.Save(file); }
        }
        finally { root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Import(CardCatalog catalog, JsonArray cards, string build) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://atomic-battlecry-ui")); }
    private static string Workspace() { for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent) if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName; throw new FileNotFoundException("Atomic Battlecry public fixtures missing."); }
    private static T C<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root) { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in VisualWalk(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static void Select(ComboBox box, int id) => box.SelectedItem = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static bool HasOption(ComboBox box, int id) => box.Items.Cast<object>().Any(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An atomic control is disabled: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void ReplaceClient(MainWindow window, string name, HttpMessageHandler handler) { ((HttpClient)Field(window, name)!).Dispose(); Set(window, name, new HttpClient(handler, false)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "An atomic regression showed a native window.");
    private static void Barrier() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Atomic UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string message) { if (ready()) return; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || clock.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), message); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS atomic battlecry UI: " + message);
    private sealed class RejectNetwork : HttpMessageHandler { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; throw new InvalidOperationException("No card HTTP is allowed."); } }
    private sealed class AdviceHandler : HttpMessageHandler
    {
        public string Summary { get; private set; } = ""; public JsonObject? LastEvaluation { get; private set; }
        public void Prepare(string summary) { Summary = summary; LastEvaluation = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; var tool = body["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool");
            var actions = NativeSteps.Select(step => new { kind = step.Kind, source_entity_id = step.Source, target_entity_id = step.Target }).ToArray();
            if (tool is null) return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", tool_calls = new[] { new { id = "atomic_battlecry_ui_line", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            LastEvaluation = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = Summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions, verification = "verified", verified_lethal = true }) } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
