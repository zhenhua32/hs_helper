using System.Collections.Frozen;
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

internal static class DeathrattleExecutionUiChecks
{
    private const string Build = "255790", Multiplier = "EDR_531e", DrawGift = "EDR_100t4e", Key = "fixture-deathrattle-execution-key";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly (string Kind, int Source, int? Target)[] NativeSteps =
        [("attack", 30, 40), ("hero_power", 12, null), ("play", 31, null), ("attack", 31, 21)];
    private static readonly (string Kind, int Source, int? Target)[] DinoSteps =
        [("attack", 30, 40), ("play", 31, null), ("attack", 31, 21)];

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "deathrattle-execution-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var rejected = new RejectNetwork(); using var handler = new AdviceHandler();
        try
        {
            ReplaceClient(window, "_http", handler); ReplaceClient(window, "_cardHttp", rejected);
            var cards = Cards(); var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_DEATHRATTLE_EXECUTION_BUILD"; Import(catalog, decoy, "999999");
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "EndpointBox").Text = "https://deathrattle-execution.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "deathrattle-execution-fixture"; C<PasswordBox>(window, "KeyBox").Password = Key;
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://deathrattle-execution.invalid/v1/chat/completions"), "deathrattle-execution-fixture", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;

            var frame = Frame("native"); var original = SnapshotCodec.Serialize(frame); var cutoff = PinSaved(window, store, frame);
            var eventsBefore = JsonSerializer.Serialize(Await(store.GetEventsAtAsync(frame.MatchId, frame.EventSequence, 2000)));
            Await(Invoke(window, "RenderBattleAsync")); var view = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
            Check(view.Local.Board.Single(card => card.EntityId == 30) is { Attack: 7, Health: 2 } &&
                view.Local.Board.Single(card => card.EntityId == 30).Status.Contains("亡语", StringComparison.Ordinal), "The real board did not retain current body attack and observed Deathrattle.");
            var tools = Tools(window); Author(tools, NativeSteps); var native = Evaluate(tools); Exact(native, "NativeImbue", 2); var state = native["Result"]!;
            Check(native["Verification"]!.ToString() == "Verified" && native["IsLethal"]!.GetValue<bool>() && native["AppliedActions"]!.GetValue<int>() == 4 &&
                state["AvailableMana"]!.GetValue<int>() == 0 && state["ImbueCountAfter"]!.GetValue<int>() == 4 && state["HeroPowerChanges"]!.AsArray().Count == 2 &&
                Entity(state, 40)["RemainingHealth"]!.GetValue<int>() == 1 && Entity(state, 31)["Attack"]!.GetValue<int>() == 5 && Entity(state, 31)["Cost"]!.GetValue<int>() == 1 &&
                state["QuestProgressPreviews"]!.AsArray().Single()!["PlayedEntityId"]!.GetValue<int>() == 31 && state["QuestProgressPreviews"]!.AsArray().Single()!["Increment"]!.GetValue<int>() == 1,
                "One actual death did not resolve two Imbues and the current Wolf/Boar chain, or the existing +2 Attack was added again.");
            Check(tools.ResultText.Contains("一次模拟死亡", StringComparison.Ordinal) && tools.ResultText.Contains("计划次数 2；已开始 2；已完成 2；尚未完成 0", StringComparison.Ordinal) &&
                tools.ResultText.Contains("虹吸生长 #51", StringComparison.Ordinal) && tools.ResultText.Contains("集合，不是触发顺序", StringComparison.Ordinal) &&
                !tools.ResultText.Contains("WRONG_DEATHRATTLE_EXECUTION_BUILD", StringComparison.Ordinal), "The real calculator confused a death batch, an activation count or the fixed multiplier source.");
            var search = Run(tools, "LocalLethalButton"); File.WriteAllText(Path.Combine(directory, "native-search.json"), search.ToJsonString());
            Check(search["VerifiedLethalFound"]!.GetValue<bool>() && search["Actions"]!.AsArray().Count == 4 &&
                search["Actions"]!.AsArray()[0]!["Kind"]!.ToString() == "Attack" && search["Actions"]!.AsArray()[0]!["TargetEntityId"]!.GetValue<int>() == 40,
                "Actual search bypassed CANT_ATTACK_HEROES or missed the death-first native route."); Exact(search["Evaluation"]!, "NativeImbue", 2);
            var dino = Frame("dino"); PinSaved(window, store, dino); tools = Tools(window); Author(tools, DinoSteps); var buff = Evaluate(tools); Exact(buff, "NativeHandDeckBuff", 2);
            Check(buff["Verification"]!.ToString() == "Verified" && buff["IsLethal"]!.GetValue<bool>() && buff["AppliedActions"]!.GetValue<int>() == 3 &&
                Entity(buff["Result"]!, 40)["RemainingHealth"]!.GetValue<int>() == 1 && Entity(buff["Result"]!, 31)["Attack"]!.GetValue<int>() == 7 &&
                Entity(buff["Result"]!, 31)["MaximumHealth"]!.GetValue<int>() == 7 && Entity(buff["Result"]!, 31)["Cost"]!.GetValue<int>() == 1 &&
                buff["Result"]!["Entities"]!.AsArray().All(entity => dino.Entities.ContainsKey(entity!["EntityId"]!.GetValue<int>())) &&
                dino.Entities[30].Attack == 10 && dino.Entities.Values.All(entity => entity.ControllerId != 1 || entity.Zone != "DECK"),
                "Dino's two native scripts did not cumulatively apply +6/+6 to the observed hand, or fabricated a hidden deck entity or another +2 Attack.");
            var dinoSearch = Run(tools, "LocalLethalButton"); Check(dinoSearch["VerifiedLethalFound"]!.GetValue<bool>() && dinoSearch["Actions"]!.AsArray().Count == 3 &&
                dinoSearch["Actions"]!.AsArray()[0]!["TargetEntityId"]!.GetValue<int>() == 40, "Dino search used its forbidden direct face attack.");
            Check(SnapshotCodec.Serialize(frame) == original && JsonSerializer.Serialize(Await(store.GetEventsAtAsync(frame.MatchId, frame.EventSequence, 2000))) == eventsBefore,
                "Candidate death execution mutated the observed body, snapshot or actual event history.");
            Pass("actual attack-first native and Dino controls plus search resolve one death with two scripts and unchanged current attack while only the real Boar play advances the quest");

