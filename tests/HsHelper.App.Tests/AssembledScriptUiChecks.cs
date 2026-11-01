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

internal static class AssembledScriptUiChecks
{
    private const string Build = "255790", Script = "Battlecry.HandMinionBuff.1.1.v1";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly (string Kind, int Source, int? Target)[] Steps = [("play", 30, null), ("play", 31, null), ("attack", 31, 21)];
    private static readonly (string Kind, int Source, int? Target)[] DualSteps = [("play", 30, null), ("play", 32, null), ("play", 31, null), ("attack", 31, 21)];

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "assembled-script-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null); launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new AdviceHandler(); using var rejected = new RejectNetwork();
        try
        {
            ReplaceClient(window, "_http", handler); ReplaceClient(window, "_cardHttp", rejected);
            var cards = Cards(); var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_ASSEMBLED_BUILD"; Import(catalog, decoy, "999999");
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "EndpointBox").Text = "https://assembled-script.invalid/v1/chat/completions"; C<TextBox>(window, "ModelBox").Text = "assembled-script-fixture"; C<PasswordBox>(window, "KeyBox").Password = "fixture-assembled-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://assembled-script.invalid/v1/chat/completions"), "assembled-script-fixture", 2) { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;
            var frame = Frame("complete"); var before = SnapshotCodec.Serialize(frame); var cutoff = PinSaved(window, store, frame); Await(Invoke(window, "RenderBattleAsync"));
            var row = ((BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext).Local.Hand.Single(card => card.EntityId == 30);
            Check(row.IsAssembled && row.EffectiveText is not null && row.EffectiveText.Contains("+1/+1", StringComparison.Ordinal) && !row.EffectiveText.Contains("发现", StringComparison.Ordinal) &&
                row.Cost == 4 && row.Attack == 5 && row.Health == 4 && !row.Status.Contains("圣盾", StringComparison.Ordinal) &&
                !row.Status.Contains("嘲讽", StringComparison.Ordinal) && !row.Status.Contains("复生", StringComparison.Ordinal), "The real battle row used native Discover, donor/base mechanics, a donor statistic or an unpinned build.");
            var tools = Tools(window); C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 0; Select(C<ComboBox>(tools, "LocalSourceBox"), 30);
            Click(C<Button>(tools, "LocalInspectEffectButton")); WaitUntil(() => !tools.HasPendingCalculation && tools.ResultText.Contains("可读文字：", StringComparison.Ordinal), "The actual effect inspector did not settle.");
            Check(tools.ResultText.Contains("+1/+1", StringComparison.Ordinal) && !tools.ResultText.Contains("发现", StringComparison.Ordinal), "The inspector did not use this entity's observed halves.");
            Author(tools); var line = Evaluate(tools); var proof = Proof(line); var changes = proof["Targets"]!.AsArray();
            Check(line["Verification"]!.ToString() == "Verified" && line["IsLethal"]!.GetValue<bool>() && line["AppliedActions"]!.GetValue<int>() == 3 &&
                proof["SourceEntityId"]!.GetValue<int>() == 30 && proof["BaseCardId"]!.ToString() == "EDR_102" && proof["ScriptKey"]!.ToString() == Script &&
                proof["FirstHalfDbfId"]!.GetValue<int>() == 0 && proof["SecondHalfDbfId"]!.GetValue<int>() == 121051 && proof["EffectCardId"]!.ToString() == "TIME_100" &&
                proof["IsTriggerActive"]!.GetValue<bool>() && proof["ExecutedCount"]!.GetValue<int>() == 1 && changes.Count == 1 && changes[0]!["EntityId"]!.GetValue<int>() == 31 &&
                changes[0]!["AttackBefore"]!.GetValue<int>() == 1 && changes[0]!["AttackAfter"]!.GetValue<int>() == 2 &&
                changes[0]!["MaximumHealthBefore"]!.GetValue<int>() == 1 && changes[0]!["MaximumHealthAfter"]!.GetValue<int>() == 2 &&
                changes[0]!["RemainingHealthBefore"]!.GetValue<int>() == 1 && changes[0]!["RemainingHealthAfter"]!.GetValue<int>() == 2,
                "The actual complete candidate did not use exactly the observed Battlecry/hand-buff script once on the other hand minion.");
            File.WriteAllText(Path.Combine(directory, "first-line.json"), line.ToJsonString());
            var state = line["Result"]!;
            Check(state["AvailableMana"]!.GetValue<int>() == 0 && Entity(state, 30)["Attack"]!.GetValue<int>() == 5 && Entity(state, 30)["MaximumHealth"]!.GetValue<int>() == 4 &&
                Entity(state, 31)["Attack"]!.GetValue<int>() == 2 && Entity(state, 31)["RemainingHealth"]!.GetValue<int>() == 2 &&
                !state["Entities"]!.AsArray().Any(entity => entity!["EntityId"]!.GetValue<int>() == 21) &&
                state["DiscoveryBoundaryReached"]?.GetValue<bool>() != true && state["DiscoveryPreviews"]!.AsArray().Count == 0 &&
                state["Entities"]!.AsArray().All(entity => frame.Entities.ContainsKey(entity!["EntityId"]!.GetValue<int>())) && SnapshotCodec.Serialize(frame) == before,
                "The source buffed itself, used printed Boar cost, invoked native Discover or created a speculative entity.");
            Check(tools.ResultText.Contains("已观察拼装成品 · 本次模拟结算", StringComparison.Ordinal) && tools.ResultText.Contains("沙漏侍者", StringComparison.Ordinal) &&
                tools.ResultText.Contains("固定卡库 255790", StringComparison.Ordinal) && tools.ResultText.Contains("保留本体半段", StringComparison.Ordinal) &&
                tools.ResultText.Contains("本次模拟执行次数 1", StringComparison.Ordinal) && tools.ResultText.Contains("攻击 1 → 2", StringComparison.Ordinal) &&
                tools.ResultText.Contains("生命上限 1 → 2", StringComparison.Ordinal) && tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal) && !tools.ResultText.Contains("WRONG_ASSEMBLED_BUILD", StringComparison.Ordinal),
                "The desktop omitted execution provenance or downgraded a fully resolved script into a pending preview.");
            var unknown = (JsonObject)line.DeepClone(); Proof(unknown)["IsTriggerActive"] = null; Proof(unknown)["ExecutedCount"] = null;
            Check(LocalToolResultFormatter.Format("evaluate_line", unknown).Contains("战吼是否启用：未知；本次模拟执行次数 未知", StringComparison.Ordinal), "Missing typed counts became zero or a definite inactive trigger.");
            var originalContext = Field(window, "_historyContext"); var dual = Frame("two-same-base", dual: true); PinSaved(window, store, dual); tools = Tools(window); Author(tools, DualSteps);
            var dualLine = Evaluate(tools); var proofs = dualLine["Result"]!["AssembledEffects"]!.AsArray();
            Check(dualLine["Verification"]!.ToString() == "Verified" && dualLine["IsLethal"]!.GetValue<bool>() && proofs.Count == 2 &&
                proofs[0]!["SourceEntityId"]!.GetValue<int>() == 30 && proofs[0]!["EffectCardId"]!.ToString() == "TIME_100" &&
                proofs[1]!["SourceEntityId"]!.GetValue<int>() == 32 && proofs[1]!["EffectCardId"]!.ToString() == "CS3_025" &&
                Entity(dualLine["Result"]!, 30)["Attack"]!.GetValue<int>() == 5 && Entity(dualLine["Result"]!, 32)["Attack"]!.GetValue<int>() == 6 &&
                Entity(dualLine["Result"]!, 32)["MaximumHealth"]!.GetValue<int>() == 5 && Entity(dualLine["Result"]!, 31)["Attack"]!.GetValue<int>() == 3 &&
                tools.ResultText.Contains("沙漏侍者", StringComparison.Ordinal) && tools.ResultText.Contains("伦萨克大王", StringComparison.Ordinal),
                "Two real same-CardId sources shared a script cache or lost their entity-specific donor and earlier simulated hand buff.");
            Set(window, "_historyContext", originalContext); Set(window, "_historyObservedAt", cutoff); Set(window, "_liveFrame", frame); tools = Tools(window); Author(tools); Evaluate(tools);
            Pass("real battle text inspector and authored play-play-attack controls execute observed hand buff once without self buff native Discover printed-cost fallback or new entities");

            handler.Prepare("ASSEMBLED_SCRIPT_UI_COMPLETE"); Await(Invoke(window, "AnalyzeCurrentAsync"));
            var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == frame.MatchId);
            Check(saved.Advice.Verification == "verified" && saved.Advice.VerifiedLethal && saved.Advice.Actions.Count == 3 && saved.Advice.CardBuild == Build &&
                saved.Advice.KnowledgeAsOf == cutoff && saved.Advice.StateVersion == frame.StateVersion && saved.Advice.EventSequence == frame.EventSequence &&
                C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal) && Proof(handler.LastEvaluation!)["EffectCardId"]!.ToString() == "TIME_100",
                "Final host main display or durable archive lost the complete assembled script chain and its original decision binding.");
            var reopened = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(reopened.InitializeAsync());
            var same = Await(reopened.GetAsync(saved.Id))!; Check(same.Advice.Actions.SequenceEqual(saved.Advice.Actions) && same.Advice.Verification == "verified", "Reopening the archive changed the host-verified chain.");
            var browser = new AdviceArchiveWindow(reopened, new(frame.MatchId, "合成拼装局面", AdviceArchiveScopeKind.Session));
            try { Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0; Check(C<TextBox>(browser, "ArchiveDetailText").Text.Contains(handler.Summary, StringComparison.Ordinal) && C<TextBox>(browser, "ArchiveDetailText").Text.Contains("原验证状态：verified", StringComparison.Ordinal), "Actual archive rendering lost the saved result."); Passive(browser); }
            finally { browser.Close(); }
            Layout(tools, directory); Pass("real Analyze fake HTTP final host and reopened archive agree on the verified chain while finite calculator controls and provenance remain readable");

            foreach (var scenario in new[] { "missing-half", "offer", "native-effect", "missing-cost", "missing-attack", "opposing" })
            {
                var denied = Frame("denied-" + scenario, scenario: scenario); PinSaved(window, store, denied); tools = Tools(window);
                if (scenario == "opposing") { Check(!HasSource(tools, 30) && !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey("EDR_102"), "A private opponent assembled body entered action/name selection."); continue; }
                Author(tools); var rejectedLine = Evaluate(tools);
                Check(rejectedLine["Verification"]!.ToString() != "Verified" && !rejectedLine["IsLethal"]!.GetValue<bool>() &&
                    !tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal), "An unconfirmed component or current parameter became a complete verified chain: " + scenario);
            }
            PinSaved(window, store, Frame("inactive-battlecry", scenario: "inactive-battlecry")); tools = Tools(window); Author(tools); var inactive = Evaluate(tools);
            Check(inactive["Verification"]!.ToString() == "Verified" && !inactive["IsLethal"]!.GetValue<bool>() &&
                Proof(inactive)["IsTriggerActive"]!.GetValue<bool>() == false && Proof(inactive)["ExecutedCount"]!.GetValue<int>() == 0 &&
                Proof(inactive)["Targets"]!.AsArray().Count == 0 && tools.ResultText.Contains("战吼是否启用：否；本次模拟执行次数 0", StringComparison.Ordinal), "An explicitly inactive current Battlecry did not remain a known zero-execution script.");
            PinSaved(window, store, Frame("unknown-battlecry", scenario: "unknown-battlecry")); tools = Tools(window); Author(tools); var unknownCount = Evaluate(tools);
            Check(unknownCount["Verification"]!.ToString() == "Unsupported" && unknownCount["AppliedActions"]!.GetValue<int>() == 0 &&
                Proof(unknownCount)["IsTriggerActive"]?.GetValue<bool>() == true && Proof(unknownCount)["ExecutedCount"] is null &&
                tools.ResultText.Contains("战吼是否启用：是；本次模拟执行次数 未知", StringComparison.Ordinal), "An actual unknown Battlecry modifier became zero, one or an executed buff.");
            var bad = new CardCatalog(Path.Combine(directory, "bad-cards.db")); Await(bad.InitializeAsync()); var badCards = (JsonArray)cards.DeepClone(); badCards.Single(card => card!["id"]!.ToString() == "TIME_100")!["text"] = "<b>圣盾</b>。在你的回合结束时，&消灭你的英雄。"; Import(bad, badCards, Build);
            (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", bad);
            try
            {
                var denied = Frame("bad-definition"); PinSaved(window, store, denied); tools = Tools(window); Author(tools); var result = Evaluate(tools);
                Check(result["Verification"]!.ToString() == "Unsupported" && result["AppliedActions"]!.GetValue<int>() == 0 &&
                    (result["Result"]?["AssembledEffects"] as JsonArray)?.Count is null or 0 && !tools.ResultText.Contains("已观察拼装成品 · 本次模拟结算", StringComparison.Ordinal), "Unaudited donor text was labeled executed or paid before rejection.");
                handler.Prepare("ASSEMBLED_SCRIPT_UI_BAD_DEFINITION"); Await(Invoke(window, "AnalyzeCurrentAsync"));
                var deniedSaved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == denied.MatchId);
                Check(deniedSaved.Advice.Verification == "unsupported" && deniedSaved.Advice.Actions.Count == 0 && !deniedSaved.Advice.VerifiedLethal &&
                    !C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal), "The model forged a verified archived lethal through a bad donor definition.");
            }
            finally { (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", catalog); }
            Pass("missing halves offer markers native-effect substitution private bodies missing current parameters and bad donor metadata cannot forge verified actions provenance or archived lethal");

            foreach (var close in new[] { false, true }) foreach (var fail in new[] { false, true })
            {
                var old = Frame("late-" + close + fail); var pending = new LocalToolsWindow(old, Build, catalog, true); var completed = false; string? replacement = null;
                try
                {
                    Await(pending.BindingReady); Author(pending);
                    Set(Field(pending, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
                    {
                        Check(observation.Success && Proof(JsonNode.Parse(observation.ResultJson!)!)["EffectCardId"]!.ToString() == "TIME_100", "The stale gate did not follow a real assembled calculation.");
                        if (close) pending.Close(); else pending.UpdateBinding(Frame("replacement", effect: 66858), Build, true);
                        replacement = pending.ResultText; completed = true; if (fail) throw new IOException("OLD_ASSEMBLED_PROVENANCE");
                    }));
                    Click(C<Button>(pending, "LocalEvaluateButton")); WaitUntil(() => completed && !pending.HasPendingCalculation, "An old assembled result did not settle across lifecycle change."); if (!close) Await(pending.BindingReady); Barrier();
                    Check(pending.ResultText == replacement && !pending.ResultText.Contains("沙漏侍者", StringComparison.Ordinal) && (close ? !pending.IsBindingValid : pending.BoundMatchId != old.MatchId), "An old successful calculation or error replaced new halves or a closed calculator."); Passive(pending);
                }
                finally { if (!close || !completed) pending.Close(); }
            }
            Check(rejected.Calls == 0, "The fixed assembled test downloaded card data."); Passive(window);
            Pass("real completed assembled calculations and late errors cannot publish old donor provenance after replacement halves or a hidden calculator close");
        }
        finally { Set(window, "_allowClose", true); window.Close(); launch.SetValue(null, previous); }
    }

    private static GameSnapshot Frame(string suffix, int effect = 121051, string? scenario = null, bool dual = false)
    {
        var engine = new PowerLogStateEngine(1, "assembled-script-ui-" + suffix);
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", $"tag=RESOURCES value={(dual ? 9 : 5)}", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", $"tag=NUM_CARDS_IN_HAND value={(dual ? 3 : 2)}",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        if (scenario == "unknown-battlecry") Feed("TAG_CHANGE Entity=2 tag=EXTRA_BATTLECRY value=1");
        Body(11, "HERO_05", 1, "HERO", "PLAY", 0, 30); Feed("tag=CLASS value=HUNTER"); Body(21, "HERO_08", 2, "HERO", "PLAY", 0, dual ? 3 : 2);
        Body(30, "EDR_102", scenario == "opposing" ? 2 : 1, "MINION", "HAND", 5, 4);
        if (scenario != "missing-cost") Feed("tag=COST value=4");
        if (scenario != "missing-half") Feed($"tag=4572 value={(scenario == "offer" ? 1 : 0)}"); Feed($"tag=4573 value={(scenario == "native-effect" ? 113822 : effect)}");
        if (scenario == "inactive-battlecry") Feed("tag=BATTLECRY value=0");
        Body(31, "CS2_171", 1, "MINION", "HAND", scenario == "missing-attack" ? null : 1, 1); Feed("tag=COST value=1", "tag=CHARGE value=1");
        if (dual) { Body(32, "EDR_102", 1, "MINION", "HAND", 5, 4); Feed("tag=COST value=4", "tag=4572 value=0", "tag=4573 value=66858"); }
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var result = engine.Snapshot(); Check(result.Quality.IsStable && result.Quality.IsComplete, "The real assembled fixture did not settle."); return result;
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        void Body(int id, string card, int owner, string type, string zone, int? attack, int health)
        {
            Feed($"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}", $"tag=CARDTYPE value={type}", $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=FROZEN value=0", "tag=EXHAUSTED value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=0");
            if (attack.HasValue) Feed($"tag=ATK value={attack}");
        }
    }
    private static JsonArray Cards()
    {
        var result = new Dictionary<string, JsonNode>();
        foreach (var relative in new[] { "tests/HsHelper.Rules.Tests/Fixtures/assembled-scripts-255790.json", "tests/HsHelper.Rules.Tests/Fixtures/lethal-search-255790.json", "tests/HsHelper.Rules.Tests/Fixtures/beast-hunter-255790.json" })
            foreach (var card in JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), relative)))!["cards"]!.AsArray()) result.TryAdd(card!["id"]!.ToString(), card.DeepClone());
        return new JsonArray(result.Values.OrderBy(card => card["id"]!.ToString(), StringComparer.Ordinal).ToArray());
    }
    private static void Author(LocalToolsWindow tools, IEnumerable<(string Kind, int Source, int? Target)>? steps = null)
    {
        foreach (var step in steps ?? Steps) { C<ComboBox>(tools, "LocalKindBox").SelectedIndex = step.Kind == "attack" ? 1 : 0; Select(C<ComboBox>(tools, "LocalSourceBox"), step.Source); Select(C<ComboBox>(tools, "LocalTargetBox"), step.Target ?? 0); Click(C<Button>(tools, "LocalAddActionButton")); }
    }
    private static JsonObject Evaluate(LocalToolsWindow tools)
    {
        JsonObject? result = null; Set(Field(tools, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation => result = JsonNode.Parse(observation.ResultJson!)!.AsObject()));
        Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => result is not null && !tools.HasPendingCalculation, "The actual assembled sequence did not settle."); return result!;
    }
    private static JsonNode Proof(JsonNode line) => line["Result"]!["AssembledEffects"]!.AsArray().Single()!;
    private static JsonNode Entity(JsonNode state, int id) => state["Entities"]!.AsArray().Single(entity => entity!["EntityId"]!.GetValue<int>() == id)!;
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame) { var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync()); var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", point.RecordedAt); Set(window, "_liveFrame", frame); return point.RecordedAt; }
    private static void Layout(LocalToolsWindow tools, string directory)
    {
        var root = (FrameworkElement)tools.Content; var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml")); var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString()); root.Resources.MergedDictionaries.Add(styles);
        try
        {
            var size = new Size(640, 460); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); Barrier(); root.UpdateLayout(); var outer = (ScrollViewer)root; var presenter = VisualWalk(outer).OfType<ScrollContentPresenter>().First();
            foreach (var name in new[] { "LocalSourceBox", "LocalAddActionButton", "LocalEvaluateButton" }) { var control = C<FrameworkElement>(tools, name); control.BringIntoView(); Barrier(); root.UpdateLayout(); var rect = control.TransformToAncestor(presenter).TransformBounds(new Rect(control.RenderSize)); Check(rect.Top >= -1 && rect.Bottom <= presenter.ActualHeight + 1, "An assembled action control is unreachable."); }
            var result = C<TextBox>(tools, "LocalResultBox"); var evidenceIndex = result.Text.IndexOf("已观察拼装成品 · 本次模拟结算", StringComparison.Ordinal); Check(evidenceIndex >= 0, "No actual assembled provenance was available to lay out.");
            var evidenceRect = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(evidenceIndex));
            outer.ScrollToVerticalOffset(outer.VerticalOffset + evidenceRect.Top - 12); Barrier(); root.UpdateLayout();
            evidenceRect = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(evidenceIndex));
            Check(!evidenceRect.IsEmpty && evidenceRect.Top >= -1 && evidenceRect.Bottom <= presenter.ActualHeight + 1, "The actual provenance heading cannot be reached at finite size.");
            Save("provenance");
            outer.ScrollToEnd(); result.ScrollToEnd(); Barrier(); root.UpdateLayout(); var last = result.TransformToAncestor(presenter).TransformBounds(result.GetRectFromCharacterIndex(result.Text.Length - 1));
            Save("end");
            Console.WriteLine($"Assembled script layout: viewport={outer.ViewportHeight}; extent={outer.ExtentHeight}; offset={outer.VerticalOffset}; lastLine={last}; PNG={directory}");
            Check(!last.IsEmpty && last.Height > 0 && last.Top >= -1 && last.Bottom <= presenter.ActualHeight + 1, "The final assembled result line cannot be reached at a finite calculator size.");
            void Save(string suffix) { var bitmap = new RenderTargetBitmap(640, 460, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual(); using (var draw = background.RenderOpen()) draw.DrawRectangle(Brushes.White, null, new Rect(size)); bitmap.Render(background); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, "assembled-script-640x460-" + suffix + ".png")); png.Save(file); }
        }
        finally { root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Import(CardCatalog catalog, JsonArray cards, string build) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://assembled-script-ui")); }
    private static string Workspace() { for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent) if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName; throw new FileNotFoundException("Assembled script fixtures missing."); }
    private static T C<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root) { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in VisualWalk(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static bool HasSource(LocalToolsWindow tools, int id) => C<ComboBox>(tools, "LocalSourceBox").Items.Cast<object>().Any(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static void Select(ComboBox box, int id) => box.SelectedItem = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An assembled control is disabled: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void ReplaceClient(MainWindow window, string name, HttpMessageHandler handler) { ((HttpClient)Field(window, name)!).Dispose(); Set(window, name, new HttpClient(handler, false)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "An assembled regression showed a native window.");
    private static void Barrier() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Assembled UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string message) { if (ready()) return; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || clock.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), message); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS assembled script UI: " + message);
    private sealed class RejectNetwork : HttpMessageHandler { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; throw new InvalidOperationException("No card HTTP is allowed."); } }
    private sealed class AdviceHandler : HttpMessageHandler
    {
        public string Summary { get; private set; } = ""; public JsonObject? LastEvaluation { get; private set; }
        public void Prepare(string summary) { Summary = summary; LastEvaluation = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; var tool = body["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool");
            var actions = Steps.Select(step => new { kind = step.Kind, source_entity_id = step.Source, target_entity_id = step.Target }).ToArray();
            if (tool is null) return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", tool_calls = new[] { new { id = "assembled_script_ui_line", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            LastEvaluation = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = Summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions, verification = "verified", verified_lethal = true }) } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
