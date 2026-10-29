# 开发与使用说明

更新日期：2026 年 10 月 29 日。

当前实验版用于验证从日志到局面、从卡库到 AI 策略建议的流程。真实国服日志的全面画面对照、标准卡池规则和牌效模拟仍按[开发清单](implementation-plan.md)推进。

## 工程结构

| 路径 | 职责 |
| --- | --- |
| src/HsHelper.App | WPF 桌面界面，连接日志、回放、卡库、套牌与手动 AI 分析 |
| src/HsHelper.Cli | 只读诊断、配置预览、日志回放与监听、卡库及牌码命令 |
| src/HsHelper.Core | 日志事件还原、不可变快照、数据质量与视角过滤 |
| src/HsHelper.Ingestion | 游戏定位、配置预览与备份 API、日志发现和增量读取 |
| src/HsHelper.Cards | SQLite 卡库、固定版本同步、查询和牌码编解码 |
| src/HsHelper.Agent | 有时限和工具预算的模型调用、策略输出与过期结果检查 |
| tests | 离线控制台回归检查与明确标记为合成的日志样本 |

## 构建与运行

Windows 上安装 .NET 10 SDK 后，在仓库根目录执行：

```powershell
./scripts/build.ps1
./scripts/run.ps1
```

脚本将 SDK 用户目录和 NuGet 缓存放到仓库的 .local 下，方便隔离构建环境。运行数据默认保存到 %LOCALAPPDATA%\HsHelper；可以通过 HSHELPER_DATA_DIR 指定其他目录。

```powershell
$env:HSHELPER_DATA_DIR = "$PWD\.local\demo-data"
./scripts/run.ps1
```

## 桌面使用流程

1. 在对局页运行自动诊断，或填写炉石安装目录、Logs 目录、Power.log 路径。
2. 选择日志回放，或开始监听。根据实际对局核对我方玩家 ID 为 1 还是 2；未确认时隐藏私有身份。
3. 查看阶段、回合、数据完整性和实体。空白属性保持未知，旧日志不会证明当前实时连接可用。
4. 在卡库页同步最新中文数据，或输入数字 build 导入 HearthstoneJSON 数组。名称和效果支持中文搜索。
5. 在套牌页粘贴牌码并解析。保存会生成带记录 ID、时间和卡库版本的 JSON 文件；数据库套牌历史仍待实现。
6. 在 AI 页填写完整 Chat Completions 兼容接口地址、模型名称与密钥。密钥只在当前进程内使用，尚未提供持久化保存。
7. 核对局面对应的卡牌 build 后填写，点击分析。只有匹配的活动卡库才用于查牌；版本不匹配会向 Agent 返回限制。

手动分析会将过滤后的局面和查询到的相关卡牌发送到所填写的模型服务。程序提供 get_snapshot 和 get_cards 工具，最多两轮工具交互、八个工具调用，默认三十秒超时。模型默认输出中文 JSON 策略建议，具体模型需要支持工具调用和 JSON 内容输出。

局面、视角或日志来源变化会使旧建议失效。当前验证级别固定为策略判断，出牌动作序列、斩杀搜索和精确胜率需要后续规则或统计能力支持。

工具调用协议参考 [OpenAI Docs 的 Function calling](https://developers.openai.com/api/docs/guides/function-calling)。模型服务地址与名称由用户填写，代码未绑定某个服务商，也不会自动发起模型请求。

## 命令行

先构建，再运行以下命令。默认数据路径与桌面应用相同，演示可显式使用 .local 路径。

```powershell
dotnet run --no-build --project src/HsHelper.Cli -- diagnose
dotnet run --no-build --project src/HsHelper.Cli -- config-preview
dotnet run --no-build --project src/HsHelper.Cli -- replay --log tests/HsHelper.Core.Tests/Fixtures/synthetic-basic.log --player 1 --out .local/snapshot.json
dotnet run --no-build --project src/HsHelper.Cli -- watch --log-root "D:\game\Hearthstone" --player 1
dotnet run --no-build --project src/HsHelper.Cli -- cards-sync --data-dir .local/demo-data
dotnet run --no-build --project src/HsHelper.Cli -- cards-import --json cards.json --build 255790 --data-dir .local/demo-data
dotnet run --no-build --project src/HsHelper.Cli -- cards-search --query 野兽 --class HUNTER --data-dir .local/demo-data
dotnet run --no-build --project src/HsHelper.Cli -- deck-decode --code "在这里填写实际牌码"
```

示例中的 build 是数字版本的写法，导入时应填写文件实际所属的 build。最新版数据也可能包含预载内容，当前卡库将标准合法性保持为 Unknown。

diagnose 和 config-preview 只读。Ingestion 类库提供显式 ApplyAsync 与 RestoreAsync，带变更检查和备份；当前桌面与命令行仅提供预览入口。沙箱运行时，系统的 LocalApplicationData 目录可能发生重定向，诊断命令可通过 --config 指定实际 log.config。

## 回归检查

```powershell
./scripts/verify.ps1
```

回归程序在失败时返回非零退出码，分别覆盖以下行为：

- Core：实体、区域、动态费用、隐藏信息、快照不可变、多局隔离、缺口和稳定性。
- Ingestion：配置预览与备份恢复、日志发现、UTF8 分块、半行、截断、替换和轮换。
- Cards：版本固定、原子切换、失败回退、中文检索、未知合法性和牌码扩展。
- Agent：使用假 HTTP 响应检查工具交互、预算、结构化输出和过期结果，不产生模型费用。

合成样本只能证明已覆盖的解析与边界行为。国服兼容性通过真实日志与游戏画面对照单独验证。公开卡牌源的真实数据导入可使用 Cards 回归程序的可选参数执行，原始下载文件保留在 .local 中。

## 当前能力边界

| 领域 | 仍需完成 |
| --- | --- |
| 国服日志 | 实际字段与关键时刻的画面对照、特殊机制与重连覆盖 |
| 玩家视角 | 自动识别我方及其可靠性验证，目前提供手动选择 |
| 选择 | 完整候选列表与发现分析，当前仅识别选择阶段 |
| 卡池 | 标准规则、临时禁用与上线信息，当前均不自动断言合法 |
| 套牌 | 卡名与费用曲线展示、数据库历史、构筑合法性与牌库变动核对 |
| 计算 | 牌库组成、抽牌概率、牌效模拟和具体动作验证 |
| Agent | 真实模型接入评估、调用用量记录、持久化凭据和自动决策点触发 |
| 复盘与界面 | 对局数据库、回合时间线、个人统计、悬浮窗与多屏验证 |

测试与诊断不会修改用户的炉石配置，也没有将真实对局日志加入仓库。
