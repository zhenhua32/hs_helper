using System.Text.Json.Nodes;
using HsHelper.App;

public static class DiscoveryToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("discovery formatting labels every native operation in Chinese without generated candidates", ChineseOperations);
        yield return ("discovery formatting distinguishes empty unknown suppressed and independent Rewind", UnknownAndSuppressed);
        yield return ("discovery line and search boundaries cannot display verified lethal", NoLethalAcrossBoundary);
    }

    private static void ChineseOperations()
    {
        foreach (var (kind, label) in new[] { ("DeckDiscover", "追踪术"), ("OddAttackBeastDiscover", "奇异的地图"),
            ("WildGodDiscover", "护路者玛洛恩"), ("SideboardDiscover", "下水道之王"),
            ("AssembleFourCostMinion", "寻回机：拼装四费随从"), ("RewindDarkGiftBeastDiscover", "迅猛龙先锋") })
        {
            var discovery = Entry(kind); discovery["RequiresChoice"] = true;
            var result = new JsonObject { ["Discovery"] = discovery };
            var text = LocalToolResultFormatter.Format("evaluate_play_triggers", result);
            Check(text.Contains(label, StringComparison.Ordinal) && text.Contains("外部选择：需要等待真实选项", StringComparison.Ordinal) &&
                text.Contains("未生成候选、手牌实体或最终费用", StringComparison.Ordinal) && text.Contains("不能证明斩杀", StringComparison.Ordinal) &&
                !text.Contains(kind, StringComparison.Ordinal) && !text.Contains("PRIVATE_GENERATED_CARD", StringComparison.Ordinal) &&
                !text.Contains("987654", StringComparison.Ordinal), "Discovery display leaked enum codes/candidate payloads or implied a completed generated hand.");
        }
    }

    private static void UnknownAndSuppressed()
    {
        var empty = Entry("DeckDiscover"); empty["DeckCountBeforeEffect"] = 0; empty["RequiresChoice"] = false;
        var text = LocalToolResultFormatter.Format("evaluate_play_triggers", new JsonObject { ["Discovery"] = empty });
        Check(text.Contains("当前牌库数：0", StringComparison.Ordinal) && text.Contains("本次原生效果无需选择；其他触发另行核验", StringComparison.Ordinal),
            "Known empty deck became a forced choice or full settlement.");
        var unknown = Entry("SideboardDiscover"); unknown["SideboardCandidateCount"] = null; unknown["RequiresChoice"] = null;
        unknown["EffectActive"] = null; unknown["IsConditionalPreview"] = true;
        text = LocalToolResultFormatter.Format("evaluate_play_triggers", new JsonObject { ["Discovery"] = unknown });
        Check(text.Contains("当前私藏野兽可选数：未知", StringComparison.Ordinal) && text.Contains("外部选择：未知", StringComparison.Ordinal) &&
            text.Contains("是否生效未知", StringComparison.Ordinal) && text.Contains("本段仅列条件预览", StringComparison.Ordinal),
            "Unknown sideboard/current activation became zero, printed three or a settled operation.");
        var rewind = Entry("RewindDarkGiftBeastDiscover"); rewind["EffectActive"] = false; rewind["RewindActive"] = true; rewind["RequiresChoice"] = true;
        text = LocalToolResultFormatter.Format("evaluate_play_triggers", new JsonObject { ["Discovery"] = rewind });
        Check(text.Contains("本次已抑制", StringComparison.Ordinal) && text.Contains("仍需等待维持或回溯选项", StringComparison.Ordinal) &&
            text.Contains("战吼被抑制不等于回溯已移除", StringComparison.Ordinal), "Suppressed battlecry incorrectly erased independent Rewind.");
    }

    private static void NoLethalAcrossBoundary()
    {
        var line = JsonNode.Parse("""{"Verification":"Verified","IsLegal":true,"IsLethal":true,"AppliedActions":1,"Result":{"AvailableMana":4,"DiscoveryBoundaryReached":true,"Entities":[]}}""")!;
        line["Result"]!["DiscoveryPreviews"] = new JsonArray(Entry("WildGodDiscover"));
        var text = LocalToolResultFormatter.Format("evaluate_line", line);
        Check(text.Contains("护路者玛洛恩", StringComparison.Ordinal) && text.Contains("投影停在该边界", StringComparison.Ordinal) &&
            text.Contains("未能证明", StringComparison.Ordinal) && !text.Contains("已验证斩杀：是", StringComparison.Ordinal) &&
            !text.Contains("验证级别：已验证", StringComparison.Ordinal), "Unsettled discovery was displayed as a verified complete line or lethal.");
        var search = new JsonObject { ["VerifiedLethalFound"] = true, ["ExploredNodes"] = 1, ["IsExhaustive"] = false, ["Evaluation"] = line.DeepClone(), ["Actions"] = new JsonArray() };
        text = LocalToolResultFormatter.Format("find_lethal", search);
        Check(!text.Contains("找到已验证的斩杀路线", StringComparison.Ordinal) && !text.Contains("已验证斩杀：是", StringComparison.Ordinal) &&
            text.Contains("未能证明", StringComparison.Ordinal), "Search banner bypassed the same pending discovery boundary.");
    }

    private static JsonObject Entry(string kind) => new()
    {
        ["Kind"] = kind, ["ResolvesEffects"] = true, ["EffectActive"] = true, ["RequiresChoice"] = true,
        ["Scope"] = "只描述本次原生选择范围，具体选项等待观测。", ["DeckCountBeforeEffect"] = 5,
        ["Unknowns"] = new JsonArray(), ["Assumptions"] = new JsonArray(),
        ["CandidateEntityIds"] = new JsonArray(987654), ["GeneratedCardId"] = "PRIVATE_GENERATED_CARD"
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
