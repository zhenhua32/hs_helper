using System.Text;
using HsHelper.Storage;

namespace HsHelper.App;

/// <summary>Displays the recorded source of limitations without inferring coverage from missing lists.</summary>
public static class AdviceLimitationsPresentation
{
    public static string Render(AdviceRecordDraft advice)
    {
        ArgumentNullException.ThrowIfNull(advice);
        var text = new StringBuilder("\n风险与限制\n");
        if (advice.LimitationsVersion != 1)
        {
            text.AppendLine("旧记录未区分本地与模型限制，不能从原风险列表推断条件、未知或未覆盖分类。");
            Append(text, "原风险（来源未区分）", advice.Risks, "旧记录未保存风险条目；不表示不存在限制。");
            return text.ToString();
        }

        text.AppendLine("本地规则限制（仅本次验证范围）");
        text.AppendLine(advice.HostLimitationsTruncated
            ? "本地限制列表已截断，以下不是完整清单，不能认定规则完整覆盖。"
            : "本地限制列表未标记截断；不表示规则已经完整覆盖。");
        text.AppendLine("未单列表示该类未单独提供；未列出不表示不存在。动作是否校验以验证状态为准。");
        Host("本地风险", advice.HostRisks);
        Host("本地条件", advice.HostAssumptions);
        Host("本地未知", advice.HostUnknowns);
        Host("本地未覆盖机制", advice.HostUnsupportedMechanics);
        text.AppendLine("\n模型意见（未经本地规则验证）");
        text.AppendLine("未单列表示模型未提供该类意见；未列出不表示不存在。");
        Model("模型条件", advice.Assumptions);
        Model("模型未知", advice.Unknowns);
        Model("模型未覆盖机制意见", advice.UnsupportedMechanics);
        Append(text, "风险摘要（本地规则与模型意见汇总）", advice.Risks,
            "本次未列出风险摘要；不表示不存在风险。");
        return text.ToString();

        void Host(string title, IReadOnlyList<string>? values) => Append(text, title, values,
            values is null ? title == "本地风险" ? "未单列（最终动作校验限制未提供）。" : "未单列。" : "未列出。");
        void Model(string title, IReadOnlyList<string>? values) => Append(text, title, values,
            values is null ? "未单列。" : "未列出。");
    }

    public static string OverlayText(AdviceRecordDraft advice)
    {
        ArgumentNullException.ThrowIfNull(advice);
        string status;
        if (advice.LimitationsVersion != 1) status = "旧记录未区分限制来源；详情见主窗口。";
        else
        {
            var missing = advice.HostRisks is null || advice.HostAssumptions is null || advice.HostUnknowns is null || advice.HostUnsupportedMechanics is null;
            status = advice.HostLimitationsTruncated ? "本地限制已截断；" : "";
            if (advice.HostRisks is null) status += "未执行或未提供最终动作校验；";
            else if (missing) status += "部分本地分类未单列；";
            var first = First(advice.HostUnsupportedMechanics, "本地未覆盖") ?? First(advice.HostUnknowns, "本地未知") ??
                First(advice.HostRisks, "本地风险") ?? First(advice.HostAssumptions, "本地条件");
            status += first ?? (missing ? "以验证状态为准。" : "本次未列出本地限制；以验证范围为准。");
        }
        // Put the local boundary before a potentially long model summary, within OverlayState's limit.
        return Clip(status, 230) + "\n验证状态：" + Clip(advice.Verification, 30) + "\n建议：" + Clip(advice.Summary, 200);
    }

    private static string? First(IReadOnlyList<string>? values, string label) =>
        values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) is { } first ? label + "：" + first : null;
    private static string Clip(string? value, int maximum)
    {
        if (value is null) return "未记录";
        if (value.Length <= maximum) return value;
        var end = maximum > 0 && char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return value[..end] + "…";
    }
    private static void Append(StringBuilder text, string title, IReadOnlyList<string>? values, string empty)
    {
        text.AppendLine("\n" + title);
        if (values is not { Count: > 0 }) text.AppendLine(empty);
        else foreach (var value in values) text.AppendLine("- " + value);
    }
}
