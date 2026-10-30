namespace HsHelper.Agent;

public enum AdviceTaskKind { Mulligan, Turn, Choice, Review, DeckPlan }

public sealed record AdviceTaskTemplate(AdviceTaskKind Kind, string Version, string Instructions);

/// <summary>Explicit task policies share the same evidence and local-rule boundaries.</summary>
public static class AdviceTaskTemplates
{
    public const string Version = "hunter-v3";

    public static AdviceTaskKind Resolve(string? task) => task?.Trim().ToLowerInvariant() switch
    {
        "mulligan" or "留牌" or "留牌建议" => AdviceTaskKind.Mulligan,
        "choice" or "choose" or "选择" or "选择建议" => AdviceTaskKind.Choice,
        "review" or "复盘" or "局面复盘" => AdviceTaskKind.Review,
        "deckplan" or "deck_plan" or "套牌分析" or "套牌计划" or "赛前计划" => AdviceTaskKind.DeckPlan,
        _ => AdviceTaskKind.Turn
    };

    public static AdviceTaskTemplate Get(AdviceTaskKind task) => new(task,
        task is AdviceTaskKind.Choice or AdviceTaskKind.Turn ? "hunter-v4/" + task.ToString().ToLowerInvariant() : Version + "/" + task.ToString().ToLowerInvariant(), task switch
    {
        AdviceTaskKind.Mulligan => "当前任务：开局留牌。逐张解释已确认我方起手牌的保留或替换理由，结合先后手、已知对手职业和导入套牌计划。先后手、职业或牌效未知时明确说明；不把未知牌或未来抽牌当作事实。留牌未结束时不提供出牌顺序或确定概率。",
        AdviceTaskKind.Turn => "当前任务：回合建议。先指出当前目标、可用资源和最重要的取舍，再给出简短方案。动态费用、攻击、生命和状态以本次局面的tags为准，不能用卡库基础值覆盖。需要验证的候选动作调用本地规则工具；结束回合用end_turn，来源必须是当前我方PLAYER实体且不带目标，不能在结束后继续己方动作。回合末触发结果不代表对手起手或后续回合已被模拟。规则未完整覆盖时只给条件性策略，不宣布确定斩杀或胜率。",
        AdviceTaskKind.Choice => "当前任务：当前选择。只比较choice中已确认属于我方且已完整读取的候选；不能推荐候选之外的牌，也不把对手选择或未知候选当作已知。缺失牌效先查工具，查不到则说明。可调用evaluate_choice比较已确认来源的直接入手、减费和牌库变化；结果是候选后果预览，不是已经发生的当前局面。范围合法不代表效果完整，未知费用、随机和后续触发不补全，不能从直接后果推导已验证的后续动作或斩杀。",
        AdviceTaskKind.Review => "当前任务：当时局面复盘。严格以event_sequence及以前的局面、公开出牌和事件为界，解释当时可见信息下的决策与备选。不得引用后续抽牌、最终胜负、未来公开身份或事后信息。信息不完整时保留不确定性；本地规则未覆盖的动作不标为已验证。",
        AdviceTaskKind.DeckPlan => "当前任务：赛前套牌分析。没有实时局面时，仅依据已导入的套牌、已核对的卡牌效果及主机提供的用户笔记解释胜利条件、留牌方向、资源分配和对局计划。缺少卡库或笔记就说明缺失，不虚构实时手牌、对手套牌、当前胜率、天梯统计或确定操作。",
        _ => throw new ArgumentOutOfRangeException(nameof(task))
    });
}
