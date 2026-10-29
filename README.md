# 炉石猎人天梯助手

面向 Windows 国服标准天梯的桌面助手，以猎人为主要使用场景，覆盖最新中文卡库、套牌管理、实时记牌、AI 建议和赛后复盘。

本地程序负责还原局面，计算工具负责核算，AI Agent 负责提出方案和解释取舍。

当前为可构建的实验版，已实现日志诊断与回放、实体快照、中文卡库、牌码解析，以及可配置模型的策略建议接口。国服日志兼容性、标准卡池规则、完整牌效模拟和复盘统计按阶段继续验证与完善。

## 启动与验证

需要 Windows 与 .NET 10 SDK。首次构建需要访问 NuGet 下载 SQLite 依赖。

```powershell
./scripts/build.ps1
./scripts/run.ps1
```

运行全部回归检查：

```powershell
./scripts/verify.ps1
```

详见[开发与使用说明](doc/development.md)。回归测试使用控制台检查程序，运行 verify.ps1 执行；dotnet test 不是这些检查的入口。

## 项目文档

- [文档入口与设计决策](doc/README.md)
- [第一版功能与技术设计](doc/v1-design.md)
- [开发顺序与验收清单](doc/implementation-plan.md)
- [开发与使用说明](doc/development.md)

下一步继续用真实国服对局对照关键快照，并围绕常用猎人套牌实现牌效与复盘。
