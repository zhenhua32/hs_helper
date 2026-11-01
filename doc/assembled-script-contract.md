# 已观察拼装成品的确定脚本

自 preview26 起支持 build 255790 中已实际观察的“纯战吼 → 手牌所有随从 +1/+1”成品。入口沿用动作编辑器、`evaluate_line`、有限斩杀搜索和最终主机验证；不生成拼装选项或新手牌实体。发行来源为 `fee5771`，范围见[发行验证](windows-release.md)。

## 依据与范围

执行语义来自三项证据的组合解释：

- [暴雪公告](https://news.blizzard.com/en-us/article/24297200/command-the-old-gods-in-reign-of-the-black-empire-hearthstone-s-next-expansion)说明以另一张牌的部分文字替换随从能力；[官方团队说明](https://www.reddit.com/r/hearthstone/comments/1wf8f4k/assemble_a_new_keyword_in_reign_of_the_black/)区分被替换能力与保留的主体身份、费用、属性、类型和名字。
- 固定 HDT 提交 `7c0550fe8388bf45e0f3dbc5d2ff25ada2e1a3ae` 的 [AssembledHalves](https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/7c0550fe8388bf45e0f3dbc5d2ff25ada2e1a3ae/Hearthstone%20Deck%20Tracker/Hearthstone/CardExtraInfo/AssembledHalves.cs)和[真实案例测试](https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/7c0550fe8388bf45e0f3dbc5d2ff25ada2e1a3ae/HDTTests/Hearthstone/RelatedCardsSystem/AssembledEffectCardIdTests.cs)确认半段 DBF 关联：4572 为前半，4573 为后半，零保留主体相应半段，一为候选按钮，大于一为供体 DBF。缺失标签不当作显式零。
- [固定中文卡库](https://api.hearthstonejson.com/v1/255790/zhCN/cards.json)与 [CardDefs](https://api.hearthstonejson.com/v1/255790/CardDefs.base.xml)提供两个确切半段。完整 SHA256 分别为 `7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299`、`EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8`。

这不是客户端私有脚本或每种组合的真实对局实验。HDT 证明显示协议与关联，不是本项目的执行器；本轮不以语义组合推断随机生成池、职业/费用资格或某次选择一定提供的供体。

完整来源样本与角色索引见 [assembled-scripts-255790.json](../tests/HsHelper.Rules.Tests/Fixtures/assembled-scripts-255790.json)，审核表见 [AssembledScriptAudit.cs](../src/HsHelper.Rules/AssembledScriptAudit.cs)：

| 角色 | 固定集合及限制 |
| --- | --- |
| 已观察主体 | 301 条带唯一分段边界的 MINION 定义；运行时另核当前身份、区域、归属、类别与属性。 |
| 纯战吼前半 | 62 条确切供体：57 MINION、3 WEAPON、2 HERO；只保留其纯战吼触发，不执行供体整牌效果。前半为零时，主体必须在其中57条 MINION 内。 |
| 手牌随从 +1/+1 后半 | 下表5条确切供体；后半为零时，主体本身须为其中一条。 |

| 效果供体 CardId | DBF |
| --- | ---: |
| BE_207 | 131236 |
| CFM_753 | 40567 |
| CORE_CFM_753 | 102196 |
| CS3_025 | 66858 |
| TIME_100 | 121051 |

306 条完整卡面定义逐项固定规范化 SHA256，并核对 `CardRecord` 与原始 JSON 一致性。不能靠名称、包含“战吼”的整段文字或供体类型模糊匹配。前半含额外关键词、复合触发或条件的其它脚本仍未覆盖；零/零继续走原生规则。

## 一条动作链中的行为

实际成品使用当前费用、攻击、最大生命与损伤；不借供体数值回填，不将主体改名成供体。先审核当前受影响的手牌集合、目标属性及相关交互，再支付一次、入场一次。本体离手后只强化仍在手牌中的我方随从，各加一攻击、一最大生命和一剩余生命，保留既有损伤和费用，不创建附魔实体或新身份。

每个实体使用独立的有效 Profile。同 CardId 的不同实例不共享拼装能力；原基础牌被替换的发现、亡语、光环、攻击后或回合末脚本不得继续按 CardId 执行。[官方37.0.1热修](https://us.forums.blizzard.com/en/hearthstone/t/3701-hotfix-patch/166622)修复过覆盖后残留嘲讽及 Jaraxxus 错换英雄技能，进一步说明不能继承被替换的旧能力。当前明确观察的额外关键词与触发仍须按相应规则审核，不能因此忽略真实反证。

没有额外次数修正的普通战吼执行一次，不要求用户另外提供“次数为一”标签。明确抑制时不执行增益；布尔/别名冲突、未知重复修正、缺少必要当前参数、未知监听或附魔仍保留边界。独立的任务、迅猛龙及其它使用后效果继续走原规则，不能因拼装战吼成功就越过随机或奖励选择边界。

例如当前成品为 EDR_102、前半0、后半121051，当前费用4；另有已观察1费1/1冲锋随从、5点法力、对方英雄2生命。完整已覆盖序列可以先出成品、再出强化至2/2的随从、攻击英雄。确定性增益直接进入该序列的模拟状态，因此可由同一搜索和最终主机核验；其它未覆盖交互仍可能使这条序列成为条件性结果。

## 输出与历史

`SimulatedState.AssembledEffects` 为只读列表，记录对局/状态/事件/卡库绑定、来源实体和主体、实际两半 DBF 与供体、脚本标识、触发状态与次数，以及逐目标攻击、最大/剩余生命的前后值。它描述候选序列中的模拟执行，不能写成已经发生的对局事实。桌面同时显示来源与执行范围；普通成功不创建待选择边界。

旧结果没有该字段时继续缺省，不反推历史脚本、触发次数或供体。模型仍只提交现有动作与实体ID，不能传入供体、半段、增益值或次数来替代主机观察。供体仅按固定版本和当前可见实体引用批量查取；私有对手手牌、未确认身份、观战私有区和未来历史不授权加载。

F3TCH3R 选择前的主随从关联、候选到实际入手的新实体映射、完整生成池及其它拼装脚本仍未完成。真实国服画面与客户端时序验收继续保留。
