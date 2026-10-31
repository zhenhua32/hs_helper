namespace HsHelper.Agent;

public enum AdviceTaskKind { Mulligan, Turn, Choice, Review, DeckPlan }

public sealed record AdviceTaskTemplate(AdviceTaskKind Kind, string Version, string Instructions);

/// <summary>Explicit task policies share the same evidence and local-rule boundaries.</summary>
public static class AdviceTaskTemplates
{
    public const string Version = "hunter-v4";
    private const string WeaponInstructions = "当前武器以entity.current_weapon或计算结果WeaponDurability为准，只有已知的现代HEALTH-DAMAGE才是可用剩余耐久；缺DAMAGE不补零，印刷durability和旧DURABILITY不能覆盖当前值。最后一次攻击会损毁武器并移除其攻击贡献，不能继续使用旧武器攻击或风怒。简单装备也须使用当前费用、攻击和最大耐久；手牌武器的战吼、过载或使用效果未覆盖时，不能声称已经合法装备。WeaponResolutionBoundaryReached表示损毁亡语、替换或风怒来源仍待真实结算，不把条件中间状态作为后续动作或斩杀依据。";
    private const string EntityEffectInstructions = "实体存在拼装标记时，按entity.effect或get_entity_effect读取该实体文字；cards/get_cards是固定版本基础定义，不能覆盖拼装文字。同CardId的不同实体也可能效果不同。候选的触发/效果半段不是供体整牌，不代表新增手牌；主目标未绑定时不合成完整效果。文字重建不表示自定义脚本已被规则模拟，缺失供体或未知文字不得回退原牌效，更不能据此宣称动作或斩杀已验证。";
    private const string RandomEffectInstructions = "evaluate_generation只预览已覆盖来源成功离手后的随机生成数量与满手损失，不抽走牌库卡牌，不提供生成牌身份、费用或新实体；GenerationBoundaryReached之后等待新局面，不能继续出牌。evaluate_wolf_blessing列出巨狼祝福各候选直接增益，只能比较分支，不能指定随机命中，也不能把不同分支的有利后果拼成一个结果；WolfBlessingBoundaryReached后等待新局面。直接预览不证明支付和使用合法，候选动作仍须调用evaluate_line。";
    private const string MapInstructions = "evaluate_choice的Map只使用当前实际提供的选项及绑定的实际已选、原己方回合HAND使用记录；同名牌或创建者不能证明新实体就是原首选。MapPlayPreviews只是本地模拟使用后的条件请求，不是实际PLAY历史或已经出现的追加选项；MapFollowupBoundaryReached后等待真实追加选择，不创建第二张手牌或继续后续动作，也不证明斩杀。";
    private const string MagmaInstructions = "evaluate_magma_split仅接收source_entity_id与target_entity_id，后者是攻击对象，不是随机命中；预览包含基础交换及后置逐包分配，整体仍可能为条件性，AttackIsLegal未知不能当成已确认合法。只能比较相关分支和数值范围，不能指定分配顺序、实际命中或概率，不能将不同目标区间的有利上下界拼成一个局面。枚举截断或未列出成功分支不证明其它结果不存在；MagmaSplitBoundaryReached之后等待新观测，不将分支当作已发生状态、接续动作或已验证斩杀。";
    private const string ExplosiveTrapInstructions = "evaluate_explosive_trap仅接收我方已安装陷阱source_entity_id和敌方公开来袭者attacker_entity_id，攻击对象固定为我方英雄，不能指定伤害、受伤目标或未来回合。只假设当前来袭发生并列直接触发后果，不执行对手动作、不推测对方奥秘。当前我方回合不触发不代表未来仍不触发；来袭者直接存活或AttackStoppedByDirectDamage为false也不证明完整攻击会继续或已经结算，未知亡语、触发和攻击合法性保持未知。不能将该条件预览当作权威新局面、后续己方动作或已验证斩杀。";
    private const string PlayTriggerInstructions = "evaluate_play_triggers只接收当前我方手牌实体，主机核对原生发现、拼装和回溯入口，并自动绑定围攻城门任务和梦缚迅猛龙来源。发现入口以成功离手为条件，使用后触发以前置效果已结算为条件，各段不能拼成已发生的局面。DiscoveryBoundaryReached后等待真实选择或新观测，不制造候选、入手实体、最终费用或完整拼装成品；战吼被抑制不证明独立回溯已移除。牌库或边栏范围未知不等于空池，可靠空池也不证明其他结算已完成。evaluate_choice的SideboardEvidence记录实际已选、来源分组及当前候选，不是当前完整边栏池；历史有缺口时次数仅为已观察下限，不能用初始三张减去已选、复制或偷取来源推定精确剩余。任务进度从当前观察值推进，缺失或冲突保持未知；不能自行给定进度、触发来源或随机额外效果。QuestRewardBoundaryReached后等待真实奖励选择，不制造候选或成品；RaptorBoundaryReached后等待随机效果的新局面，不选定词条、不将范围当确定次数或继续后续动作。该直接预览不证明支付、战吼或完整结算合法，也不能证明斩杀，仍须evaluate_line核验候选顺序。";

