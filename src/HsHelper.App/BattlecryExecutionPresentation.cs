using System.Text.Json.Nodes;

namespace HsHelper.App;

internal static class BattlecryExecutionPresentation
{
    public static void AppendPreviewScope(List<string> lines, JsonNode preview)
    {
        if (preview["IsSingleBattlecryActivationPreview"]?.ToString() != "true") return;
        var multiplicity = preview["BattlecryMultiplicity"];
        lines.Add("以下仅预览一轮战吼，不是整张牌多次战吼的合计结果。");
        lines.Add("整张牌的战吼计划次数：" + (multiplicity?["Activations"]?.ToString() ?? "未知") +
            "；次数已确认：" + YesNo(multiplicity?["IsResolved"]) + "。不能自行相乘后安排后续动作。");
    }

    public static string EvidenceSummary(JsonNode? result)
    {
        if (result?["Result"]?["BattlecryExecutions"] is not JsonArray { Count: > 0 } executions) return "";
        var values = executions.OfType<JsonObject>().Take(8).Select(execution =>
            "来源 #" + (execution["SourceEntityId"]?.ToString() ?? "未知") +
            "，计划/开始/完成 " + (execution["PlannedActivations"]?.ToString() ?? "未知") + "/" +
            (execution["StartedActivations"]?.ToString() ?? "未知") + "/" +
            (execution["CompletedActivations"]?.ToString() ?? "未知"));
        return "；同一次出牌内的战吼结算：" + string.Join("；", values) +
            (executions.Count > 8 ? "；其余条目未列入摘要" : "") + "，不重复出牌或支付。";
    }

    public static void Append(List<string> lines, JsonObject execution, Func<int, string> entityName,
        Func<string, string> cardName)
    {
        var source = execution["SourceEntityId"] is JsonValue value && value.TryGetValue<int>(out var sourceId)
            ? entityName(sourceId) + " #" + sourceId
            : execution["SourceCardId"] is { } card ? cardName(card.ToString()) : "来源未知";
        lines.Add("战吼逐次结算 · 候选动作 #" + Number("ActionOrdinal") + "：" + source);
        lines.Add("固定卡库 " + (execution["CardBuild"]?.ToString() ?? "未知") + "；同一次出牌内结算，不会再次出牌或支付。");
        lines.Add("战吼是否启用：" + YesNo(execution["IsEnabled"]) + "；次数已确认：" + YesNo(execution["IsMultiplicityResolved"]) + "。");
        lines.Add("计划次数 " + Number("PlannedActivations") + "；已开始 " + Number("StartedActivations") +
            "；已完成 " + Number("CompletedActivations") + "；尚未完成 " + Number("RemainingActivations") + "。");
        if (execution["EnchantmentEntityIds"] is JsonArray { Count: > 0 } enchantments)
            lines.Add("附魔来源：" + string.Join("、", enchantments.Select(Identity)) + "。");
        else lines.Add("本条次数证据未列附魔来源；不据此推断已获得赠福。");
        if (execution["Activations"] is JsonArray activations)
        {
            foreach (var activation in activations.OfType<JsonObject>())
            {
                var status = activation["Status"]?.ToString() switch
                {
                    "Completed" => "已完成", "Fizzled" => "目标已失效，本次未产生效果",
                    "AwaitingObservation" => "等待新观测", "Interrupted" => "中断", _ => "状态未知"
                };
                lines.Add("· 第 " + (activation["ActivationOrdinal"]?.ToString() ?? "未知") + " 次：" + status +
                    "；该次结算完整：" + YesNo(activation["IsComplete"]) + "。" + Reason(activation["StopReason"]));
                if (activation["AffectedEntityIds"] is JsonArray { Count: > 0 } targets)
                    lines.Add("  本次涉及：" + string.Join("、", targets.Select(Identity)) + "。");
            }
            if (activations.Count == 0) lines.Add("尚未开始任何一次战吼；不补造后续执行。");
        }
        else lines.Add("逐次执行记录未提供，不能补造已开始或已完成的次数。");
        lines.Add("本条结算完整：" + YesNo(execution["IsComplete"]) + "；" + Reason(execution["StopReason"]));
        if (execution["RemainingActivations"]?.ToString() != "0")
            lines.Add("未完成的计划轮次不代表还会执行；对局结束或观测边界会停止后续结算。");
        lines.Add("完整序列仍以本次本地验证级别和限制为准，不以次数证据单独证明斩杀。");

        string Number(string key) => execution[key]?.ToString() ?? "未知";
        string Identity(JsonNode? id) => id is JsonValue number && number.TryGetValue<int>(out var entityId)
            ? entityName(entityId) + " #" + entityId : "实体未知";
    }

    private static string YesNo(JsonNode? value) => value?.ToString() switch
    { "true" => "是", "false" => "否", _ => "未知" };

    private static string Reason(JsonNode? value) => value?.ToString() switch
    {
        "ActivationResolved" => "该次已覆盖结算。", "AllActivationsResolved" => "计划轮次均已覆盖结算。",
        "Suppressed" => "当前战吼被抑制。", "SourceNotEntered" => "来源尚未入场，未开始战吼。",
        "UnknownMultiplicity" => "触发次数尚未确认。", "PriorResolutionPending" => "前序结算等待新观测。",
        "GameEnded" => "英雄已死亡，停止后续轮次。", "PaymentEndedGame" => "支付已结束对局，未开始战吼。",
        "RelatedResolutionRequiresObservation" => "相关触发等待新观测。", "EvaluationLimit" => "达到本次计算上限。",
        "OriginalTargetUnavailable" => "原指定目标已失效，不另选目标。",
        "DrawRequiresObservation" => "抽牌后续等待新观测。", "GenerationRequiresObservation" => "随机生成等待新观测。",
        "DiscoveryRequiresObservation" => "发现或拼装选择等待新观测。",
        "RepeatedPaymentRequiresObservation" => "重复的生命支付效果等待新观测。",
        "ActivationInterrupted" => "本轮计算中断。", _ => "停止或完成原因未单列，详见本次整体限制。"
    };
}
