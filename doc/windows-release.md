# Windows 预览版发行与快速启动

随包的 `QUICKSTART.zh-CN.md` 由独立的[用户指南正文](user-quickstart.zh-CN.md)和当前构建版本／发布方式生成。本页保留仓库中的构建说明与验收记录，不再整份复制进新包；包内 `package-manifest.json`、`SHA256SUMS` 和 ZIP 旁的收据提供该包的来源与完整性信息。

此包面向 Windows x64。默认自包含包固定包含 .NET 10.0.14 Windows Desktop 运行时，使用者无需安装 SDK 或 .NET。请把 ZIP **完整解压**到一个新目录，再双击 `HsHelper.App.exe`；所有 DLL 和 `samples` 目录应留在程序旁。不要在 ZIP 预览窗口内启动。

首次启动的数据目录默认为 `%LOCALAPPDATA%\HsHelper`，与程序目录分开。卡库、套牌、历史、模型加密配置和缓存都保存在该目录。替换程序目录升级时保留该数据目录；不要把它复制进发行包。使用 `--data-dir "D:\HsHelperData"` 或环境变量 `HSHELPER_DATA_DIR` 可指定其他数据目录，命令行优先；日常启动无需参数。

本版将旧卡库无损升级为支持构筑约束的新格式，卡牌、卡池和历史哈希保留。升级前可备份数据目录；旧版程序会拒绝读取升级后的卡库，需要回退时恢复升级前备份，避免旧版忽略新增限制。

本版还会将历史库升级到 schema 3，为新事件保存所属状态版本；旧套牌、事件正文和快照保留。旧事件无法确定同序号内所属状态时，回看会明确提示并省略该部分，不补造时间归属。升级前请备份数据目录；旧版程序不能读取升级后的历史库，回退需恢复升级前备份。

模型配置可读取旧格式 1；明确加密保存后写成格式 2，保留工具开关及输出格式。旧程序会拒绝格式 2，回退时需恢复原配置或重新填写。工具开关和提示 JSON／JSON 对象／JSON Schema 可在“模型与预算设置”选择；后两种需服务支持，失败不会自动切换模式。保存会先应用设置，再写盘；写盘失败会提示尚未保存，关闭不回滚已应用值。

首次使用可先导入 `samples\decks\beast-hunter.txt` 中的公开示例牌码，配置游戏日志目录，再按界面提示获取对应版本卡库。包内不附带完整卡库，也不会自动导入真实对局。最新标准系列成员与完整合法性检查仍有证据边界，界面出现 Unknown 时不能理解为全部合法。

在“AI 教练”页填写自己的服务地址、模型和密钥并保存，连接测试由使用者主动执行。Windows 加密配置仅供当前 Windows 用户使用。发行包不含 `.env`、模型密钥、历史数据库或用户日志；本机开发用 `.env` 不应放进解压目录或随包分享。AI 只给建议，不会自动操作游戏。复杂牌效、未知奥秘和随机结果仍会标记限制。

## 当前版本：preview28

2026 年 11 月 1 日，本机已生成并验证 `0.1.0-preview.28` Windows x64 自包含包，来源为干净提交 `33b7328956ff354567e86ae6289403ee92365b76`，`sourceDirty=false`。直接运行 `.local\publish\0.1.0-preview.28-self-contained\HsHelper.App.exe`；包含 Core/Desktop 10.0.14，无需另装运行时。

- ZIP：`.local\publish\0.1.0-preview.28-self-contained.zip`，67,368,427 字节，完整解压后共 415 个文件。
- ZIP SHA256：`a762a50da40b4cbe298cc7c69cec5cd4fd3f82bb8f78bd66fec77711b33512fc`，同级 `.zip.sha256` 可核对。
- 包内指南已区分卡库 schema 4／历史 schema 3，并说明首次空数据启动会自动保存并选中“野兽快”。

此包纳入 preview27 后的 20／40 张主牌构筑修复、中文提示和赛前计划背景，保留已有记牌、历史、悬浮窗、本地计算和 AI 教练。首次使用按[快速指南](user-quickstart.zh-CN.md)准备日志和对应版本卡库，AI 服务可按需配置。

