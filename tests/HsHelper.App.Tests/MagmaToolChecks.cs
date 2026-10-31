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

public static class MagmaToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("magma formatting preserves conditional legality and correlated Chinese branches", BranchDisplay);
        yield return ("magma formatting preserves unknown values and truncated enumeration", UnknownDisplay);
        yield return ("magma line and search boundaries cannot display verified lethal", BoundaryDisplay);
        yield return ("magma schema accepts only the ordinary source and attacked target", ToolSchema);
        yield return ("turn and choice v12 policies constrain magma branches without changing other tasks", PolicyScope);
    }

    private static void BranchDisplay()
    {
        var result = Preview();
        var text = LocalToolResultFormatter.Format("evaluate_magma_split", result, id => "公开目标-" + id);
        Check(text.Contains("攻击与相关机制合法性：未知", StringComparison.Ordinal) &&
            text.Contains("已预览基础攻击动作数：1", StringComparison.Ordinal) &&
            text.Contains("分配伤害 0～2", StringComparison.Ordinal) && text.Contains("公开目标-20", StringComparison.Ordinal) &&
            text.Contains("候选分支 branch-1", StringComparison.Ordinal) && text.Contains("请求伤害包已用尽", StringComparison.Ordinal) &&
            text.Contains("圣盾 否；存活 是", StringComparison.Ordinal) && text.Contains("吸血恢复 2", StringComparison.Ordinal) &&
            text.Contains("不能拼接上下界", StringComparison.Ordinal) && text.Contains("攻击对象不是随机命中", StringComparison.Ordinal) &&
            text.Contains("不能选择分支", StringComparison.Ordinal) && !text.Contains("AllPacketsApplied", StringComparison.Ordinal),
            "Magma display turned conditional legality into true/false, lost correlated branch effects or exposed mechanism codes.");
        result["AttackIsLegal"] = false;
        Check(LocalToolResultFormatter.Format("evaluate_magma_split", result).Contains("合法性：不成立", StringComparison.Ordinal),
            "A known illegal attack was displayed as merely unobserved.");
    }

    private static void UnknownDisplay()
    {
        var result = Preview(); var split = result["Split"]!;
        split["RequestedPackets"] = null; split["DamagePerPacket"] = null; split["IsEnumerationComplete"] = false;
        split["PriorResolutionPending"] = true;
        split["Targets"]![0]!["Damage"] = new JsonObject { ["Minimum"] = 0, ["Maximum"] = null };
        split["Targets"]![0]!["RemainingHealth"] = null;
        split["Branches"]![0]!["IsComplete"] = false; split["Branches"]![0]!["StopReason"] = "PacketLimit";
        split["Branches"]![0]!["SourceHeroHealthAfter"] = null;
        var text = LocalToolResultFormatter.Format("evaluate_magma_split", result);
        Check(text.Contains("请求伤害包数：未知", StringComparison.Ordinal) && text.Contains("修正）：未知", StringComparison.Ordinal) &&
            text.Contains("0～上界未知；生命 未知", StringComparison.Ordinal) && text.Contains("已列分支不代表全部可能结果", StringComparison.Ordinal) &&
            text.Contains("达到枚举限制，结果已截断", StringComparison.Ordinal) && text.Contains("前序结算仍待确认", StringComparison.Ordinal) &&
            text.Contains("我方英雄生命预览 未知", StringComparison.Ordinal) && !text.Contains("PacketLimit", StringComparison.Ordinal),
            "Magma display replaced missing values with zero or treated partial enumeration as the complete outcome space.");
    }

    private static void BoundaryDisplay()
    {
        var line = JsonNode.Parse("""{"Verification":"Verified","IsLegal":true,"IsLethal":true,"AppliedActions":1,"Result":{"MagmaSplitBoundaryReached":true,"Entities":[]}}""")!;
        line["Result"]!["MagmaSplitPreviews"] = new JsonArray(Preview()["Split"]!.DeepClone());
        var text = LocalToolResultFormatter.Format("evaluate_line", line);
        Check(text.Contains("熔岩猎犬逐包分配预览", StringComparison.Ordinal) && text.Contains("未能证明", StringComparison.Ordinal) &&
            text.Contains("后续动作", StringComparison.Ordinal) && !text.Contains("验证级别：已验证", StringComparison.Ordinal) &&
            !text.Contains("已验证斩杀：是", StringComparison.Ordinal), "A random split boundary displayed a verified full line or lethal.");
        var search = new JsonObject { ["VerifiedLethalFound"] = true, ["ExploredNodes"] = 2,
            ["IsExhaustive"] = false, ["Evaluation"] = line.DeepClone(), ["Actions"] = new JsonArray() };
        text = LocalToolResultFormatter.Format("find_lethal", search);
        Check(!text.Contains("找到已验证的斩杀路线", StringComparison.Ordinal) && text.Contains("未能证明", StringComparison.Ordinal),
            "The lethal search banner bypassed the unresolved magma boundary.");
    }

    private static void ToolSchema()
    {
        var definition = SnapshotCoach.Definitions.Single(tool => tool.Name == "evaluate_magma_split");
        Check(definition.ValidateArguments(new() { ["source_entity_id"] = 30, ["target_entity_id"] = 40 }) &&
            definition.Parameters["additionalProperties"]?.GetValue<bool>() == false &&
            definition.Parameters["properties"]!.AsObject().Count == 2 &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 30 }) &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 30, ["target_entity_id"] = 0 }),
            "Magma tool widened its ordinary attack schema.");
        foreach (var key in new[] { "random_target_entity_id", "branch_id", "packets", "damage_per_packet", "allocation" })
            Check(!definition.ValidateArguments(new() { ["source_entity_id"] = 30, ["target_entity_id"] = 40, [key] = 1 }),
                "Magma tool accepted a model-selected random outcome or simulator parameter.");
    }

    private static void PolicyScope()
    {
        foreach (var task in new[] { AdviceTaskKind.Turn, AdviceTaskKind.Choice })
        {
            var policy = AdviceTaskTemplates.Get(task);
            Check(policy.Version == "hunter-v12/" + task.ToString().ToLowerInvariant() &&
                policy.Instructions.Contains("evaluate_magma_split", StringComparison.Ordinal) &&
                policy.Instructions.Contains("MagmaSplitBoundaryReached", StringComparison.Ordinal) &&
                policy.Instructions.Contains("AttackIsLegal未知", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不能将不同目标区间", StringComparison.Ordinal) &&
                policy.Instructions.Contains("MapPlayPreviews", StringComparison.Ordinal) &&
                policy.Instructions.Contains("SideboardEvidence", StringComparison.Ordinal),
                "The current policy omitted magma uncertainty or replaced existing choice lifecycle constraints.");
        }
        foreach (var task in new[] { AdviceTaskKind.Mulligan, AdviceTaskKind.Review, AdviceTaskKind.DeckPlan })
            Check(AdviceTaskTemplates.Get(task).Version.StartsWith("hunter-v4/", StringComparison.Ordinal) &&
                !AdviceTaskTemplates.Get(task).Instructions.Contains("evaluate_magma_split", StringComparison.Ordinal),
                "Magma policy changed an unrelated task template.");
    }

    /// <summary>Actual hidden WPF button to bound host adapter; no real logs or model calls.</summary>
    public static void RunOnDispatcher(CardCatalog catalog, GameSnapshot basis)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Magma button smoke needs the STA dispatcher.");
        var entities = basis.Entities.Where(pair => pair.Key is 1 or 2 or 10 or 20).ToDictionary();
        entities[30] = Card(30, "FIR_953", 1, "PLAY", "MINION", ("CARDRACE", "BEAST"), ("ATK", "2"), ("HEALTH", "7"),
            ("DAMAGE", "0"), ("EXHAUSTED", "0"), ("NUM_TURNS_IN_PLAY", "1"), ("NUM_ATTACKS_THIS_TURN", "0"));
        entities[40] = Card(40, "CORE_UNG_912", 2, "PLAY", "MINION", ("ATK", "0"), ("HEALTH", "1"), ("DAMAGE", "0"));
        entities[41] = Card(41, "CORE_UNG_912", 2, "PLAY", "MINION", ("ATK", "0"), ("HEALTH", "3"), ("DAMAGE", "0"));
        entities[90] = Card(90, "PRIVATE_MAGMA_UI_PROBE", 2, "HAND", "SPELL");
        var frame = basis with { StateVersion = 90, EventSequence = 190, Entities = entities.ToFrozenDictionary(), DeckTracking = null, PublicPlays = null };
        var before = JsonSerializer.Serialize(frame); var window = new LocalToolsWindow(frame, BattleViewRegression.Build, catalog);
        try
        {
            Await(window.BindingReady); SelectAttack();
            var button = Control<Button>(window, "LocalPreviewMagmaSplitButton");
            Check(button.IsEnabled && button.Content?.ToString() == "预览熔岩猎犬分配", "The actual magma button was missing or disabled for a public ordinary attack.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitUntil(() => !window.HasPendingCalculation && window.ResultText.Contains("候选分支", StringComparison.Ordinal),
                "The actual magma button did not reach the bound host attack adapter.");
            Check(window.ResultText.Contains("合法性：未知", StringComparison.Ordinal) &&
                window.ResultText.Contains("请求伤害包数：2", StringComparison.Ordinal) &&
                window.ResultText.Contains("各目标区间互相关联", StringComparison.Ordinal) &&
                !window.ResultText.Contains("PRIVATE_MAGMA_UI_PROBE", StringComparison.Ordinal) && JsonSerializer.Serialize(frame) == before,
                "The actual UI leaked private identities, mutated its frame or certified a conditional random attack.");
            Select(window, "LocalTargetBox", 20); Check(!button.IsEnabled, "Attacking a hero enabled a minion-attack split preview.");
            Select(window, "LocalTargetBox", 40); Check(button.IsEnabled, "Changing the attack target did not refresh preview eligibility.");
            foreach (var rejected in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
                frame with { Quality = frame.Quality with { IsStable = false } }, frame with { Quality = frame.Quality with { IsComplete = false } },
                frame with { ActivePlayerId = 2 } })
            {
                window.UpdateBinding(rejected, BattleViewRegression.Build); Await(window.BindingReady);
                if (Control<ComboBox>(window, "LocalSourceBox").Items.Count > 0) SelectAttack();
                Check(!button.IsEnabled, "A private, unstable or inactive frame enabled the magma attack button.");
            }
            var conflict = entities[40].Tags.ToDictionary(); conflict["202"] = "SPELL";
            entities[40] = entities[40] with { Tags = conflict.ToFrozenDictionary() };
            window.UpdateBinding(frame with { Entities = entities.ToFrozenDictionary() }, BattleViewRegression.Build); Await(window.BindingReady); SelectAttack();
            Check(!button.IsEnabled, "A contradictory numeric target type enabled a supposedly native attack preview.");
            window.UpdateBinding(frame, BattleViewRegression.Build); Await(window.BindingReady); SelectAttack();
            window.Invalidate("magma fixture moved");
            Check(!button.IsEnabled && window.ResultText.StartsWith("magma fixture moved", StringComparison.Ordinal) &&
                !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "A stale magma result remained actionable or the hidden smoke created a visible window.");
        }
        finally { window.Close(); }

        void SelectAttack() { Control<ComboBox>(window, "LocalKindBox").SelectedIndex = 1; Select(window, "LocalSourceBox", 30); Select(window, "LocalTargetBox", 40); }
    }

    private static JsonObject Preview() => JsonNode.Parse("""
        {"AttackIsLegal":null,"AppliedActions":1,"Split":{"TriggerActive":true,"RequestedPackets":2,"DamagePerPacket":1,
         "ResolvesBranches":true,"IsEnumerationComplete":true,"PriorResolutionPending":false,
         "Targets":[{"EntityId":20,"Damage":{"Minimum":0,"Maximum":2},"RemainingHealth":{"Minimum":8,"Maximum":10},
         "RemainingArmor":{"Minimum":0,"Maximum":0},"Survives":true}],
         "Branches":[{"Id":"branch-1","PacketsApplied":2,"PacketsRemaining":0,"IsComplete":true,"StopReason":"AllPacketsApplied",
         "Targets":[{"EntityId":20,"Type":"HERO","RemainingHealth":8,"RemainingArmor":0,"DivineShield":false,"IsAlive":true}],
         "SourceHeroHealthAfter":20,"LifestealHealing":2}],"Unknowns":[],"Assumptions":[]},"Errors":[],"Assumptions":[]}
        """)!.AsObject();
    private static EntitySnapshot Card(int id, string cardId, int owner, string zone, string type, params (string Key, string Value)[] extra) =>
        new(id, cardId, EntityKind.Card, extra.Concat(new[] { ("CONTROLLER", owner.ToString()), ("ZONE", zone), ("CARDTYPE", type) })
            .ToFrozenDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal));
    private static T Control<T>(LocalToolsWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static void Select(LocalToolsWindow window, string name, int id)
    {
        var combo = Control<ComboBox>(window, name);
        combo.SelectedItem = combo.Items.OfType<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Magma binding timed out."); task.GetAwaiter().GetResult(); }
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
