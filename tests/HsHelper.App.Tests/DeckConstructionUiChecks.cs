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
using HsHelper.Storage;

internal static class DeckConstructionUiChecks
{
    private const string Build = "255790", OtherBuild = "999927", Key = "deck-construction-ui-fixture-key";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "deck-construction-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var oldLaunch = App.LaunchSettings; typeof(App).GetProperty(nameof(App.LaunchSettings))!.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new PlanningHandler();
        try
        {
            ReplaceHttp(window, "_http", handler); ReplaceHttp(window, "_cardHttp", new RejectNetwork());
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Cards.Tests", "Fixtures", "deck-size-255790.json")))!;
            var cards = fixture["cards"]!.AsArray(); Import(catalog, cards, OtherBuild); Import(catalog, cards, Build); Rules(catalog, cards, Build); Rules(catalog, cards, OtherBuild);
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            Set(window, "_manifest", Await(catalog.GetActiveManifestAsync()));
            var fillers = cards.Where(card => card!["cardClass"]!.ToString() == "NEUTRAL" && card["rarity"]?.ToString() != "LEGENDARY" && card["collectible"]?.GetValue<bool>() == true && card["type"]!.ToString() != "HERO").Select(card => card!["dbfId"]!.GetValue<int>()).ToArray();
            DeckDefinition Deck(int count, params int[] specials)
            {
                var main = specials.Select(dbf => new DeckCard(dbf, 1)).ToList(); var remaining = count - specials.Length;
                foreach (var dbf in fillers) { if (remaining <= 0) break; var copies = Math.Min(2, remaining); main.Add(new(dbf, copies)); remaining -= copies; }
                Check(remaining == 0, "The public fixture did not provide enough ordinary fillers.");
                return new(2, [813], main.ToArray(), []);
            }
            void Decode(DeckDefinition deck)
            {
                var code = DeckCode.Encode(deck); C<TextBox>(window, "DeckBox").Text = code;
                FindButton(window, "解析牌码").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wait(() => Field(window, "_deckDescriptionCancellation") is null, "Actual decode did not complete.");
                Check(C<TextBox>(window, "DeckBox").Text == code && Field(window, "_selectedDeckVersion") is null, "Description changed the deck code or invented saved provenance.");
            }
            string Text() => C<TextBox>(window, "DeckResultBox").Text;
            foreach (var (owner, required) in new[] { (126055, 20), (79767, 40) })
            {
                Decode(Deck(required, owner));
                Check(Text().Contains($"要求 {required} 张；满足") && Text().Contains("build " + Build) && Text().Contains("完整构筑检查：未知") && Text().Contains("张数来源："), "A known special size was hidden by partial pool legality: " + Text());
                Decode(Deck(30, owner)); Check(Text().Contains($"要求 {required} 张；不满足"), "The ordinary 30-card default erased the special requirement.");
            }
            Decode(Deck(30)); Check(Text().Contains("普通要求 30 张；满足") && Text().Contains("不是开局复制"), "Ordinary construction or its scope was misstated.");
            Pass("real decode buttons distinguish 20 and 40 card construction from ordinary 30 and partial overall legality without changing code or saved source");

            Decode(Deck(30, 126055, 79767));
            Check(Text().Contains("要求未知或存在冲突") && Text().Contains("要求 20 张") && Text().Contains("要求 40 张") && !Text().Contains("；满足。"), "Conflicting special requirements were silently resolved.");
            Decode(Deck(30, 104819)); Check(Text().Contains("要求未知或存在冲突"), "An unreviewed replacement deck was represented as ordinary 30.");
            var original = Await(store.SaveDeckVersionAsync("构筑来源A", Deck(20, 126055), Build));
            var other = Await(store.SaveDeckVersionAsync("构筑来源B", Deck(20, 126055), OtherBuild));
            Await(Call(window, "LoadDeckLibraryAsync", original.Id));
            Select(window, "SavedDeckBox", original.Id); Wait(() => Text().Contains("build " + Build) && Text().Contains("要求 20 张；满足"), "Original saved build was not used.");
            Import(catalog, cards, OtherBuild); Await(Call(window, "RefreshCatalogAsync"));
            Check(Text().Contains("build " + Build) && ((DeckVersion)Field(window, "_selectedDeckVersion")!).Id == original.Id, "An active catalog switch relabeled the saved source.");
            Select(window, "SavedDeckBox", other.Id); Wait(() => Text().Contains("build " + OtherBuild), "The actual saved-deck picker did not switch builds.");
            Check(Text().Contains("要求未知或存在冲突") && !Text().Contains("要求 20 张；满足"), "Old reviewed size leaked into an unreviewed build.");
            var reread = Await(store.ListDeckVersionsAsync()); Check(reread.Single(deck => deck.Id == original.Id).CardBuild == Build && reread.Single(deck => deck.Id == other.Id).CardBuild == OtherBuild, "Description rewrote saved provenance.");
            Pass("conflicting unknown and unreviewed build evidence stays explicit while the saved deck retains its original build across active catalog changes");

            C<TextBox>(window, "EndpointBox").Text = "https://deck-construction.invalid/v1/chat/completions"; C<TextBox>(window, "ModelBox").Text = "deck-size-ui"; C<PasswordBox>(window, "KeyBox").Password = Key;
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://deck-construction.invalid/v1/chat/completions"), "deck-size-ui", 0));
            Select(window, "PlanDeckBox", original.Id); Await(Call(window, "AnalyzeDeckPlanCoreAsync"));
            var projected = handler.Context?["Snapshot"]?["deck_construction"];
            Check(handler.Calls == 1 && projected?["card_build"]?.ToString() == Build && projected["actual_main_cards"]?.GetValue<int>() == 20 &&
                projected["special_main_deck_size"]?["required_cards"]?.GetValue<int>() == 20 && projected["special_main_deck_size"]?["known"]?.GetValue<bool>() == true &&
                projected["legality"]?.ToString() == "Unknown" && C<TextBox>(window, "AdviceBox").Text.Contains("DECK_SIZE_PLAN_HOST"), "The actual pre-game HTTP context omitted or upgraded host construction facts.");
            var archived = Await(archive.ListAsync(new AdviceArchiveFilter(MatchId: "deck-" + original.Id)));
            Check(archived.Count == 1 && archived[0].Advice.PromptTemplateVersion == "hunter-v8/deckplan" && archived[0].Advice.DeckVersionId == original.Id && archived[0].Advice.CardBuild == Build,
                "The real plan archive lost its source build or current planning template.");
            var unknownSource = Await(store.SaveDeckVersionAsync("构筑来源版本未知", Deck(20, 126055), "unknown"));
            var unknownCode = unknownSource.Code;
            Import(catalog, cards, Build); Await(Call(window, "RefreshCatalogAsync"));
            Check(((CardManifest)Field(window, "_manifest")!).Build == Build, "The counterexample did not actually activate the reviewed build.");
            Await(Call(window, "LoadDeckLibraryAsync", unknownSource.Id));
            Select(window, "SavedDeckBox", unknownSource.Id); Wait(() => Text().Contains("卡库版本未知"), "The unknown saved source borrowed the editor's active build.");
            Select(window, "PlanDeckBox", unknownSource.Id); Await(Call(window, "AnalyzeDeckPlanCoreAsync"));
            var unknownContext = handler.Context!; var unknownSnapshot = unknownContext["Snapshot"]!; var unknownConstruction = unknownSnapshot["deck_construction"]!;
            Check(handler.Calls == 2 && unknownContext["CardBuild"]?.ToString() == "unknown" && unknownSnapshot["card_build"] is null &&
                unknownSnapshot["cards"]!.AsArray().Count == 0 && unknownConstruction["card_build"]?.ToString() == "unknown" &&
                unknownConstruction["legality"]?.ToString() == "Unknown" && unknownConstruction["special_main_deck_size"] is null &&
                unknownConstruction["pool_rule_id"] is null && unknownConstruction["issues"]!.AsArray().Any(issue => issue?.ToString().Contains("没有固定卡库版本", StringComparison.Ordinal) == true),
                "Actual plan HTTP upgraded an unknown saved build using active reviewed card definitions or a known twenty-card rule.");
            var unknownArchive = Await(archive.ListAsync(new AdviceArchiveFilter(MatchId: "deck-" + unknownSource.Id)));
            var unknownReread = Await(store.GetDeckVersionAsync(unknownSource.Id));
            Check(unknownArchive.Count == 1 && unknownArchive[0].Advice.CardBuild == "unknown" && unknownArchive[0].Advice.DeckVersionId == unknownSource.Id &&
                unknownArchive[0].Advice.PromptTemplateVersion == "hunter-v8/deckplan" && unknownReread is not null &&
                unknownReread.Id == unknownSource.Id && unknownReread.Code == unknownCode && unknownReread.CardBuild == "unknown" && C<TextBox>(window, "DeckBox").Text == unknownCode,
                "Planning rewrote the unknown saved deck's source/code or archived the active build as its original provenance.");
            Pass("actual planning fake HTTP and archive retain known partial evidence and unknown saved provenance independently of the active reviewed build");

            Select(window, "SavedDeckBox", original.Id); Wait(() => Field(window, "_deckDescriptionCancellation") is null, "Saved description did not settle.");
            foreach (var fail in new[] { false, true })
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var hits = 0;
                Set(window, "_deckDescriptionReadCompleted", (Func<DeckDescription, CancellationToken, Task>)(async (_, _) => { if (Interlocked.Increment(ref hits) != 1) return; reached.SetResult(); await release.Task; if (fail) throw new IOException("STALE_DECK_SIZE_ERROR"); }));
                var stale = Call(window, "RefreshDeckDescriptionAsync"); Await(reached.Task);
                Select(window, "SavedDeckBox", other.Id); Wait(() => Text().Contains("build " + OtherBuild), "New deck description did not replace pending old data.");
                C<TextBlock>(window, "StatusText").Text = "NEW_DECK_SIZE_STATUS"; release.SetResult(); Await(stale);
                Check(Text().Contains("build " + OtherBuild) && !Text().Contains("要求 20 张；满足") && C<TextBlock>(window, "StatusText").Text == "NEW_DECK_SIZE_STATUS", "A late success/error replaced the new build or status.");
                Set(window, "_deckDescriptionReadCompleted", null); Select(window, "SavedDeckBox", original.Id); Wait(() => Text().Contains("build " + Build), "The known deck did not restore.");
            }
            Layout(window, directory);
            var closedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var closedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_deckDescriptionReadCompleted", (Func<DeckDescription, CancellationToken, Task>)(async (_, _) => { closedReached.SetResult(); await closedRelease.Task; throw new IOException("CLOSED_DECK_SIZE_ERROR"); }));
            var closingRead = Call(window, "RefreshDeckDescriptionAsync"); Await(closedReached.Task); window.Close();
            var closingText = Text(); closedRelease.SetResult(); Await(closingRead); Pump();
            Check(Text() == closingText && !C<TextBlock>(window, "StatusText").Text.Contains("CLOSED_DECK_SIZE_ERROR"), "Closing allowed a late description to publish.");
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "The hidden regression created a native window.");
            Pass("post real catalog IO stale successes failures and close cannot publish old size while the fixed version and final result remain reachable at finite size");
        }
        finally
        {
            Set(window, "_allowClose", true); window.Close();
            typeof(App).GetProperty(nameof(App.LaunchSettings))!.SetValue(null, oldLaunch);
        }
    }

    private static void Rules(CardCatalog catalog, JsonArray cards, string build)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1); var until = from.AddDays(3);
        var evidence = new DeckConstructionEvidence("ui-size-evidence-" + build, build, 2, from, until, "fixture://reviewed-size", [], [])
        { MainDeckSizes = [new("JAIL_430", 20, true), new("REV_018", 40, true)] };
        var pool = new CardPoolRules("ui-size-pool-" + build, build, 2, from, until, "fixture://partial-pool",
            cards.Select(card => card!["set"]!.ToString()).Distinct().ToArray(), [], [], [], false) { Construction = evidence };
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(pool)); Await(catalog.ImportPoolRulesAsync(stream));
    }
    private static void Import(CardCatalog catalog, JsonArray cards, string build)
    { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(stream, build, source: "fixture://deck-size-ui")); }
    private static void Select(MainWindow window, string name, string id)
    {
        var box = C<ComboBox>(window, name); var item = box.Items.Cast<object>().Single(value => ((DeckVersion)value.GetType().GetProperty("Value")!.GetValue(value)!).Id == id);
        if (ReferenceEquals(box.SelectedItem, item)) { Set(window, "_loadingDecks", true); try { box.SelectedItem = null; } finally { Set(window, "_loadingDecks", false); } }
        box.SelectedItem = item;
    }
    private static void ReplaceHttp(MainWindow window, string name, HttpMessageHandler handler)
    { var old = (HttpClient)Field(window, name)!; Set(window, name, new HttpClient(handler)); old.Dispose(); }
    private static Task Call(MainWindow window, string name, params object?[] args) => (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args)!;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T C<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new Exception("Missing control " + name);
    private static Button FindButton(DependencyObject root, string text) => Walk(root).OfType<Button>().Single(button => Equals(button.Content, text));
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Walk(child)) yield return item; }
    private static string Workspace() { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "AGENTS.md"))) return path.FullName; throw new FileNotFoundException("Deck size fixture missing."); }
    private static void Await(Task task) { Wait(() => task.IsCompleted, "Deck construction operation timed out."); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump() { var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); Await(operation.Task); }
    private static void Wait(Func<bool> done, string message) { var timer = Stopwatch.StartNew(); while (!done()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception(message); var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); } }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pass(string text) => Console.WriteLine("PASS deck construction UI: " + text);

    private static void Layout(MainWindow window, string directory)
    {
        var box = C<TextBox>(window, "DeckResultBox"); var old = window.Content; var content = (FrameworkElement)old; var resources = content.Resources;
        var xaml = XDocument.Load(Path.Combine(Workspace(), "src", "HsHelper.App", "App.xaml")); var node = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        content.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"), node.Elements()).ToString());
        var tabs = C<TabControl>(window, "MainTabs"); var previous = tabs.SelectedIndex;
        try
        {
            tabs.SelectedIndex = tabs.Items.Cast<TabItem>().Select((item, index) => (item, index)).Single(pair => Walk(pair.item).Contains(box)).index;
            window.Content = null; content.Measure(new Size(640, 460)); content.Arrange(new Rect(0, 0, 640, 460)); content.UpdateLayout(); Pump();
            Check(box.ActualHeight > 60 && box.Text.Contains("要求 20 张；满足"), "Finite deck result lost its actual size proof.");
            var outer = C<ScrollViewer>(window, "DeckDescriptionScroll"); var presenter = Visuals(outer).OfType<ScrollContentPresenter>().First();
            foreach (var button in new[] { FindButton(content, "解析牌码"), FindButton(content, "保存到套牌库"), C<Button>(window, "ExportDeckJsonButton"), C<Button>(window, "ExportDeckCodeButton") })
            {
                button.BringIntoView(); Pump(); content.UpdateLayout(); var rect = button.TransformToAncestor(presenter).TransformBounds(new Rect(button.RenderSize));
                Check(rect.Top >= -1 && rect.Bottom <= presenter.ActualHeight + 1, "An actual deck workflow button cannot be reached.");
            }
            box.ScrollToHome(); Pump(); content.UpdateLayout(); Reveal(box.Text.IndexOf("主牌构筑张数", StringComparison.Ordinal)); Save("size");
            Reveal(box.Text.IndexOf("完整构筑检查", StringComparison.Ordinal)); Save("legality");
            box.ScrollToEnd(); Pump(); content.UpdateLayout(); Reveal(box.Text.Length - 1); Save("end");
            Console.WriteLine($"Deck construction layout: root={content.ActualWidth}x{content.ActualHeight}; result={box.ActualWidth}x{box.ActualHeight}; outerViewport={outer.ViewportHeight}; extent={outer.ExtentHeight}; PNG={directory}");
            void Reveal(int index)
            {
                Check(index >= 0, "Required construction text missing.");
                var rect = box.TransformToAncestor(presenter).TransformBounds(box.GetRectFromCharacterIndex(index));
                outer.ScrollToVerticalOffset(outer.VerticalOffset + rect.Top - 8); Pump(); content.UpdateLayout();
                rect = box.TransformToAncestor(presenter).TransformBounds(box.GetRectFromCharacterIndex(index));
                Check(!rect.IsEmpty && rect.Top >= -1 && rect.Bottom <= presenter.ActualHeight + 1, "Construction proof or final line cannot be reached through actual inner and outer scrolling.");
            }
            void Save(string suffix)
            {
                var bitmap = new RenderTargetBitmap(640, 460, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 460));
                bitmap.Render(background); bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "deck-construction-640x460-" + suffix + ".png")); encoder.Save(file);
            }
        }
        finally { content.Resources = resources; window.Content = old; tabs.SelectedIndex = previous; }
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root) { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private sealed class RejectNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected external card HTTP."); }
    private sealed class PlanningHandler : HttpMessageHandler
    {
        public int Calls; public JsonNode? Context;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Check(request.RequestUri?.Host == "deck-construction.invalid" && request.Headers.Authorization?.Parameter == Key, "Unexpected model destination or synthetic credential.");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; Context = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>());
            var content = JsonSerializer.Serialize(new { summary = "DECK_SIZE_PLAN_HOST", reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>() });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } }, usage = new { prompt_tokens = 8, completion_tokens = 2, total_tokens = 10 } }), Encoding.UTF8, "application/json") };
        }
    }
}
