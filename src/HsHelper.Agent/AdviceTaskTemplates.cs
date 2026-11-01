namespace HsHelper.Agent;

public enum AdviceTaskKind { Mulligan, Turn, Choice, Review, DeckPlan }

public sealed record AdviceTaskTemplate(AdviceTaskKind Kind, string Version, string Instructions);

/// <summary>Explicit task policies share the same evidence and local-rule boundaries.</summary>
public static class AdviceTaskTemplates
{
    public const string Version = "hunter-v7";
    private const string PublicHandMemoryInstructions = "public_hand_memory只记录同一实体曾公开并连续追踪到对手手牌的基础身份，须与普通未知手牌和推测分开。kind=public_return是公开回手，kind=history_target及direct_reveal是历史展示经相关结算块闭合后确认，不表示该牌回手；entered_hand_at_sequence缺失不补造。SHOW及HISTORY_TARGET序号是处理事件锚点，不表示该时刻已确认公开，必须以confirmed_at_sequence/public_at_sequence为界。来源只表示当时公开的基础身份，不是当前来源牌效或动作证明。它不使该实体当前可见，不能以印刷费用、关键词或曾见属性补齐当前费用、增益、效果或手牌位置；需要当前实体效果的工具仍可拒绝查询。complete为false时列表可能遗漏，条目也不证明对手其余手牌身份；不得从后来公开信息回填较早复盘，失效或缺失记录保持未知。";
    private const string TerrorInstructions = "evaluate_line中end_turn的TerrorEndTurnPreviews列出神秘恐魔回合末有限相关分支。SourceEntityIds是联合预览的全部来源集合，不是触发顺序；每个分支的SourceOrder只是一条已开始阶段的见证前缀，Stages按FirstCombatIndex及CombatCount引用同一Combats列表，不补出尚未开始的来源或战斗。多个来源共享该分支中已经消耗的圣盾、存活、生命及累计吸血，后一个来源可再次迫使仍存活的敌方随从攻击；不得把各来源独立计算后拼接。每个分支的见证战斗顺序、英雄生命、吸血与随从存活须一起比较，不能将不同分支的有利数值拼成一个局面；见证顺序不表示实际发生或等概率。单个恐魔死亡只结束该来源阶段，未知亡语、复生或攻击后效果会停止整个联合分支，不能把已开始阶段或一次来源完成当作全部来源已结算。IsEnumerationComplete只覆盖本次计算范围。BranchProjection出现时，TotalBranches是摘要前已计算并保留的分支数量，ShownBranches才是当前展示数；AllBranchesIncluded为false时未展示分支的结果和优劣未知，不从已展示的8条推导全局上下界或唯一最优。截断或未展开触发不证明其他结果不存在；不把分支、前缀或保留的普通攻击次数/就绪字段当作权威后继状态，不继续跨回合动作或据此宣称已验证斩杀。旧结果缺少联合字段时，不从实体编号或战斗目标猜出联合来源顺序。";
    private const string WeaponInstructions = "当前武器以entity.current_weapon或计算结果WeaponDurability为准，只有已知的现代HEALTH-DAMAGE才是可用剩余耐久；缺DAMAGE不补零，印刷durability和旧DURABILITY不能覆盖当前值。最后一次攻击会损毁武器并移除其攻击贡献，不能继续使用旧武器攻击或风怒。简单装备也须使用当前费用、攻击和最大耐久；手牌武器的战吼、过载或使用效果未覆盖时，不能声称已经合法装备。WeaponResolutionBoundaryReached表示损毁亡语、替换或风怒来源仍待真实结算，不把条件中间状态作为后续动作或斩杀依据。";
    private const string EntityEffectInstructions = "实体存在拼装标记时，按entity.effect或get_entity_effect读取该实体文字；cards/get_cards是固定版本基础定义，不能覆盖拼装文字。同CardId的不同实体也可能效果不同。候选的触发/效果半段不是供体整牌，不代表新增手牌；主目标未绑定时不合成完整效果。文字重建不表示自定义脚本已被规则模拟，缺失供体或未知文字不得回退原牌效，更不能据此宣称动作或斩杀已验证。";
    private const string AssembledScriptInstructions = "evaluate_line对固定版本、完整定义和当前属性均已核对的已观察成品，可执行纯战吼给手牌随从+1/+1的脚本；AssembledEffects记录此候选序列的模拟来源、触发次数及逐实体属性变化，不是对局中已发生的事实，也不证明候选生成池或其他组合已覆盖。仅完整序列Verified可支持确定结果；缺失供体、额外触发或其它未覆盖交互保持未知，不执行基础牌已被替换的效果。";
    private const string BattlecryRepeatInstructions = "BattlecryExecutions是本次候选动作的战吼执行证据：PlannedActivations是已审核计划次数，StartedActivations和CompletedActivations才是已开始和已完成的模拟轮数，RemainingActivations含未完成的当前轮；它们不是对局中已发生的事实。单个已观察且完整审核的粗暴唤醒可重复已覆盖战吼；支付、入场及使用后触发仍各一次，不提交第二条play冒充第二轮战吼。固定目标失效的Fizzled不允许另选目标，伤害须逐包核盾/甲/死亡，不能直接乘次数；游戏已结束时剩余轮不会启动。Direct预览的BattlecryMultiplicity只说明次数依据；IsSingleBattlecryActivationPreview为true时，抽牌/生成/发现数值只是一轮，不能乘次数当整牌合计，也不能据此继续后续动作。首次选择、新牌、随机或未知后置边界仍等待新观察。未知全局叠加、未审载体或缺失字段不补0/1/2次，不把双战吼泛化为双亡语；完整结果仍以本次evaluate_line和最终主机核验为准。";
    private const string RandomEffectInstructions = "evaluate_generation只预览已覆盖来源成功离手后的随机生成数量与满手损失，不抽走牌库卡牌，不提供生成牌身份、费用或新实体；GenerationBoundaryReached之后等待新局面，不能继续出牌。evaluate_wolf_blessing列出巨狼祝福各候选直接增益，只能比较分支，不能指定随机命中，也不能把不同分支的有利后果拼成一个结果；WolfBlessingBoundaryReached后等待新局面。直接预览不证明支付和使用合法，候选动作仍须调用evaluate_line。";
    private const string DeathrattleDrawInstructions = "evaluate_deathrattle_draw只接收当前我方场上随从的source_entity_id，条件预览实际已观察且审核通过的EDR_100t4e心中的恶魔在一次假想死亡后一轮抽两张的直接入手、爆牌和疲劳。工具不会杀死随从，不证明死亡已经发生；该随从原在场，不会因本次预览从手牌离开或腾出手牌位置。DeathrattleMultiplicity只是次数来源，IsSingleDeathrattleActivationPreview为true时数量只覆盖一轮，不是所有重复亡语的合计，不能乘次数声称四抽或继续动作。以各字段相关范围和未知项为准，不把其它亡语、复生、重复或入手触发视为已经展开。evaluate_line中的DrawPreviews若为DeathrattleOrdinary，只是该已覆盖交换后的直接抽牌预览；DrawBoundaryReached后等待真实新局面，不生成新牌身份、费用或后续动作，不能据此认证完整结算或已验证斩杀。";
    private const string DeathrattleRepeatInstructions = "DeathrattleExecutions是候选动作中一次模拟死亡后逐个已审亡语脚本的执行证据，不是新的出牌、再次死亡或真实对局历史。ActionOrdinal指造成死亡的候选动作；DeathBatchEntityIds是同批死亡集合，不是触发顺序，IsBatchOrderIndependent只说明本次已证明的可交换范围。SourceEntityId是死亡本体，EffectSourceEntityId/EffectEnchantmentEntityId是原生或实际获得的效果来源，MultiplierSourceEntityIds是倍率来源，不能混为一项。EffectKind=Unknown时来源尚未确认，不能从IsAcquiredEffect=false推定原生或没有获得脚本。PlannedActivations是已审计划；StartedActivations/CompletedActivations才是已开始/已完成的模拟轮数，RemainingActivations包括当前未完成轮，未知缺字段不补0/1/2。单个实际EDR_531e可重复已覆盖原生亡语，其+2攻击已经在当前属性中，不能再次施加；当前EDR_531母牌的文字不能证明会生成这个附魔。RLK_912只重复其实际获得的亡语，不凭本体印刷文字生成亡语。第一轮抽牌、新身份、未知监听/复生或批次顺序边界先停止，不假装剩余轮已完成；英雄已死亡也不启动后续轮。全局base/additional公式、多个来源叠加、复制和未审脚本继续未知。只用本次完整evaluate_line及最终主机结论判断合法与斩杀，不提交death或deathrattle动作来制造第二次死亡。";
    private const string MapInstructions = "evaluate_choice的Map只使用当前实际提供的选项及绑定的实际已选、原己方回合HAND使用记录；同名牌或创建者不能证明新实体就是原首选。MapPlayPreviews只是本地模拟使用后的条件请求，不是实际PLAY历史或已经出现的追加选项；MapFollowupBoundaryReached后等待真实追加选择，不创建第二张手牌或继续后续动作，也不证明斩杀。";
    private const string MagmaInstructions = "evaluate_magma_split仅接收source_entity_id与target_entity_id，后者是攻击对象，不是随机命中；预览包含基础交换及后置逐包分配，整体仍可能为条件性，AttackIsLegal未知不能当成已确认合法。只能比较相关分支和数值范围，不能指定分配顺序、实际命中或概率，不能将不同目标区间的有利上下界拼成一个局面。枚举截断或未列出成功分支不证明其它结果不存在；MagmaSplitBoundaryReached之后等待新观测，不将分支当作已发生状态、接续动作或已验证斩杀。";
    private const string ExplosiveTrapInstructions = "evaluate_explosive_trap仅接收我方已安装陷阱source_entity_id和敌方公开来袭者attacker_entity_id，攻击对象固定为我方英雄，不能指定伤害、受伤目标或未来回合。只假设当前来袭发生并列直接触发后果，不执行对手动作、不推测对方奥秘。当前我方回合不触发不代表未来仍不触发；来袭者直接存活或AttackStoppedByDirectDamage为false也不证明完整攻击会继续或已经结算，未知亡语、触发和攻击合法性保持未知。不能将该条件预览当作权威新局面、后续己方动作或已验证斩杀。";
    private const string PlayTriggerInstructions = "evaluate_play_triggers只接收当前我方手牌实体，主机核对原生发现、拼装和回溯入口，并自动绑定围攻城门任务和梦缚迅猛龙来源。发现入口以成功离手为条件，使用后触发以前置效果已结算为条件，各段不能拼成已发生的局面。DiscoveryBoundaryReached后等待真实选择或新观测，不制造候选、入手实体、最终费用或完整拼装成品；战吼被抑制不证明独立回溯已移除。牌库或边栏范围未知不等于空池，可靠空池也不证明其他结算已完成。evaluate_choice的SideboardEvidence记录实际已选、来源分组及当前候选，不是当前完整边栏池；InitialReference若有IsSessionBound及DeckVersionId，仅表示本局记录时固定的起始牌码参考，不证明此来源实体的完整池或复制、偷取后的继承关系；历史有缺口时次数仅为已观察下限，不能用初始三张减去已选、复制或偷取来源推定精确剩余。任务进度从当前观察值推进，缺失或冲突保持未知；不能自行给定进度、触发来源或随机额外效果。QuestRewardBoundaryReached后等待真实奖励选择，不制造候选或成品；RaptorBoundaryReached后等待随机效果的新局面，不选定词条、不将范围当确定次数或继续后续动作。该直接预览不证明支付、战吼或完整结算合法，也不能证明斩杀，仍须evaluate_line核验候选顺序。";

