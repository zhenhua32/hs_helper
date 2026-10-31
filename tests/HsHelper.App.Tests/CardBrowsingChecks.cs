using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
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

/// <summary>Actual hidden MainWindow controls, real SQLite reads and bounded post-IO completion gates.</summary>
internal static class CardBrowsingChecks
{
    private const string A = "991071", B = "991072";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Root = "CB_ROOT", Many = "CB_MANY", Cross = "CB_TOKEN_CROSS", Cycle = "CB_CYCLE", Missing = "CB_ONLY_IN_B";

    public static void RunOnDispatcher(MainWindow window)
    {
        var catalog = (CardCatalog)Field(window, "_catalog")!;
        var directory = Path.Combine((string)Field(window, "_dataRoot")!, "hunter-card-browser"); Directory.CreateDirectory(directory);
        var first = Fixture(false); var second = Fixture(true);
        var oldHttp = (HttpClient)Field(window, "_cardHttp")!;
        using var images = new FakeImages(); using var http = new HttpClient(images);
        Set(window, "_cardHttp", http);
        try
        {
            Activate(catalog, B, second); Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync"));
            Search(window, "", 0, true);
            Check(Rows(window).Select(card => card.Id).Order().SequenceEqual(new[] { Many, Root }.Order()), "Default search leaked tokens or foreign collectible cards.");
            Search(window, "", 1, false);
            Check(Rows(window).Count == 200 && C<TextBlock>(window, "CardSearchStatusText").Text.Contains("超过 200", StringComparison.Ordinal),
                "Broad all-card search silently omitted results without its explicit limit notice.");
            Search(window, "CB_EMPTY", 2, false); Select(window, "CB_EMPTY");
            Check(Detail(window).Contains(new string('<', 100), StringComparison.Ordinal) &&
                C<TextBlock>(window, "RelatedPageText").Text.Contains("未提供关联记录", StringComparison.Ordinal) &&
                C<TextBlock>(window, "RelatedPageText").Text.Contains("不代表没有衍生牌", StringComparison.Ordinal),
                "Long literal card text crashed or absent entourage metadata was presented as proof of no derivatives.");
            Search(window, Cross, 1, true); Check(Rows(window).Count == 0, "Hunter-only search accepted a Mage token.");
            Search(window, Cross, 1, false); Check(Rows(window) is [{ Id: Cross, Collectible: false }], "All-card/all-class search could not find a token.");
            C<CheckBox>(window, "StandardPoolBox").IsChecked = true;
            C<ComboBox>(window, "CardCollectibleBox").SelectedIndex = 2;
            Click(window, "CardSearchButton"); Await(TaskField(window, "_cardSearchTask"));
            Check(C<CheckBox>(window, "StandardPoolBox") is { IsChecked: false, IsEnabled: false } && Rows(window) is [{ Id: Cross }] &&
                C<TextBlock>(window, "CardSearchStatusText").Text.Contains("不可收藏", StringComparison.Ordinal), "Noncollectible mode silently retained an impossible Standard filter.");
            Pass("real search controls distinguish collectible all and noncollectible cards and clear the contradictory Standard filter");

            Search(window, Root, 0, true); Select(window, Root);
            Check(Related(window).Select(row => row.CardId).SequenceEqual(new[] { Cross, Missing, Cycle }), "Direct entourage order was lost or recursively expanded.");
            Activate(catalog, B, second); // Active data changes; the already displayed search/detail remains pinned to A.
            C<DataGrid>(window, "CardGrid").SelectedItem = null; Select(window, Root);
            var absent = Related(window).Single(row => row.CardId == Missing);
            C<DataGrid>(window, "RelatedCardsGrid").SelectedItem = absent;
            Check(absent.IsMissing && !C<Button>(window, "OpenRelatedCardButton").IsEnabled && Detail(window).Contains(A), "A missing related ID fell back to active build B.");
            Open(window, Cross); Check(Detail(window).Contains("TOKEN_A") && Detail(window).Contains("MAGE") && Detail(window).Contains("不可收藏"), "Related navigation inherited collectible/class search filters.");
            Open(window, Root); Check(Detail(window).Contains("ROOT_A"), "Explicit cycle navigation failed.");
            Click(window, "CardBackButton"); WaitDetail(window, "TOKEN_A");
            Click(window, "CardBackButton"); WaitDetail(window, "ROOT_A");
            Check(!C<Button>(window, "CardBackButton").IsEnabled && Related(window).Count == 3, "Explicit cycle traversal grew an automatic recursive history.");
            Check(C<TextBlock>(window, "CardImageText").Text.Contains("图片", StringComparison.Ordinal), "Image/source disclaimer disappeared.");
            Pass("fixed-build direct links open foreign tokens preserve explicit cycles and never fill a missing A definition from active B");

            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync")); Search(window, Many, 0, true); Select(window, Many);
            var reached = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= 6; page++)
            {
                WaitPage(window, page, page == 6 ? 5 : 100);
                foreach (var row in Related(window)) Check(reached.Add(row.CardId) && !row.IsMissing, "Related paging repeated or lost a real definition.");
                if (page < 6) { Click(window, "RelatedNextPageButton"); Await(TaskField(window, "_cardDetailTask")); }
            }
            Check(reached.Count == 505 && !C<Button>(window, "RelatedNextPageButton").IsEnabled, "Relations beyond the 500-ID catalog API cap were truncated.");
            var last = Related(window)[0].CardId; Open(window, last);
            Click(window, "CardBackButton"); WaitPage(window, 6, 5);
            Set(window, "_relatedCardsOverride", (Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)(async (ids, build, _) =>
            { await catalog.GetByIdsAsync(ids, build); throw new IOException("synthetic related failure"); }));
            Click(window, "RelatedPrevPageButton"); Await(TaskField(window, "_cardDetailTask"));
            Check(C<TextBlock>(window, "RelatedPageText").Text.Contains("失败", StringComparison.Ordinal) && C<Button>(window, "RelatedReloadButton").IsEnabled, "Current related failure cannot be retried.");
            Set(window, "_relatedCardsOverride", null); Click(window, "RelatedReloadButton"); Await(TaskField(window, "_cardDetailTask")); WaitPage(window, 5, 100);
            Pass("all 505 related IDs remain reachable in real hundred-row pages with page-preserving Back and current-page failure recovery");

            SearchRaces(window, catalog, first, second);
            RelatedRaces(window, catalog, first, second);
            Pass("after-IO old searches and related success or failure cannot overwrite newer query build detail or Back state");
            ImageRaces(window, catalog, first, second, images);
            Pass("real PNG cache requests ignore late success and failure after selection same-ID build changes and Back while retaining the latest-image disclaimer");
            ClosingReads(catalog, first, images, false);
            ClosingReads(catalog, first, images, true);
            Pass("closing actual hidden windows cancels search related and image reads and post-IO completions cannot update their controls");

            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync")); Search(window, Root, 0, true); Select(window, Root); Open(window, Cross); Open(window, Many);
            Await(TaskField(window, "_cardImageTask"));
            Layout(window, directory);
            Pass("actual App styles keep relation Back and pagination controls reachable at three finite content sizes with saved PNG evidence");
            Check(images.UnexpectedRequests == 0, "A card browsing test attempted an unexpected HTTP source."); Passive(window);
        }
        finally
        {
            Set(window, "_catalogSearchOverride", null); Set(window, "_relatedCardsOverride", null);
            typeof(MainWindow).GetMethod("CancelCardBrowsing", Private)!.Invoke(window, null);
            Set(window, "_cardHttp", oldHttp);
        }
    }

    private static void SearchRaces(MainWindow window, CardCatalog catalog, byte[] first, byte[] second)
    {
        foreach (var fail in new[] { false, true })
        {
            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync"));
            using var gate = new ReadGate<IReadOnlyList<CardRecord>>(fail);
            Set(window, "_catalogSearchOverride", (Func<CardSearchQuery, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)((query, build, _) =>
                query.Text == Root ? gate.Hold(catalog.SearchAsync(query, build)) : catalog.SearchAsync(query, build)));
            StartSearch(window, Root, 0, true); var old = TaskField(window, "_cardSearchTask"); Await(gate.Entered.Task);
            Search(window, Many, 0, true); var currentText = C<TextBlock>(window, "CardSearchStatusText").Text;
            gate.Release(); Await(old); Barrier();
            Check(gate.Returned && Rows(window) is [{ Id: Many }] && C<TextBlock>(window, "CardSearchStatusText").Text == currentText, "A late old-query completion changed current rows/status.");
            Set(window, "_catalogSearchOverride", null);
        }
        Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync"));
        using var buildGate = new ReadGate<IReadOnlyList<CardRecord>>(false);
        Set(window, "_catalogSearchOverride", (Func<CardSearchQuery, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)((query, build, _) =>
            build == A && query.Text == Root ? buildGate.Hold(catalog.SearchAsync(query, build)) : catalog.SearchAsync(query, build)));
        StartSearch(window, Root, 0, true); var pending = TaskField(window, "_cardSearchTask"); Await(buildGate.Entered.Task);
        Activate(catalog, B, second); Await(Invoke(window, "RefreshCatalogAsync")); Select(window, Root);
        buildGate.Release(); Await(pending); Barrier();
        Check(Rows(window) is [{ Id: Root, Text: "ROOT_B" }] && Detail(window).Contains(B) && Detail(window).Contains("ROOT_B"), "An old search replaced the same CardId from a new build.");
        Set(window, "_catalogSearchOverride", null);
    }

    private static void RelatedRaces(MainWindow window, CardCatalog catalog, byte[] first, byte[] second)
    {
        foreach (var back in new[] { false, true })
        foreach (var fail in new[] { false, true })
        {
            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync")); Search(window, "", 0, true); Select(window, Root);
            using var gate = new ReadGate<IReadOnlyList<CardRecord>>(fail);
            Set(window, "_relatedCardsOverride", (Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)((ids, build, _) =>
                (back ? ids.Contains(Root) : ids.Contains(Cross)) ? gate.Hold(catalog.GetByIdsAsync(ids, build)) : catalog.GetByIdsAsync(ids, build)));
            if (back) OpenStart(window, Cross); else { C<DataGrid>(window, "CardGrid").SelectedItem = null; SelectStart(window, Root); }
            var old = TaskField(window, "_cardDetailTask"); Await(gate.Entered.Task);
            if (back) { Click(window, "CardBackButton"); WaitDetail(window, "ROOT_A"); }
            else { Select(window, Many); WaitPage(window, 1, 100); }
            var text = Detail(window); var page = C<TextBlock>(window, "RelatedPageText").Text; var rows = C<DataGrid>(window, "RelatedCardsGrid").ItemsSource;
            gate.Release(); Await(old); Barrier();
            Check(gate.Returned && text == Detail(window) && page == C<TextBlock>(window, "RelatedPageText").Text &&
                ReferenceEquals(rows, C<DataGrid>(window, "RelatedCardsGrid").ItemsSource), "A late related result or error rewrote a newer detail/Back page.");
            Set(window, "_relatedCardsOverride", null);
        }
        foreach (var fail in new[] { false, true })
        {
            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync")); Search(window, Root, 0, true);
            using var gate = new ReadGate<IReadOnlyList<CardRecord>>(fail);
            Set(window, "_relatedCardsOverride", (Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)((ids, build, _) =>
                build == A ? gate.Hold(catalog.GetByIdsAsync(ids, build)) : catalog.GetByIdsAsync(ids, build)));
            SelectStart(window, Root); var old = TaskField(window, "_cardDetailTask"); Await(gate.Entered.Task);
            Activate(catalog, B, second); Await(Invoke(window, "RefreshCatalogAsync")); Select(window, Root);
            var rows = C<DataGrid>(window, "RelatedCardsGrid").ItemsSource;
            gate.Release(); Await(old); Barrier();
            Check(gate.Returned && Detail(window).Contains("ROOT_B", StringComparison.Ordinal) && Related(window) is [{ CardId: Missing, IsMissing: false }] &&
                ReferenceEquals(rows, C<DataGrid>(window, "RelatedCardsGrid").ItemsSource), "A late same-ID related lookup replaced the new build's entourage.");
            Set(window, "_relatedCardsOverride", null);
        }
    }

    private static void ImageRaces(MainWindow window, CardCatalog catalog, byte[] first, byte[] second, FakeImages images)
    {
        foreach (var fail in new[] { false, true })
        foreach (var sameIdBuild in new[] { false, true })
        {
            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync")); Search(window, "", 0, true); DeleteImage(window, Root);
            using var gate = images.Delay(Root, fail); Select(window, Root); var old = TaskField(window, "_cardImageTask"); Await(gate.Entered.Task);
            if (sameIdBuild) { Activate(catalog, B, second); Await(Invoke(window, "RefreshCatalogAsync")); Select(window, Root); }
            else Select(window, Many);
            Await(TaskField(window, "_cardImageTask"));
            var current = C<Image>(window, "CardImage").Source; var label = C<TextBlock>(window, "CardImageText").Text; var text = Detail(window);
            Check(current is not null && label.Contains("最新", StringComparison.Ordinal) && label.Contains("不是历史图片", StringComparison.Ordinal), "Successful image has no latest-only evidence boundary.");
            gate.Release(); Await(old); Barrier();
            Check(gate.Returned && ReferenceEquals(current, C<Image>(window, "CardImage").Source) && label == C<TextBlock>(window, "CardImageText").Text && text == Detail(window),
                "Late image completion overwrote current selection or same-ID/new-build detail.");
        }
        foreach (var fail in new[] { false, true })
        {
            Activate(catalog, A, first); Await(Invoke(window, "RefreshCatalogAsync")); Search(window, Root, 0, true); Select(window, Root); Await(TaskField(window, "_cardImageTask")); DeleteImage(window, Cross);
            using var gate = images.Delay(Cross, fail); Open(window, Cross); var old = TaskField(window, "_cardImageTask"); Await(gate.Entered.Task);
            Click(window, "CardBackButton"); WaitDetail(window, "ROOT_A"); Await(TaskField(window, "_cardImageTask"));
            var current = C<Image>(window, "CardImage").Source; var label = C<TextBlock>(window, "CardImageText").Text;
            gate.Release(); Await(old); Barrier();
            Check(gate.Returned && ReferenceEquals(current, C<Image>(window, "CardImage").Source) && label == C<TextBlock>(window, "CardImageText").Text,
                "Late child image changed the returned parent card.");
        }
    }

    private static void ClosingReads(CardCatalog catalog, byte[] first, FakeImages images, bool detail)
    {
        Activate(catalog, A, first);
        var window = new MainWindow(); var closed = false; window.Closed += (_, _) => closed = true;
        var original = (HttpClient)Field(window, "_cardHttp")!; Set(window, "_cardHttp", new HttpClient(images, false)); original.Dispose();
        Set(window, "_manifest", Await(catalog.GetManifestAsync(A)));
        using var gate = new ReadGate<IReadOnlyList<CardRecord>>(!detail);
        using var image = detail ? images.Delay(Root, true) : null;
        Task old; Task? oldImage = null;
        try
        {
            if (detail)
            {
                Search(window, Root, 0, true); DeleteImage(window, Root);
                Set(window, "_relatedCardsOverride", (Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)((ids, build, _) => gate.Hold(catalog.GetByIdsAsync(ids, build))));
                SelectStart(window, Root); old = TaskField(window, "_cardDetailTask"); oldImage = TaskField(window, "_cardImageTask"); Await(image!.Entered.Task);
            }
            else
            {
                Set(window, "_catalogSearchOverride", (Func<CardSearchQuery, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>)((query, build, _) => gate.Hold(catalog.SearchAsync(query, build))));
                StartSearch(window, Root, 0, true); old = TaskField(window, "_cardSearchTask");
            }
            Await(gate.Entered.Task); window.Close(); WaitUntil(() => closed, "Card browser close did not finish.");
            var rows = C<DataGrid>(window, "CardGrid").ItemsSource; var text = Detail(window); var label = C<TextBlock>(window, "CardImageText").Text;
            gate.Release(); image?.Release(); Await(old); if (oldImage is not null) Await(oldImage); Barrier();
            Check(gate.Returned && ReferenceEquals(rows, C<DataGrid>(window, "CardGrid").ItemsSource) && text == Detail(window) && label == C<TextBlock>(window, "CardImageText").Text &&
                Field(window, "_cardSearchCancellation") is null && Field(window, "_cardRelatedCancellation") is null && Field(window, "_cardImageCancellation") is null,
                "A post-close IO completion changed card controls or retained active read sources."); Passive(window);
        }
        finally { if (!closed) { window.Close(); WaitUntil(() => closed, "Card closing fixture cleanup failed."); } }
    }

    private static void Layout(MainWindow window, string directory)
    {
        var root = (FrameworkElement)window.Content; var tabs = C<TabControl>(window, "MainTabs"); var oldTab = tabs.SelectedItem;
        var workspace = new DirectoryInfo((string)Field(window, "_dataRoot")!);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var xaml = XDocument.Load(Path.Combine(workspace!.FullName, "src", "HsHelper.App", "App.xaml"));
        var resources = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString());
        root.Resources.MergedDictionaries.Add(styles);
        try
        {
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Header, "中文卡库"));
            var scroll = C<ScrollViewer>(window, "CardDetailsScroll"); var header = C<ScrollViewer>(window, "CardHeaderScroll");
            var import = C<Expander>(window, "CardImportExpander"); var originalExpanded = import.IsExpanded;
            C<DataGrid>(window, "RelatedCardsGrid").SelectedItem = Related(window)[0];
            foreach (var size in new[] { new Size(760, 480), new Size(800, 550), new Size(1180, 800), new Size(744, 441) })
            foreach (var expanded in new[] { false, true })
            {
                import.IsExpanded = expanded;
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); Barrier(); root.UpdateLayout();
                header.ScrollToTop(); Barrier(); header.UpdateLayout();
                scroll.ScrollToTop(); Barrier(); scroll.UpdateLayout(); Save("top");
                var metrics = $"size={size}; expanded={expanded}; root={root.ActualWidth}/{root.ActualHeight}; headerViewport={header.ViewportHeight}; details={scroll.ActualWidth}/{scroll.ActualHeight}; viewport={scroll.ViewportWidth}/{scroll.ViewportHeight}; extent={scroll.ExtentWidth}/{scroll.ExtentHeight}; scrollable={scroll.ScrollableHeight}; directory={directory}";
                Console.WriteLine("card browser layout: " + metrics);
                Check(scroll.ViewportHeight >= 60 && scroll.ActualHeight <= size.Height && scroll.ActualWidth <= size.Width, "Card header/filter rows collapsed the actual finite detail viewport: " + metrics);
                Reach(C<Button>(window, "CardSearchButton"), header);
                if (expanded)
                {
                    foreach (var label in new[] { "导入卡牌JSON", "同步客户端版本卡库", "同步最新卡库", "同步标准系列池", "导入完整卡池规则" })
                        Reach(Walk(import).OfType<Button>().Single(button => Equals(button.Content, label)), header);
                    Reach(C<Button>(window, "CardSearchButton"), header); Save("header-search");
                }
                Reach(C<Button>(window, "CardBackButton"), scroll);
                Reach(C<Button>(window, "RelatedNextPageButton"), scroll);
                Reach(C<Button>(window, "OpenRelatedCardButton"), scroll);
                Save("links"); scroll.ScrollToEnd(); Barrier(); scroll.UpdateLayout(); Save("bottom");
                Check(C<Button>(window, "CardBackButton").IsEnabled && C<Button>(window, "RelatedNextPageButton").IsEnabled && C<Button>(window, "OpenRelatedCardButton").IsEnabled,
                    "Card layout fixture did not exercise available Back/open/pagination actions.");
                void Reach(FrameworkElement control, ScrollViewer owner)
                {
                    control.BringIntoView(); Barrier(); owner.UpdateLayout();
                    var point = control.TransformToAncestor(owner).Transform(new Point());
                    Check(control.ActualHeight > 10 && point.Y >= -1 && point.Y + control.ActualHeight <= owner.ActualHeight + 1 &&
                        point.X >= -1 && point.X + control.ActualWidth <= owner.ActualWidth + 1,
                        $"Actual {control.Name}/{(control as Button)?.Content} cannot be scrolled into viewport {owner.Name}: {size}, expanded={expanded}, rect={point}/{control.ActualWidth}/{control.ActualHeight} viewport={owner.ActualWidth}/{owner.ActualHeight}.");
                }
                void Save(string position)
                {
                    if (size.Width != 760 && (expanded || position != "links")) return;
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    var white = new DrawingVisual(); using (var drawing = white.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(size));
                    bitmap.Render(white); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"card-browser-{(int)size.Width}x{(int)size.Height}-{(expanded ? "expanded" : "collapsed")}-{position}.png")); png.Save(file);
                }
            }
            import.IsExpanded = originalExpanded;
        }
        finally { tabs.SelectedItem = oldTab; root.Resources.MergedDictionaries.Remove(styles); }
    }

    private static byte[] Fixture(bool newer)
    {
        var cards = new List<object>();
        object Card(string id, string name, bool collectible, string cardClass, string text, string[]? entourage = null) => new
        { id, dbfId = 700000 + cards.Count, name, text, type = "MINION", cost = 1, cardClass, set = "SYNTHETIC", collectible, entourage = entourage ?? [], mechanics = Array.Empty<string>() };
        cards.Add(Card(Root, "浏览母牌", true, "HUNTER", newer ? "ROOT_B" : "ROOT_A", newer ? [Missing] : [Cross, Missing, Cycle]));
        cards.Add(Card(Many, "大量关联母牌", true, "NEUTRAL", "MANY_PARENT", Enumerable.Range(0, 505).Select(index => "CB_BULK_" + index.ToString("D3")).ToArray()));
        cards.Add(Card(Cross, "异职衍生", false, "MAGE", newer ? "TOKEN_B" : "TOKEN_A", [Root, Cycle, Many]));
        cards.Add(Card(Cycle, "循环衍生", false, "HUNTER", "CYCLE_TOKEN", [Root]));
        cards.Add(Card("CB_MAGE_MAIN", "异职主牌", true, "MAGE", "FOREIGN_COLLECTIBLE"));
        cards.Add(Card("CB_EMPTY", "未提供关联的文字压力样本", false, "NEUTRAL", new string('<', 50000)));
        if (newer) cards.Add(Card(Missing, "仅新版存在", false, "MAGE", "B_ONLY_TOKEN"));
        for (var index = 0; index < 505; index++) cards.Add(Card("CB_BULK_" + index.ToString("D3"), "关联衍生" + index.ToString("D3"), false, "MAGE", "BULK_TOKEN"));
        return JsonSerializer.SerializeToUtf8Bytes(cards);
    }

    private sealed class ReadGate<T>(bool fail) : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Returned { get; private set; }
        public async Task<T> Hold(Task<T> realRead)
        {
            var value = await realRead;
            if (Interlocked.Exchange(ref _started, 1) != 0) return value;
            Entered.TrySetResult();
            try { await _release.Task; if (fail) throw new IOException("synthetic delayed failure"); return value; }
            finally { Returned = true; }
        }
        public void Release() => _release.TrySetResult();
        public void Dispose() => _release.TrySetCanceled();
    }

    private sealed class FakeImages : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<ReadGate<byte[]>>> _delays = new();
        private readonly byte[] _png = Png();
        public int UnexpectedRequests { get; private set; }
        public ReadGate<byte[]> Delay(string id, bool fail)
        { var gate = new ReadGate<byte[]>(fail); if (!_delays.TryGetValue(id, out var values)) _delays[id] = values = new(); values.Enqueue(gate); return gate; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.Host != "art.hearthstonejson.com" || !uri.AbsolutePath.Contains("/render/latest/zhCN/256x/", StringComparison.Ordinal))
            { UnexpectedRequests++; throw new HttpRequestException("Unexpected synthetic image request."); }
            var id = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            var bytes = _delays.TryGetValue(id, out var gates) && gates.Count > 0 ? await gates.Dequeue().Hold(Task.FromResult(_png)) : _png;
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        }
        private static byte[] Png()
        {
            var pixels = Enumerable.Repeat(new byte[] { 80, 170, 70, 255 }, 64).SelectMany(value => value).ToArray();
            var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32); image.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        }
    }

    private static void Activate(CardCatalog catalog, string build, byte[] json)
    { using var data = new MemoryStream(json); Await(catalog.ImportAsync(data, build, source: "synthetic://hidden-card-browser")); }
    private static void StartSearch(MainWindow window, string text, int collectible, bool hunter)
    { C<ComboBox>(window, "CardCollectibleBox").SelectedIndex = collectible; C<CheckBox>(window, "HunterOnlyBox").IsChecked = hunter;
        C<CheckBox>(window, "StandardPoolBox").IsChecked = false; C<TextBox>(window, "KeywordBox").Text = ""; C<TextBox>(window, "SearchBox").Text = text; Click(window, "CardSearchButton"); }
    private static void Search(MainWindow window, string text, int collectible, bool hunter) { StartSearch(window, text, collectible, hunter); Await(TaskField(window, "_cardSearchTask")); }
    private static IReadOnlyList<CardRecord> Rows(MainWindow window) => C<DataGrid>(window, "CardGrid").Items.Cast<CardRecord>().ToArray();
    private static IReadOnlyList<CardBrowseRelatedRow> Related(MainWindow window) => C<DataGrid>(window, "RelatedCardsGrid").Items.Cast<CardBrowseRelatedRow>().ToArray();
    private static string Detail(MainWindow window) => C<TextBox>(window, "CardDetailText").Text;
    private static void SelectStart(MainWindow window, string id) => C<DataGrid>(window, "CardGrid").SelectedItem = Rows(window).Single(card => card.Id == id);
    private static void Select(MainWindow window, string id) { SelectStart(window, id); Await(TaskField(window, "_cardDetailTask")); }
    private static void OpenStart(MainWindow window, string id) { C<DataGrid>(window, "RelatedCardsGrid").SelectedItem = Related(window).Single(row => row.CardId == id); Click(window, "OpenRelatedCardButton"); }
    private static void Open(MainWindow window, string id) { OpenStart(window, id); Await(TaskField(window, "_cardDetailTask")); }
    private static void WaitDetail(MainWindow window, string text) => WaitUntil(() => Detail(window).Contains(text, StringComparison.Ordinal) && TaskField(window, "_cardDetailTask").IsCompleted, "Detail/Back did not complete: " + text);
    private static void WaitPage(MainWindow window, int page, int count) => WaitUntil(() => C<TextBlock>(window, "RelatedPageText").Text.Contains($"关联第 {page}/", StringComparison.Ordinal) && Related(window).Count == count, "Related page did not settle: " + page);
    private static void DeleteImage(MainWindow window, string id)
    { var path = Path.GetFullPath(Path.Combine((string)Field(window, "_dataRoot")!, "images", "zhCN-" + id + ".png"));
        Check(path.Contains(Path.DirectorySeparatorChar + ".local" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Image fixture must remain local."); if (File.Exists(path)) File.Delete(path); }
    private static T C<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing card browser control " + name);
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Walk(child)) yield return item; }
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static Task Invoke(MainWindow window, string name) => (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null)!;
    private static Task TaskField(MainWindow window, string name) => (Task)Field(window, name)!;
    private static void Click(MainWindow window, string name) { var button = C<Button>(window, name); Check(button.IsEnabled, "Disabled card browser action " + name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Actual card IO/handler did not complete."); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Barrier() { var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Await(operation.Task); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Card test created a visible/native window.");
    private static void Pass(string text) => Console.WriteLine("PASS card browser: " + text);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
