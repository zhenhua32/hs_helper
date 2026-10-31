using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;

public static class PlayTriggerToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("play-trigger formatting preserves unknown quest progress and bounded random counts", FormatUnknowns);
        yield return ("line trigger formatting marks reward and random observation boundaries", FormatLineBoundaries);
    }

    private static void FormatUnknowns()
    {
        var result = JsonNode.Parse("""
            {"QuestProgress":[{"QuestEntityId":50,"CurrentProgress":null,"Total":3,"PlayedCardQualifies":null,"Increment":null,
            "ResultingProgress":null,"CompletesQuest":null,"RequiresRewardChoice":false,"CanProjectProgress":false,"Unknowns":["alias conflict"]}],
            "Raptor":{"TargetEntityId":30,"ResolvesTriggerCount":false,"TriggerCount":{"Minimum":0,"Maximum":2},
            "Triggers":[{"SourceEntityId":40,"TargetEntityId":30,"Count":{"Minimum":0,"Maximum":2},"Eligibility":"SelfRegistrationUnconfirmed"}],
            "RequiresObservation":true,"IsBonusPoolComplete":false,"Unknowns":["unknown bonus"],"Assumptions":[]}}
            """)!;
        var text = LocalToolResultFormatter.Format("evaluate_play_triggers", result, id => "card-" + id);
        Check(text.Contains("当前进度 未知 / 3", StringComparison.Ordinal) && text.Contains("本次增量 未知", StringComparison.Ordinal) &&
            text.Contains("直接后果进度 未知", StringComparison.Ordinal) && text.Contains("次数：0～2", StringComparison.Ordinal) &&
            text.Contains("card-40 → card-30", StringComparison.Ordinal) && text.Contains("alias conflict", StringComparison.Ordinal) &&
            text.Contains("不能从空列表推断没有可用效果", StringComparison.Ordinal) && text.Contains("不能选定词条", StringComparison.Ordinal) &&
            text.Contains("不证明出牌合法", StringComparison.Ordinal) && text.Contains("自身触发时点尚未确认", StringComparison.Ordinal) &&
            !text.Contains("SelfRegistrationUnconfirmed", StringComparison.Ordinal), "The trigger display filled missing progress, made the random count exact or exposed internal codes.");
        Check(!text.Contains("达到或可能达到任务完成条件", StringComparison.Ordinal), "Unknown progress was presented as an actual completed task.");
    }

    private static void FormatLineBoundaries()
    {
        var result = JsonNode.Parse("""
            {"Verification":"Conditional","IsLegal":true,"IsLethal":true,"AppliedActions":1,"Result":{"AvailableMana":6,
            "QuestRewardBoundaryReached":true,"QuestProgressPreviews":[{"QuestEntityId":50,"CurrentProgress":2,"Total":3,
            "PlayedCardQualifies":true,"Increment":1,"ResultingProgress":3,"CompletesQuest":true,"RequiresRewardChoice":true,"CanProjectProgress":true,"Unknowns":[]}],
            "RaptorBoundaryReached":true,"RaptorPreviews":[{"ResolvesTriggerCount":true,"TriggerCount":{"Minimum":1,"Maximum":1},
            "Triggers":[],"RequiresObservation":true,"IsBonusPoolComplete":false,"Unknowns":[],"Assumptions":[]}],"Entities":[]}}
            """)!;
        var text = LocalToolResultFormatter.Format("evaluate_line", result);
        Check(text.Contains("当前进度 2 / 3", StringComparison.Ordinal) && text.Contains("本次增量 1；直接后果进度 3", StringComparison.Ordinal) &&
            text.Contains("等待真实奖励选择", StringComparison.Ordinal) && text.Contains("不能生成候选或成品", StringComparison.Ordinal) &&
            text.Contains("次数：1", StringComparison.Ordinal) && text.Contains("后续动作须等待新局面", StringComparison.Ordinal) &&
            text.Contains("未能证明", StringComparison.Ordinal), "The line display omitted the progress effect or allowed a boundary result to prove lethal.");
    }

    /// <summary>Runs through the actual button and SnapshotCoach without creating a visible window or model request.</summary>
    public static void RunOnDispatcher(CardCatalog catalog, GameSnapshot basis)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Play-trigger smoke needs an STA dispatcher.");
        var entities = basis.Entities.ToDictionary();
        entities[30] = Card(30, "EDR_227", "HAND", "MINION", ("CARDRACE", "BEAST"), ("COST", "4"), ("ATK", "5"), ("HEALTH", "2"), ("DAMAGE", "0"));
        entities[40] = Card(40, "EDR_849", "PLAY", "MINION", ("CARDRACE", "BEAST"), ("ATK", "2"), ("HEALTH", "1"), ("DAMAGE", "0"), ("SILENCED", "0"), ("DORMANT", "0"));
        entities[50] = Card(50, "TLC_EVENT_400", "SECRET", "SPELL", ("QUEST_PROGRESS", "2"), ("QUEST_PROGRESS_TOTAL", "3"), ("SIDE_QUEST", "1"));
        var frame = basis with { StateVersion = 70, EventSequence = 170, Entities = entities.ToFrozenDictionary(), DeckTracking = null, PublicPlays = null };
        var before = JsonSerializer.Serialize(frame);
        var window = new LocalToolsWindow(frame, BattleViewRegression.Build, catalog);
        try
        {
            Await(window.BindingReady);
            var button = Control<Button>(window, "LocalPreviewPlayTriggersButton");
            Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 0;
            Check(button.IsEnabled && button.Content?.ToString() == "预览使用后触发", "The actual play-trigger button was absent or disabled for known own hand.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("直接后果进度 3", StringComparison.Ordinal), "The actual button did not reach the bound host trigger resolver.");
            Check(window.ResultText.Contains("等待真实奖励选择", StringComparison.Ordinal) && window.ResultText.Contains("梦缚迅猛龙额外效果触发次数", StringComparison.Ordinal) &&
                window.ResultText.Contains("不证明出牌合法", StringComparison.Ordinal) && JsonSerializer.Serialize(frame) == before,
                "The actual result view lost scoped trigger evidence or changed its original snapshot.");
            var unknownTags = entities[50].Tags.ToDictionary(); unknownTags.Remove("QUEST_PROGRESS");
            entities[50] = entities[50] with { Tags = unknownTags.ToFrozenDictionary() };
            window.UpdateBinding(frame with { StateVersion = 71, EventSequence = 171, Entities = entities.ToFrozenDictionary() }, BattleViewRegression.Build);
            Await(window.BindingReady); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("当前进度 未知 / 3", StringComparison.Ordinal), "Unobserved progress was replaced by zero through the actual UI tool.");
            Check(window.ResultText.Contains("直接后果进度 未知", StringComparison.Ordinal), "Unknown task progress became a known completion in the actual view.");
            var trackingEntities = entities.ToDictionary(); trackingEntities.Remove(40); trackingEntities.Remove(50);
            trackingEntities[30] = Card(30, "CORE_DS1_184", "HAND", "SPELL", ("COST", "1"));
            var playerTags = trackingEntities[1].Tags.ToDictionary();
            playerTags["NUM_CARDS_IN_DECK"] = Math.Max(1, trackingEntities.Values.Count(entity => entity.Kind == EntityKind.Card && entity.ControllerId == 1 && entity.Zone == "DECK")).ToString();
            trackingEntities[1] = trackingEntities[1] with { Tags = playerTags.ToFrozenDictionary() };
            var trackingFrame = frame with { StateVersion = 72, EventSequence = 172, Entities = trackingEntities.ToFrozenDictionary() };
            var trackingBefore = JsonSerializer.Serialize(trackingFrame);
            window.UpdateBinding(trackingFrame, BattleViewRegression.Build); Await(window.BindingReady);
            Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 0; SelectSource(window, 30);
            Check(button.IsEnabled && button.ToolTip?.ToString()?.Contains("原生发现", StringComparison.Ordinal) == true,
                "Tracking could not reuse the existing play-trigger button.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("原生使用入口 · 追踪术", StringComparison.Ordinal),
                "The existing button did not reach native Tracking discovery.");
            Check(window.ResultText.Contains("外部选择：需要等待真实选项", StringComparison.Ordinal) &&
                window.ResultText.Contains("未生成候选、手牌实体或最终费用", StringComparison.Ordinal) &&
                !window.ResultText.Contains("DeckDiscover", StringComparison.Ordinal) && JsonSerializer.Serialize(trackingFrame) == trackingBefore,
                "Tracking's real button created candidates, displayed mechanism codes or lost its pending choice boundary.");
            foreach (var id in trackingEntities.Values.Where(entity => entity.ControllerId == 1 && entity.Zone == "DECK").Select(entity => entity.Id).ToArray()) trackingEntities.Remove(id);
            playerTags["NUM_CARDS_IN_DECK"] = "0";
            trackingEntities[1] = trackingEntities[1] with { Tags = playerTags.ToFrozenDictionary() };
            var emptyFrame = trackingFrame with { StateVersion = 73, EventSequence = 173, Entities = trackingEntities.ToFrozenDictionary() };
            var emptyBefore = JsonSerializer.Serialize(emptyFrame);
            window.UpdateBinding(emptyFrame, BattleViewRegression.Build); Await(window.BindingReady);
            Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 0; SelectSource(window, 30);
            Check(button.IsEnabled, "Confirmed empty Tracking scope disabled a hypothetical direct-effect preview.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("当前牌库数：0", StringComparison.Ordinal),
                "Explicit empty deck evidence did not reach the actual button's discovery display.");
            Check(window.ResultText.Contains("外部选择：本次原生效果无需选择", StringComparison.Ordinal) &&
                window.ResultText.Contains("不证明出牌合法", StringComparison.Ordinal) && JsonSerializer.Serialize(emptyFrame) == emptyBefore,
                "Empty Tracking forced a candidate or was presented as proof of full settlement/legal play.");
            Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 1;
            Check(!button.IsEnabled, "An attack source enabled a hand-play preview.");
            foreach (var rejected in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
                frame with { Quality = frame.Quality with { IsStable = false } }, frame with { Quality = frame.Quality with { IsComplete = false } } })
            {
                window.UpdateBinding(rejected, BattleViewRegression.Build); Await(window.BindingReady);
                Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 0;
                Check(!button.IsEnabled, "A private or incomplete frame enabled the play-trigger button.");
            }
            window.UpdateBinding(frame, BattleViewRegression.Build); Await(window.BindingReady);
            window.Invalidate("trigger frame changed");
            Check(!button.IsEnabled && window.ResultText.StartsWith("trigger frame changed", StringComparison.Ordinal), "A stale trigger preview remained actionable.");
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "The hidden trigger smoke displayed or created a window handle.");
        }
        finally { window.Close(); }
    }

    private static EntitySnapshot Card(int id, string card, string zone, string type, params (string Key, string Value)[] extra) =>
        new(id, card, EntityKind.Card, extra.Concat(new[] { ("CONTROLLER", "1"), ("ZONE", zone), ("CARDTYPE", type) })
            .ToFrozenDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal));
    private static T Control<T>(LocalToolsWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static void SelectSource(LocalToolsWindow window, int id)
    {
        var sources = Control<ComboBox>(window, "LocalSourceBox");
        sources.SelectedItem = sources.Items.OfType<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "The trigger catalog task timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> complete, string message)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(complete(), message);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