本轮九套源码检查共 1521 项通过：Core 177、Ingestion 9、Cards 60、Agent 127、Configuration 37、Storage 122、Rules 618、App 115、Integration 256；另 27 项指南检查通过。完整构建零错误，NuGet 漏洞索引不可达产生 25 条 NU1900 警告，本轮未取得在线漏洞审计结果。整套 WPF 检查总预算调整为 180 秒，单步骤限时及断言保留；本机 loopback 集成检查在沙箱外使用合成数据与模拟 HTTP 完成。

发布脚本已通过包内运行时的无窗口启动诊断、415 文件清单／SHA256 和 ZIP 逐文件核验。另通过 27 项包安全及 3 项包指南语义检查；链接拒绝案例所需的合成 Junction 创建在沙箱外完成。实际包主窗口与套牌页已打开核对，示例套牌自动选中，正常关闭退出 0。界面验证使用独立测试数据目录和测试模型配置，只做自动诊断，未启动监听／回放或模型请求。日志保存在 `.local/v1-ready-*.log`。

本轮仅生成上述自包含包，未生成依赖框架包，也未上传产物。真实国服对局、完整复杂牌效、多屏／DPI 和干净 Windows 验收仍未完成；本机主窗口验证不替代这些验收。以下旧版记录保留当时的范围，不表示本工作区仍保存其全部文件，也未在本轮重新核验。

<a id="当前版本preview27"></a>

## 历史记录：preview27

2026 年 11 月 1 日，本机已验证 `0.1.0-preview.27` 两种包，均来自干净提交 `9c781a00574aa5a9c6f9fedc262a69991de79345`，`sourceDirty=false`。产物保留在 `.local\publish`，未上传或加入版本控制：

| 部署方式 | ZIP 文件 | ZIP 字节 | 展开文件数／字节 |
| --- | --- | ---: | ---: |
| 自包含 | `0.1.0-preview.27-self-contained.zip` | 67,355,110 | 415／151,170,406 |
| 依赖框架 | `0.1.0-preview.27-framework-dependent.zip` | 2,229,303 | 20／4,845,122 |

- 自包含 ZIP SHA256：`615fe2d0d993c29c5ecb5fd1beb0e1207093f42b5d317d8ca7baacf7dadc62e0`。
- 依赖框架 ZIP SHA256：`a235c5b8d21d24cf073b2f86f1dc9a012707e6f3eef0f376a27f4bfb07038776`。

本版加入已观察来源的双战吼与双亡语，分别记录计划、开始、完成及未完成轮数；一次出牌只支付、入场和触发使用后效果一次，一次死亡只移除本体一次。支持已审灌注、固定目标伤害和拼装手牌增益的逐轮战吼，以及幽爪熊、震地雷龙原生亡语的完整后续路线；天灾巨魔获得抽牌脚本后仍在首轮未知身份处等待。未知叠加、全局来源、复制／复生和隐藏牌库保持边界，不从当前母牌推造赠福来源。具体见[战吼范围](battlecry-repeat-contract.md)与[亡语范围](deathrattle-repeat-contract.md)。回合／选择模板为`hunter-v25`，其它任务基础版本保持`hunter-v7`。

发行来源通过1493项完整源码回归及27项指南检查。唯一正式双包会话13034真实退出0，第四份产品完整性收据writer也退出0；四份机器收据与日志位于`.local/release-preview27/0.1.0-preview.27-*`，精确终态及收据长度／SHA保存在同目录`terminal-verification.json`。两种包各通过27项安全、3项指南语义、ZIP逐文件及正常用户无窗口诊断；SC Core/Desktop10.0.14的七个关键DLL与官方缓存／nupkg条目一致，两个整归档SHA512匹配。八产品DLL跨模式字节相同；161项实际包构建输入在发布前后逐Gitblob／SHA绑定9c781a0，两次构建默认NuGetAudit、零警告／错误。产品源码、测试和构建输入未因后续文档更新改变。

实际包代码另通过Agent122＋App115共237项检查，十一类隐藏WPF流程各4组，包括双战吼、双亡语、拼装及多恐魔。唯一77524真实退出0，原始收据与终态位于`.local/desktop-preview27-smoke/06f0ab656f9e4112b8ef5497381e9d3a`。仅编hook和测试，实际App八项／Agent六项产品加载路径、版本和SHA均匹配包内DLL副本；三次test-only构建默认NuGetAudit、零警告／错误。15项公开输入为13份CardJSON、App.xaml和包内样本，53份原测试源码及六份附加来源另有哈希；卡牌／样式／测试副本绑定9c的实际Git blob。App十三个真实workspace查找方法加AppContext祖先记录均定位到隔离副本，Agent祖先也定位到该目录；四张新增流程PNG实际生成于副本内，双战吼及双亡语640×460中文结果已检视。fake环境与数据库只在新建合成目录，不读取真实模型配置。

