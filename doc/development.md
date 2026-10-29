# 开发与使用说明

更新日期：2026 年 10 月 29 日。

当前实验版已贯通本地记牌、固定局面 AI 工具和历史回放。完整第一版验收仍见[开发清单](implementation-plan.md)，离线检查不能替代国服画面对照。

## 构建与运行

需要 Windows 与 .NET 10 SDK。首次恢复需要网络下载 SQLite 依赖。

```powershell
./scripts/verify.ps1
./scripts/run.ps1
```

脚本默认把构建输出放在 .local/build，避免覆盖正在运行的旧版 bin 文件。支持 -ArtifactsPath .local/build-current；验证和启动应使用同一路径。运行数据默认在 %LOCALAPPDATA%\HsHelper，也可使用 ./scripts/run.ps1 -DataDirectory .local/demo-data。SDK 用户目录、NuGet 缓存、构建输出和运行数据留在 Git 之外。

## 模型配置

在仓库根目录创建 .env，可复制 [.env.example](../.env.example) 后填写 api_url、model_name、api_key。支持等价的大写别名，只按文本读取，不执行变量展开。地址可填基础路径 /v1 或完整 /v1/chat/completions；只接受 HTTPS，本机服务允许 HTTP。

启动时先查找 .env，没有时读取本地加密配置。“AI 教练”可重新加载、检查连接及加密保存。加密使用 Windows DPAPI 当前用户。.env 本身仍是用户管理的明文文件，已被 Git 忽略。界面使用密码框，诊断与审计不输出密钥和接口返回正文。

