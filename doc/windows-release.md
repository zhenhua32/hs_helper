# Windows 预览版发行与快速启动

此包面向 Windows x64。默认自包含包固定包含 .NET 10.0.14 Windows Desktop 运行时，使用者无需安装 SDK 或 .NET。请把 ZIP **完整解压**到一个新目录，再双击 `HsHelper.App.exe`；所有 DLL 和 `samples` 目录应留在程序旁。不要在 ZIP 预览窗口内启动。

首次启动的数据目录默认为 `%LOCALAPPDATA%\HsHelper`，与程序目录分开。卡库、套牌、历史、模型加密配置和缓存都保存在该目录。替换程序目录升级时保留该数据目录；不要把它复制进发行包。使用 `--data-dir "D:\HsHelperData"` 或环境变量 `HSHELPER_DATA_DIR` 可指定其他数据目录，命令行优先；日常启动无需参数。

本版将旧卡库无损升级为支持构筑约束的新格式，卡牌、卡池和历史哈希保留。升级前可备份数据目录；旧版程序会拒绝读取升级后的卡库，需要回退时恢复升级前备份，避免旧版忽略新增限制。

首次使用可先导入 `samples\decks\beast-hunter.txt` 中的公开示例牌码，配置游戏日志目录，再按界面提示获取对应版本卡库。包内不附带完整卡库，也不会自动导入真实对局。最新标准系列成员与完整合法性检查仍有证据边界，界面出现 Unknown 时不能理解为全部合法。

在“模型配置”中填写自己的服务地址、模型和密钥并保存，连接测试由使用者主动执行。Windows 加密配置仅供当前 Windows 用户使用。发行包不含 `.env`、模型密钥、历史数据库或用户日志；本机开发用 `.env` 不应放进解压目录或随包分享。AI 只给建议，不会自动操作游戏。复杂牌效、未知奥秘和随机结果仍会标记限制。

## 构建发行包

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