            foreach (var scenario in new[] { "draw-full", "draw-opponent-turn", "draw-fatigue", "draw-immune", "draw-attachment" })
            {
                var drawFrame = Frame(scenario); var before = SnapshotCodec.Serialize(drawFrame); PinSaved(window, store, drawFrame); tools = Tools(window); var draw = Preview(tools);
                Check(draw["DeathrattleMultiplicity"]!["Activations"]!.GetValue<int>() == 2 && draw["IsSingleDeathrattleActivationPreview"]!.GetValue<bool>() &&
                    draw["TriggerEntityId"]!.GetValue<int>() == 30 && draw["EnchantmentEntityId"]!.GetValue<int>() == 50,
                    "A real direct button confused acquired effect, body and multiplier identity."); Number(draw, "RequestedDraws", 2);
                if (scenario is "draw-full" or "draw-opponent-turn" or "draw-attachment") { Number(draw, "CardsBurned", 2); Number(draw, "CardsAddedToHand", 0); Number(draw, "FatigueDamage", 0); }
                else { Number(draw, "CardsAddedToHand", 1); Number(draw, "FatigueDamage", scenario == "draw-immune" ? 0 : 3); Number(draw, "HeroHealthAfter", scenario == "draw-immune" ? 20 : 19); }
                var expectedMultiplier = scenario == "draw-attachment" ? 51 : 30;
                Check(draw["DeathrattleMultiplicity"]!["EffectSourceEntityId"]!.GetValue<int>() == 50 &&
                    draw["DeathrattleMultiplicity"]!["IsAcquiredEffect"]!.GetValue<bool>() &&
                    draw["DeathrattleMultiplicity"]!["MultiplierSourceEntityIds"]!.AsArray().Single()!.GetValue<int>() == expectedMultiplier &&
                    tools.ResultText.Contains("假设该随从死亡一次", StringComparison.Ordinal) && tools.ResultText.Contains("一轮亡语", StringComparison.Ordinal) &&
                    !tools.ResultText.Contains("抽取次数 4", StringComparison.Ordinal) &&
                    tools.ResultText.Contains("不能乘次数", StringComparison.Ordinal) && SnapshotCodec.Serialize(drawFrame) == before && drawFrame.Entities[30].Zone == "PLAY",
                    "Direct preview claimed four cards complete, applied the multiplier's stat gain, or executed its hypothetical death.");
            }
            var drawLineFrame = Frame("draw-line"); PinSaved(window, store, drawLineFrame); tools = Tools(window); Author(tools, [("attack", 30, 40), ("attack", 32, 21)]); var drawLine = Evaluate(tools);
            var partial = Execution(drawLine, "ObservedEnchantmentDraw");
            Check(drawLine["Verification"]!.ToString() != "Verified" && !drawLine["IsLethal"]!.GetValue<bool>() && drawLine["AppliedActions"]!.GetValue<int>() == 1 &&
                drawLine["Result"]!["DrawBoundaryReached"]!.GetValue<bool>() && partial["StartedActivations"]!.GetValue<int>() == 1 && partial["CompletedActivations"]!.GetValue<int>() == 0 &&
                partial["RemainingActivations"]!.GetValue<int>() == 2 && !partial["IsComplete"]!.GetValue<bool>() && drawLine["Result"]!["DrawPreviews"]!.AsArray().Count == 1 &&
                !drawLine["Result"]!["Entities"]!.AsArray().Any(entity => entity!["Zone"]!.ToString() == "HAND") && Entity(drawLine["Result"]!, 11)["RemainingHealth"]!.GetValue<int>() == 20,
                "The first unknown drawn identity completed both scripts, created a hand entity or allowed the following lethal attack.");
            Pass("actual own-PLAY and opponent-turn draw buttons distinguish one death and one direct draw round from two planned activations while the real exchange stops at the first new-card boundary");

