using System.Diagnostics;
using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;

/// <summary>Invoke on the existing hidden smoke's STA dispatcher before its Application shuts down.</summary>
public static class LocalToolsHiddenSmoke
{
    public static void RunOnDispatcher(string directory)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Local tool checks require an STA dispatcher.");
        Directory.CreateDirectory(directory);
        var catalog = new CardCatalog(Path.Combine(directory, "local-tools-cards.db"));
        Await(catalog.InitializeAsync());
        var cardDefinitions = JsonNode.Parse(BattleViewRegression.CardsJson())!.AsArray();
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json"))))
            foreach (var definition in fixture.RootElement.GetProperty("cards").EnumerateArray()
                .Where(card => card.GetProperty("id").GetString() is "CORE_DS1_184" or "FIR_921"))
                cardDefinitions.Add(JsonNode.Parse(definition.GetRawText()));
        var effectDonor = 0;
        using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Cards.Tests", "Fixtures", "assembled-card-text-255790.json"))))
            foreach (var definition in fixture.RootElement.GetProperty("cards").EnumerateArray()
                .Where(card => card.GetProperty("id").GetString() is "TIME_428" or "CORE_REV_308"))
            {
                cardDefinitions.Add(JsonNode.Parse(definition.GetRawText()));
                if (definition.GetProperty("id").GetString() == "CORE_REV_308") effectDonor = definition.GetProperty("dbfId").GetInt32();
            }
        using (var cards = new MemoryStream(Encoding.UTF8.GetBytes(cardDefinitions.ToJsonString())))
            Await(catalog.ImportAsync(cards, BattleViewRegression.Build, source: "synthetic://hidden-local-tools"));
        var frame = BattleViewRegression.Frame();
        var window = new LocalToolsWindow(frame, BattleViewRegression.Build, catalog);
        try
        {
            Await(window.BindingReady); Passive(window);
            var card = Control<ComboBox>(window, "LocalCardBox");
            Check(card.Items.OfType<object>().All(item => !item.ToString()!.Contains("UI_PRIVATE", StringComparison.Ordinal)), "Hidden opposing hand entered probability targets.");
            card.SelectedItem = card.Items.OfType<object>().Single(item => item.GetType().GetProperty("CardId")!.GetValue(item)?.ToString() == "UI_HAND");
            Control<TextBox>(window, "LocalDrawsBox").Text = "2";
            Click(Control<Button>(window, "LocalOddsButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("37.78", StringComparison.Ordinal), "Exact local hypergeometric odds did not reach the result view.");
            Check(Control<TextBox>(window, "LocalDeckText").Text.Contains("已确认牌库组成", StringComparison.Ordinal), "Exact deck composition was not displayed.");
            Passive(window);

            var kinds = Control<ComboBox>(window, "LocalKindBox"); kinds.SelectedIndex = 1;
            var sources = Control<ComboBox>(window, "LocalSourceBox");
            Check(sources.Items.Count == 2 && sources.Items.OfType<object>().All(item => !item.ToString()!.Contains("UI_PRIVATE", StringComparison.Ordinal)), "Attack source dropdown contains an opposing or hidden entity.");
            sources.SelectedItem = sources.Items.OfType<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == 31);
            var targets = Control<ComboBox>(window, "LocalTargetBox");
            targets.SelectedItem = targets.Items.OfType<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == 20);
            RefreshLabels(window);
            Check((int)sources.SelectedItem.GetType().GetProperty("Id")!.GetValue(sources.SelectedItem)! == 31 &&
                (int)targets.SelectedItem.GetType().GetProperty("Id")!.GetValue(targets.SelectedItem)! == 20,
                "Asynchronous name refresh discarded the authored source or target.");
            Click(Control<Button>(window, "LocalAddActionButton"));
            Click(Control<Button>(window, "LocalEvaluateButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("验证级别", StringComparison.Ordinal), "Candidate attack did not reach the local rule evaluator.");
            Check(window.ResultText.Contains("已应用动作：1", StringComparison.Ordinal), "Local evaluator did not apply the authored attack.");
            Click(Control<Button>(window, "LocalLethalButton"));
            window.Invalidate("fixture switched frame");
            Drain();
            Check(window.ResultText.StartsWith("fixture switched frame", StringComparison.Ordinal) && !Control<Button>(window, "LocalLethalButton").IsEnabled &&
                !Control<Button>(window, "LocalOddsButton").IsEnabled, "A stale calculation overwrote the invalidated frame or remained actionable.");
            Passive(window);

            var choiceFrame = frame with { StateVersion = 8, EventSequence = 110, Phase = GamePhase.Choice,
                Choice = new(77, 1, "GENERAL", null, 1, 1, [new(101, "UI_PUBLIC", 1, 1), new(102, "UI_HAND", 1, 2)], true),
                DeckTracking = null, PublicPlays = null };
            window.UpdateBinding(choiceFrame, BattleViewRegression.Build, isHistorical: true); Await(window.BindingReady);
            var choices = Control<ListBox>(window, "LocalChoicesList");
            Check(choices.Items.Count == 2 && window.BoundEventSequence == 110, "Current own choice was not rebound.");
            choices.SelectedIndex = 1; RefreshLabels(window);
            Check(choices.SelectedItems.Count == 1, "Asynchronous name refresh discarded the current choice selection.");
            Click(Control<Button>(window, "LocalValidateChoiceButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("仅核对候选范围与数量", StringComparison.Ordinal), "Choice count validation did not display its limited coverage.");
            Check(window.ResultText.Contains("历史局面", StringComparison.Ordinal) && window.ResultText.Contains("已验证", StringComparison.Ordinal), "Historical choice result lost its as-of binding.");

            var entities = choiceFrame.Entities.ToDictionary();
            entities[1] = entities[1] with { Tags = entities[1].Tags.Append(new KeyValuePair<string, string>("MAXHANDSIZE", "10")).ToFrozenDictionary() };
            entities[98] = new(98, "CORE_DS1_184", EntityKind.Card,
                new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "GRAVEYARD", ["CARDTYPE"] = "SPELL" }.ToFrozenDictionary());
            var trackingChoice = choiceFrame with { Entities = entities.ToFrozenDictionary(), Choice = choiceFrame.Choice! with { SourceEntityId = 98, SourceCardId = "CORE_DS1_184" } };
            window.UpdateBinding(trackingChoice, BattleViewRegression.Build, isHistorical: true); Await(window.BindingReady);
            choices = Control<ListBox>(window, "LocalChoicesList"); choices.SelectedIndex = 1;
            Click(Control<Button>(window, "LocalEvaluateChoiceButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("选择来源：追踪术", StringComparison.Ordinal),
                "Choice-effect button did not execute fixed-build direct consequences.");
            Check(window.ResultText.Contains("直接加入手牌", StringComparison.Ordinal) && window.ResultText.Contains("入手后当前费用：未知", StringComparison.Ordinal) &&
                window.ResultText.Contains("不能据此继续执行", StringComparison.Ordinal), "Local choice UI invented a current cost or following action.");
            var drawEntities = frame.Entities.ToDictionary();
            var sourceTags = drawEntities[30].Tags.ToDictionary(); sourceTags["COST"] = "3";
            drawEntities[30] = drawEntities[30] with { CardId = "FIR_921", Tags = sourceTags.ToFrozenDictionary() };
            var drawPlayerTags = drawEntities[1].Tags.ToDictionary();
            foreach (var (key, value) in new[] { ("3527", "2"), ("NUM_CARDS_IN_HAND", "1"), ("MAXHANDSIZE", "10"), ("NUM_CARDS_IN_DECK", "0"), ("FATIGUE", "0") }) drawPlayerTags[key] = value;
            drawEntities[1] = drawEntities[1] with { Tags = drawPlayerTags.ToFrozenDictionary() };
            var drawFrame = frame with { StateVersion = 9, EventSequence = 120, Entities = drawEntities.ToFrozenDictionary(), DeckTracking = null };
            window.UpdateBinding(drawFrame, BattleViewRegression.Build, false); Await(window.BindingReady);
            Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 0;
            var drawSources = Control<ComboBox>(window, "LocalSourceBox");
            drawSources.SelectedItem = drawSources.Items.OfType<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == 30);
            Check(Control<Button>(window, "LocalPreviewDrawButton").IsEnabled, "Covered source did not enable the draw preview button.");
            Click(Control<Button>(window, "LocalPreviewDrawButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("抽取次数 2", StringComparison.Ordinal), "Draw preview button did not reach the actual tool.");
            Check(window.ResultText.Contains("不证明出牌合法", StringComparison.Ordinal) && window.ResultText.Contains("不生成新手牌实体", StringComparison.Ordinal),
                "Draw UI turned a scoped preview into an executable next state.");
            var assemblyEntities = drawFrame.Entities.ToDictionary();
            var assemblyTags = assemblyEntities[30].Tags.ToDictionary();
            assemblyTags["CRAFTABLE_FIRST_HALF_DBID"] = "0"; assemblyTags["CRAFTABLE_SECOND_HALF_DBID"] = effectDonor.ToString();
            assemblyEntities[30] = assemblyEntities[30] with { CardId = "TIME_428", Tags = assemblyTags.ToFrozenDictionary() };
            var assemblyFrame = drawFrame with { StateVersion = 10, EventSequence = 125, Entities = assemblyEntities.ToFrozenDictionary() };
            window.UpdateBinding(assemblyFrame, BattleViewRegression.Build, false); Await(window.BindingReady);
            var effectSources = Control<ComboBox>(window, "LocalSourceBox");
            effectSources.SelectedItem = effectSources.Items.OfType<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == 30);
            Check(Control<Button>(window, "LocalInspectEffectButton").IsEnabled, "Visible assembled source did not enable effect lookup.");
            Click(Control<Button>(window, "LocalInspectEffectButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("可读文字：", StringComparison.Ordinal), "Effect lookup did not query the actual entity tool.");
            Check(window.ResultText.Contains("召唤", StringComparison.Ordinal) && window.ResultText.Contains("不改变当前费用和属性", StringComparison.Ordinal),
                "Entity effect lookup lost the donor text or changed the current stat contract.");
            assemblyTags["CRAFTABLE_SECOND_HALF_DBID"] = "2147483000";
            assemblyEntities[30] = assemblyEntities[30] with { Tags = assemblyTags.ToFrozenDictionary() };
            window.UpdateBinding(assemblyFrame with { StateVersion = 11, EventSequence = 126, Entities = assemblyEntities.ToFrozenDictionary() }, BattleViewRegression.Build, false);
            Await(window.BindingReady); Click(Control<Button>(window, "LocalInspectEffectButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("当前效果未知", StringComparison.Ordinal), "Missing donor fell back to the native base text.");
            foreach (var rejected in new[]
            {
                trackingChoice with { IsSpectating = true },
                trackingChoice with { Quality = trackingChoice.Quality with { IsStable = false } },
                trackingChoice with { Quality = trackingChoice.Quality with { IsComplete = false } },
                trackingChoice with { Quality = trackingChoice.Quality with { OpenBlockCount = 1 } },
                trackingChoice with { Choice = trackingChoice.Choice! with { Id = 0 } },
                trackingChoice with { Choice = trackingChoice.Choice! with { Options = [new(101, "PRIVATE_LOCAL_CHOICE", 2, 1)] } }
            })
            {
                window.UpdateBinding(rejected, BattleViewRegression.Build); Await(window.BindingReady);
                Check(Control<ListBox>(window, "LocalChoicesList").Items.Count == 0 &&
                    !Control<Button>(window, "LocalValidateChoiceButton").IsEnabled && !Control<Button>(window, "LocalEvaluateChoiceButton").IsEnabled,
                    "An unconfirmed/foreign choice remained visible or calculable through the local-window projection.");
            }

            window.UpdateBinding(choiceFrame with { Phase = GamePhase.Main }, BattleViewRegression.Build); Await(window.BindingReady);
            Check(Control<ListBox>(window, "LocalChoicesList").Items.Count == 0 && !Control<Button>(window, "LocalValidateChoiceButton").IsEnabled,
                "Residual choice remained available in Main phase.");
            window.UpdateBinding(frame, BattleViewRegression.Build); Await(window.BindingReady);
            kinds = Control<ComboBox>(window, "LocalKindBox"); kinds.SelectedIndex = 3;
            Check(Control<ComboBox>(window, "LocalSourceBox").Items.Count == 1 && !Control<ComboBox>(window, "LocalTargetBox").IsEnabled,
                "Local end-turn source was not bound uniquely to the current player or allowed a target.");
            Click(Control<Button>(window, "LocalAddActionButton")); Click(Control<Button>(window, "LocalEvaluateButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("本地模拟已结束我方回合", StringComparison.Ordinal),
                "Local end-turn action did not reach the rule boundary or its explanation.");
            window.UpdateBinding(frame with { Quality = frame.Quality with { IsComplete = false }, DeckTracking = frame.DeckTracking! with { IsExact = false, UnknownCount = 3 } }, BattleViewRegression.Build);
            Await(window.BindingReady);
            Click(Control<Button>(window, "LocalOddsButton"));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("抽中概率：未知", StringComparison.Ordinal), "Unknown composition became exact odds in UI.");
            window.UpdateBinding(frame, "999999");
            Check(!window.IsBindingValid && !Control<Button>(window, "LocalOddsButton").IsEnabled, "Mismatched card build was accepted by the local tool host.");
            Passive(window);
        }
        finally { window.Close(); }
    }

    private static T Control<T>(LocalToolsWindow window, string name) where T : class => window.FindName(name) as T ?? throw new Exception("Missing local control: " + name);
    private static void RefreshLabels(LocalToolsWindow window) => typeof(LocalToolsWindow).GetMethod("RenderBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Click(Button button) { Check(button.IsEnabled, "Local action was unexpectedly disabled."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
        "Local calculator created or displayed a window handle during hidden checks.");
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Local catalog task timed out."); task.GetAwaiter().GetResult(); }
    private static void Drain()
    {
        var task = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        Await(task);
    }
    private static void WaitUntil(Func<bool> complete, string message)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(complete(), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
