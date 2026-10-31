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
using HsHelper.Core;
using HsHelper.Storage;

public static class ModelCapabilitiesUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string FixtureKey = "model-cap-ui-isolated-key";

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "model-cap-ui"); Directory.CreateDirectory(directory);
        var oldData = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR"); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", directory);
        var window = new MainWindow();
        using var handler = new ModelHandler();
        using var http = new HttpClient(handler);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            C<TextBox>(window, "EndpointBox").Text = "https://model-cap-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "model-cap-ui"; C<PasswordBox>(window, "KeyBox").Password = FixtureKey;
            var initial = new ModelSettings(new("https://model-cap-ui.invalid/v1/chat/completions"), "model-cap-ui", 3, 1600, 40);
            Set(window, "_loadedModelSettings", initial);
            var configuration = (ProtectedModelConfigurationStore)Field(window, "_modelStore")!;
            var revision = (long)Field(window, "_configurationRevision")!;
            var panel = Panel(window); var completed = false;
            panel.Completed += (_, result) => completed = !result.Saved;
            var rounds = C<TextBox>(panel, "ModelToolRoundsBox"); var enabled = C<CheckBox>(panel, "ModelEnableToolsBox");
            enabled.IsChecked = false; Check(rounds.Text == "3" && !rounds.IsEnabled, "Disabling tools erased the configured interaction rounds.");
            enabled.IsChecked = true; Check(rounds.Text == "3" && rounds.IsEnabled, "Re-enabling tools did not restore the retained rounds.");
            C<TextBox>(panel, "ModelOutputTokensBox").Text = "99"; Save(panel);
            Check(C<TextBlock>(panel, "ModelOptionsErrorText").Text.Contains("范围") && panel.SelectedSettings is null &&
                (long)Field(window, "_configurationRevision")! == revision && !File.Exists(Path.Combine(directory, "model-config.json")),
                "An invalid numeric field changed or saved the active configuration.");
            Click(C<Button>(panel, "CancelModelOptionsButton"));
            Check(completed && ReferenceEquals(initial, Field(window, "_loadedModelSettings")) && !File.Exists(Path.Combine(directory, "model-config.json")),
                "Cancelling the real panel changed or persisted settings.");
            Pass("the actual settings editor retains tool rounds and cancel/invalid values make no configuration write");

            var ioGate = (SemaphoreSlim)Field(window, "_configurationIoGate")!;
            ioGate.Wait();
            try
            {
                panel = Panel(window); SetCapabilities(panel, false, ModelResponseFormat.JsonSchema);
                Click(C<Button>(panel, "SaveModelOptionsButton"));
                Check(panel.IsBusy && Field(window, "_configurationWrite") is CancellationTokenSource, "The real options save did not wait at the configuration IO gate.");
                C<TextBox>(window, "ModelBox").Text = "changed-during-options-save";
                Await(panel.PendingOperation);
                Check(!panel.IsBusy && panel.SelectedSettings is null && C<Button>(panel, "SaveModelOptionsButton").IsEnabled &&
                    C<TextBlock>(panel, "ModelOptionsErrorText").Text.Contains("配置已变化") && !File.Exists(Path.Combine(directory, "model-config.json")) &&
                    C<TextBox>(window, "ModelBox").Text == "changed-during-options-save" && !C<TextBlock>(window, "ModelConfigText").Text.Contains("已加密保存"),
                    "An old delayed options save overwrote newer fields, reported success or closed the panel.");
            }
            finally { ioGate.Release(); }
            C<TextBox>(window, "ModelBox").Text = "model-cap-ui"; Set(window, "_loadedModelSettings", initial);
            var blocked = Path.Combine(directory, "blocked-config.json"); Directory.CreateDirectory(blocked);
            Set(window, "_modelStore", new ProtectedModelConfigurationStore(blocked, new WindowsSecretProtector()));
            panel = Panel(window); SetCapabilities(panel, false, ModelResponseFormat.JsonSchema); Save(panel);
            Check(panel.SelectedSettings is null && !panel.IsBusy && C<Button>(panel, "SaveModelOptionsButton").IsEnabled &&
                C<TextBlock>(panel, "ModelOptionsErrorText").Text.Contains("尚未保存") && C<TextBlock>(panel, "ModelOptionsErrorText").Text.Contains("关闭不还原") &&
                Field(window, "_loadedModelSettings") is ModelSettings { EnableTools: false, ResponseFormat: ModelResponseFormat.JsonSchema } &&
                !File.Exists(Path.Combine(directory, "model-config.json")), "A real protected-store write failure hid the applied-memory/unsaved-disk distinction.");
            var applied = Field(window, "_loadedModelSettings"); Click(C<Button>(panel, "CancelModelOptionsButton"));
            Check(ReferenceEquals(applied, Field(window, "_loadedModelSettings")), "Closing after a failed write silently rolled back applied settings.");
            Set(window, "_modelStore", configuration);
            Pass("actual delayed and failed protected saves retain the open editor and never overwrite newer configuration fields or report success");

            var budget = (AnalysisBudget)Field(window, "_analysisBudget")!; budget.SeedMatch("cap-ui-prior-usage", 4, 57);
            foreach (var tools in new[] { false, true })
            foreach (var mode in Enum.GetValues<ModelResponseFormat>())
            {
                panel = Panel(window); SetCapabilities(panel, tools, mode); Save(panel);
                Check(panel.SelectedSettings is { } selected && selected.EnableTools == tools && selected.ResponseFormat == mode && selected.MaxToolRounds == 3,
                    "The real save button did not retain capabilities and numeric tuning.");
                var saved = Await(configuration.LoadAsync())!;
                Check(saved.Settings.EnableTools == tools && saved.Settings.ResponseFormat == mode && saved.Settings.MaxToolRounds == 3 && saved.ApiKey == FixtureKey,
                    "Encrypted save/reopen lost a capability or the isolated credential.");
                using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "model-config.json"))))
                    Check(document.RootElement.GetProperty("Version").GetInt32() == 2 && !document.RootElement.GetRawText().Contains(FixtureKey), "Capability settings were not saved as protected version 2.");
                Set(window, "_loadedModelSettings", saved.Settings);
                var reopened = Panel(window);
                Check(C<CheckBox>(reopened, "ModelEnableToolsBox").IsChecked == tools && C<ComboBox>(reopened, "ModelResponseFormatBox").SelectedIndex == (int)mode,
                    "Reopening the product panel disagreed with the saved settings.");
                var count = handler.Requests.Count; Click(C<Button>(window, "CheckModelButton"));
                WaitUntil(() => handler.Requests.Count == count + 1 && Field(window, "_connectionCheck") is null, "The real connection-check button did not finish.");
                RequestMode(handler.Requests[^1], tools, mode);
                Check(C<TextBlock>(window, "ModelConfigText").Text.Contains("模型连接成功"), "The chosen-capability fake HTTP probe did not actually succeed.");
                Check(ReferenceEquals(budget, Field(window, "_analysisBudget")) && budget.Snapshot("cap-ui-prior-usage") is { RequestsStarted: 4, ChargedTokens: 57 },
                    "Changing only capabilities reset already consumed per-match budget.");
            }
            Pass("six real panel save/reopen/check combinations send the selected request fields without resetting prior usage");

            foreach (var fail in new[] { false, true })
            {
                var gate = handler.DelayNext(fail); Click(C<Button>(window, "CheckModelButton")); Await(gate.Entered.Task);
                var check = (CancellationTokenSource)Field(window, "_connectionCheck")!; var checkToken = check.Token;
                using var analysis = new CancellationTokenSource(); Set(window, "_analysis", analysis);
                panel = Panel(window); SetCapabilities(panel, fail, fail ? ModelResponseFormat.JsonSchema : ModelResponseFormat.JsonObject); Save(panel);
                var label = C<TextBlock>(window, "ModelConfigText").Text;
                Check(checkToken.IsCancellationRequested && analysis.IsCancellationRequested, "Changing capabilities did not cancel the old check and analysis.");
                gate.Release.SetResult(); WaitUntil(() => Field(window, "_connectionCheck") is null, "The stale connection request did not finish.");
                Check(C<TextBlock>(window, "ModelConfigText").Text == label && label.Contains("加密保存"), "A stale connection success/failure overwrote the new capability status.");
                Set(window, "_analysis", null);
            }
            Pass("capability changes cancel old connection and analysis tokens while post-IO success and failure cannot replace the saved status");

            ActualAnalysisIsCancelled(window, handler, directory, budget);
            Pass("a real fixed-frame analysis is cancelled on capability save and its settled HTTP attempt remains in the existing budget");
            panel = Panel(window); panel.Measure(new Size(420, 460)); panel.Arrange(new Rect(0, 0, 420, 460)); panel.UpdateLayout();
            Check(C<Button>(panel, "SaveModelOptionsButton").ActualHeight > 20 && C<Button>(panel, "CancelModelOptionsButton").ActualHeight > 20,
                "The actual settings panel did not retain bounded, reachable save/cancel controls.");
            SavePanelImage(panel, directory);
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Model options tests displayed a native window.");
        }
        finally
        {
            Set(window, "_allowClose", true); window.Close(); Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", oldData);
        }
    }

    private static void ActualAnalysisIsCancelled(MainWindow window, ModelHandler handler, string directory, AnalysisBudget budget)
    {
        var catalog = (CardCatalog)Field(window, "_catalog")!; Await(catalog.InitializeAsync());
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(BattleViewRegression.CardsJson()))) Await(catalog.ImportAsync(input, "255790", source: "fixture://model-cap-ui"));
        var sessions = new SessionStore(Path.Combine(directory, "sessions.db")); Await(sessions.InitializeAsync()); Set(window, "_store", sessions);
        var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
        var archives = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archives.InitializeAsync()); Set(window, "_adviceArchive", archives);
        var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
        var frame = BattleViewRegression.Frame();
        Set(window, "_historyContext", new HistoricalContext(frame.MatchId, "255790", null, null, frame, []));
        Set(window, "_historyObservedAt", DateTimeOffset.UtcNow.AddMinutes(-1));
        C<TextBox>(window, "SessionBuildBox").Text = "255790"; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;
        var gate = handler.DelayNext(false, delayBody: true);
        var analysis = (Task)Invoke(window, "AnalyzeCurrentAsync")!; Await(gate.Entered.Task);
        var before = budget.Snapshot(frame.MatchId); Check(before.RequestsStarted == 1 && before.InFlightRequests == 1, "The actual analysis never reserved its HTTP budget.");
        var cancellation = ((CancellationTokenSource)Field(window, "_analysis")!).Token;
        var panel = Panel(window); SetCapabilities(panel, true, ModelResponseFormat.Omitted); Save(panel);
        Check(cancellation.IsCancellationRequested && ReferenceEquals(budget, Field(window, "_analysisBudget")), "An actual pending analysis survived a capability change or its budget was replaced.");
        gate.Release.SetResult(); WaitUntil(() => analysis.IsCompleted, "The cancelled actual analysis did not settle.");
        // The desktop intentionally catches cancellation and completes its persistence/finally path.
        analysis.GetAwaiter().GetResult();
        var after = budget.Snapshot(frame.MatchId);
        Check(after.RequestsStarted == before.RequestsStarted && after.InFlightRequests == 0 && after.ChargedTokens > 0 &&
            !C<TextBox>(window, "AdviceBox").Text.Contains("MODEL_CAP_LATE_ANSWER") && Await(archives.ListAsync()).Count == 0,
            "A stale answer was published or its already-started request disappeared from the ledger.");
        Await((Task)Invoke(window, "FlushTracesAsync")!);
        var reopened = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(reopened.InitializeAsync());
        var totals = Await(reopened.GetBudgetTotalsAsync(frame.MatchId)); var requests = Await(reopened.ListRequestsAsync(frame.MatchId));
        Check(totals.RequestCount == 1 && totals.ChargedTokens == after.ChargedTokens &&
            requests is [{ Status: AnalysisTraceStatus.Completed, ChargedTokens: > 0 }], "Reopening the trace store lost a paid cancelled request or its settled charge.");
    }

    private static void SavePanelImage(ModelOptionsPanel panel, string directory)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var document = XDocument.Load(Path.Combine(workspace!.FullName, "src", "HsHelper.App", "App.xaml"));
        var resources = document.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var styles = (ResourceDictionary)XamlReader.Parse(new XElement(resources.Name.Namespace + "ResourceDictionary", resources.Elements().Select(element => new XElement(element))).ToString());
        panel.Resources.MergedDictionaries.Add(styles);
        try
        {
            SetCapabilities(panel, false, ModelResponseFormat.JsonSchema);
            panel.Measure(new Size(420, 460)); panel.Arrange(new Rect(0, 0, 420, 460)); panel.UpdateLayout();
            var bitmap = new RenderTargetBitmap(420, 460, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual(); using (var drawing = white.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 420, 460));
            bitmap.Render(white); bitmap.Render(panel);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, "model-options-420x460.png"); using var file = File.Create(path); png.Save(file);
            Console.WriteLine("Model options styled panel: " + path);
        }
        finally { panel.Resources.MergedDictionaries.Remove(styles); }
    }

    private static void SetCapabilities(ModelOptionsPanel panel, bool tools, ModelResponseFormat mode)
    { C<CheckBox>(panel, "ModelEnableToolsBox").IsChecked = tools; C<ComboBox>(panel, "ModelResponseFormatBox").SelectedIndex = (int)mode; }
    private static void RequestMode(JsonObject body, bool tools, ModelResponseFormat mode)
    {
        Check(body.ContainsKey("tools") == tools && body.ContainsKey("tool_choice") == tools && (!tools || body["tool_choice"]?.ToString() == "none"), "The connection probe ignored the selected tool capability.");
        Check(mode == ModelResponseFormat.Omitted ? !body.ContainsKey("response_format") : body["response_format"]?["type"]?.ToString() == (mode == ModelResponseFormat.JsonObject ? "json_object" : "json_schema"),
            "The connection probe ignored the selected response format.");
    }
    private static ModelOptionsPanel Panel(MainWindow window) => (ModelOptionsPanel)Invoke(window, "CreateModelOptionsPanel")!;
    private static void Save(ModelOptionsPanel panel) { Click(C<Button>(panel, "SaveModelOptionsButton")); Await(panel.PendingOperation); }
    private static void Click(Button button) { Check(button.IsEnabled, "A real model option action was disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Model capability operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var elapsed = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Console.WriteLine("PASS model capabilities UI: " + message);

    private sealed class ResponseGate(bool fail, bool delayBody)
    {
        public bool Fail { get; } = fail;
        public bool DelayBody { get; } = delayBody;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ModelHandler : HttpMessageHandler
    {
        private readonly Queue<ResponseGate> _delayed = new();
        public List<JsonObject> Requests { get; } = [];
        public ResponseGate DelayNext(bool fail, bool delayBody = false) { var gate = new ResponseGate(fail, delayBody); _delayed.Enqueue(gate); return gate; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject(); Requests.Add(body);
            var message = new JsonObject { ["summary"] = "MODEL_CAP_LATE_ANSWER", ["reasons"] = new JsonArray(), ["alternative"] = null,
                ["risks"] = new JsonArray(), ["assumptions"] = new JsonArray(), ["unknowns"] = new JsonArray(), ["unsupported_mechanics"] = new JsonArray() };
            if (body["response_format"]?["json_schema"]?["schema"]?["properties"] is JsonObject properties && properties.ContainsKey("actions")) message["actions"] = new JsonArray();
            var response = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = message.ToJsonString() } } },
                usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 } });
            if (_delayed.TryDequeue(out var gate))
            {
                if (gate.DelayBody) return new(HttpStatusCode.OK) { Content = new DelayedBody(response, gate) };
                gate.Entered.SetResult(); await gate.Release.Task;
                if (gate.Fail) throw new HttpRequestException("synthetic late transport error");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class DelayedBody(string response, ResponseGate gate) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            gate.Entered.SetResult(); await gate.Release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