            foreach (var scenario in new[] { "global", "duplicate", "reborn" })
            {
                PinSaved(window, store, Frame(scenario)); tools = Tools(window); Author(tools, NativeSteps); var denied = Evaluate(tools);
                Check(denied["Verification"]!.ToString() != "Verified" && !denied["IsLethal"]!.GetValue<bool>(), "Unproven global stacking or Reborn became complete repeated Deathrattles: " + scenario);
            }
            foreach (var scenario in new[] { "disabled", "silenced" })
            {
                PinSaved(window, store, Frame(scenario)); tools = Tools(window); Author(tools, [("attack", 30, 40)]); var zero = Evaluate(tools);
                Check(zero["Result"]!["HeroPowerChanges"]!.AsArray().Count == 0 && zero["Result"]!["ImbueCountAfter"]!.GetValue<int>() == 2 &&
                    !tools.ResultText.Contains("计划次数 2；已开始 2；已完成 2", StringComparison.Ordinal), "Explicit suppression or silence executed a native death script.");
            }
            PinSaved(window, store, Frame("private")); tools = Tools(window); C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1;
            Check(!HasOption(C<ComboBox>(tools, "LocalSourceBox"), 30) && !C<Button>(tools, "LocalDeathrattleDrawButton").IsEnabled &&
                !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(Multiplier) &&
                !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(DrawGift), "A private target's multiplier/effect became an actionable source or a name-preload proof.");
            var missing = (JsonObject)native.DeepClone(); var missingExecution = (JsonObject)Execution(missing, "NativeImbue");
            foreach (var key in new[] { "PlannedActivations", "StartedActivations", "CompletedActivations", "RemainingActivations" }) missingExecution.Remove(key);
            Check(LocalToolResultFormatter.Format("evaluate_line", missing).Contains("计划次数 未知；已开始 未知；已完成 未知；尚未完成 未知", StringComparison.Ordinal), "Missing legacy DR fields became zero execution facts.");
            var unknownOrigin = (JsonObject)native.DeepClone(); var unknownExecution = (JsonObject)Execution(unknownOrigin, "NativeImbue");
            unknownExecution["EffectKind"] = "Unknown"; unknownExecution["IsAcquiredEffect"] = false;
            var unknownText = LocalToolResultFormatter.Format("evaluate_line", unknownOrigin);
            Check(unknownText.Contains("是否为获得的亡语：未知", StringComparison.Ordinal) && !unknownText.Contains("是否为获得的亡语：否", StringComparison.Ordinal),
                "An unresolved effect reused a native-scope false boolean as proof that no acquired script existed.");
            Pass("global duplicate Reborn suppression private attachments and missing legacy evidence preserve their exact unknown or zero scope rather than fabricating completed scripts");

