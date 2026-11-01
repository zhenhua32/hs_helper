using System.Text.Json.Nodes;

namespace HsHelper.App;

internal static class DeathrattleExecutionPresentation
{
    public static string EvidenceSummary(JsonNode? result)
    {
        if (result?["Result"]?["DeathrattleExecutions"] is not JsonArray { Count: > 0 } executions) return "";
        var values = executions.OfType<JsonObject>().Take(8).Select(execution =>
            "随从 #" + Number(execution, "SourceEntityId") + "、候选死亡批次 #" + Number(execution, "DeathBatchOrdinal") +
            "、" + Effect(execution["EffectKind"]) + "，计划/开始/完成 " + Number(execution, "PlannedActivations") + "/" +
            Number(execution, "StartedActivations") + "/" + Number(execution, "CompletedActivations"));
        return "；候选模拟的亡语记录：" + string.Join("；", values) +
            (executions.Count > 8 ? "；其余条目未列入摘要" : "") + "。同一随从在同一批次只计一次模拟死亡，不代表实际死亡或最终采纳路线。";
    }

    public static string DrawEvidenceSummary(JsonNode? draw)
    {
        var multiplicity = draw?["DeathrattleMultiplicity"];
        return "假设所选随从死亡一次的单轮亡语抽牌范围；随从 #" + Number(draw, "SourceEntityId") +
            "；抽牌附魔 #" + Number(draw, "EnchantmentEntityId") + "；亡语计划次数 " + Number(multiplicity, "Activations") +
            "。不是全部轮次的合计，不证明实际死亡或生成新手牌。";
    }

    public static void AppendDrawScope(List<string> lines, JsonNode draw, Func<int, string> entityName)
    {
        var multiplicity = draw["DeathrattleMultiplicity"];
        lines.Add("这是一轮亡语的直接抽牌范围；不能乘次数当作全部亡语的合计或新局面。");
        if (multiplicity is null)
        { lines.Add("亡语次数依据未提供，不能补成一次或两次。"); return; }
        lines.Add("该效果的亡语计划次数：" + Number(multiplicity, "Activations") + "；次数已确认：" + YesNo(multiplicity["IsResolved"]) +
            "；是否启用：" + YesNo(multiplicity["IsEnabled"]) + "；固定卡库 " + Number(multiplicity, "CardBuild") + "。");
        lines.Add("本条效果来源：" + Identity(multiplicity["EffectSourceEntityId"], entityName) +
            "；是否为获得的亡语：" + YesNo(multiplicity["IsAcquiredEffect"]) + "。次数只适用于本条效果，不推广到其它亡语。");
        AddSources(lines, multiplicity["MultiplierSourceEntityIds"], entityName);
        if (draw["IsSingleDeathrattleActivationPreview"]?.ToString() == "true")
            lines.Add("本次只预览首轮；新牌及后置仍待观察，不能认为后续轮次也已完成。");
    }