    private const string ObservedDarkGiftInstructions = "已观察黑暗之赐静态附魔只在build255790按精确定义、附着、归属和当前属性审核：EDR_100te、EDR_100t6e、EDR_100t1e、EDR_100t2e、EDR_100t3e、EDR_100t8e1、EDR_100t13e，以及已结算的EDR_100t8e、EDR_100t5e2、EDR_100t5e5可交evaluate_line验证已支持出牌与战斗链。同目标全部来源都已审核为静态时使用当前聚合值，不重新分配来源、施加增益或减费，不能再次赋予关键词；已破圣盾不补回，风怒仍受实际攻击次数限制，扰魔不能由仅禁法术或技能的标签替代。1/1、2/2和已打乱属性记录只核当前已知值，不证明复制来源、生成时序或随机结果。不能从选择按钮推定新入手实体或尚未观察的关键词，不能重复支付；其它未审亡语脚本、使用时复制、未审重复载体、特殊复生和三次盾等未覆盖来源仍保留边界，以本次工具和主机最终验证为准。";

    public static AdviceTaskKind Resolve(string? task) => task?.Trim().ToLowerInvariant() switch
    {
        "mulligan" or "留牌" or "留牌建议" => AdviceTaskKind.Mulligan,
        "choice" or "choose" or "选择" or "选择建议" => AdviceTaskKind.Choice,
        "review" or "复盘" or "局面复盘" => AdviceTaskKind.Review,
        "deckplan" or "deck_plan" or "套牌分析" or "套牌计划" or "赛前计划" => AdviceTaskKind.DeckPlan,
        _ => AdviceTaskKind.Turn
    };

