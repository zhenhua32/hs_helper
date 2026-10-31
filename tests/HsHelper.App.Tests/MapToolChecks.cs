using System.Text.Json.Nodes;
using HsHelper.App;

public static class MapToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("map choice display distinguishes actual remaining options and incomplete lifecycle", ChoiceStages);
        yield return ("map simulated use and search cannot display verified lethal across the followup boundary", PendingUse);
    }

    private static void ChoiceStages()
    {
        var choice = JsonNode.Parse("""{"Verification":"Conditional","IsLegal":true,"CardOutcomes":[{"Name":"幽爪熊","Origin":"MapRemainingOptions","AddedToHand":true}],"Map":{"Stage":"FollowupChoice","InitialSelectedEntityId":40,"InitialSelectedPlayedAtEventSequence":120,"RemainingOptions":[{"EntityId":41,"Name":"幽爪熊"}]}}""")!;
        var text = LocalToolResultFormatter.Format("evaluate_choice", choice);
        Check(text.Contains("来自地图原剩余选项", StringComparison.Ordinal) && text.Contains("本回合实际使用首选", StringComparison.Ordinal) &&
            text.Contains("实际使用事件：120", StringComparison.Ordinal) && !text.Contains("FollowupChoice", StringComparison.Ordinal),
            "Map followup displayed as a fresh generic pool or lost its actual-use evidence.");
        choice["Map"]!["Stage"] = "Unconfirmed"; choice["Map"]!["InitialSelectedPlayedAtEventSequence"] = null;
        text = LocalToolResultFormatter.Format("evaluate_choice", choice);
        Check(text.Contains("证据不足", StringComparison.Ordinal) && text.Contains("实际使用事件：尚未确认", StringComparison.Ordinal) &&
            text.Contains("不生成后续候选或可执行手牌", StringComparison.Ordinal), "Unknown lifecycle was displayed as a completed followup.");
    }

    private static void PendingUse()
    {
        var line = JsonNode.Parse("""{"Verification":"Verified","IsLegal":true,"IsLethal":true,"AppliedActions":1,"Result":{"AvailableMana":3,"Entities":[],"MapFollowupBoundaryReached":true,"MapPlayPreviews":[{"MapSourceEntityId":30,"PlayedEntityId":40,"IdentityMatch":false,"PriorResolutionPending":true}]}}""")!;
        var text = LocalToolResultFormatter.Format("evaluate_line", line);
        Check(text.Contains("模拟使用已记录为条件请求", StringComparison.Ordinal) && text.Contains("脚本关联仍待确认", StringComparison.Ordinal) &&
            text.Contains("不是实际使用记录或追加候选", StringComparison.Ordinal) && text.Contains("投影停在该边界", StringComparison.Ordinal) &&
            !text.Contains("已验证斩杀：是", StringComparison.Ordinal) && !text.Contains("验证级别：已验证", StringComparison.Ordinal),
            "Map pending request became an authoritative actual play or verified lethal.");
        var search = new JsonObject { ["VerifiedLethalFound"] = true, ["ExploredNodes"] = 1, ["IsExhaustive"] = false,
            ["Evaluation"] = line.DeepClone(), ["Actions"] = new JsonArray() };
        text = LocalToolResultFormatter.Format("find_lethal", search);
        Check(!text.Contains("找到已验证的斩杀路线", StringComparison.Ordinal) && !text.Contains("已验证斩杀：是", StringComparison.Ordinal),
            "Search bypassed the pending Map boundary.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