            PinSaved(window, store, frame); Set(window, "_historyObservedAt", cutoff); tools = Tools(window); Author(tools, NativeSteps); Evaluate(tools);
            handler.Prepare("DEATHRATTLE_EXECUTION_UI_COMPLETE"); Await(Invoke(window, "AnalyzeCurrentAsync"));
            var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == frame.MatchId);
            Check(saved.Advice.Verification == "verified" && saved.Advice.VerifiedLethal && saved.Advice.Actions.Count == 4 && saved.Advice.KnowledgeAsOf == cutoff &&
                saved.Advice.StateVersion == frame.StateVersion && saved.Advice.EventSequence == frame.EventSequence && saved.Advice.CardBuild == Build &&
                saved.Advice.Evidence.Any(evidence => evidence.ToolName == "evaluate_line" && evidence.Summary.Contains("计划/开始/完成 2/2/2", StringComparison.Ordinal) && evidence.Summary.Contains("不代表实际死亡或最终采纳路线", StringComparison.Ordinal)) &&
                C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal), "Final host, main advice or durable evidence promoted a candidate death to actual history or lost the verified route.");
            Exact(handler.LastEvaluation!, "NativeImbue", 2);
            var requests = Await(traces.ListRequestsAsync(frame.MatchId)); var toolTraces = Await(traces.ListToolsAsync(frame.MatchId));
            Check(requests.Count == 2 && requests.Sum(request => request.ChargedTokens) == 20 && toolTraces.Any(trace =>
                trace.ToolName == "evaluate_line" && saved.Advice.Evidence.Any(evidence => evidence.ResultHash == trace.ResultHash)), "Real fake-HTTP usage or tool hashes were not retained.");
            var reopened = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(reopened.InitializeAsync()); var same = Await(reopened.GetAsync(saved.Id))!;
            Check(same.Advice.Actions.SequenceEqual(saved.Advice.Actions) && same.Advice.Evidence.SequenceEqual(saved.Advice.Evidence), "Reopening changed the candidate DR evidence.");
            var browser = new AdviceArchiveWindow(reopened, new(frame.MatchId, "合成亡语逐次结算", AdviceArchiveScopeKind.Session));
            try { Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0; var text = C<TextBox>(browser, "ArchiveDetailText").Text;
                Check(text.Contains("计划/开始/完成 2/2/2", StringComparison.Ordinal) && text.Contains("不代表实际死亡或最终采纳路线", StringComparison.Ordinal), "Actual archive rendering confused a simulated death and actual history."); Passive(browser); }
            finally { browser.Close(); }
            Check(JsonSerializer.Serialize(Await(store.GetEventsAtAsync(frame.MatchId, frame.EventSequence, 2000))) == eventsBefore && SnapshotCodec.Serialize(frame) == original,
                "Model or archive publication inserted predicted deaths into recorded game history.");
            var bad = new CardCatalog(Path.Combine(directory, "bad-cards.db")); Await(bad.InitializeAsync()); var badCards = (JsonArray)cards.DeepClone();
            badCards.Single(card => card!["id"]!.ToString() == Multiplier)!["dbfId"] = 1; Import(bad, badCards, Build);
            (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", bad);
            try
            {
                var denied = Frame("bad-definition"); PinSaved(window, store, denied); tools = Tools(window); Author(tools, NativeSteps); var badLine = Evaluate(tools);
                Check(badLine["Verification"]!.ToString() != "Verified" && !badLine["IsLethal"]!.GetValue<bool>(), "Wrong multiplier metadata became a verified route.");
                handler.Prepare("DEATHRATTLE_EXECUTION_UI_BAD_DEFINITION"); Await(Invoke(window, "AnalyzeCurrentAsync"));
                var badSaved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == denied.MatchId);
                Check(badSaved.Advice.Verification != "verified" && badSaved.Advice.Actions.Count == 0 && !badSaved.Advice.VerifiedLethal, "The model forged verified archived lethal through bad DR metadata.");
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
                        Check(observation.Success && Execution(JsonNode.Parse(observation.ResultJson!)!, "NativeImbue")["CompletedActivations"]!.GetValue<int>() == 2, "The lifecycle gate did not follow a completed actual DR calculation.");
                        if (close) pending.Close(); else { var entities = old.Entities.Where(pair => pair.Key != 51).ToDictionary(pair => pair.Key, pair => pair.Value); entities[52] = old.Entities[51] with { Id = 52 }; pending.UpdateBinding(old with { Entities = entities.ToFrozenDictionary() }, Build, true); }
                        replacement = pending.ResultText; completed = true; if (fail) throw new IOException("OLD_DEATHRATTLE_EXECUTION");
                    }));
                    Click(C<Button>(pending, "LocalEvaluateButton")); WaitUntil(() => completed && !pending.HasPendingCalculation, "A completed DR result did not settle across lifecycle change.");
                    if (!close) Await(pending.BindingReady); Barrier();
                    Check(pending.ResultText == replacement && !pending.ResultText.Contains("#51", StringComparison.Ordinal) && !pending.ResultText.Contains("OLD_DEATHRATTLE_EXECUTION", StringComparison.Ordinal),
                        "An old source identity or error survived a same-key multiplier replacement or close."); Passive(pending);
                }
                finally { if (!close || !completed) pending.Close(); }
            }
            PinSaved(window, store, frame); tools = Tools(window); Author(tools, NativeSteps); Evaluate(tools); Layout(tools, directory);
            Check(rejected.Calls == 0, "A fixed DR UI test downloaded card data."); Passive(window);
            Pass("real fake-HTTP final host main reopened archive and traces retain candidate-only DR evidence without changing actual history while same-key source changes close and late errors cannot publish stale results");
        }
        finally { Set(window, "_allowClose", true); window.Close(); launch.SetValue(null, previous); }
    }

    private static GameSnapshot Frame(string scenario)
    {
        var dino = scenario == "dino"; var draw = scenario.StartsWith("draw-", StringComparison.Ordinal); var privateBody = scenario == "private";
        var full = scenario is "draw-full" or "draw-opponent-turn" or "draw-attachment"; var line = scenario == "draw-line";
        var hand = draw ? line ? 0 : full ? 10 : 9 : privateBody ? 0 : 1; var deck = draw ? full ? 2 : 1 : 0;
        var engine = new PowerLogStateEngine(1, "deathrattle-execution-ui-" + scenario);
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", $"tag=CURRENT_PLAYER value={(scenario == "draw-opponent-turn" ? 0 : 1)}", "tag=HERO_ENTITY value=11", "tag=HERO_POWER_ENTITY value=12",
            $"tag=RESOURCES value={(draw || privateBody ? 0 : dino ? 1 : 3)}", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=3527 value=2",
            $"tag=NUM_CARDS_IN_HAND value={hand}", $"tag=NUM_CARDS_IN_DECK value={deck}", "tag=MAXHANDSIZE value=10", "tag=FATIGUE value=2",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", $"tag=CURRENT_PLAYER value={(scenario == "draw-opponent-turn" ? 1 : 0)}", "tag=HERO_ENTITY value=21");
        Body(11, "HERO_05", 1, "HERO", "PLAY", 0, 30, draw ? 10 : 0); Feed("tag=CLASS value=HUNTER", $"tag=ARMOR value={(draw ? 2 : 0)}", $"tag=IMMUNE value={(scenario == "draw-immune" ? 1 : 0)}");
        Body(21, "HERO_08", 2, "HERO", "PLAY", 0, line ? 3 : draw || privateBody ? 30 : dino ? 7 : 5); Feed("tag=CLASS value=MAGE");
        Feed("FULL_ENTITY - Creating ID=12 CardID=EDR_850p", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER", "tag=COST value=2", "tag=EXHAUSTED value=0", "tag=TAG_SCRIPT_DATA_NUM_1 value=2", "tag=TAG_SCRIPT_DATA_NUM_2 value=2");
        var card = privateBody ? "RLK_912" : draw ? scenario == "draw-attachment" ? "EDR_852" : "RLK_912" : dino ? "DINO_421" : "EDR_227";
        Body(30, card, privateBody ? 2 : 1, "MINION", privateBody ? "HAND" : "PLAY", draw || privateBody ? scenario == "draw-attachment" ? 4 : 1 : dino ? 10 : 7,
            draw || privateBody ? 3 : dino ? 8 : 2, line ? 2 : dino ? 7 : 0);
        Feed($"tag=DEATHRATTLE value={(scenario == "disabled" ? 0 : 1)}", "tag=CANT_ATTACK_HEROES value=1");
        if (card == "EDR_227") Feed("tag=RUSH value=1", "tag=CARDRACE value=BEAST");
        if (dino) Feed("tag=TAUNT value=1", "tag=ELUSIVE value=1", "tag=CARDRACE value=BEAST");
        if (card == "RLK_912") Feed("tag=CARDRACE value=UNDEAD", "tag=EXTRA_DEATHRATTLES_BASE value=1");
        if (scenario == "silenced") Feed("tag=SILENCED value=1"); if (scenario == "reborn") Feed("tag=REBORN value=1");
        if (!draw && !privateBody || scenario == "draw-attachment") Enchantment(51, Multiplier, 1);
        if (scenario == "duplicate") Enchantment(52, Multiplier, 1);
        if (draw || privateBody) Enchantment(50, DrawGift, privateBody ? 2 : 1);
        if (privateBody) Enchantment(51, Multiplier, 2);
        if (scenario == "global") Feed("TAG_CHANGE Entity=2 tag=1131 value=1");
        if (draw)
        {
            for (var index = 0; index < hand; index++) Feed($"FULL_ENTITY - Creating ID={100 + index} CardID=", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION");
            for (var index = 0; index < deck; index++) Feed($"FULL_ENTITY - Creating ID={200 + index} CardID=", "tag=CONTROLLER value=1", "tag=ZONE value=DECK");
            if (line) { Body(40, "CS2_172", 2, "MINION", "PLAY", 1, 5); Body(32, "CS2_171", 1, "MINION", "PLAY", 3, 1); Feed("tag=CHARGE value=1"); }
        }
        else if (!privateBody)
        {
            Body(40, "CS2_172", 2, "MINION", "PLAY", dino ? 1 : 2, dino ? 11 : 8);
            Body(31, "CS2_171", 1, "MINION", "HAND", 1, 1); Feed($"tag=COST value={(dino ? 1 : 5)}", "tag=CHARGE value=1", "tag=CARDRACE value=BEAST");
            Feed("FULL_ENTITY - Creating ID=70 CardID=TLC_EVENT_400", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SIDE_QUEST value=1", "tag=QUEST_PROGRESS value=0", "tag=QUEST_PROGRESS_TOTAL value=3");
        }
        Feed("TAG_CHANGE Entity=1 tag=TURN value=5", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var result = engine.Snapshot(); Check(result.Quality.IsStable && result.Quality.IsComplete, "The real DR fixture did not settle."); return result;
        void Feed(params string[] lines) { foreach (var text in lines) engine.ProcessLine(text); }
        void Enchantment(int id, string source, int owner) => Feed($"FULL_ENTITY - Creating ID={id} CardID={source}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
        void Body(int id, string source, int owner, string type, string zone, int attack, int health, int damage = 0) => Feed($"FULL_ENTITY - Creating ID={id} CardID={source}",
            $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}", $"tag=CARDTYPE value={type}", $"tag=ATK value={attack}", $"tag=HEALTH value={health}", $"tag=DAMAGE value={damage}",
            "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=FROZEN value=0", "tag=EXHAUSTED value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=DIVINE_SHIELD value=0", "tag=DEATHRATTLE value=0", "tag=REBORN value=0");
    }

    private static JsonArray Cards()
    {
        var result = new Dictionary<string, JsonNode>(); foreach (var name in new[] { "deathrattle-repeat", "deathrattle-draw", "native-definitions", "beast-hunter", "lethal-search", "quest-progress" })
            foreach (var card in JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")))!["cards"]!.AsArray()) result.TryAdd(card!["id"]!.ToString(), card.DeepClone());
        return new JsonArray(result.Values.OrderBy(card => card["id"]!.ToString(), StringComparer.Ordinal).ToArray());
    }
    private static void Exact(JsonNode line, string kind, int count)
    {
        var execution = Execution(line, kind); Check(execution["ActionOrdinal"]!.GetValue<int>() == 1 && execution["DeathBatchOrdinal"]!.GetValue<int>() == 1 &&
            execution["DeathBatchEntityIds"]!.AsArray().Select(value => value!.GetValue<int>()).SequenceEqual([30]) && execution["SourceEntityId"]!.GetValue<int>() == 30 &&
            execution["EffectSourceEntityId"]!.GetValue<int>() == 30 && execution["EffectEnchantmentEntityId"] is null && !execution["IsAcquiredEffect"]!.GetValue<bool>() &&
            execution["EffectSourceCardId"]!.ToString() == execution["SourceCardId"]!.ToString() && execution["MultiplierSourceEntityIds"]!.AsArray().Single()!.GetValue<int>() == 51 &&
            execution["CardBuild"]!.ToString() == Build && execution["IsMultiplicityResolved"]!.GetValue<bool>() && execution["PlannedActivations"]!.GetValue<int>() == count &&
            execution["StartedActivations"]!.GetValue<int>() == count && execution["CompletedActivations"]!.GetValue<int>() == count && execution["RemainingActivations"]!.GetValue<int>() == 0 &&
            execution["IsComplete"]!.GetValue<bool>() && execution["Activations"]!.AsArray().Count == count, "The real native DR result lost its one-death/two-activation provenance: " + execution.ToJsonString());
    }
    private static JsonNode Execution(JsonNode line, string kind) => line["Result"]!["DeathrattleExecutions"]!.AsArray().Single(execution => execution!["SourceEntityId"]!.GetValue<int>() == 30 && execution["EffectKind"]!.ToString() == kind)!;
    private static JsonNode Entity(JsonNode state, int id) => state["Entities"]!.AsArray().Single(entity => entity!["EntityId"]!.GetValue<int>() == id)!;
    private static void Number(JsonNode value, string key, int expected) => Check(value[key]!["Minimum"]!.GetValue<int>() == expected && value[key]!["Maximum"]!.GetValue<int>() == expected, key + " was not the exact single-round value.");
    private static void Author(LocalToolsWindow tools, IEnumerable<(string Kind, int Source, int? Target)> steps)
    { foreach (var step in steps) { C<ComboBox>(tools, "LocalKindBox").SelectedIndex = step.Kind switch { "attack" => 1, "hero_power" => 2, _ => 0 }; Select(C<ComboBox>(tools, "LocalSourceBox"), step.Source); Select(C<ComboBox>(tools, "LocalTargetBox"), step.Target ?? 0); Click(C<Button>(tools, "LocalAddActionButton")); } }
    private static JsonObject Preview(LocalToolsWindow tools) { C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1; Select(C<ComboBox>(tools, "LocalSourceBox"), 30); return Run(tools, "LocalDeathrattleDrawButton"); }
    private static JsonObject Evaluate(LocalToolsWindow tools) => Run(tools, "LocalEvaluateButton");
    private static JsonObject Run(LocalToolsWindow tools, string name) { JsonObject? result = null; Set(Field(tools, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation => result = JsonNode.Parse(observation.ResultJson!)!.AsObject())); Click(C<Button>(tools, name)); WaitUntil(() => result is not null && !tools.HasPendingCalculation, "An actual DR calculation did not settle."); return result!; }
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame)
    { var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync()); var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", point.RecordedAt); Set(window, "_liveFrame", frame); return point.RecordedAt; }
    private static void Layout(LocalToolsWindow tools, string directory)
    {
        var root = (FrameworkElement)tools.Content; var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml")); var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString()); root.Resources.MergedDictionaries.Add(styles);
        try
        {
            var size = new Size(640, 460); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); Barrier(); root.UpdateLayout(); var outer = (ScrollViewer)root; var presenter = VisualWalk(outer).OfType<ScrollContentPresenter>().First();
            foreach (var name in new[] { "LocalSourceBox", "LocalAddActionButton", "LocalEvaluateButton" }) { var control = C<FrameworkElement>(tools, name); control.BringIntoView(); Barrier(); root.UpdateLayout(); var rect = control.TransformToAncestor(presenter).TransformBounds(new Rect(control.RenderSize)); Check(rect.Top >= -1 && rect.Bottom <= presenter.ActualHeight + 1, "A DR action control is unreachable."); }
            var result = C<TextBox>(tools, "LocalResultBox"); var index = result.Text.IndexOf("亡语逐次结算", StringComparison.Ordinal); Check(index >= 0, "No actual DR evidence was available."); var evidence = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(index));
            outer.ScrollToVerticalOffset(outer.VerticalOffset + evidence.Top - 12); Barrier(); root.UpdateLayout();
            evidence = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(index));
            Check(!evidence.IsEmpty && evidence.Top >= -1 && evidence.Bottom <= presenter.ActualHeight + 1, "The actual DR proof heading is unreachable at finite size.");
            Save("provenance"); outer.ScrollToEnd(); result.ScrollToEnd(); Barrier(); root.UpdateLayout();
            var last = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(result.Text.Length - 1)); Save("end"); Check(!last.IsEmpty && last.Height > 0 && last.Top >= -1 && last.Bottom <= presenter.ActualHeight + 1, "The final DR result line is clipped at finite size.");
            Console.WriteLine($"Deathrattle execution layout: viewport={outer.ViewportHeight}; extent={outer.ExtentHeight}; offset={outer.VerticalOffset}; lastLine={last}; PNG={directory}");
            void Save(string suffix) { var bitmap = new RenderTargetBitmap(640, 460, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual(); using (var draw = background.RenderOpen()) draw.DrawRectangle(Brushes.White, null, new Rect(size)); bitmap.Render(background); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, "deathrattle-execution-640x460-" + suffix + ".png")); png.Save(file); }
        }
        finally { root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Import(CardCatalog catalog, JsonArray cards, string build) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://observed-deathrattle-execution-ui")); }
    private static string Workspace() { for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent) if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName; throw new FileNotFoundException("Deathrattle execution fixtures missing."); }
    private static T C<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root) { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in VisualWalk(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static void Select(ComboBox box, int id) => box.SelectedItem = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static bool HasOption(ComboBox box, int id) => box.Items.Cast<object>().Any(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "A DR control is disabled: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void ReplaceClient(MainWindow window, string name, HttpMessageHandler handler) { ((HttpClient)Field(window, name)!).Dispose(); Set(window, name, new HttpClient(handler, false)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A DR regression showed a native window.");
    private static void Barrier() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "DR UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string message) { if (ready()) return; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || clock.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), message); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS repeated deathrattle UI: " + message);
    private sealed class RejectNetwork : HttpMessageHandler { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; throw new InvalidOperationException("No card HTTP is allowed."); } }
    private sealed class AdviceHandler : HttpMessageHandler
    {
        public string Summary { get; private set; } = ""; public JsonObject? LastEvaluation { get; private set; }
        public void Prepare(string summary) { Summary = summary; LastEvaluation = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; var tool = body["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool"); var actions = NativeSteps.Select(step => new { kind = step.Kind, source_entity_id = step.Source, target_entity_id = step.Target }).ToArray();
            if (tool is null) return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", tool_calls = new[] { new { id = "repeated_deathrattle_ui_line", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            LastEvaluation = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject(); return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = Summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions, verification = "verified", verified_lethal = true }) } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