    public static AdviceTaskKind Resolve(string? task) => task?.Trim().ToLowerInvariant() switch
    {
        "mulligan" or "留牌" or "留牌建议" => AdviceTaskKind.Mulligan,
        "choice" or "choose" or "选择" or "选择建议" => AdviceTaskKind.Choice,
        "review" or "复盘" or "局面复盘" => AdviceTaskKind.Review,
        "deckplan" or "deck_plan" or "套牌分析" or "套牌计划" or "赛前计划" => AdviceTaskKind.DeckPlan,
        _ => AdviceTaskKind.Turn
    };

    public static AdviceTaskTemplate Get(AdviceTaskKind task) => new(task,
        task is AdviceTaskKind.Choice or AdviceTaskKind.Turn ? "hunter-v13/" + task.ToString().ToLowerInvariant() : Version + "/" + task.ToString().ToLowerInvariant(), task switch
    {
        AdviceTaskKind.Mulligan => "当前任务：开局留牌。逐张解释已确认我方起手牌的保留或替换理由，结合先后手、已知对手职业和导入套牌计划。先后手、职业或牌效未知时明确说明；不把未知牌或未来抽牌当作事实。留牌未结束时不提供出牌顺序或确定概率。",
        AdviceTaskKind.Turn => "当前任务：回合建议。先指出目标、当前资源和取舍，再给出简短方案。动态费用、攻击、生命和状态以本次tags为准，不用印刷值覆盖。候选动作调用evaluate_line核验；evaluate_draw仅假设本体成功使用后预览直接抽牌、满手损失和疲劳范围，不能证明出牌合法。DrawBoundaryReached之后实体投影只到抽牌前，不生成新手牌或继续安排动作；训犬师抽牌后的灌注须等待新局面。结束回合用end_turn，来源必须是我方PLAYER实体且不带目标，不能在结束后继续己方动作。回合末结果不代表对手起手。未完整覆盖时只给条件性策略，不宣布确定斩杀或胜率。",
        AdviceTaskKind.Choice => "当前任务：当前选择。只比较choice中确认我方且完整就绪的候选；不推荐候选之外的牌，不把对手或未知候选当已知。缺失牌效先查工具，查不到则说明。evaluate_choice比较直接入手、授予减费、设费、牌库变化和多阶段后果，预览不是已发生局面。组件不等于成品，阶段和前次组件须有同局实际已选证据。延系须有实际出牌时类型及上个己方真实回合证据，静态关键词不证明激活；玛洛恩设费为1不等于减1，最终费用仍未知。回溯仅表示请求重做，不能自行恢复法力、手牌、随机或旧局面。范围合法不代表效果完整，不能从预览推导后续动作或已验证斩杀。",
        AdviceTaskKind.Review => "当前任务：当时局面复盘。严格以event_sequence及以前的局面、公开出牌和事件为界，解释当时可见信息下的决策与备选。不得引用后续抽牌、最终胜负、未来公开身份或事后信息。信息不完整时保留不确定性；本地规则未覆盖的动作不标为已验证。",
        AdviceTaskKind.DeckPlan => "当前任务：赛前套牌分析。没有实时局面时，仅依据已导入的套牌、已核对的卡牌效果及主机提供的用户笔记解释胜利条件、留牌方向、资源分配和对局计划。缺少卡库或笔记就说明缺失，不虚构实时手牌、对手套牌、当前胜率、天梯统计或确定操作。",
        _ => throw new ArgumentOutOfRangeException(nameof(task))
    } + EntityEffectInstructions + (task is AdviceTaskKind.Turn or AdviceTaskKind.Choice ? RandomEffectInstructions + PlayTriggerInstructions + MapInstructions + MagmaInstructions + ExplosiveTrapInstructions + WeaponInstructions : ""));
}