Core177＋Storage122原完整测试另通过299项，唯一88721真实退出0，收据`.local/package27-core-storage-checks/c344084eaccb4a08903b3d095b627f4c/package-core-storage-receipt.json`及`.local/package27-core-storage-checks/terminal-verification.json`保留证据。两个测试入口均为net10.0-windows，Storage122全套使用与SC27同字节的四个SQLite托管依赖及原生DLL；四个产品与SQLite托管模块的实际加载路径、版本、SHA及MVID逐项核对SC/FW两包。原生路径按Windows完整路径大小写语义比较，SHA严格一致；无托管MVID且两边版本元数据均空，保持不适用／空值，不推断SQLite语义版本。三个test-only构建默认NuGetAudit、零警告／错误，无重跑或额外子集计数；已有DirectReveal3＋Recorder1包含在122内，Core177不宣称SQLite功能覆盖。

实际Core/Cards/Rules发布DLL的副本另通过完整Rules618项，唯一74729真实退出0。40份原测试源码、22份全部fixture和365项源码／测试／构建／样本输入逐Gitblob与SHA绑定9c；只编测试与hook，两个构建默认NuGetAudit、零警告／错误。三个实际加载模块的路径、版本、SHA及MVID均核对SC/FW两包，包含全部301拼装主体、双战吼／双亡语和多恐魔原用例。收据`.local/package27-rules-checks/2207385fc2024105aeee96a6093bf774/package-rules-receipt.json`及`.local/package27-rules-checks/terminal-verification.json`保留完整记录。上述237／299／618分别记录，不累加进1493项源码检查；未重编产品或再次运行同字节FW产品。

桌面原终态中的旧smoke字节摘要误记为0，独立只读`post-run-readonly-addendum.json`纠正为422项、37,623,598字节，并再次核对逐项长度／SHA及旧25／26完整基线；原收据保留，未重跑测试。原clean门实际完成于七份文档更新前，之后修改的新driver未执行，补充记录不以新脚本冒充原执行证据。

本版双包、ZIP及旁文件共439项、225,600,152字节，在上述检查前后集合、长度及SHA不变。旧preview26全部发布前缀与发行、桌面抽查、Core/Storage及Rules检查四个完整目录共1022项、307,611,034字节也保持不变，冻结清单`.local/release-preview27/preview26-frozen-files.json`；旧preview25原1016项、337,521,011字节再次验证不变。旧版记录保留各自当时的覆盖范围。

未调用真实模型、操作游戏或上传产物；App ZIP不附仓库CLI。包代码检查使用本机Core10.0.0，WPF另用Desktop10.0.0，从隔离输出加载包的同字节副本；依赖框架诊断也使用本机Core/Desktop10.0.0，SC10.0.14入口另经无窗口验证。这些检查不等于干净Windows、真实多屏／DPI或国服对局验收。

## preview27之后的构筑修复

后续源码已修复preview27中阿扎莉娜20张／雷纳索尔王子40张的主牌要求误判，支持独立张数证据、中文提示、赛前计划及小窗口滚动。旧包的特殊张数判断不应作为合法性依据；完整合成卡池只用于复现结构问题，不代表实服标准可用性。该修复现已纳入上述 preview28 自包含包；preview27 历史验收记录保留其原有范围。

源码卡库升级至schema4，旧程序不能读取升级后的卡库；回退需恢复升级前备份。具体来源、版本范围与新字段见[主牌张数合同](main-deck-size-contract.md)。

## preview26 保留记录

2026 年 11 月 1 日，本机已验证 `0.1.0-preview.26` 的两种包，均来自干净提交 `fee5771a59752de457fa745ed6d6425175c51b30`，`sourceDirty=false`。产物保留在 `.local\publish`，未上传或加入版本控制：

| 部署方式 | ZIP 文件 | ZIP 字节 | 展开文件数／字节 |
| --- | --- | ---: | ---: |
| 自包含 | `0.1.0-preview.26-self-contained.zip` | 67,315,751 | 415／151,034,214 |
| 依赖框架 | `0.1.0-preview.26-framework-dependent.zip` | 2,189,939 | 20／4,708,930 |

