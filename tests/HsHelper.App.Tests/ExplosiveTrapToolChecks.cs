using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.Agent;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

public static class ExplosiveTrapToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("trap display distinguishes direct simultaneous damage from a completed incoming attack", DirectDisplay);
        yield return ("trap display preserves unknown values and current own-turn non-trigger", UnknownDisplay);
        yield return ("trap attacker survival does not certify that the full attack continues", SurvivingAttacker);
        yield return ("trap tool rejects model-selected targets damage and future turns", Schema);
        yield return ("v14 trap policy preserves random boundaries and unrelated task versions", Policy);
    }

    private static void DirectDisplay()
    {
        var result = Result();
        result["Targets"]!.AsArray().Add(JsonNode.Parse("""
            {"EntityId":41,"Type":"MINION","Damage":0,"HealthBefore":4,"HealthAfter":4,"ArmorBefore":0,"ArmorAfter":0,
             "DivineShieldBefore":true,"DivineShieldAfter":false,"IsAliveAfter":true}
            """));
        var text = LocalToolResultFormatter.Format("evaluate_explosive_trap", result, id => "可见角色-" + id);
        Check(text.Contains("尚未证明攻击实际发生", StringComparison.Ordinal) && text.Contains("固定攻击对象：可见角色-10", StringComparison.Ordinal) &&
            text.Contains("每个敌方角色的防御处理前伤害：2", StringComparison.Ordinal) && text.Contains("生命 2 → 0", StringComparison.Ordinal) &&
            text.Contains("护甲 0 → 0；圣盾 否 → 否", StringComparison.Ordinal) && text.Contains("直接伤害后存活 否", StringComparison.Ordinal) &&
            text.Contains("可见角色-41：直接伤害 0；生命 4 → 4；护甲 0 → 0；圣盾 是 → 否", StringComparison.Ordinal) &&
            text.Contains("在所列条件下会停止", StringComparison.Ordinal) && text.Contains("不修改原局面", StringComparison.Ordinal) &&
            text.Contains("不提供后续己方动作或斩杀证明", StringComparison.Ordinal) && !text.Contains("MINION", StringComparison.Ordinal) &&
            !text.Contains("已验证斩杀", StringComparison.Ordinal), "The trap display completed an attack, omitted direct armor/shield effects or exposed internal type codes.");
    }

    private static void UnknownDisplay()
    {
        var result = Result(); result["TriggerActive"] = null; result["ResolvesDirectDamage"] = false;
        result["DamagePerEnemy"] = null; result["SpellDamage"] = null; result["TrapConsumed"] = null;
        result["AttackerSurvivesDirectDamage"] = null; result["AttackStoppedByDirectDamage"] = null;
        result["PriorResolutionPending"] = true; result["Targets"] = new JsonArray();
        var text = LocalToolResultFormatter.Format("evaluate_explosive_trap", result);
        Check(text.Contains("不能默认触发或不触发", StringComparison.Ordinal) && text.Contains("当前法术伤害加成：未知", StringComparison.Ordinal) &&
            text.Contains("防御处理前伤害：未知；陷阱消耗：未知", StringComparison.Ordinal) && text.Contains("空列表不表示没有伤害", StringComparison.Ordinal) &&
            text.Contains("前置奥秘或触发仍待结算", StringComparison.Ordinal) && text.Contains("未知，不能补成攻击继续或停止", StringComparison.Ordinal),
            "Missing direct damage or pending trigger evidence became zero, certainty or a completed attack.");
        result["TriggerActive"] = false; result["TrapConsumed"] = false;
        text = LocalToolResultFormatter.Format("evaluate_explosive_trap", result);
        Check(text.Contains("当前不触发；不预测未来回合", StringComparison.Ordinal) && text.Contains("陷阱消耗：否", StringComparison.Ordinal),
            "Current own-turn non-trigger was turned into a future-turn prediction or consumed secret.");
    }

    private static void SurvivingAttacker()
    {
        var result = Result(); result["AttackerSurvivesDirectDamage"] = true; result["AttackStoppedByDirectDamage"] = false;
        result["VerifiedLethalFound"] = true; result["IsLethal"] = true; result["IsLegal"] = true;
        var text = LocalToolResultFormatter.Format("evaluate_explosive_trap", result);
        Check(text.Contains("来袭者直接伤害后存活：是", StringComparison.Ordinal) &&
            text.Contains("未被本次直接伤害停止；完整攻击是否继续仍未知", StringComparison.Ordinal) &&
            !text.Contains("攻击确定继续", StringComparison.Ordinal) && !text.Contains("已验证斩杀", StringComparison.Ordinal),
            "Surviving direct damage was presented as a legal/full attack or lethal proof.");
    }

    private static void Schema()
    {
        var definition = SnapshotCoach.Definitions.Single(tool => tool.Name == "evaluate_explosive_trap");
        Check(definition.ValidateArguments(new() { ["source_entity_id"] = 50, ["attacker_entity_id"] = 40 }) &&
            definition.Parameters["additionalProperties"]?.GetValue<bool>() == false && definition.Parameters["properties"]!.AsObject().Count == 2 &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 50 }) &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 0, ["attacker_entity_id"] = 40 }), "Trap schema lost its source/attacker-only scope.");
        foreach (var key in new[] { "target_entity_id", "damage", "spell_damage", "future_turn", "selected_enemy_ids", "trigger_active" })
            Check(!definition.ValidateArguments(new() { ["source_entity_id"] = 50, ["attacker_entity_id"] = 40, [key] = 1 }),
                "A caller could select trap damage targets/strength or invent a future trigger.");
    }

    private static void Policy()
    {
        foreach (var task in new[] { AdviceTaskKind.Turn, AdviceTaskKind.Choice })
        {
            var policy = AdviceTaskTemplates.Get(task);
            Check(policy.Version == "hunter-v19/" + task.ToString().ToLowerInvariant() &&
                policy.Instructions.Contains("evaluate_explosive_trap", StringComparison.Ordinal) &&
                policy.Instructions.Contains("攻击对象固定为我方英雄", StringComparison.Ordinal) &&
                policy.Instructions.Contains("完整攻击会继续或已经结算", StringComparison.Ordinal) &&
                policy.Instructions.Contains("MagmaSplitBoundaryReached", StringComparison.Ordinal) &&
                policy.Instructions.Contains("MapPlayPreviews", StringComparison.Ordinal), "The v14 trap policy lost conditional scope or prior lifecycle constraints.");
        }
        foreach (var task in new[] { AdviceTaskKind.Mulligan, AdviceTaskKind.Review, AdviceTaskKind.DeckPlan })
            Check(AdviceTaskTemplates.Get(task).Version.StartsWith("hunter-v5/", StringComparison.Ordinal) &&
                !AdviceTaskTemplates.Get(task).Instructions.Contains("evaluate_explosive_trap", StringComparison.Ordinal), "Trap policy changed an unrelated task.");
    }

    public static void RunOnDispatcher(CardCatalog catalog, GameSnapshot basis)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Trap smoke needs an STA dispatcher.");
        var entities = basis.Entities.Where(pair => pair.Key is 1 or 2 or 10 or 20).ToDictionary();
        entities[40] = Card(40, "CORE_UNG_912", 2, "PLAY", "MINION", ("ATK", "3"), ("HEALTH", "2"), ("DAMAGE", "0"),
            ("EXHAUSTED", "0"), ("NUM_TURNS_IN_PLAY", "1"), ("NUM_ATTACKS_THIS_TURN", "0"), ("FROZEN", "0"));
        entities[50] = Card(50, "CORE_EX1_610", 1, "SECRET", "SPELL", ("SECRET", "1"));
        entities[91] = Card(91, "PRIVATE_TRAP_SECRET_PROBE", 2, "SECRET", "SPELL", ("SECRET", "1"));
        entities[92] = Card(92, "PRIVATE_TRAP_HAND_PROBE", 2, "HAND", "MINION");
        var frame = basis with { StateVersion = 100, EventSequence = 200, ActivePlayerId = 2, Entities = entities.ToFrozenDictionary(), DeckTracking = null, PublicPlays = null };
        var before = JsonSerializer.Serialize(frame); var window = new LocalToolsWindow(frame, BattleViewRegression.Build, catalog);
        try
        {
            Await(window.BindingReady);
            var source = Control<ComboBox>(window, "LocalTrapSourceBox"); var attacker = Control<ComboBox>(window, "LocalTrapAttackerBox");
            var button = Control<Button>(window, "LocalPreviewExplosiveTrapButton");
            Check(source.Items.Count == 1 && source.Items[0]!.ToString()!.Contains("爆炸陷阱", StringComparison.Ordinal) &&
                !source.Items.OfType<object>().Concat(attacker.Items.OfType<object>()).Any(item => item.ToString()!.Contains("PRIVATE_TRAP_", StringComparison.Ordinal)),
                "Trap inputs exposed an opposing secret/hand or did not identify the installed own trap.");
            Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 1;
            Check(!Control<ComboBox>(window, "LocalSourceBox").Items.OfType<object>().Any(item => Id(item) == 40),
                "The incoming attacker leaked into the ordinary own-action editor.");
            Select(attacker, 40); Check(button.IsEnabled, "The current enemy turn disabled the independent incoming trap preview.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("在所列条件下会停止", StringComparison.Ordinal),
                "The actual trap button did not reach direct trigger damage and stop the two-health attacker.");
            Check(window.ResultText.Contains("每个敌方角色的防御处理前伤害：2", StringComparison.Ordinal) &&
                window.ResultText.Contains("未执行对手动作", StringComparison.Ordinal) && !window.ResultText.Contains("PRIVATE_TRAP_", StringComparison.Ordinal) &&
                JsonSerializer.Serialize(frame) == before, "Actual trap preview leaked hidden identities, changed its input or lost direct-effect scope.");
            var coach = new SnapshotCoach(frame, CompactAdviceContextBuilder.Build(frame, BattleViewRegression.Build), catalog, BattleViewRegression.Build);
            foreach (var args in new[] { new JsonObject { ["source_entity_id"] = 91, ["attacker_entity_id"] = 40 },
                new JsonObject { ["source_entity_id"] = 50, ["attacker_entity_id"] = 92 } })
            {
                var denied = coach.ExecuteAsync("evaluate_explosive_trap", args, default); Await(denied);
                Check(denied.Result["error"] is not null && !denied.Result.ToJsonString().Contains("PRIVATE_TRAP_", StringComparison.Ordinal) &&
                    denied.Result["Targets"] is null, "A direct private trap/hand lookup returned identities or target effects.");
            }
            window.UpdateBinding(frame with { StateVersion = 101, EventSequence = 201, ActivePlayerId = 1 }, BattleViewRegression.Build); Await(window.BindingReady);
            Select(attacker, 40); Check(button.IsEnabled, "Own-turn current non-trigger evidence could not be inspected.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("当前不触发；不预测未来回合", StringComparison.Ordinal),
                "The own-turn preview pretended an incoming attack occurred on a future enemy turn.");
            foreach (var rejected in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
                frame with { Quality = frame.Quality with { IsStable = false } }, frame with { Quality = frame.Quality with { IsComplete = false } } })
            { window.UpdateBinding(rejected, BattleViewRegression.Build); Await(window.BindingReady); Check(!button.IsEnabled, "An unconfirmed or unstable trap frame stayed actionable."); }
            var conflict = entities[50].Tags.ToDictionary(); conflict["49"] = "3";
            entities[50] = entities[50] with { Tags = conflict.ToFrozenDictionary() };
            window.UpdateBinding(frame with { Entities = entities.ToFrozenDictionary() }, BattleViewRegression.Build); Await(window.BindingReady);
            Check(!button.IsEnabled && source.Items.Count == 0, "A stale HAND/SECRET alias contradiction entered installed trap choices.");
            window.UpdateBinding(frame, BattleViewRegression.Build); Await(window.BindingReady);
            window.Invalidate("trap fixture moved");
            Check(!button.IsEnabled && window.ResultText.StartsWith("trap fixture moved", StringComparison.Ordinal) &&
                !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A stale trap preview stayed enabled or the hidden window acquired a handle.");
        }
        finally { window.Close(); }
    }

    private static JsonObject Result() => JsonNode.Parse("""
        {"SecretEntityId":50,"SecretCardId":"CORE_EX1_610","AttackerEntityId":40,"DefendingHeroEntityId":10,
         "TriggerActive":true,"ResolvesDirectDamage":true,"TrapConsumed":true,"SpellDamage":0,"DamagePerEnemy":2,
         "Targets":[{"EntityId":40,"Type":"MINION","Damage":2,"HealthBefore":2,"HealthAfter":0,"ArmorBefore":0,"ArmorAfter":0,
         "DivineShieldBefore":false,"DivineShieldAfter":false,"IsAliveAfter":false}],
         "AttackerSurvivesDirectDamage":false,"AttackStoppedByDirectDamage":true,"RequiresObservation":true,"PriorResolutionPending":false,
         "Unknowns":[],"Assumptions":[]}
        """)!.AsObject();
    private static EntitySnapshot Card(int id, string cardId, int owner, string zone, string type, params (string Key, string Value)[] extra) =>
        new(id, cardId, EntityKind.Card, extra.Concat(new[] { ("CONTROLLER", owner.ToString()), ("ZONE", zone), ("CARDTYPE", type) })
            .ToFrozenDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal));
    private static T Control<T>(LocalToolsWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static int Id(object item) => (int)item.GetType().GetProperty("Id")!.GetValue(item)!;
    private static void Select(ComboBox combo, int id) => combo.SelectedItem = combo.Items.OfType<object>().Single(item => Id(item) == id);
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Trap binding timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> complete, string message)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(complete(), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
