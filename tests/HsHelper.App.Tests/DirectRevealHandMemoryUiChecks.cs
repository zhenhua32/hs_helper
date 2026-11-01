using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

/// <summary>Uses the actual parser and hidden desktop controls, without authorizing private SHOW lines.</summary>
internal static class DirectRevealHandMemoryUiChecks
{
    private const string Build = "255790", Card = "DIRECT_MEMORY_BODY", CardName = "历史展示手牌样本";
    private const string Returned = "DIRECT_RETURNED_BODY", Source = "DIRECT_PUBLIC_SOURCE", SourceName = "公开展示来源样本";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "direct-reveal-hand-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var prior = launch.GetValue(null);
        var oldData = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR"); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", directory);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); var closed = false; using var network = new RejectNetwork();
        try
        {
            ReplaceClient(window, "_http", network); ReplaceClient(window, "_cardHttp", network);
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); Import(catalog, Build, false); Import(catalog, "999999", true);
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var engine = Engine("main"); var before = engine.Snapshot();
            Check(Memory(before).All(entry => entry.EntityId != 30), "A never-public private hand was remembered before its history display.");
            foreach (var line in RevealLines())
            {
                engine.ProcessLine(line); Pin(window, engine.Snapshot()); Await(Invoke(window, "RenderBattleAsync"));
                Check(View(window).OpponentHandMemory.All(row => row.CardId != Card) && engine.Snapshot().Entities[30].CardId is null,
                    "A direct history-display candidate reached the actual view before the root block and task settled.");
            }
            engine.ProcessLine("BLOCK_END"); Pin(window, engine.Snapshot()); Await(Invoke(window, "RenderBattleAsync"));
            Check(View(window).OpponentHandMemory.All(row => row.CardId != Card), "A closed block was presented before its task settled.");
            engine.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete"); var frame = engine.Snapshot(); var original = SnapshotCodec.Serialize(frame);
            Pin(window, frame); InvokeObject(window, "RenderSnapshot"); Await(Invoke(window, "RenderBattleAsync")); Barrier();
            var direct = View(window).OpponentHandMemory.Single(row => row.CardId == Card); var proof = Memory(frame).Single(entry => entry.EntityId == 30).DirectReveal!;
            Check(direct.Name == CardName && direct.EvidenceKind == "历史展示确认" && direct.EnteredHandAtSequence is null &&
                direct.PublicSource.Contains("展示 #" + proof.ShowAtSequence, StringComparison.Ordinal) && direct.PublicSource.Contains("确认 #" + proof.ConfirmedAtSequence, StringComparison.Ordinal) &&
                direct.Continuity.Contains(SourceName, StringComparison.Ordinal) && direct.Continuity.Contains("公开场上 #" + proof.SourcePublicAtSequence, StringComparison.Ordinal) &&
                direct.Continuity.Contains("来源 #60", StringComparison.Ordinal) &&
                !direct.DisplayText.Contains("回手", StringComparison.Ordinal) && View(window).OpponentHandMemory.Single(row => row.CardId == Returned).EvidenceKind == "公开回手",
                "The actual memory rows conflated a direct display with a returned card or lost their exact public source sequences.");
            Check(C<DataGrid>(window, "OpponentHandMemoryGrid").Items.OfType<BattleHandMemoryView>().Count() == 2 && View(window).Opponent.Hand.Count == 0 &&
                frame.Entities[30].CardId is null && frame.Entities[30].Cost is null && !frame.Entities.ContainsKey(50), "Direct memory restored current private identity attributes or attachments.");
            var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady);
            var loaded = (IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!;
            Check(loaded[Card].Name == CardName && loaded[Source].Name == SourceName && !loaded.ContainsKey("DIRECT_PRIVATE_GIFT"), "Name preload used an active-build decoy or a private attachment.");
            foreach (var kind in Enumerable.Range(0, 4))
            {
                C<ComboBox>(tools, "LocalKindBox").SelectedIndex = kind;
                Check(!Ids(C<ComboBox>(tools, "LocalSourceBox")).Contains(30) && !Ids(C<ComboBox>(tools, "LocalTargetBox")).Contains(30), "A direct memory became an actionable calculator entity.");
            }
            var effect = Await(((SnapshotCoach)Field(tools, "_coach")!).ExecuteAsync("get_entity_effect", new() { ["entity_id"] = 30 }, CancellationToken.None));
            Check(effect["error"] is not null && SnapshotCodec.Serialize(frame) == original, "A preload exposed the hidden card effect or mutated its original snapshot.");
            Feed(engine, "TAG_CHANGE Entity=60 tag=ZONE value=DECK", "TAG_CHANGE Entity=61 tag=ZONE value=DECK", "PowerProcessor.EndCurrentTaskList() - complete");
            frame = engine.Snapshot(); Pin(window, frame); Await(Invoke(window, "RenderBattleAsync")); direct = View(window).OpponentHandMemory.Single(row => row.CardId == Card);
            Check(frame.Entities[60].CardId is null && frame.Entities[61].CardId is null && direct.SourceName == SourceName &&
                BattleViewBuilder.VisibleCardIds(frame, Build).Contains(Source), "A later private source location erased its confirmed historical source or restored its current identity.");
            Pass("actual parser lines remain private before settled root closure and then show direct identity evidence separately from an existing public return");

            var recorder = new SessionRecorder(store, new(null, Build, 1)); recorder.Observe(frame, 1); Await(recorder.CompleteAsync());
            var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Set(window, "_historyContext", null); Set(window, "_liveFrame", frame); Set(window, "_engine", engine);
            Click(FindButton(window, "复盘最后观察局面"));
            WaitUntil(() => Field(window, "_historyContext") is HistoricalContext context && context.Snapshot.MatchId == frame.MatchId && !(bool)Field(window, "_historyLoading")!, "Strict historical display did not finish.");
            Check((DateTimeOffset?)Field(window, "_historyObservedAt") == point.RecordedAt &&
                C<TextBox>(window, "HistorySnapshotText").Text.Contains(direct.PublicSource, StringComparison.Ordinal) &&
                C<TextBox>(window, "HistorySnapshotText").Text.Contains(SourceName, StringComparison.Ordinal), "Saved direct evidence lost its original cutoff, display sequence or public-source name.");
            var overlay = new OverlayWindow(); Set(window, "_overlay", overlay); Await(Invoke(window, "UpdateOverlayStateAsync", overlay, false));
            Check(overlay.State.OpponentHandMemory.Contains(direct.DisplayText) && !overlay.State.Hand.Any(text => text.Contains(CardName, StringComparison.Ordinal)), "The actual overlay lost direct evidence or treated it as current hand data.");
            PixelLayout(overlay, direct.DisplayText, directory); Passive(window); Passive(overlay);
            Pass("saved exact history and the hidden pixel-scrolling overlay preserve SHOW confirmation and public-source text through the last wrapped line");

            foreach (var scenario in new[] { "show-only", "override", "nested-override", "changed" })
            {
                var denied = Engine(scenario);
                if (scenario == "show-only") Feed(denied, Show());
                else
                {
                    if (scenario == "nested-override") Feed(denied, Block());
                    Feed(denied, RevealLines());
                    if (scenario == "nested-override") Feed(denied, "BLOCK_END");
                    if (scenario is "override" or "nested-override") Feed(denied, "META_DATA - Meta=OVERRIDE_HISTORY Data=0 InfoCount=0");
                    Feed(denied, "BLOCK_END");
                }
                Feed(denied, "PowerProcessor.EndCurrentTaskList() - complete");
                if (scenario == "changed") Feed(denied, "CHANGE_ENTITY - Updating Entity=[entityName=changed id=30 zone=HAND zonePos=1 cardId=DIRECT_CHANGED_HAND player=2] CardID=DIRECT_CHANGED_HAND", "PowerProcessor.EndCurrentTaskList() - complete");
                var invalid = denied.Snapshot(); Pin(window, invalid); Await(Invoke(window, "RenderBattleAsync")); Await(Invoke(window, "UpdateOverlayStateAsync", overlay, false));
                Check(View(window).OpponentHandMemory.All(row => row.CardId != Card) && !BattleViewBuilder.VisibleCardIds(invalid, Build).Contains(Card) &&
                    !overlay.State.OpponentHandMemory.Any(text => text.Contains(CardName, StringComparison.Ordinal)), "An unconfirmed or invalidated reveal re-entered a view/preload: " + scenario);
            }
            Pass("bare private SHOW late history override and a later actual transformation cannot publish direct identities in views or card-name preload");

            foreach (var kind in new[] { "battle", "overlay" }) foreach (var fail in new[] { false, true })
            {
                var oldEngine = Engine("same-evidence"); Feed(oldEngine, RevealLines(60)); Feed(oldEngine, "BLOCK_END", "PowerProcessor.EndCurrentTaskList() - complete"); var old = oldEngine.Snapshot();
                var newEngine = Engine("same-evidence"); Feed(newEngine, RevealLines(61)); Feed(newEngine, "BLOCK_END", "PowerProcessor.EndCurrentTaskList() - complete"); var current = newEngine.Snapshot();
                Check(old.MatchId == current.MatchId && old.StateVersion == current.StateVersion && old.EventSequence == current.EventSequence &&
                    Memory(old).Single(entry => entry.EntityId == 30).CardId == Memory(current).Single(entry => entry.EntityId == 30).CardId &&
                    Memory(old).Single(entry => entry.EntityId == 30).DirectReveal!.SourceEntityId != Memory(current).Single(entry => entry.EntityId == 30).DirectReveal!.SourceEntityId,
                    "The same-key stale fixture does not differ solely in its actual reveal source proof.");
                Set(window, "_historyContext", null); Set(window, "_liveFrame", old);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (actual, captured, build, _) =>
                { Check(actual == kind && captured.MatchId == old.MatchId && build == Build, "The gate did not follow a real fixed-build catalog read."); entered.SetResult(); await release.Task; if (fail) throw new IOException("OLD_DIRECT_PROOF"); }));
                var pending = kind == "battle" ? Invoke(window, "RenderBattleAsync") : Invoke(window, "UpdateOverlayStateAsync", overlay, false); Await(entered.Task);
                var oldView = C<FrameworkElement>(window, "BattleViewRoot").DataContext; var oldOverlay = overlay.State;
                Set(window, "_liveFrame", current); release.SetResult(); Await(pending); Set(window, "_aggregatedCardReadCompleted", null);
                Check(ReferenceEquals(oldView, C<FrameworkElement>(window, "BattleViewRoot").DataContext) && ReferenceEquals(oldOverlay, overlay.State), "A same-card same-key old success or failure overwrote changed direct evidence.");
            }
            Pin(window, frame); var closingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var closingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (_, _, _, _) => { closingEntered.SetResult(); await closingRelease.Task; throw new IOException("CLOSED_DIRECT_PROOF"); }));
            var closing = Invoke(window, "UpdateOverlayStateAsync", overlay, false); Await(closingEntered.Task); var priorOverlay = overlay.State;
            Set(window, "_allowClose", true); window.Close(); closed = true; closingRelease.SetResult(); Await(closing);
            Check(ReferenceEquals(priorOverlay, overlay.State) && network.Calls == 0, "A closed host published old evidence or the memory UI performed HTTP."); Passive(window); Passive(overlay);
            Pass("post-SQL old success and failure cannot replace changed direct evidence under identical keys and closing rejects late completion");
        }
        finally
        {
            Set(window, "_aggregatedCardReadCompleted", null); if (!closed) { Set(window, "_allowClose", true); window.Close(); }
            launch.SetValue(null, prior); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", oldData);
        }
    }

    private static IReadOnlyList<PublicHandMemoryEntry> Memory(GameSnapshot frame) => PublicHandMemoryVisibility.Confirmed(frame)?.Entries ?? [];
    private static PowerLogStateEngine Engine(string suffix)
    {
        var engine = new PowerLogStateEngine(1, "direct-hand-ui-" + suffix);
        Feed(engine, "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=NUM_CARDS_IN_HAND value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21", "tag=NUM_CARDS_IN_HAND value=2");
        foreach (var (id, card, owner) in new[] { (11, "HERO_05", 1), (21, "HERO_08", 2) }) Feed(engine, $"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0");
        Feed(engine, "FULL_ENTITY - Creating ID=30 CardID=DIRECT_MEMORY_BODY", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=COST value=99", "tag=ATK value=88", "tag=HEALTH value=77",
            "FULL_ENTITY - Creating ID=31 CardID=DIRECT_RETURNED_BODY", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "TAG_CHANGE Entity=31 tag=ZONE value=HAND",
            "FULL_ENTITY - Creating ID=40 CardID=DIRECT_OWN_BODY", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=1", "tag=HEALTH value=2", "tag=DAMAGE value=0");
        foreach (var id in new[] { 60, 61 }) Feed(engine, $"FULL_ENTITY - Creating ID={id} CardID=DIRECT_PUBLIC_SOURCE", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=SPELL");
        Feed(engine, "FULL_ENTITY - Creating ID=50 CardID=DIRECT_PRIVATE_GIFT", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }
    private static string Block(int source = 60) => $"BLOCK_START BlockType=POWER Entity=[entityName=source id={source} zone=PLAY zonePos=0 cardId=DIRECT_PUBLIC_SOURCE player=1] EffectCardId= EffectIndex=0 Target=0 SubOption=-1";
    private static string Show() => "SHOW_ENTITY - Updating Entity=[entityName=hidden id=30 zone=HAND zonePos=1 cardId=DIRECT_MEMORY_BODY player=2] CardID=DIRECT_MEMORY_BODY";
    private static string[] RevealLines(int source = 60) => [Block(source), Show(), "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = [entityName=hidden id=30 zone=HAND zonePos=1 cardId=DIRECT_MEMORY_BODY player=2]"];
    private static void Feed(PowerLogStateEngine engine, params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Import(CardCatalog catalog, string build, bool decoy)
    {
        var definitions = new[] { (Card, CardName, "MINION"), (Returned, "公开回手对照", "MINION"), (Source, SourceName, "SPELL"), ("DIRECT_OWN_BODY", "我方样本", "MINION"),
            ("DIRECT_PRIVATE_GIFT", "PRIVATE_DIRECT_ENCHANTMENT", "ENCHANTMENT"), ("DIRECT_CHANGED_HAND", "PRIVATE_CHANGED_HAND", "MINION"), ("HERO_05", "我方英雄", "HERO"), ("HERO_08", "对手英雄", "HERO") }
            .Select((item, index) => new { id=item.Item1, dbfId=990000+index, name=decoy ? "WRONG_DIRECT_BUILD" : item.Item2, type=item.Item3, text="BASE_TEXT_NOT_CURRENT_EFFECT", cardClass="NEUTRAL", cost=7, attack=7, health=7, collectible=false, set="SYNTHETIC", mechanics=Array.Empty<string>() });
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(definitions)); Await(catalog.ImportAsync(input, build, source: "fixture://direct-reveal-hand-ui"));
    }
    private static void PixelLayout(OverlayWindow overlay, string displayText, string directory)
    {
        var root = (FrameworkElement)overlay.Content; var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml"));
        var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString()); root.Resources.MergedDictionaries.Add(styles);
        try
        {
            var size = new Size(220, 300); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); Barrier(); root.UpdateLayout();
            var outer = C<ScrollViewer>(overlay, "OverlayContentScroll"); var list = C<ListBox>(overlay, "OpponentMemoryList"); outer.ScrollToEnd(); Barrier(); root.UpdateLayout();
            var point = list.TransformToAncestor(outer).Transform(new Point());
            Check(list.ActualHeight >= 20 && point.Y >= -1 && point.Y + list.ActualHeight <= outer.ViewportHeight + 1, "The direct identity list cannot fit fully in the minimum overlay viewport.");
            var inner = VisualWalk(list).OfType<ScrollViewer>().Single(); var presenter = VisualWalk(inner).OfType<ScrollContentPresenter>().Single();
            var text = VisualWalk(list).OfType<TextBlock>().Single(block => block.Text == displayText);
            inner.ScrollToVerticalOffset(text.TransformToAncestor(presenter).Transform(new Point()).Y + inner.VerticalOffset); Barrier(); root.UpdateLayout();
            Save(root, Path.Combine(directory, "direct-memory-overlay-220x300-start.png"));
            inner.ScrollToEnd(); Barrier(); root.UpdateLayout();
            var last = text.TransformToAncestor(presenter).TransformBounds(text.ContentEnd.GetCharacterRect(LogicalDirection.Backward));
            var metrics = $"list={point}/{list.ActualHeight}; outerViewport={outer.ViewportHeight}; innerPixel={!inner.CanContentScroll}; innerViewport={inner.ViewportHeight}; innerOffset={inner.VerticalOffset}; textHeight={text.ActualHeight}; lastLine={last}";
            Console.WriteLine("Direct memory overlay metrics: " + metrics);
            Save(root, Path.Combine(directory, "direct-memory-overlay-220x300-end.png"));
            Check(!inner.CanContentScroll && text.ActualHeight > presenter.ActualHeight && !last.IsEmpty && last.Height > 0 && last.Top >= -1 && last.Bottom <= presenter.ActualHeight + 1,
                "The last wrapped direct source/continuity line is not reachable by actual pixel scrolling: " + metrics);
            Console.WriteLine("Direct hand memory UI PNG directory: " + directory);
        }
        finally { root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static void Save(Visual root, string path) { var bitmap = new RenderTargetBitmap(220, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file); }
    private static BattleViewState View(MainWindow window) => (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
    private static void Pin(MainWindow window, GameSnapshot frame) { Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_liveFrame", frame); }
    private static IEnumerable<int> Ids(ComboBox box) => box.Items.Cast<object>().Select(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)!);
    private static string Workspace() { for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent) if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName; throw new FileNotFoundException("Direct-memory test workspace was not found."); }
    private static Button FindButton(DependencyObject root, string content) => Walk(root).OfType<Button>().Single(button => Equals(button.Content, content));
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Walk(child)) yield return item; }
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root) { yield return root; for (var index=0; index<VisualTreeHelper.GetChildrenCount(root); index++) foreach(var child in VisualWalk(VisualTreeHelper.GetChild(root,index))) yield return child; }
    private static T C<T>(FrameworkElement root, string name) where T:class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance,value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance,args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance,name,args)!;
    private static void Click(Button button) { Check(button.IsEnabled,"The actual direct-memory button is disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button)); }
    private static void ReplaceClient(MainWindow window, string name, HttpMessageHandler handler) { ((HttpClient)Field(window,name)!).Dispose(); Set(window,name,new HttpClient(handler,false)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,"A direct-memory check showed a window or created a native handle.");
    private static void Barrier() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle).Task);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(()=>task.IsCompleted,"Direct-memory UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready,string error) { if(ready())return; var frame=new DispatcherFrame(); var clock=Stopwatch.StartNew(); var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(10)}; timer.Tick+=(_,_)=>{if(ready()||clock.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;}; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(),error); }
    private static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS direct reveal hand memory UI: " + message);
    private sealed class RejectNetwork:HttpMessageHandler { public int Calls{get;private set;} protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;throw new InvalidOperationException("No HTTP is permitted in direct-memory UI checks.");} }
}