- 自包含 ZIP SHA256：`afc00f06035aafbbecd33424330182b1ce234bef45b3a479e0f3993b6bf28aab`。
- 依赖框架 ZIP SHA256：`529248bf509e812c649681a62a0aaff0331165ad86dabf610186a07ed096a914`。

本版新增多神秘恐魔联合相关预览，以及已观察拼装成品的纯战吼→手牌随从+1/+1确定脚本。多恐魔共享同一分支中的盾、存活和吸血，保留来源阶段见证、截断及未知后置边界；模型的大结果摘要八条不代表全部可能或最优。拼装脚本保留主体和当前属性，报告实际半段来源、战吼启用状态、模拟执行次数及逐目标变化；不生成候选、身份或尚未观察的新手牌，不恢复已被替换的原生效果。详见[牌效覆盖](hunter-rule-coverage.md)与[拼装脚本范围](assembled-script-contract.md)。回合／选择模板为 `hunter-v23`，其它任务基础版本仍为 `hunter-v7`。

发行来源通过1439项完整源码回归及27项指南检查。唯一正式双包会话60077已退出0，四份机器收据与阶段日志位于`.local/release-preview26/0.1.0-preview.26-*`，其长度、哈希和精确终态记录在同目录`terminal-verification.json`。两种包各通过27项安全、3项指南语义、ZIP逐文件与正常用户无窗口诊断；自包含Core/Desktop10.0.14的七个关键DLL与官方缓存、nupkg条目一致，两个整归档SHA512收据匹配。八个产品DLL跨模式字节相同，入口、依赖、SQLite和样本完整；两次发布均保留默认NuGetAudit，构建零警告／错误。

实际包代码另通过Agent118＋App115共233项检查，九类隐藏WPF流程各4组，包含新增多恐魔及拼装完整动作链。唯一92545已退出0，收据`.local/desktop-preview26-smoke/0c55a7fcf85e4967a4dc972fae33f3f5/package-code-smoke-receipt.json`。仅编hook和测试，实际加载的App八项／Agent六项产品位置、版本与SHA均匹配发布DLL副本；13项公开输入为11份CardJSON＋同源App.xaml＋包内样本，副本与固定源逐SHA核对，另核49份拷贝测试源及四份附加来源。部分测试从AppContext祖先定位到当时干净仓库，实际读取该处同源公开fixture／App.xaml；不能把副本哈希核验说成所有测试都从副本读取。fake环境和数据库在新建合成GUID目录，发布DLL仍从隔离测试输出加载。三次test-only构建默认NuGetAudit、零警告／错误。

另用未改动的Core177＋Storage122测试核验包内四项托管产品副本，共299项通过。两个测试入口均以net10.0-windows构建；Storage122全套实际使用与Windows包同字节的四个SQLite托管依赖及e_sqlite3原生DLL，路径、SHA与可用版本元数据均核对，不再沿用preview25主299组的不同provider。原DirectReveal3＋Recorder1已包含在122中，不重复计数；Core177不运行SQLite，不把预载四产品当作额外功能覆盖。三次仅测试／hook构建默认NuGetAudit、零警告／错误。收据`.local/package26-core-storage-checks/e1da306bbb9342118fc8dd1c626a7f55/finalization/package-core-storage-receipt.json`及`.local/package26-core-storage-checks/terminal-verification.json`记录完整过程。

299组原包装会话96833退出1，原因是收据比较把Windows同一路径的`e_sqlite3.DLL`／`.dll`当作不同文件；两测试子进程均已退出0并完整通过。原驱动、日志与加载证据保留，独立finalizer仅按Windows完整路径语义重新核对，退出0，未重跑或重编产品／测试。原生DLL两边均无文件／产品版本元数据，按相同空值与SHA核对，不推断SQLite语义版本。

实际发布Core/Cards/Rules三个DLL的副本另通过原Rules全部584项检查，包含完整306定义审核、301主体动作链及多恐魔分支。唯一76119退出0，36份原测试源码、20份完整fixture及全部344项相关构建输入逐Gitblob绑定fee5771；只编测试与加载hook，两个构建默认NuGetAudit、零警告／错误。实际加载路径、版本、SHA和MVID与发布DLL匹配，收据`.local/package26-rules-checks/6fa335546dbd4ffeb53e379496151b5e/package-rules-receipt.json`，终态`.local/package26-rules-checks/terminal-verification.json`。此584与前述233／299分项记录，不重复累加到1439项源码检查。

