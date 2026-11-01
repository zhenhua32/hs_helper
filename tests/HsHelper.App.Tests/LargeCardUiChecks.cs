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
using System.Windows.Threading;
using HsHelper.Agent;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;

internal static class LargeCardUiChecks
{
    private const string Build = "255790", Late = "LARGE_1000", LateName = "后批关键卡", LateText = "LARGE_LAST_BATCH_EFFECT";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "large-card-ui"); Directory.CreateDirectory(directory);
        var oldData = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR"); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", directory);
        var window = new MainWindow(); var closed = false;
        using var handler = new AdviceHandler(); using var http = new HttpClient(handler);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(CatalogJson()))) Await(catalog.ImportAsync(input, Build, source: "fixture://large-card-ui"));
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "EndpointBox").Text = "https://large-card-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "large-card-ui"; C<PasswordBox>(window, "KeyBox").Password = "fixture-large-card-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://large-card-ui.invalid/v1/chat/completions"), "large-card-ui", 0)
                { EnableTools = false, BudgetLimits = new(MaxTokensPerMatch: 5000000) });
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;
            var deck = new DeckDefinition(2, [31], [new(900000, 1)], [new(900000, Enumerable.Range(1, 1000).Select(i => new DeckCard(900000 + i, 1)).ToArray())]);
            var savedDeck = Await(store.SaveDeckVersionAsync("large-reference", deck, Build));
            var frame = Frame(); Pin(window, frame, savedDeck);

            Await(Invoke(window, "RenderBattleAsync"));
            var view = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
            Check(view.Local.Hand.Single(card => card.EntityId == 30).Name == LateName &&
                view.Opponent.PublicPlays.Count == 1001 && view.Opponent.PublicPlays.Single(card => card.CardId == Late).Name == LateName,
                "Battle rendering omitted later batches or failed before publishing the actual view.");
            var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady);
            var localCards = (IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!;
            Check(Enumerable.Range(0, 1001).All(i => localCards.ContainsKey(Id(i))) && localCards[Late].Name == LateName,
                "The local tool name binding silently truncated its public source IDs.");
            var source = (ComboBox)Field(tools, "_source")!;
            source.SelectedItem = source.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == 30);
            var effectButton = (Button)Field(tools, "_inspectEffect")!; Check(effectButton.IsEnabled, "Actual local effect button was unavailable.");
            effectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, effectButton));
            WaitUntil(() => Field(tools, "_calculation") is null, "Actual local effect lookup did not settle.");
            Check(tools.ResultText.Contains(LateText, StringComparison.Ordinal), "The actual local effect tool did not resolve the later-batch hand card.");
            var overlay = new OverlayWindow(); Set(window, "_overlay", overlay);
            Await(Invoke(window, "UpdateOverlayStateAsync", overlay, false));
            Check(overlay.State.Hand.Any(text => text.Contains(LateName, StringComparison.Ordinal)) && !overlay.State.Hand.Any(text => text.Contains("隐藏探针", StringComparison.Ordinal)),
                "The actual hidden overlay publisher lost a later-batch name or exposed opposing hand identity.");
            Pass("1001 distinct public IDs reach actual battle, local effect-button and hidden overlay paths without truncation");

            Await(Invoke(window, "AnalyzeCurrentAsync"));
            AssertContext(handler.Requests.Single(), 1001);
            Check(C<TextBox>(window, "AdviceBox").Text.Contains("LARGE_CARD_ADVICE", StringComparison.Ordinal) && Await(archive.ListAsync()).Count == 1,
                "The actual large-frame analysis failed before publishing and archiving its fake-HTTP answer.");
            Await(Invoke(window, "LoadDeckLibraryAsync", savedDeck.Id));
            Await(Invoke(window, "AnalyzeDeckPlanCoreAsync"));
            AssertContext(handler.Requests.Last(), 1001);
            Check(handler.Requests.Count == 2 && Await(archive.ListAsync()).Count == 2,
                "The actual deck-plan DBF aggregation did not reach fake HTTP and durable archive.");
            var planningContext = JsonNode.Parse(handler.Requests.Last()["messages"]!.AsArray()
                .Last(node => node?["role"]?.ToString() == "user")!["content"]!.GetValue<string>())!["Snapshot"]!;
            var construction = planningContext["deck_construction"]!;
            var imported = planningContext["imported_deck"]!;
            Check(construction["legality"]?.ToString() == "Unknown" && construction["input_limit_reached"]?.GetValue<bool>() == true &&
                construction["maximum_checked_entries"]?.GetValue<int>() == 500 && construction["special_main_deck_size"] is null &&
                construction["issues"]!.AsArray().Any(issue => issue?.ToString().Contains("超过500项构筑检查上限", StringComparison.Ordinal) == true) &&
                imported["cards"]!.AsArray() is [{ } main] && main["dbf_id"]?.GetValue<int>() == 900000 && main["count"]?.GetValue<int>() == 1 &&
                imported["sideboards"]!.AsArray() is [{ } board] && board["cards"]!.AsArray().Count == 1000 &&
                board["cards"]!.AsArray().Last()?["dbf_id"]?.GetValue<int>() == 901000,
                "The large deck plan hid its construction limit, claimed a known size, or truncated the actual imported deck.");
            Pass("actual snapshot and deck-plan analysis resolve 1001 catalog references at the pinned build before fake HTTP and archive");

            foreach (var kind in new[] { "battle", "overlay" })
            foreach (var fail in new[] { false, true })
            {
                var original = Frame("large-old-" + kind + fail); Pin(window, original);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (actualKind, observed, build, token) =>
                {
                    Check(actualKind == kind && observed.MatchId == original.MatchId && build == Build && token == ((CancellationTokenSource)Field(window, "_source")!).Token,
                        "The post-real-IO gate lost its exact read source or cancellation token.");
                    entered.SetResult(); await release.Task;
                    if (fail) throw new InvalidDataException("STALE_LARGE_READ");
                }));
                var pending = kind == "battle" ? Invoke(window, "RenderBattleAsync") : Invoke(window, "UpdateOverlayStateAsync", overlay, false);
                Await(entered.Task); Set(window, "_aggregatedCardReadCompleted", null);
                Pin(window, Frame("large-new-" + kind + fail));
                Await(kind == "battle" ? Invoke(window, "RenderBattleAsync") : Invoke(window, "UpdateOverlayStateAsync", overlay, false));
                var newView = C<FrameworkElement>(window, "BattleViewRoot").DataContext; var newOverlay = overlay.State;
                release.SetResult(); Await(pending);
                Check(ReferenceEquals(newView, C<FrameworkElement>(window, "BattleViewRoot").DataContext) && ReferenceEquals(newOverlay, overlay.State),
                    "A post-IO stale success/failure overwrote a newer battle or overlay publication.");
            }
            Pass("after-real-SQL delayed battle and overlay success and failure cannot publish over another match");

            Pin(window, Frame("large-close"));
            var battleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var overlayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_aggregatedCardReadCompleted", (Func<string, GameSnapshot, string?, CancellationToken, Task>)(async (kind, _, _, _) =>
            { (kind == "battle" ? battleEntered : overlayEntered).SetResult(); await closeRelease.Task; throw new InvalidDataException("CLOSED_LARGE_READ"); }));
            var closingBattle = Invoke(window, "RenderBattleAsync"); var closingOverlay = Invoke(window, "UpdateOverlayStateAsync", overlay, false);
            Await(Task.WhenAll(battleEntered.Task, overlayEntered.Task));
            var beforeClose = C<FrameworkElement>(window, "BattleViewRoot").DataContext; var beforeOverlayClose = overlay.State;
            Check(!window.IsVisible && !overlay.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero &&
                new WindowInteropHelper(overlay).Handle == IntPtr.Zero && new WindowInteropHelper(tools).Handle == IntPtr.Zero,
                "Large-card UI checks created a visible or native window.");
            Set(window, "_allowClose", true); window.Close(); closed = true;
            closeRelease.SetResult(); Await(Task.WhenAll(closingBattle, closingOverlay));
            Check(ReferenceEquals(beforeClose, C<FrameworkElement>(window, "BattleViewRoot").DataContext) && ReferenceEquals(beforeOverlayClose, overlay.State),
                "A closed host accepted a delayed catalog result or error.");
            Pass("actual hidden host close rejects both delayed catalog completions without HWND or external model access");
        }
        finally
        {
            Set(window, "_aggregatedCardReadCompleted", null);
            if (!closed) { Set(window, "_allowClose", true); window.Close(); }
            Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", oldData);
        }
    }

    private static GameSnapshot Frame(string match = "large-card-main")
    {
        var baseline = BattleViewRegression.Frame();
        var entities = baseline.Entities.Values.Select(entity => entity.Id == 30 ? entity with { CardId = Late } : entity)
            .Concat(Enumerable.Range(0, 1001).Select(i => new EntitySnapshot(10000 + i, Id(i), EntityKind.Card,
                new Dictionary<string, string> { ["ZONE"] = "GRAVEYARD", ["CONTROLLER"] = "2", ["CARDTYPE"] = "MINION" }.ToFrozenDictionary())));
        return baseline with { MatchId = match, StateVersion = 71, EventSequence = 2000, Entities = entities.ToFrozenDictionary(entity => entity.Id),
            DeckTracking = null, PublicPlays = new(match, 71, Enumerable.Range(0, 1001).Select(i => new PlayedCardRecord(i + 1, 10000 + i, Id(i), 2)).ToArray(), true, []) };
    }
    private static string CatalogJson()
    {
        var cards = JsonNode.Parse(BattleViewRegression.CardsJson())!.AsArray();
        foreach (var i in Enumerable.Range(0, 1001)) cards.Add(new JsonObject
        {
            ["id"] = Id(i), ["dbfId"] = 900000 + i, ["name"] = i == 1000 ? LateName : "公开大集合卡" + i,
            ["text"] = i == 1000 ? LateText : "", ["type"] = "MINION", ["cardClass"] = "HUNTER", ["cost"] = 1,
            ["attack"] = 1, ["health"] = 1, ["set"] = "SYNTHETIC", ["collectible"] = false, ["mechanics"] = new JsonArray()
        });
        return cards.ToJsonString();
    }
    private static void AssertContext(JsonObject request, int count)
    {
        var content = request["messages"]!.AsArray().Last(node => node?["role"]?.ToString() == "user")!["content"]!.GetValue<string>();
        var context = JsonNode.Parse(content)!["Snapshot"]!;
        var cards = context["cards"]!.AsArray();
        Check(cards.Count(node => node?["id"]?.GetValue<string>().StartsWith("LARGE_", StringComparison.Ordinal) == true) == count &&
            cards.Single(node => node?["id"]?.ToString() == Late)!["name"]?.ToString() == LateName &&
            context["card_build"]?.ToString() == Build && !cards.Any(node => node?["id"]?.ToString() == "UI_PRIVATE"),
            "The actual HTTP context lost a later batch, changed build or exposed private card definitions.");
    }
    private static string Id(int i) => "LARGE_" + i.ToString("D4");
    private static void Pin(MainWindow window, GameSnapshot frame, DeckVersion? deck = null)
    {
        Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, deck?.Id, deck, frame, []));
        Set(window, "_historyObservedAt", DateTimeOffset.UtcNow.AddMinutes(-1));
    }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Large-card UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string error)
    {
        if (ready()) return;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), error);
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Pass(string message) => Console.WriteLine("PASS large-card UI: " + message);
    private sealed class AdviceHandler : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject());
            var content = JsonSerializer.Serialize(new { summary = "LARGE_CARD_ADVICE", reasons = Array.Empty<string>(), alternative = (string?)null,
                risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>() });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = 13, completion_tokens = 7, total_tokens = 20 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
