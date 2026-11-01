# 固定版本的主牌构筑张数

preview27之后的源码支持固定build255790的两项主牌张数规则：裂魂者阿扎莉娜`JAIL_430/126055`要求提交20张，雷纳索尔王子`REV_018/79767`要求40张。这里核对的是导入／保存的主牌，不是开局复制、洗牌或其他效果结算后的牌库数量，也不模拟起始生命或战吼。

旧preview27的实际Cards DLL已复现错误：在用于隔离构筑的完整合成卡池中，两者各自的30张主牌都被判为Legal，正确的20／40张却仅得到普通30张警告。合成卡池不证明这些卡当前都在国服标准上线；旧包的这两项特殊张数判断不应作为合法性依据。该修复尚未重新打包，旧ZIP和验收记录保持原样。

## 证据与适用范围

固定来源为[255790中文卡牌JSON](https://api.hearthstonejson.com/v1/255790/zhCN/cards.json)及[CardDefs](https://api.hearthstonejson.com/v1/255790/CardDefs.base.xml)。完整源SHA256分别为`7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299`和`EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8`；完整固定样本见[deck-size-255790.json](../tests/HsHelper.Cards.Tests/Fixtures/deck-size-255790.json)。

自动同步审核两张卡的完整CardDefs实体，包含`DECK_RULE_MOD_DECK_SIZE/997`的20／40、DBF、文本及其他字段；使用时再次核对完整规范化卡牌JSON与解析后的Record。只按名称、ID、中文数字、单一997标签或用户填写的RequiredCards不能获得已审核结论。官方[阿扎莉娜卡库](https://hearthstone.blizzard.com/en-gb/cards/126055-azalina-soulsever/)、[雷纳索尔卡库](https://hearthstone.blizzard.com/en-us/cards/79767-prince-renathal/)及[36.0.3说明](https://hearthstone.blizzard.com/en-gb/news/24290431)可辅助核对；网页和卡文不证明客户端复制、洗牌或组合优先级。

规则只由主牌中的实际来源触发，边栏中的同一张卡不触发。两项来源同时出现时，不猜20和40的优先级；未知构筑例外、任一主牌定义缺失、固定来源被篡改、有效期／build／模式不匹配时，整体张数要求仍未知。单项已核对来源可以保留，但不能任选一个有利张数。威兹班、泽尔及其他替换整副套牌的规则仍需独立实现。

张数已确认且不匹配时为Illegal；匹配仅通过这一项结构要求。职业、普通同名牌限制、古神组、边栏、禁用、上线及卡池完整性继续分别检查。官方系列池仍为partial，20／40张符合不能将整体Unknown升级为Legal。

## 导入、兼容和输出

版本化卡池JSON的`Construction`可包含新的`MainDeckSizes`列表：

```json
"MainDeckSizes": [
  { "OwnerCardId": "JAIL_430", "RequiredCards": 20, "KeepsOrdinaryCopyLimits": true },
  { "OwnerCardId": "REV_018", "RequiredCards": 40, "KeepsOrdinaryCopyLimits": true }
]
```

它与原`CardGroups`、`Sideboards`共享明确的来源、build、模式与有效期，子证据必须位于父卡池时间范围内。空约束、重复拥有者、空项和无效数量被拒绝，导入失败不替换旧记录。此列表不是放行未经审核卡牌或未来版本的通用公式。

查询按JSON中的完整生效时点选取最新已生效规则，SQLite毫秒列仅作粗筛。同一毫秒内尚晚一tick的规则不能提前生效，实际较晚规则不受导入先后影响；最新已生效版本过期后仍不复活更旧版本。旧规则正文与哈希不因精度修复重写。

卡库schema3无损升级为4，原卡牌、活动版本、旧规则JSON及哈希保留。旧证据缺少`MainDeckSizes`时继续省略该字段；新版不能在尚未升级的旧schema中写入新约束。旧程序启动时拒绝schema4，回退前需恢复升级前备份；不能通过降版本号让旧程序忽略张数规则。

`DeckDescription.MainDeckSize`独立返回实际张数、可缺省要求、`IsKnown`、可缺省`IsSatisfied`、各来源与未知项。普通无特殊来源时保持旧的null结果；未知或冲突不补成30张。桌面显示固定卡库与提交的张数，并区分张数符合及完整构筑合法性。已保存套牌沿用原build，切换、关闭和规则更新后的迟到读取不能重新显示旧结论；小窗口可滚动访问按钮、张数和结果末行。

赛前模型背景新增`deck_construction`，绑定同一原套牌、build和查询时点，桌面与CLI的已绑定套牌计划共用。赛前策略单独升至`hunter-v8/deckplan`，回合／选择保持v25，留牌／复盘保持v7；旧归档不改模板。超过现有500项构筑检查上限的巨大参考套牌保留完整资料供策略分析，构筑摘要明确Unknown和上限，不能因额外检查中断已有分批查卡，也不截掉原套牌来制造符合要求的结果。

## 验证范围

验证使用完整公开固定记录、隔离SQLite、合成卡池、假HTTP与隐藏WPF；不读取真实日志／模型密钥或操作游戏。源码完整回归与最终结果见[开发记录](development.md#主牌张数构筑规则与赛前背景)。实服完整特殊构筑、复制／替换牌效、国服兼容性、多屏／DPI与干净Windows验收仍按[清单](implementation-plan.md)推进。

最终1521项源码检查及27项指南检查通过，构建零警告／错误、默认NuGetAudit；34项冻结输入前后哈希一致。包含实际CLI、保存版本build未知的真实赛前HTTP／归档，以及640×460套牌页检查，不把合成验证视为实战或新安装包验收。
