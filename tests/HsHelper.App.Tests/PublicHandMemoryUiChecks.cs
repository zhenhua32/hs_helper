using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>The public identity ledger has a separate UI and never restores a private card projection.</summary>
internal static class PublicHandMemoryUiChecks
{
    private const string Build = "255790", Remembered = "MEMORY_PUBLIC_BODY", Name = "公开回手样本", PrivateGift = "MEMORY_PRIVATE_ENCH";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "public-hand-memory-app", "data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var prior = launch.GetValue(null);
        var oldData = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR"); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", directory);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); var closed = false; using var rejected = new RejectNetwork();
        try
        {
            ReplaceClient(window, "_http", rejected); ReplaceClient(window, "_cardHttp", rejected);
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync()); Import(catalog, Build, false); Import(catalog, "999999", true);
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var engine = Engine("main"); var frame = engine.Snapshot(); var before = SnapshotCodec.Serialize(frame);
            Check(PublicHandMemoryVisibility.Confirmed(frame)?.Entries is { Count: 1 }, "Real public PLAY-to-HAND replay did not create one bound identity memory.");
            Pin(window, frame); InvokeObject(window, "RenderSnapshot"); Await(Invoke(window, "RenderBattleAsync")); Barrier();
            var view = View(window); var row = view.OpponentHandMemory.Single();
            Check(row.Name == Name && row.CardId == Remembered && row.PublicZone == "公开场上" && row.PublicAtSequence > 0 &&
                row.EnteredHandAtSequence >= row.PublicAtSequence && view.Opponent.Hand.Count == 0 && frame.Entities[30].CardId is null &&
                frame.Entities[30].Cost is null && !frame.Entities.ContainsKey(50), "Public identity memory became a current hand identity/stat or exposed its private attachment.");
            Check(C<DataGrid>(window, "OpponentHandMemoryGrid").Items.OfType<BattleHandMemoryView>().Single().Name == Name &&
                C<DataGrid>(window, "OpponentHandMemoryGrid").IsReadOnly && !C<DataGrid>(window, "OpponentHandMemoryGrid").AutoGenerateColumns &&
                C<TextBlock>(window, "OpponentHandMemoryStatusText").Text.Contains("当前费用、增益、位置及效果未知", StringComparison.Ordinal), "The actual main controls did not display separate identity-only memory.");
            var propertyNames = typeof(BattleHandMemoryView).GetProperties().Select(property => property.Name).ToHashSet();
            Check(!new[] { "EntityId", "Cost", "Attack", "Health", "Position", "Status", "EffectiveText" }.Any(propertyNames.Contains) &&
                !JsonSerializer.Serialize(row).Contains("PRIVATE_CURRENT_EFFECT", StringComparison.Ordinal), "A memory row exposes actionable entities or current/printed effect data.");
            var tools = Tools(window); var names = (IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!;
            Check(names[Remembered].Name == Name && !names.ContainsKey(PrivateGift) && !names.ContainsKey("MEMORY_NEVER_PUBLIC"), "Fixed-build name preload lost public memory or loaded private identities.");
            foreach (var kind in Enumerable.Range(0, 4))
            {
                C<ComboBox>(tools, "LocalKindBox").SelectedIndex = kind;
                Check(!Ids(C<ComboBox>(tools, "LocalSourceBox")).Contains(30) && !Ids(C<ComboBox>(tools, "LocalTargetBox")).Contains(30), "A remembered opponent hand entity became a calculator source or target.");
            }
            Check(!C<ComboBox>(tools, "LocalCardBox").Items.Cast<object>().Any(item => item.GetType().GetProperty("CardId")!.GetValue(item)?.ToString() == Remembered), "Identity memory was inserted into the deck-odds inventory.");
            var effect = Await(((SnapshotCoach)Field(tools, "_coach")!).ExecuteAsync("get_entity_effect", new() { ["entity_id"] = 30 }, CancellationToken.None));
            Check(effect["error"] is not null && !effect.ToJsonString().Contains(PrivateGift, StringComparison.Ordinal) && SnapshotCodec.Serialize(frame) == before,
                "Name preloading restored a private entity effect or mutated the original snapshot.");
            Pass("real main battle controls and fixed-build preload show public identity memory separately while hand attributes attachments and calculator entities remain private");

            var recorder = new SessionRecorder(store, new(null, Build, 1)); recorder.Observe(frame, 1); Await(recorder.CompleteAsync());
            var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single(); Set(window, "_historyContext", null); Set(window, "_liveFrame", frame); Set(window, "_engine", engine);
            Click(FindButton(window, "复盘最后观察局面"));
            WaitUntil(() => Field(window, "_historyContext") is HistoricalContext context && context.Snapshot.MatchId == frame.MatchId && !(bool)Field(window, "_historyLoading")!, "The actual last-observed button did not publish its strict saved memory frame.");
            Check((DateTimeOffset?)Field(window, "_historyObservedAt") == point.RecordedAt && C<TextBox>(window, "HistorySnapshotText").Text.Contains(Name, StringComparison.Ordinal) &&
                C<TextBox>(window, "HistorySnapshotText").Text.Contains("对手公开手牌记忆", StringComparison.Ordinal), "Strict historical name preload or knowledge cutoff lost the saved memory source.");
            var overlay = new OverlayWindow(); Set(window, "_overlay", overlay); Await(Invoke(window, "UpdateOverlayStateAsync", overlay, false));
            Check(overlay.State.OpponentHandMemory.Single().Contains(Name, StringComparison.Ordinal) && overlay.State.OpponentHandMemory.Single().Contains("仅身份记忆", StringComparison.Ordinal) &&
                C<ListBox>(overlay, "OpponentMemoryList").Items.Count == 1 && !overlay.State.Hand.Any(text => text.Contains(Name, StringComparison.Ordinal)) &&
                !JsonSerializer.Serialize(overlay.State).Contains("PRIVATE_CURRENT_EFFECT", StringComparison.Ordinal), "The actual hidden overlay mixed remembered identity with current hand or effect data.");
            Layout(window, overlay, directory); Passive(window); Passive(overlay);
            Pass("the real saved-decision reader and hidden overlay retain public source sequences and exact historical binding with reachable independent memory controls");

            var memory = frame.PublicHandMemory!;
            PublicHandMemorySnapshot Ledger(string? match = null, long? state = null, long? seq = null, int? local = null, string? perspective = null, IReadOnlyList<PublicHandMemoryEntry>? entries = null) =>
                new(match ?? memory.MatchId, state ?? memory.StateVersion, seq ?? memory.EventSequence, local ?? memory.LocalPlayerId, perspective ?? memory.PerspectiveSource,
                    entries ?? memory.Entries, true, [], true);
            var invalid = new[] {
                frame with { PublicHandMemory = null }, frame with { PublicHandMemory = Ledger(match: "wrong-match") },
                frame with { PublicHandMemory = Ledger(state: frame.StateVersion + 1) }, frame with { PublicHandMemory = Ledger(seq: frame.EventSequence + 1) },
                frame with { PublicHandMemory = Ledger(local: 2) }, frame with { PublicHandMemory = Ledger(perspective: "unconfirmed") },
                frame with { PublicHandMemory = Ledger(entries: [memory.Entries[0] with { EnteredHandAtSequence = frame.EventSequence + 1 }]) },
                frame with { PublicHandMemory = Ledger(entries: [memory.Entries[0], memory.Entries[0]]) },
                frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } } };
            foreach (var denied in invalid)
            {
                Pin(window, denied); Await(Invoke(window, "RenderBattleAsync")); Await(Invoke(window, "UpdateOverlayStateAsync", overlay, false)); tools = Tools(window);
                Check(View(window).OpponentHandMemory.Count == 0 && overlay.State.OpponentHandMemory.Count == 0 &&
                    !BattleViewBuilder.VisibleCardIds(denied, Build).Contains(Remembered) && !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(Remembered),
                    "An unbound unknown future duplicated or spectator memory entered a view or name preload.");
            }
            var gap = Engine("gap"); gap.MarkDataGap("synthetic lost segment"); gap.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete");
            Pin(window, gap.Snapshot()); Await(Invoke(window, "RenderBattleAsync"));
            Check(View(window).OpponentHandMemory.Count == 0 && View(window).OpponentHandMemoryStatus.Contains("遗漏", StringComparison.Ordinal), "A gap retained an old unproved identity or became a complete empty memory.");
            gap.ProcessLine("TAG_CHANGE Entity=30 tag=ZONE value=PLAY");
            gap.ProcessLine("SHOW_ENTITY - Updating Entity=[entityName=sample id=30 zone=PLAY zonePos=1 cardId=MEMORY_PUBLIC_BODY player=2] CardID=MEMORY_PUBLIC_BODY");
            gap.ProcessLine("TAG_CHANGE Entity=30 tag=ZONE value=HAND"); gap.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete");
            var fresh = gap.Snapshot(); Pin(window, fresh); Await(Invoke(window, "RenderBattleAsync")); Await(Invoke(window, "UpdateOverlayStateAsync", overlay, false));
            Check(View(window).OpponentHandMemory.Single().Name == Name && View(window).OpponentHandMemoryStatus.Contains("遗漏", StringComparison.Ordinal) &&
                overlay.State.OpponentHandMemory.Count == 1 && !fresh.Quality.IsComplete, "A new settled public proof after a gap was discarded or mislabeled as complete.");
            Pass("unknown mismatched future duplicated and spectator ledgers are hidden while a real new post-gap public proof is shown explicitly as incomplete");

            foreach (var kind in new[] { "battle", "overlay" }) foreach (var fail in new[] { false, true })
            {
                var old = Engine("old-" + kind + fail).Snapshot(); Pin(window, old);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (actual, captured, build, _) =>
                { Check(actual == kind && captured.MatchId == old.MatchId && build == Build, "The memory gate did not follow real pinned-build SQL."); entered.SetResult(); await release.Task; if (fail) throw new IOException("OLD_MEMORY_READ"); }));
                var pending = kind == "battle" ? Invoke(window, "RenderBattleAsync") : Invoke(window, "UpdateOverlayStateAsync", overlay, false); Await(entered.Task);
                Set(window, "_aggregatedCardReadCompleted", null); Pin(window, old with { PublicHandMemory = null, StateVersion = old.StateVersion + 1 });
                Await(kind == "battle" ? Invoke(window, "RenderBattleAsync") : Invoke(window, "UpdateOverlayStateAsync", overlay, false));
                var currentView = C<FrameworkElement>(window, "BattleViewRoot").DataContext; var currentOverlay = overlay.State; release.SetResult(); Await(pending);
                Check(ReferenceEquals(currentView, C<FrameworkElement>(window, "BattleViewRoot").DataContext) && ReferenceEquals(currentOverlay, overlay.State), "A delayed old public memory success/failure replaced a newer binding.");
            }
            foreach (var kind in new[] { "battle", "overlay" }) foreach (var reason in new[] { "cleared", "perspective" })
            {
                Barrier(); var old = Engine("same-keys-" + kind + reason).Snapshot(); Set(window, "_historyContext", null); Set(window, "_liveFrame", old);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (_, _, _, _) => { entered.SetResult(); await release.Task; }));
                var pending = kind == "battle" ? Invoke(window, "RenderBattleAsync") : Invoke(window, "UpdateOverlayStateAsync", overlay, false); Await(entered.Task);
                var viewBefore = C<FrameworkElement>(window, "BattleViewRoot").DataContext; var overlayBefore = overlay.State;
                Set(window, "_liveFrame", reason == "cleared" ? old with { PublicHandMemory = null } : old with { PerspectiveSource = "initial_private_hand" });
                release.SetResult(); Await(pending); Set(window, "_aggregatedCardReadCompleted", null);
                Check(ReferenceEquals(viewBefore, C<FrameworkElement>(window, "BattleViewRoot").DataContext) && ReferenceEquals(overlayBefore, overlay.State),
                    "An old memory read published after only the bound evidence/perspective changed, despite identical match/state/event keys.");
            }
            Pin(window, frame); var closingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var closingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (_, _, _, _) => { closingEntered.SetResult(); await closingRelease.Task; throw new IOException("CLOSED_MEMORY_READ"); }));
            var closingRead = Invoke(window, "UpdateOverlayStateAsync", overlay, false); Await(closingEntered.Task); var priorOverlay = overlay.State;
            Set(window, "_allowClose", true); window.Close(); closed = true; closingRelease.SetResult(); Await(closingRead);
            Check(ReferenceEquals(priorOverlay, overlay.State) && rejected.Calls == 0, "A closed host published old memory or the read-only UI made an external request."); Passive(window); Passive(overlay);
            Pass("post-SQL old successes failures and host close cannot publish remembered identities across another frame or a closed overlay");
        }
        finally
        {
            Set(window, "_aggregatedCardReadCompleted", null); if (!closed) { Set(window, "_allowClose", true); window.Close(); }
            launch.SetValue(null, prior); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", oldData);
        }
    }

    private static PowerLogStateEngine Engine(string suffix)
    {
        var engine = new PowerLogStateEngine(1, "public-hand-memory-ui-" + suffix);
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=NUM_CARDS_IN_HAND value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21", "tag=NUM_CARDS_IN_HAND value=2");
        foreach (var (id, card, owner) in new[] { (11, "HERO_05", 1), (21, "HERO_08", 2) })
            Feed($"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0");
        Feed("FULL_ENTITY - Creating ID=30 CardID=MEMORY_PUBLIC_BODY", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=7", "tag=HEALTH value=7", "tag=DAMAGE value=0",
            "TAG_CHANGE Entity=30 tag=ZONE value=HAND", "TAG_CHANGE Entity=30 tag=COST value=0", "TAG_CHANGE Entity=30 tag=ATK value=99", "TAG_CHANGE Entity=30 tag=HEALTH value=88", "TAG_CHANGE Entity=30 tag=ZONE_POSITION value=4",
            "FULL_ENTITY - Creating ID=40 CardID=MEMORY_OWN_BODY", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=1", "tag=HEALTH value=2", "tag=DAMAGE value=0",
            "FULL_ENTITY - Creating ID=41 CardID=MEMORY_OWN_BODY", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=COST value=1",
            "FULL_ENTITY - Creating ID=70 CardID=MEMORY_NEVER_PUBLIC", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=COST value=6",
            "FULL_ENTITY - Creating ID=50 CardID=MEMORY_PRIVATE_ENCH", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }
    private static void Import(CardCatalog catalog, string build, bool decoy)
    {
        var cards = new[] { (Remembered, Name, "MINION", "BASE_ONLY_NOT_CURRENT_TEXT"), ("MEMORY_OWN_BODY", "我方样本", "MINION", ""),
            ("MEMORY_NEVER_PUBLIC", "PRIVATE_UNSEEN_NAME", "MINION", ""), (PrivateGift, "PRIVATE_ENCHANT_NAME", "ENCHANTMENT", "PRIVATE_CURRENT_EFFECT"),
            ("HERO_05", "我方英雄", "HERO", ""), ("HERO_08", "对手英雄", "HERO", "") }
            .Select((value, index) => new { id = value.Item1, dbfId = 980000 + index, name = decoy ? "WRONG_MEMORY_BUILD" : value.Item2,
                type = value.Item3, text = value.Item4, cardClass = "NEUTRAL", cost = 7, attack = 7, health = 7, collectible = false, set = "SYNTHETIC", mechanics = Array.Empty<string>() });
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards)); Await(catalog.ImportAsync(input, build, source: "fixture://public-hand-memory-ui"));
    }
    private static void Layout(MainWindow window, OverlayWindow overlay, string directory)
    {
        var root = (FrameworkElement)window.Content; var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml"));
        var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString());
        root.Resources.MergedDictionaries.Add(styles);
        try
        {
            var tab = C<TabControl>(window, "MainTabs"); tab.SelectedIndex = 0; root.Measure(new(760, 480)); root.Arrange(new Rect(0, 0, 760, 480)); root.UpdateLayout(); Barrier();
            var memory = C<DataGrid>(window, "OpponentHandMemoryGrid"); var battleScroll = C<ScrollViewer>(window, "BattleDetailsScroll"); var header = C<ScrollViewer>(window, "BattleHeaderScroll");
            foreach (var label in new[] { "自动诊断", "本地概率与动作计算", "复盘最后观察局面" })
            {
                var control = FindButton(window, label); control.BringIntoView(); Barrier(); root.UpdateLayout();
                var rect = control.TransformToAncestor(header).Transform(new Point());
                Check(rect.Y >= -1 && rect.Y + control.ActualHeight <= header.ViewportHeight + 1, "A bounded log header made an original action unreachable: " + label);
            }
            memory.BringIntoView(); Barrier(); root.UpdateLayout(); var memoryPoint = memory.TransformToAncestor(battleScroll).Transform(new Point());
            Console.WriteLine($"Public memory main metrics: table={memory.ActualWidth}/{memory.ActualHeight}; point={memoryPoint}; viewport={battleScroll.ViewportHeight}; header={header.ViewportHeight}");
            Check(memory.ActualHeight >= 45 && memory.ActualWidth > 100 && memory.Columns.Count == 1 && battleScroll.ViewportHeight >= 60 &&
                memoryPoint.Y >= -1 && memoryPoint.Y + memory.ActualHeight <= battleScroll.ViewportHeight + 1, "The actual main memory table cannot be scrolled fully into its finite viewport.");
            Save(root, new(760, 480), Path.Combine(directory, "memory-main-760x480.png"), white: true);
            var content = (FrameworkElement)overlay.Content;
            foreach (var size in new[] { new Size(360, 620), new Size(220, 300) })
            {
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout(); Barrier(); content.UpdateLayout();
                var scroll = C<ScrollViewer>(overlay, "OverlayContentScroll"); var list = C<ListBox>(overlay, "OpponentMemoryList");
                scroll.ScrollToEnd(); Barrier(); content.UpdateLayout();
                var point = list.TransformToAncestor(scroll).Transform(new Point());
                Check(list.ActualHeight >= 20 && point.Y >= -1 && point.Y + list.ActualHeight <= scroll.ViewportHeight + 1 &&
                    point.X >= -1 && point.X + list.ActualWidth <= scroll.ViewportWidth + 1 && scroll.ViewportHeight > 100,
                    $"The actual overlay memory list was unreachable: {size}, rect={point}/{list.ActualHeight}, viewport={scroll.ViewportHeight}.");
                var inner = VisualWalk(list).OfType<ScrollViewer>().Single();
                var presenter = VisualWalk(inner).OfType<ScrollContentPresenter>().Single();
                var text = VisualWalk(list).OfType<TextBlock>().Single(block => block.Text == overlay.State.OpponentHandMemory.Single());
                inner.ScrollToTop(); Barrier(); content.UpdateLayout();
                Save(content, size, Path.Combine(directory, $"memory-overlay-{(int)size.Width}x{(int)size.Height}-top.png"));
                inner.ScrollToEnd(); Barrier(); content.UpdateLayout();
                var lastLocal = text.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                var lastVisible = text.TransformToAncestor(presenter).TransformBounds(lastLocal);
                var metrics = $"size={size}; outerRect={point}/{list.ActualWidth}/{list.ActualHeight}; outerViewport={scroll.ViewportWidth}/{scroll.ViewportHeight}; outerOffset={scroll.VerticalOffset}; innerCanContentScroll={inner.CanContentScroll}; innerExtent={inner.ExtentHeight}; innerViewport={inner.ViewportHeight}; innerOffset={inner.VerticalOffset}; presenter={presenter.ActualWidth}/{presenter.ActualHeight}; textHeight={text.ActualHeight}; lastLine={lastVisible}";
                Console.WriteLine("Public memory overlay end metrics: " + metrics);
                Save(content, size, Path.Combine(directory, $"memory-overlay-{(int)size.Width}x{(int)size.Height}-end.png"));
                if (size.Width == 220) Check(text.ActualHeight > presenter.ActualHeight + 1, "The minimum-size fixture did not exercise an item taller than its viewport: " + metrics);
                Check(!lastLocal.IsEmpty && lastVisible.Height > 0 && lastVisible.Top >= -1 && lastVisible.Bottom <= presenter.ActualHeight + 1,
                    "The full wrapped memory DisplayText cannot scroll to its last line: " + metrics);
            }
            Console.WriteLine("Public hand memory UI PNG directory: " + directory);
        }
        finally { root.Resources.MergedDictionaries.Remove(styles); }
    }
    private static void Save(Visual visual, Size size, string path, bool white = false) { var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); if (white) { var background = new DrawingVisual(); using (var draw = background.RenderOpen()) draw.DrawRectangle(Brushes.White, null, new Rect(size)); bitmap.Render(background); } bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file); }
    private static BattleViewState View(MainWindow window) => (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
    private static void Pin(MainWindow window, GameSnapshot frame) { Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_liveFrame", frame); }
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static IEnumerable<int> Ids(ComboBox box) => box.Items.Cast<object>().Select(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)!);
    private static string Workspace() { for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent) if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName; throw new FileNotFoundException("Public hand memory test workspace was not found."); }
    private static Button FindButton(DependencyObject root, string content) => Walk(root).OfType<Button>().Single(button => Equals(button.Content, content));
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Walk(child)) yield return item; }
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root) { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in VisualWalk(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "The actual memory UI button was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void ReplaceClient(MainWindow window, string name, HttpMessageHandler handler) { ((HttpClient)Field(window, name)!).Dispose(); Set(window, name, new HttpClient(handler, false)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A memory regression showed a window or created a native handle.");
    private static void Barrier() => Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Public hand memory UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string error) { if (ready()) return; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (ready() || clock.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), error); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS public hand memory UI: " + message);
    private sealed class RejectNetwork : HttpMessageHandler { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; throw new InvalidOperationException("No network is permitted in the public hand memory UI fixture."); } }
}
