using System.Text.Json.Nodes;
using HsHelper.App;

public static class SideboardEvidenceToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("JAIL display separates actual source records and current offer count from an unknown complete pool", () =>
        {
            var result = Result();
            var text = LocalToolResultFormatter.Format("evaluate_choice", result);
            Check(text.Contains("历史来源实体 #30（提供时为下水道之王）", StringComparison.Ordinal) &&
                text.Contains("实际已选 EDR_489", StringComparison.Ordinal) && text.Contains("选择 #7，候选 #80，事件 30", StringComparison.Ordinal) &&
                text.Contains("已选次数：EDR_489 2 次", StringComparison.Ordinal) && text.Contains("当前实际提供候选：2 项", StringComparison.Ordinal) &&
                text.Contains("仅指本次提供数量", StringComparison.Ordinal) && text.Contains("完整剩余边栏池未知", StringComparison.Ordinal) &&
                !text.Contains("直接加入手牌", StringComparison.Ordinal), "Historical sideboard facts were shown as an exact current pool or extra hand cards.");
        });
        yield return ("JAIL history gaps display selection lower bounds without subtracting the initial three", () =>
        {
            var result = Result(); var evidence = result["SideboardEvidence"]!;
            evidence["IsHistoryComplete"] = false; evidence["SelectedCounts"]![0]!["IsLowerBound"] = true;
            evidence["InitialReference"] = new JsonObject { ["IsValid"] = true, ["TotalCards"] = 3 };
            var text = LocalToolResultFormatter.Format("evaluate_choice", result);
            Check(text.Contains("至少 2 次", StringComparison.Ordinal) && text.Contains("次数仅为已观察下限", StringComparison.Ordinal) &&
                text.Contains("起始牌码参考数量：3", StringComparison.Ordinal) && text.Contains("不代表当前余量", StringComparison.Ordinal) &&
                !text.Contains("剩余 1", StringComparison.Ordinal), "A partial observed count or starting reference acquired a precise remaining pool.");
        });
        yield return ("JAIL transformed or consumed sources retain historical labels and do not fabricate current offers", () =>
        {
            var result = Result(); var evidence = result["SideboardEvidence"]!;
            evidence["IsCurrentSourceConfirmed"] = false; evidence["CurrentOffer"] = null;
            var text = LocalToolResultFormatter.Format("evaluate_choice", result, _ => "TRANSFORMED_CURRENT_NAME");
            Check(text.Contains("当前来源原生身份未确认", StringComparison.Ordinal) && text.Contains("过去冻结的选择事实", StringComparison.Ordinal) &&
                text.Contains("当前没有绑定到该来源的已确认候选", StringComparison.Ordinal) && !text.Contains("TRANSFORMED_CURRENT_NAME", StringComparison.Ordinal) &&
                !text.Contains("当前实际提供候选：2", StringComparison.Ordinal), "A transformed current name rewrote a past source or consumed offers were recreated.");
        });
    }

    private static JsonNode Result() => JsonNode.Parse("""
        {"Verification":"Unsupported","IsLegal":true,"ResolvesEffects":false,"CardOutcomes":[],
        "SideboardEvidence":{"ResolvesEvidence":true,"IsCurrentSourceConfirmed":true,"IsHistoryComplete":true,"CurrentPoolIsExact":false,
        "Sources":[{"SourceEntityId":30,"Selections":[{"ChoiceId":7,"SelectedEntityId":80,"CardId":"EDR_489","ChosenAtEventSequence":30}]}],
        "SelectedCounts":[{"CardId":"EDR_489","ObservedSelections":2,"IsLowerBound":false}],
        "CurrentOffer":{"OptionCount":2,"HasCompleteCardIdentities":true,"IsExactOffer":true},"InitialReference":null,"Unknowns":[]}}
        """)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