“检查连接”只发送固定连接检查文本，不发送对局。分析会发送玩家视角过滤后的固定局面、相关牌效和指定套牌资料。自动建议默认关闭；开启后才在实时稳定决策点发送请求。协议是 Chat Completions 工具调用，服务需支持对应工具协议与 JSON 输出，参见 [Function calling 官方说明](https://developers.openai.com/api/docs/guides/function-calling)。

## 桌面流程

1. 中文卡库页同步最新 zhCN 数据，或导入有明确数字 build 的 HearthstoneJSON。支持名称、效果与关键词搜索、图片缓存。
2. 套牌页首用载入用户提供的“野兽快”：30 张主牌、3 张边栏。解析可查看名称和费用曲线，保存到套牌库创建不可变版本并保留边栏；导出 JSON 是独立入口。
3. 对局页确认“记录套牌”，诊断路径或填入 Logs/Power.log，开始监听或导入。启动时固定套牌版本，修改草稿不改写已记录的对局。
4. 自动视角仅在起手阶段有唯一且充分的私有手牌证据时确认；证据不足保持未知。可手动选择 1 或 2，切换会先停止并保存。
5. 悬浮窗默认隐藏，显示我方手牌、已知剩余牌库、对手公开出牌台账、建议和质量。支持拖动、缩放、穿透与设置。Ctrl+Shift+H 显隐、Ctrl+Shift+A 分析、Ctrl+Shift+T 穿透；冲突显示在主窗口。
6. AI 页确认局面 build 与配置，请求留牌、回合、选择或复盘建议。历史分析绑定当时卡库和套牌，缺少对应版本时不会提供已验证牌效。
7. 记录页选择一局和一个决策点，查看当时快照及事件并请求分析。统计默认仅国服标准猎人天梯，可按 build、套牌版本 ID、对手职业及先后手筛选。记录时间是观察/导入时间，不能当作原始游戏时间。
8. 停止、切换或退出会保存尚未落盘的记录。保留期限清理只移除旧已结束对局，不删除未完成局、卡库或套牌。

## Agent 与计算边界

每次分析绑定 MatchId、StateVersion 与卡牌 build。

| 工具 | 行为 |
| --- | --- |
| get_snapshot | 固定玩家视角局面与质量 |
| get_cards | 固定 build 牌效及缺失标记 |
| get_recent_events | 固定时刻以前的匿名事件和公开出牌台账 |
| calculate_draw_odds | 完整稳定、组成精确的牌库无放回抽牌概率 |
| evaluate_line | 有界动作验证，返回实际覆盖、条件和缺失机制 |
| find_lethal | 有界搜索；未找到不代表不存在斩杀 |

规则覆盖基础战斗、费用与部分 build 255790 猎人牌效。模型动作由主机再次校验：只有 Verified 且合法才显示动作；Conditional/Unsupported 移除动作并显示风险；Invalid 拒绝。确定斩杀只来自规则结果。尚未实现整套“野兽快”的完整模拟，复杂战吼、死亡之声、随机或奥秘等可能使结果降级。

牌库跟踪需要从开局持续观察且与确认套牌一致。晚接入、缺口和未知洗入会保留未知量，已知数量可能退化为下限，不伪造精确概率。对手台账记录公开的 HAND→PLAY 事件，回手后再打分别记录；衍生物不自动算作打出的牌，未知身份不使用未来信息回填历史。

单次默认最多 2 轮工具交互、8 次工具调用、30 秒、1500 输出令牌；按 MatchId 设置预算：60 次 HTTP 请求、200000 令牌、1 个并发。发送前预留用量，完成后按服务报告结算；实际报告可能超过预留，达到预算后拦截后续请求。成功响应未报告用量时按预留量计入；失败请求释放令牌预留但仍计请求次数。对局分析的匿名用量和工具耗时持久化，重启恢复已记录消耗；连接检查不计入对局预算。Storage 提供审计详情清理 API，桌面清理入口只清对局记录；两者都不会重置已消费预算。不显示未经核实的费用金额。

局面、来源、视角、套牌、配置或历史选点变化会取消旧请求，返回前再核对。模型故障不停止本地记牌。自动建议等待 1.2 秒稳定，并跳过回放与日志追赶的中间局面。

## 标准卡池证据

卡库按 build/语言不可变保存，下载失败保留旧活动版本。可同步官方标准系列成员证据，并按固定 build CardDefs 映射；这不能完整证明临时禁用、未上线或国服差异，因此默认合法性仍为 Unknown。显式导入完整且有效的版本规则后才给出确定合法性。规则保留来源、生效区间、系列、CardId、禁用与未上线集合。

标准系列筛选只表示成员关系，不保证当前国服可用。卡图可能来自显示服务的最新图片，不作为历史牌效依据。

## 命令行

先构建再执行，路径使用脚本默认值。

```powershell
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- diagnose
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- model-check --env .env
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- cards-sync --data-dir .local/demo-data
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- deck-describe --deck-file samples/decks/beast-hunter.txt --build 255790 --data-dir .local/demo-data
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- record --log path/Power.log --player auto --deck-file samples/decks/beast-hunter.txt --deck-name 野兽快 --data-dir .local/demo-data
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- sessions --data-dir .local/demo-data
dotnet run --no-build --artifacts-path .local/build --project src/HsHelper.Cli -- timeline --session SESSION_ID --data-dir .local/demo-data
```

advise --snapshot path --env .env --data-dir path --out advice.json 可对手动指定的过滤快照请求分析；完整建议只写输出文件，控制台仅显示验证与用量摘要。其他命令包括 config-preview、replay、watch、cards-import/search、deck-decode、pool-sync/import、historical-context 和 stats；运行 help 查看参数。record 共享读取并固定完整行边界，Ctrl+C 保存已观察部分。diagnose/config-preview 只读。沙箱可能重定向 LocalApplicationData，可用 diagnose --config 指定实际文件。

## 工程与验证

| 项目 | 职责 |
| --- | --- |
| Core / Ingestion | 不可变视角状态、牌库及公开出牌；日志读取与诊断 |
| Cards / Storage | 版本卡库、牌码、规则与图片；套牌、事件、快照、战绩及审计 |
| Rules / Coaching | 有界规则计算；固定局面工具与动作复验 |
| Agent | 模型工具循环、结构校验、预算与陈旧结果过滤 |
| App / Cli | Windows 桌面与命令行 |

verify.ps1 构建后运行 9 个控制台套件，失败返回非零码；dotnet test 不是入口。覆盖状态隐私、不可变性、日志边界、卡库版本、边栏、SQLite 重启/迁移/幂等、历史无未来信息、凭据保护、预算、规则和完整合成对局链路。UI 纯逻辑检查不启动窗口或注册热键。

Integration 的 --export-fixture .local/synthetic-smoke 可导出明确标记的合成快照和卡库，用于真实服务额外验收。普通回归使用假 HTTP，不产生模型费用。源日志仅共享读取，不复制到仓库；验证下载与数据保存在 .local，日常运行数据使用配置的数据目录。

仍需实际游戏画面对照、窗口/DPI实际可用性、整套猎人复杂牌效与建议质量、套牌计划/笔记查询和模型故障体验验收。参见[第一版设计](v1-design.md)。