以上包代码检查运行本机Core10.0.0，WPF另用Desktop10.0.0；从隔离测试输出加载同字节副本。SC10.0.14入口另经无窗口验证，依赖框架包也使用本机可用运行时启动，没有重复执行同字节FW产品代码，不等于干净Windows、真实多屏／DPI或国服对局验收。未调用真实模型或操作游戏，App ZIP不附仓库CLI。

双包、ZIP及旁文件共439项、225,249,045字节在抽查前后集合、长度、SHA不变。旧preview25的全部发布前缀、整个发行／桌面抽查／Core和Storage检查目录共1016项、337,521,011字节也保持不变；冻结清单为`.local/release-preview26/preview25-frozen-files.json`。此前各版验收范围保留如下，不能把新版检查回填旧产物。

## preview25 保留记录

2026 年 11 月 1 日，本机已验证 `0.1.0-preview.25` 的两种包，来源提交为 `de871408d282ed729934d1a8652e633daac6057d`，构建时均为 `sourceDirty=false`。产物保留在 `.local\publish`，未上传或加入版本控制；旧 preview24 的包、ZIP、发行证明、桌面抽查、连续性专项及附加诊断／Audit 原始证明共 505 项、225,592,600 字节的文件集合、长度及哈希保持不变：

| 部署方式 | ZIP 文件 | ZIP 字节 | 展开文件数／字节 |
| --- | --- | ---: | ---: |
| 自包含 | `0.1.0-preview.25-self-contained.zip` | 67,278,562 | 415／150,933,350 |
| 依赖框架 | `0.1.0-preview.25-framework-dependent.zip` | 2,152,748 | 20／4,608,066 |

- 自包含 ZIP SHA256：`0823f349d4421dc9ba6f34f43906a372d01b01f8a21957f86a7c13b3e9249293`。
- 依赖框架 ZIP SHA256：`50a9e4a4e3beb5e11fc0f623a1b1c1a3eea60251eb9c0568f14f7d76d0530a1d`。

该源码通过 1398 项完整离线回归及 27 项指南生成检查。两种包各通过正常用户无窗口诊断、清单／ZIP 逐文件校验、27 项安全和 3 项指南语义检查。自包含 Core 与 Windows Desktop 均为 10.0.14，七个关键运行时 DLL 与官方缓存及 nupkg 条目一致，两个整归档 SHA512 收据也匹配。依赖框架诊断使用本机 Core/Desktop 10.0.0，不是干净 Windows 安装验收。两次发布保留默认 NuGetAudit，构建零警告、零错误。

本版包含独立的对手公开回手记忆：只展示同一实体在公开场面／墓地确认、回手后仍连续可靠的基础身份与来源，贯通主窗、悬浮窗、模型及精确历史。费用、增益、当前效果和牌序保持未知，隐藏实体与私有附魔不因此公开。中途观察及缺口后的新公开事实可重新追踪，整体质量仍不完整；错绑的模型上下文被整份拒绝，不能沿用晚帧定义或事件。小窗口中的主窗分区及浮窗长条目可分别滚动，迟到查询不能重画失效记忆。

preview25 新增限定的手牌“历史展示确认”：build 255790 的同一实体实际 SHOW、完整 HISTORY_TARGET、可靠公开来源及相关祖先块闭合后，还须等待任务结算。它与公开回手分别保存来源，不补造回手事实；裸 SHOW、REVEALED、锻造／准备和未知隐藏块仍不能授权身份。实体复用、不同身份 SHOW 的旧类别、描述符反证和身份跨期均有针对性修复；同事件序号的较晚录制可追加不可变事件，保持旧精确决策和重复导入不变。此处是离线能力，不证明实际客户端时序。

preview24 修复了 preview23 标准抽查之外新发现的异常日志遗漏：无对应开始的 BLOCK_END 统一清除旧手牌记忆和旧公开来源。新连续性证明不会补入旧 JSON；旧异常记录的原字节／哈希保留，只在派生视图中省略其不可靠关联，正常旧记忆继续可用。另用实际 preview24 Core／Storage DLL 通过 6 组专项检查，覆盖首次／重复异常、旧来源不复活、新公开证明恢复和两份实际 preview23 旧记录；这份旧版专项收据原样保留，路径为 `.local/public-memory-underflow-fixed-audit/package24-receipt.json`。