    public static void Append(List<string> lines, JsonArray executions, Func<int, string> entityName)
    {
        var seenDeaths = new HashSet<(int Action, int Batch, int Body)>();
        foreach (var execution in executions.OfType<JsonObject>())
        {
            var actionKnown = Positive(execution["ActionOrdinal"], out var action);
            var batchKnown = Positive(execution["DeathBatchOrdinal"], out var batch);
            var bodyKnown = Positive(execution["SourceEntityId"], out var body);
            var sameDeath = actionKnown && batchKnown && bodyKnown && !seenDeaths.Add((action, batch, body));
            lines.Add("亡语逐次结算 · " + (sameDeath ? "同一次模拟死亡的另一条效果" : "候选中的一次模拟死亡") +
                " · " + Identity(execution["SourceEntityId"], entityName));
            lines.Add("候选动作 #" + Number(execution, "ActionOrdinal") + "；死亡批次 #" + Number(execution, "DeathBatchOrdinal") +
                "；固定卡库 " + Number(execution, "CardBuild") + "。这不是实际对局死亡记录，也不会重复执行死亡、出牌或支付。");
            if (execution["DeathBatchEntityIds"] is JsonArray { Count: > 0 } deathBatch)
                lines.Add("同批死亡实体（集合，不是触发顺序）：" + string.Join("、", deathBatch.Select(id => Identity(id, entityName))) + "。");
            lines.Add("同批次顺序是否独立：" + YesNo(execution["IsBatchOrderIndependent"]) + "；未确认时不能按集合顺序推断结算。");
            lines.Add("本条效果：" + Effect(execution["EffectKind"]) + "；效果来源 " + Identity(execution["EffectSourceEntityId"], entityName) +
                "；是否为获得的亡语：" + AcquiredOrigin(execution) + "。");
            lines.Add("亡语是否启用：" + YesNo(execution["IsEnabled"]) + "；次数已确认：" + YesNo(execution["IsMultiplicityResolved"]) + "。");
            lines.Add("计划次数 " + Number(execution, "PlannedActivations") + "；已开始 " + Number(execution, "StartedActivations") +
                "；已完成 " + Number(execution, "CompletedActivations") + "；尚未完成 " + Number(execution, "RemainingActivations") + "。");
            AddSources(lines, execution["MultiplierSourceEntityIds"], entityName);
            if (execution["Activations"] is JsonArray activations)
            {
                foreach (var activation in activations.OfType<JsonObject>())
                {
                    lines.Add("· 第 " + Number(activation, "ActivationOrdinal") + " 轮：" + Status(activation["Status"]) +
                        "；该轮结算完整：" + YesNo(activation["IsComplete"]) + "。");
                    if (activation["AffectedEntityIds"] is JsonArray { Count: > 0 } affected)
                        lines.Add("  本轮涉及：" + string.Join("、", affected.Select(id => Identity(id, entityName))) + "。");
                }
                if (activations.Count == 0) lines.Add("尚未开始任何一轮亡语，不补造已执行的效果。");
            }
            else lines.Add("逐轮记录未提供，不能补造已开始或已完成次数。");
            lines.Add("本条结算完整：" + YesNo(execution["IsComplete"]) + "；" + Reason(execution["StopReason"]));
            if (execution["RemainingActivations"]?.ToString() != "0")
                lines.Add("尚未完成包含当前未完成轮，不承诺剩余轮必然执行；抽牌、未知后置或对局结束会停止后续。");
            lines.Add("完整序列仍取本次验证级别与限制，不能仅凭计划次数证明斩杀或写入实际历史。");
        }
    }

    private static void AddSources(List<string> lines, JsonNode? sources, Func<int, string> entityName)
    {
        if (sources is JsonArray { Count: > 0 } values)
            lines.Add("重复次数来源：" + string.Join("、", values.Select(id => Identity(id, entityName))) + "；仅解释次数，不重新施加已观察的属性加成。");
        else lines.Add("本条未列重复来源，不据此推断已获得双亡语。");
    }
    private static string Identity(JsonNode? value, Func<int, string> entityName) => Positive(value, out var id) ? entityName(id) + " #" + id : "实体未知";
    private static bool Positive(JsonNode? value, out int number) { number = 0; return value is JsonValue scalar && scalar.TryGetValue<int>(out number) && number > 0; }
    private static string Number(JsonNode? value, string name) => value?[name]?.ToString() ?? "未知";
    private static string YesNo(JsonNode? value) => value?.ToString() switch { "true" => "是", "false" => "否", _ => "未知" };
    private static string AcquiredOrigin(JsonObject execution) => execution["EffectKind"]?.ToString() switch
    {
        "NativeImbue" or "NativeHandDeckBuff" when execution["IsAcquiredEffect"]?.ToString() == "false" => "否",
        "ObservedEnchantmentDraw" when execution["IsAcquiredEffect"]?.ToString() == "true" => "是",
        _ => "未知"
    };
    private static string Effect(JsonNode? value) => value?.ToString() switch
    { "NativeImbue" => "原生灌注", "NativeHandDeckBuff" => "原生手牌与牌库随从增益", "ObservedEnchantmentDraw" => "已观察附魔的抽牌", _ => "效果未覆盖或未知" };
    private static string Status(JsonNode? value) => value?.ToString() switch
    { "Completed" => "已模拟完成", "Fizzled" => "本轮未产生效果", "AwaitingObservation" => "等待新观测", "Interrupted" => "中断", _ => "状态未知" };
    private static string Reason(JsonNode? value) => value?.ToString() switch
    {
        "AllActivationsResolved" => "计划轮次均已覆盖结算。", "Suppressed" => "亡语已明确被抑制。",
        "GameEnded" => "英雄已死亡，后续轮次不再启动。", "UnknownMultiplicity" => "亡语次数尚未确认。",
        "DrawRequiresObservation" => "首轮抽牌后续等待新观测。", "PriorResolutionPending" => "前序死亡或触发尚待观察。",
        "RelatedResolutionRequiresObservation" => "相关后置等待新观测。", "EvaluationLimit" => "达到计算上限。",
        "ActivationInterrupted" => "本轮计算中断。", _ => "完成或停止原因未单列，详见本次整体限制。"
    };
}
