using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

internal static class JointTerrorEndTurnUiChecks
{
    private const string Build = "255790";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "joint-terror-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null)); var window = new MainWindow();
        using var rejected = new RejectNetwork();
        try
        {
            ReplaceClient(window, "_http", rejected); ReplaceClient(window, "_cardHttp", rejected);
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json")))!;
            Check(fixture["build"]!.ToString() == Build, "The joint fixture is not fixed255790."); var cards = fixture["cards"]!.AsArray(); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_JOINT_BUILD"; Import(catalog, decoy, "999999");
            var frame = Frame("main"); var before = SnapshotCodec.Serialize(frame); Pin(window, frame); Await(Invoke(window, "RenderBattleAsync"));
            var tools = Tools(window); var actual = Evaluate(tools); var preview = Preview(actual); var branches = preview["Branches"]!.AsArray();
            Check(preview["SourceEntityIds"]!.AsArray().Select(value => value!.GetValue<int>()).SequenceEqual([30, 31]) &&
                preview["ResolvesBranches"]!.GetValue<bool>() && preview["IsEnumerationComplete"]!.GetValue<bool>() && branches.Count > 0 &&
                actual["Verification"]!.ToString() != "Verified" && actual["AppliedActions"]!.GetValue<int>() == 1 && actual["IsLethal"]!.GetValue<bool>() == false,
                "The actual end-turn button did not obtain a bounded joint conditional outcome from the host.");
            foreach (var branch in branches) ValidateStages(branch!, [30, 31]);
            Check(branches.Any(branch => branch!["OwnHeroHealthAfter"]!.GetValue<int>() == 28 && branch["LifestealHealing"]!.GetValue<int>() == 8 &&
                    Row(branch!, 31)["RemainingHealth"]!.GetValue<int>() == 4 && Row(branch!, 41)["RemainingHealth"]!.GetValue<int>() == 2) &&
                branches.Where(branch => branch!["OwnHeroHealthAfter"]!.GetValue<int>() == 30).All(branch => branch!["LifestealHealing"]!.GetValue<int>() == 10 && !Row(branch!, 41)["IsAlive"]!.GetValue<bool>()),
                "Correlated healing and enemy survival were lost or stitched between different joint branches.");
            Check(branches.All(branch => branch!["IsComplete"]!.GetValue<bool>()) &&
                tools.ResultText.Contains("联合来源 2 个（集合，不是触发顺序）", StringComparison.Ordinal) && tools.ResultText.Contains("神秘恐魔 #30", StringComparison.Ordinal) &&
                tools.ResultText.Contains("神秘恐魔 #31", StringComparison.Ordinal) && tools.ResultText.Contains("已开始阶段的见证前缀", StringComparison.Ordinal) &&
                tools.ResultText.Contains("阶段 1", StringComparison.Ordinal) && tools.ResultText.Contains("战斗 #1", StringComparison.Ordinal) &&
                tools.ResultText.Contains("吸血实际恢复", StringComparison.Ordinal) && tools.ResultText.Contains("不能拼接不同分支", StringComparison.Ordinal) &&
                tools.ResultText.Contains("当前主体投影仍停在回合末触发前", StringComparison.Ordinal) &&
                !tools.ResultText.Contains("WRONG_JOINT_BUILD", StringComparison.Ordinal) && !tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                "The desktop lost source identity/stage correlation or promoted a witness to actual lethal.");
            Check(actual["Result"]!["Entities"]!.AsArray().Single(row => row!["EntityId"]!.GetValue<int>() == 11)!["RemainingHealth"]!.GetValue<int>() == 20 &&
                actual["Result"]!["Entities"]!.AsArray().Single(row => row!["EntityId"]!.GetValue<int>() == 30)!["RemainingHealth"]!.GetValue<int>() == 5 &&
                actual["Result"]!["Entities"]!.AsArray().Single(row => row!["EntityId"]!.GetValue<int>() == 31)!["RemainingHealth"]!.GetValue<int>() == 10 &&
                SnapshotCodec.Serialize(frame) == before && !actual.ToJsonString().Contains("PRIVATE_JOINT_HAND", StringComparison.Ordinal), "Joint branches replaced the pre-trigger projection or exposed private identity.");
            Console.WriteLine($"Joint Terror baseline: branches={branches.Count}; outcomes=" + string.Join(" | ", branches.Select(branch => $"{branch!["OwnHeroHealthAfter"]}/{branch["LifestealHealing"]}/" + branch["SourceOrder"])));
            Pass("actual end-turn controls obtain fixed-build joint source stages with correlated combat healing and terminal boards while the bound observation remains unchanged");

            Pin(window, Frame("zero", zeroCombatStage: true)); tools = Tools(window); var zero = Preview(Evaluate(tools));
            Check(zero["Branches"]!.AsArray().Any(branch => branch!["Stages"]!.AsArray().Any(stage => stage!["CombatCount"]!.GetValue<int>() == 0 && stage["IsComplete"]!.GetValue<bool>())) &&
                tools.ResultText.Contains("无已计算战斗（0 次）", StringComparison.Ordinal) && tools.ResultText.Contains("已处理本分支全部来源阶段", StringComparison.Ordinal),
                "A real zero-combat source phase was omitted or a completed joint branch lost its stop reason.");
            Pin(window, Frame("death", unknownDeath: true)); tools = Tools(window); var partialLine = Evaluate(tools); var partial = Preview(partialLine);
            Check(!partial["IsEnumerationComplete"]!.GetValue<bool>() && partial["Branches"]!.AsArray().Count > 0 &&
                partial["Branches"]!.AsArray().All(branch => !branch!["IsComplete"]!.GetValue<bool>() && branch["SourceOrder"]!.AsArray().Count == 1 &&
                    branch["Stages"]!.AsArray().Count == 1 && !branch["Stages"]![0]!["IsComplete"]!.GetValue<bool>()) &&
                tools.ResultText.Contains("未进入阶段的来源", StringComparison.Ordinal) && tools.ResultText.Contains("其后顺序与结果未补出", StringComparison.Ordinal) &&
                tools.ResultText.Contains("亡语或复生", StringComparison.Ordinal) && tools.ResultText.Contains("未完成，仅已计算前缀", StringComparison.Ordinal),
                "An unknown death trigger was followed by invented remaining source stages or complete global outcomes.");
            foreach (var branch in partial["Branches"]!.AsArray()) ValidateStages(branch!, [30, 31]);
            var missing = (JsonObject)actual.DeepClone(); var missingBranch = Preview(missing)["Branches"]![0]!; missingBranch["SourceOrder"] = null; missingBranch["Stages"] = null;
            var missingText = LocalToolResultFormatter.Format("evaluate_line", missing);
            Check(missingText.Contains("来源阶段记录未提供或对应关系未确认", StringComparison.Ordinal), "Old or missing stage JSON invented a source order.");
            var malformed = (JsonObject)actual.DeepClone(); Preview(malformed)["Branches"]![0]!["Stages"]![0]!["FirstCombatIndex"] = int.MaxValue;
            Check(LocalToolResultFormatter.Format("evaluate_line", malformed).Contains("来源阶段记录未提供或对应关系未确认", StringComparison.Ordinal), "A contradictory stage span became a trusted combat-source mapping.");
            Pass("real zero-combat stages remain visible while unknown death prefixes missing legacy fields and contradictory spans never invent unstarted sources");

            Pin(window, Frame("long", longList: true)); tools = Tools(window); var longLine = Evaluate(tools); var longPreview = Preview(longLine);
            Check(longPreview["SourceEntityIds"]!.AsArray().Count == 3 && longPreview["Branches"]!.AsArray().Count == 256 &&
                longPreview["BranchProjection"] is null && tools.ResultText.Length > 2000,
                "The direct local calculator lost the complete bounded host result or received a model-only branch projection.");
            Console.WriteLine("Joint Terror local full branches: " + longPreview["Branches"]!.AsArray().Count);
            if (longPreview["Branches"]!.AsArray().Count > 8) Check(tools.ResultText.Contains("个分支未在此摘要展开", StringComparison.Ordinal), "The bounded display silently omitted real host branches.");
            if (!longPreview["IsEnumerationComplete"]!.GetValue<bool>()) Check(tools.ResultText.Contains("未列出某个结果不能证明它不存在", StringComparison.Ordinal), "A real enumeration limit became a complete result claim.");
            Layout(tools, directory); Passive(window); Passive(tools);
            Pass("real long joint results retain branch omission and enumeration scope with actual controls and the last wrapped result line reachable at finite size");

            foreach (var close in new[] { false, true }) foreach (var fail in new[] { false, true })
            {
                var staleFrame = Frame("late-" + close + fail); var stale = new LocalToolsWindow(staleFrame, Build, catalog, true); var completed = false; string? replacement = null;
                try
                {
                    Await(stale.BindingReady); AddEndTurn(stale);
                    Set(Field(stale, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
                    {
                        Check(observation.Name == "evaluate_line" && observation.Success && Preview(JsonNode.Parse(observation.ResultJson!)!)["SourceEntityIds"]!.AsArray().Count == 2,
                            "The lifecycle test did not follow a real joint end-turn calculation.");
                        if (close) stale.Close(); else stale.UpdateBinding(Frame("replacement", single: true), Build, true);
                        replacement = stale.ResultText; completed = true; if (fail) throw new IOException("OLD_JOINT_COMPLETION");
                    }));
                    Click(C<Button>(stale, "LocalEvaluateButton")); WaitUntil(() => completed && !stale.HasPendingCalculation, "The old joint calculation did not settle after replacement or close.");
                    if (!close) Await(stale.BindingReady); Barrier();
                    Check(stale.ResultText == replacement && !stale.ResultText.Contains("联合来源 2", StringComparison.Ordinal) && (close ? !stale.IsBindingValid : stale.BoundMatchId != staleFrame.MatchId),
                        "An old joint success or failure overwrote a replacement binding or closed calculator."); Passive(stale);
                }
                finally { if (!close || !completed) stale.Close(); }
            }
            Check(rejected.Calls == 0, "The joint calculator attempted real HTTP.");
            Pass("a completed real joint result and its late error cannot cross a binding replacement or hidden calculator close");
        }
        finally { Set(window, "_allowClose", true); window.Close(); launch.SetValue(null, previous); }
    }

    private static GameSnapshot Frame(string suffix, bool zeroCombatStage = false, bool unknownDeath = false, bool longList = false, bool single = false)
    {
        var engine = new PowerLogStateEngine(1, "joint-terror-ui-" + suffix);
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Body(11, "HERO_05", 1, "HERO", 0, 30); Feed("tag=DAMAGE value=10"); Body(21, "HERO_08", 2, "HERO", 0, 30);
        Body(12, "HERO_05bp", 1, "HERO_POWER", 0, 0); Body(22, "HERO_08bp", 2, "HERO_POWER", 0, 0);
        Body(30, "CORE_TTN_866", 1, "MINION", zeroCombatStage ? 8 : 4, zeroCombatStage ? 100 : 5); Feed("tag=LIFESTEAL value=1");
        if (!single) { Body(31, "CORE_TTN_866", 1, "MINION", zeroCombatStage ? 8 : 2, zeroCombatStage ? 100 : 10); Feed("tag=LIFESTEAL value=1"); }
        if (longList) { Body(32, "CORE_TTN_866", 1, "MINION", 3, 7); Feed("tag=LIFESTEAL value=1"); }
        Body(40, unknownDeath ? "EDR_227" : "CS2_172", 2, "MINION", zeroCombatStage || unknownDeath ? 0 : 5, zeroCombatStage || unknownDeath ? 1 : 5);
        if (unknownDeath) Feed("tag=DEATHRATTLE value=1");
        Body(41, "CORE_CS2_231", 2, "MINION", zeroCombatStage ? 0 : 1, zeroCombatStage ? 1 : 4);
        if (longList) { Body(42, "CS2_172", 2, "MINION", 2, 9); Body(43, "CS2_172", 2, "MINION", 4, 6); Body(44, "CS2_172", 2, "MINION", 3, 8); }
        Feed("FULL_ENTITY - Creating ID=90 CardID=PRIVATE_JOINT_HAND", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=99", "tag=HEALTH value=99",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var frame = engine.Snapshot(); Check(frame.Quality.IsStable && frame.Quality.IsComplete, "The actual parser did not settle the joint fixture."); return frame;
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        void Body(int id, string card, int owner, string type, int attack, int health) => Feed($"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", $"tag=CARDTYPE value={type}", $"tag=ATK value={attack}", $"tag=HEALTH value={health}",
            "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1");
    }
    private static void ValidateStages(JsonNode branch, int[] sources)
    {
        var order = branch["SourceOrder"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray(); var stages = branch["Stages"]!.AsArray(); var combats = branch["Combats"]!.AsArray(); var first = 0;
        Check(order.Length == stages.Count && order.Distinct().Count() == order.Length && order.All(sources.Contains), "A real branch stage prefix is not bound to the declared source set.");
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index]!; var count = stage["CombatCount"]!.GetValue<int>();
            Check(stage["SourceEntityId"]!.GetValue<int>() == order[index] && stage["FirstCombatIndex"]!.GetValue<int>() == first && count >= 0 && first + count <= combats.Count &&
                Enumerable.Range(first, count).All(combat => combats[combat]!["TargetEntityId"]!.GetValue<int>() == order[index]), "A real stage did not map its exact slice of correlated combats."); first += count;
        }
        Check(first == combats.Count, "The witness stage prefix omitted computed combats.");
    }
    private static JsonNode Preview(JsonNode line) => line["Result"]!["TerrorEndTurnPreviews"]!.AsArray().Single()!;
    private static JsonNode Row(JsonNode branch, int entityId) => branch["Entities"]!.AsArray().Single(row => row!["EntityId"]!.GetValue<int>() == entityId)!;
    private static JsonObject Evaluate(LocalToolsWindow tools)
    {
        AddEndTurn(tools); JsonObject? result = null;
        Set(Field(tools, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation => { Check(observation.Name == "evaluate_line" && observation.Success, "The actual evaluation button did not call the host."); result = JsonNode.Parse(observation.ResultJson!)!.AsObject(); }));
        Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => result is not null && !tools.HasPendingCalculation, "The actual joint candidate did not settle."); return result!;
    }
    private static void AddEndTurn(LocalToolsWindow tools) { C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 3; Check(!C<ComboBox>(tools, "LocalTargetBox").IsEnabled, "End turn unexpectedly accepts a target."); Click(C<Button>(tools, "LocalAddActionButton")); }
    private static void Layout(LocalToolsWindow tools, string directory)
    {
        var root = (FrameworkElement)tools.Content; var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml")); var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString()); root.Resources.MergedDictionaries.Add(styles);
        try
        {
            var size = new Size(640, 460); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); Barrier(); root.UpdateLayout(); var outer = (ScrollViewer)root;
            var presenter = VisualWalk(outer).OfType<ScrollContentPresenter>().First(); var result = C<TextBox>(tools, "LocalResultBox");
            foreach (var name in new[] { "LocalKindBox", "LocalAddActionButton", "LocalEvaluateButton" })
            { var control = C<FrameworkElement>(tools, name); control.BringIntoView(); Barrier(); root.UpdateLayout(); var rect = control.TransformToAncestor(presenter).TransformBounds(new Rect(control.RenderSize)); Check(rect.Top >= -1 && rect.Bottom <= presenter.ActualHeight + 1, "A real end-turn control was unreachable in the finite calculator: " + name); }
            outer.ScrollToEnd(); result.ScrollToEnd(); Barrier(); root.UpdateLayout();
            var local = result.GetRectFromCharacterIndex(result.Text.Length - 1); var last = result.TransformToAncestor(presenter).TransformBounds(local);
            Console.WriteLine($"Joint Terror layout: outerViewport={outer.ViewportHeight}; extent={outer.ExtentHeight}; offset={outer.VerticalOffset}; result={result.ActualWidth}/{result.ActualHeight}; lastLine={last}");
            var bitmap = new RenderTargetBitmap(640, 460, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual(); using (var draw = background.RenderOpen()) draw.DrawRectangle(Brushes.White, null, new Rect(size)); bitmap.Render(background); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, "joint-terror-640x460-end.png")); png.Save(file);
            Check(outer.ViewportHeight > 100 && !local.IsEmpty && last.Height > 0 && last.Top >= -1 && last.Bottom <= presenter.ActualHeight + 1, "The final wrapped joint result line was not reachable by real finite scrolling.");
            Console.WriteLine("Joint Terror UI PNG directory: " + directory);
        }
        finally { root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static void Pin(MainWindow window, GameSnapshot frame) { Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_liveFrame", frame); }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Import(CardCatalog catalog, JsonArray cards, string build) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://joint-terror-ui")); }
    private static string Workspace() { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName; throw new FileNotFoundException("Joint Terror fixture root is missing."); }
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root) { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in VisualWalk(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An actual joint test button is disabled: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void ReplaceClient(MainWindow window, string name, HttpMessageHandler handler) { ((HttpClient)Field(window, name)!).Dispose(); Set(window, name, new HttpClient(handler, false)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A joint test showed a native window.");
    private static void Barrier() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Joint UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string message) { if (ready()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), message); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS joint Terror end-turn UI: " + message);
    private sealed class RejectNetwork : HttpMessageHandler { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; throw new InvalidOperationException("No network is permitted in joint Terror UI checks."); } }
}