“心中的恶魔”单次亡语抽两张预览、十条已观察静态赠福、已覆盖强制交换、有限搜索中的硬币／冲锋组合及 88 项完整原生定义审核均保留。抽牌预览不杀死随从、不生成新牌或放行后续动作，重复亡语／复生及未知触发仍待观察；附魔公开身份继续继承目标和观察者范围。回合／选择策略为 `hunter-v21`，其它任务基础版本为 `hunter-v7`。其它直接公开机制、完整复杂牌效、真实国服流程及多屏体验尚未完成。

大局面分批查卡、最后观察复盘的精确保存点绑定、历史续问及来源审计均保留。仓库 CLI 也是完整源码回归的一部分，**本次 App ZIP 不附带 CLI 程序**。

实际包内程序集另通过 Agent 113 项、App 115 项，共 228 项抽查；原 DarkGift、扩展静态赠福、FindLethal、附魔隐私、亡语抽牌、公开回手记忆及新增直接展示的七类隐藏 WPF 流程分别明确通过四组。仅重编 hook 和测试，实际加载的产品 DLL 路径、SHA256 与版本均匹配发布文件；三次 test-only 构建保留默认 NuGetAudit，零警告、零错误。两包 8 个产品 DLL 逐一字节相同，依赖声明与必需入口、SQLite、样本及指南完整；SC 415／FW 20 文件在抽查前后集合、长度和哈希均不变。九个公开卡牌 fixture、同源 App.xaml 与包内样本共十一项另有来源哈希，新增直接展示检查源码和 hunter-v21/v7 模板源码也单列哈希；这些来源仅用于核对，抽查未重编产品。抽查使用本机 Core/Desktop 10.0.0；依赖框架包的八个产品 DLL 字节相同，未重复执行同一套代码抽查。这不替代自包含 10.0.14 入口、真实多屏或干净 Windows 验收；没有调用真实模型或操作游戏。旧 preview24 的完整桌面抽查目录 205 项也保持原样。

另用同源 Core177 项和 Storage122 项测试，单独核验实际 preview25 的托管产品 DLL 副本，共299项通过。只编检查与加载 hook，不编产品；四个产品副本的实际加载位置已记录，SHA256 与版本均与包一致，三次构建默认 NuGetAudit、零警告／错误，唯一会话29259退出0。收据为 `.local/package25-core-storage-checks/bcb5646bb27a4480a8543890b619b14b/package-core-storage-receipt.json`，父目录 `terminal-verification.json` 记录终态。此组使用本机 Core10.0.0 和原 net10.0 测试目标的 NuGet SQLite 依赖，后者与 Windows 包的 provider 不同，不能将这299项说成包内 SQLite 依赖已运行验收。
随后将原 Storage 子集的直接展示3项和同序号录制1项，置于新 net10.0-windows 检查项目重复核验，4/4通过；不把它们另计为新增独立用例。实际加载位置已记录；四个产品副本、四个SQLite托管DLL及e_sqlite3原生DLL的SHA256逐项匹配SC25；这次确实使用Windows包同字节的provider。两次test-only构建默认NuGetAudit、零警告／错误，唯一会话76131退出0，收据为 `.local/package25-core-storage-checks/windows-provider/42f5005bfb3e4d56ae3d8c5331151d80/windows-provider-receipt.json`。它仍运行本机Core10.0.0，加载路径是独立测试输出，未重编产品，也不代替SC10.0.14入口、真实国服或干净Windows验收；原299项收据保持原样，双包和ZIP／旁文件439项前后不变。
四份机器收据与阶段日志位于 `.local/release-preview25/0.1.0-preview.25-*`，分别核对包、指南、运行时及产品完整性；正式顺序验收会话 `75471` 已退出 0。包内抽查会话 `50849` 已退出 0，收据位于 `.local/desktop-preview25-smoke/2b834763eb1b466f9cf218a5a4579684/package-code-smoke-receipt.json`。包内指南只绑定当前版本／模式与稳定正文，不夹带本地验收记录。

## 构建发行包

发布脚本先生成本包指南，再计算 manifest、逐文件校验和及 ZIP 收据。自包含包头明确无需另装运行时；依赖框架包头明确需要 x64 .NET 10 Windows Desktop Runtime。验包除了检查字节哈希，还会核对指南中的版本与发布方式，旧指南即使被重新计算哈希也不能冒充新包。指南不嵌入构建后才会产生的验收计数、ZIP 大小或哈希。

