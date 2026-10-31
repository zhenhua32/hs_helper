# Windows 预览版发行与快速启动

随包的 `QUICKSTART.zh-CN.md` 由独立的[用户指南正文](user-quickstart.zh-CN.md)和当前构建版本／发布方式生成。本页保留仓库中的构建说明与验收记录，不再整份复制进新包；包内 `package-manifest.json`、`SHA256SUMS` 和 ZIP 旁的收据提供该包的来源与完整性信息。

此包面向 Windows x64。默认自包含包固定包含 .NET 10.0.14 Windows Desktop 运行时，使用者无需安装 SDK 或 .NET。请把 ZIP **完整解压**到一个新目录，再双击 `HsHelper.App.exe`；所有 DLL 和 `samples` 目录应留在程序旁。不要在 ZIP 预览窗口内启动。

首次启动的数据目录默认为 `%LOCALAPPDATA%\HsHelper`，与程序目录分开。卡库、套牌、历史、模型加密配置和缓存都保存在该目录。替换程序目录升级时保留该数据目录；不要把它复制进发行包。使用 `--data-dir "D:\HsHelperData"` 或环境变量 `HSHELPER_DATA_DIR` 可指定其他数据目录，命令行优先；日常启动无需参数。

本版将旧卡库无损升级为支持构筑约束的新格式，卡牌、卡池和历史哈希保留。升级前可备份数据目录；旧版程序会拒绝读取升级后的卡库，需要回退时恢复升级前备份，避免旧版忽略新增限制。

本版还会将历史库升级到 schema 3，为新事件保存所属状态版本；旧套牌、事件正文和快照保留。旧事件无法确定同序号内所属状态时，回看会明确提示并省略该部分，不补造时间归属。升级前请备份数据目录；旧版程序不能读取升级后的历史库，回退需恢复升级前备份。

模型配置可读取旧格式 1；明确加密保存后写成格式 2，保留工具开关及输出格式。旧程序会拒绝格式 2，回退时需恢复原配置或重新填写。工具开关和提示 JSON／JSON 对象／JSON Schema 可在“模型与预算设置”选择；后两种需服务支持，失败不会自动切换模式。保存会先应用设置，再写盘；写盘失败会提示尚未保存，关闭不回滚已应用值。

首次使用可先导入 `samples\decks\beast-hunter.txt` 中的公开示例牌码，配置游戏日志目录，再按界面提示获取对应版本卡库。包内不附带完整卡库，也不会自动导入真实对局。最新标准系列成员与完整合法性检查仍有证据边界，界面出现 Unknown 时不能理解为全部合法。

在“AI 教练”页填写自己的服务地址、模型和密钥并保存，连接测试由使用者主动执行。Windows 加密配置仅供当前 Windows 用户使用。发行包不含 `.env`、模型密钥、历史数据库或用户日志；本机开发用 `.env` 不应放进解压目录或随包分享。AI 只给建议，不会自动操作游戏。复杂牌效、未知奥秘和随机结果仍会标记限制。

2026 年 10 月 31 日，本机已验证 `0.1.0-preview.20` 的两种包，来源提交为 `28e86cfa1a51681092722a1d3e0899f5b04c340c`，构建时均为 `sourceDirty=false`。产物保留在 `.local\publish`，未上传或加入版本控制；preview19 原包及收据保持不变：

| 部署方式 | ZIP 文件 | ZIP 字节 | 展开文件数／字节 |
| --- | --- | ---: | ---: |
| 自包含 | `0.1.0-preview.20-self-contained.zip` | 67,228,130 | 415／150,800,230 |
| 依赖框架 | `0.1.0-preview.20-framework-dependent.zip` | 2,102,320 | 20／4,474,946 |

- 自包含 ZIP SHA256：`315b23571ce032ee8112e17b211c6f72e72389f4f80f7c78d1c3ddaa942874a5`。
- 依赖框架 ZIP SHA256：`4001a4878a0cb05465e878fdb4e69ec191c1938b4d96494331c868b4093a5d93`。

该源码已通过 1232 项完整离线回归及独立的 27 项指南生成检查。两种包分别通过正常用户无窗口诊断、清单/ZIP 逐文件校验、27 项安全和 3 项指南语义检查。自包含的 Core 与 Windows Desktop 均为 10.0.14，七个关键运行时 DLL 与官方缓存及 nupkg 条目一致，两个整归档 SHA512 收据也匹配。依赖框架包的诊断使用本机可用的 Core/Desktop 10.0.0，不是干净 Windows 安装验收。

本版保留 preview19 的已观察冲锋／吸血附魔审核与手工使用后攻击，新增有限斩杀搜索中的硬币和可靠冲锋入场组合，并保留原生战吼的目标要求。随从入场不再清掉已观察的攻击限制，硬币在支付或其它类型分支前核验固定完整定义；畸形沉默和伪随从／武器定义不能绕过新候选审核。搜索仍受深度与节点限制，不跨未知赠福、奥秘、随机或选择边界，未找到不代表没有斩杀。回合／选择策略仍为 `hunter-v17`，其它任务基础版本保持 `hunter-v5`。完整复杂牌效、真实国服流程及多屏体验尚未完成。

preview17 中已复现的超过 500 项聚合查卡抛错，以及最后观察复盘的同序号多状态事件／旧详情问题，现已随 preview18 修复并验证。原复现仅证明历史上下文与界面不一致，未证明模型取得未来事件；本次没有放宽对这一问题的事实陈述。

仓库 CLI 已支持精确保存点的历史绑定、受限续问、审计来源、一致的能力配置及大局面查询；它是开发命令，**本次 App ZIP 不附带 CLI 程序**。1232 项源码回归包含这一入口的 19 组实际进程检查。

另对实际包内程序集完成 Agent 101 项、App 115 项，共 216 项功能抽查，原 DarkGift 和新增 FindLethal 隐藏 WPF 分别明确通过四组：真实搜索按钮、硬币与冲锋组合、模型返回动作后的最终主机／主建议／归档、非法打脸提案拒绝及迟到结果保护；也覆盖既有模型、查卡、复盘和用量链路。只重编测试源码，未重编产品；实际加载位置、SHA256 与版本匹配发行文件，未回退到源码输出，SC 415＋FW 20 文件前后不变，两包 8 个产品 DLL 字节相同且均在各自 deps 目标声明。此抽查使用本机 Core/Desktop 10.0.0，布局子测试读取同源公开 App.xaml 样式，七个固定卡牌 fixture 与公开样本共九项另有来源哈希，不替代自包含入口、真实多屏或干净 Windows 验收。抽查收据位于 `.local/desktop-preview20-smoke/58d90df629194ba19e799f91853013e0/package-code-smoke-receipt.json`。

两次发布及测试编译均保留默认 NuGet 审计，最终构建零警告、零错误。自包含机器收据和运行时收据保持原格式，另以 `0.1.0-preview.20-guide-verification.json` 记录双模式指南与包的绑定，`0.1.0-preview.20-product-completeness-verification.json` 记录产品依赖与必需文件；各阶段日志位于 `.local\publish\0.1.0-preview.20-*`。包头与运行要求各自绑定本包模式，固定指南正文不夹带旧版收据。preview19 的包内文件、ZIP、校验文件、日志及收据共 462 个文件，前后哈希和文件集合均未改变。

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