    public static AdviceTaskTemplate Get(AdviceTaskKind task) => new(task,
        task is AdviceTaskKind.Choice or AdviceTaskKind.Turn ? "hunter-v25/" + task.ToString().ToLowerInvariant() : Version + "/" + task.ToString().ToLowerInvariant(), task switch
    {
        AdviceTaskKind.Mulligan => "当前任务：开局留牌。逐张解释已确认我方起手牌的保留或替换理由，结合先后手、已知对手职业和导入套牌计划。先后手、职业或牌效未知时明确说明；不把未知牌或未来抽牌当作事实。留牌未结束时不提供出牌顺序或确定概率。",
        AdviceTaskKind.Turn => "当前任务：回合建议。先指出目标、当前资源和取舍，再给出简短方案。动态费用、攻击、生命和状态以本次tags为准，不用印刷值覆盖。候选动作调用evaluate_line核验；evaluate_draw仅假设本体成功使用后预览直接抽牌、满手损失和疲劳范围，不能证明出牌合法。DrawBoundaryReached之后实体投影只到抽牌前，不生成新手牌或继续安排动作；训犬师抽牌后的灌注须等待新局面。结束回合用end_turn，来源必须是我方PLAYER实体且不带目标，不能在结束后继续己方动作。回合末结果不代表对手起手。未完整覆盖时只给条件性策略，不宣布确定斩杀或胜率。",
        AdviceTaskKind.Choice => "当前任务：当前选择。只比较choice中确认我方且完整就绪的候选；不推荐候选之外的牌，不把对手或未知候选当已知。缺失牌效先查工具，查不到则说明。evaluate_choice比较直接入手、授予减费、设费、牌库变化和多阶段后果，预览不是已发生局面。组件不等于成品，阶段和前次组件须有同局实际已选证据。延系须有实际出牌时类型及上个己方真实回合证据，静态关键词不证明激活；玛洛恩设费为1不等于减1，最终费用仍未知。回溯仅表示请求重做，不能自行恢复法力、手牌、随机或旧局面。范围合法不代表效果完整，不能从预览推导后续动作或已验证斩杀。",
        AdviceTaskKind.Review => "当前任务：当时局面复盘。严格以event_sequence及以前的局面、公开出牌和事件为界，解释当时可见信息下的决策与备选。不得引用后续抽牌、最终胜负、未来公开身份或事后信息。信息不完整时保留不确定性；本地规则未覆盖的动作不标为已验证。",
        AdviceTaskKind.DeckPlan => "当前任务：赛前套牌分析。没有实时局面时，仅依据已导入的套牌、已核对的卡牌效果及主机提供的用户笔记解释胜利条件、留牌方向、资源分配和对局计划。缺少卡库或笔记就说明缺失，不虚构实时手牌、对手套牌、当前胜率、天梯统计或确定操作。",
        _ => throw new ArgumentOutOfRangeException(nameof(task))
    } + EntityEffectInstructions + PublicHandMemoryInstructions + (task is AdviceTaskKind.Turn or AdviceTaskKind.Choice ? AssembledScriptInstructions + BattlecryRepeatInstructions + DeathrattleRepeatInstructions + RandomEffectInstructions + DeathrattleDrawInstructions + PlayTriggerInstructions + ObservedDarkGiftInstructions + MapInstructions + MagmaInstructions + ExplosiveTrapInstructions + WeaponInstructions + TerrorInstructions : ""));
}