开发机需要仓库 `global.json` 兼容的 .NET 10 SDK，首次发布可能需要联网下载官方 win-x64 运行时包。仓库根目录执行：

```powershell
pwsh -File scripts/publish.ps1 -RunDiagnostics
```

App 项目仅在 `SelfContained=true` 时把 `RuntimeFrameworkVersion` 固定为 `10.0.14`，使旧版兼容 SDK 也下载并打包已选定的维护补丁；普通构建和依赖框架包保留 .NET 10.0.0 最低运行版本。更新自包含运行时前，核对[官方 .NET 10 发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)，并在实际发行包中复核 `HsHelper.App.runtimeconfig.json` 的 `includedFrameworks` 与运行时二进制版本。[RuntimeFrameworkVersion 的官方说明](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#runtimeframeworkversion)

默认版本来自 App 项目元数据，输出位于 `.local\publish\<版本>-self-contained`，旁边生成 ZIP 及 ZIP 的 `.sha256`。输出目录或 ZIP 已存在时会拒绝覆盖；重试可指定新的版本或 `.local\publish` 内的新目录：

```powershell
pwsh -File scripts/publish.ps1 -Version 0.1.0-preview.2 -RunDiagnostics
pwsh -File scripts/verify-package.ps1 -PackageDirectory .local\publish\0.1.0-preview.2-self-contained -RunDiagnostics
pwsh -File scripts/verify-package.ps1 -PackageDirectory .local\publish\0.1.0-preview.2-self-contained -ZipPath .local\publish\0.1.0-preview.2-self-contained.zip
pwsh -File scripts/test-package.ps1 -PackageDirectory .local\publish\0.1.0-preview.2-self-contained
```

包内 `package-manifest.json` 固定清单格式版本 1，记录应用版本、源码提交、工作区是否有未提交改动、架构、部署模式、生成时间及文件大小/SHA256；`SHA256SUMS` 额外覆盖 manifest。校验器要求入口的 `runtimeconfig.json`、`deps.json` 与 .NET 10、Windows x64 和声明的部署模式一致，检查依赖清单中的运行时与原生文件实际存在。它拒绝额外文件、哈希变更、路径跳转、链接、数据库、日志及密钥配置路径，并直接读取 EXE 内嵌 manifest 核对 DPI 与普通用户权限声明。指定 `-ZipPath` 时还会校验 ZIP 收据和每个归档文件，安全回归脚本使用新建的合成小包验证拒绝路径，不修改原发行包。哈希用于完整性核对，不能代替发行者签名或可信下载渠道。脚本只生成本机文件，不上传或自动发布。

早期预览包保留其原始文件与收据；新版校验器要求生成式指南包头，因此不接受早期的旧指南格式。重新核对早期包时应依据该版本的收据和校验流程，不能修改旧包或旧收据以绕过检查。

可选更小的依赖框架包：

```powershell
pwsh -File scripts/publish.ps1 -FrameworkDependent -RunDiagnostics
```

该模式的目录名和 manifest 会明确标记 `framework-dependent`，使用者必须预先安装 **x64 .NET 10 Windows Desktop Runtime**；仅安装 ASP.NET 或普通 .NET Runtime 不足以运行 WPF。下载与部署模式见 [Microsoft .NET 发布文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/) 和 [Windows .NET 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。

## 无窗口诊断与验收边界

```powershell
.\HsHelper.App.exe --diagnostic-output "D:\Temp\hshelper-diagnostic.json"
```

诊断目标文件必须不存在。该模式仅输出版本、数据路径、程序路径、进程架构和 DPI 声明，不打开主窗口、不加载模型配置、不连接模型、不创建用户数据库。`verify-package.ps1 -RunDiagnostics` 从包外工作目录启动此模式，清除子进程的数据目录覆盖变量并核对 `%LOCALAPPDATA%` 默认值；自包含包还使用不存在的外部运行时路径并禁用多级查找，以检查包内运行时启动。不显示真实模型或用户信息。

程序 manifest 明确声明 `PerMonitorV2,PerMonitor` 和普通用户权限。此声明与无窗口启动检查不能代替真实屏幕验收：多显示器间移动、125%/150%/200% 缩放、炉石不同窗口模式下的浮层位置与清晰度仍需实际测试。没有安装 SDK/运行时的干净 Windows 电脑运行验证、数字签名与安装器分发也尚未完成。[DPI manifest 官方说明](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)
