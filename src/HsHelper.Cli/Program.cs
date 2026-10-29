using System.Text.Json;
using System.Text.Json.Serialization;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Ingestion;
using HsHelper.Agent;
using System.Text.Json.Nodes;
using HsHelper.Storage;
using HsHelper.Coaching;
using System.Collections.Concurrent;

return await MainAsync(args);
static async Task<int> MainAsync(string[] args)
{
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("""
            hs-helper — Windows 国服日志与卡库实验版
            diagnose [--game-dir path] [--config path]
            config-preview [--config path]
            replay --log path [--player 1|2] [--out snapshot.json]
            watch --log-root path [--player 1|2]
            cards-sync [--build latest|number] [--data-dir path]
            cards-import --json path --build number [--data-dir path]
            cards-search [--query text] [--class HUNTER] [--build number] [--data-dir path]
            deck-decode --code base64
            model-check [--env .env]
            advise --snapshot path --out advice.json [--env .env] [--data-dir path] [--task text]
            record --log path [--player auto|1|2] [--deck-file path] [--deck-name name] [--data-dir path]
            sessions [--data-dir path]
            timeline --session id [--data-dir path]
            historical-context --session id --event-seq number [--data-dir path]
            stats [--build number] [--data-dir path]
            pool-sync --build number [--data-dir path]
            pool-import --json path [--data-dir path]
            deck-describe --deck-file path --build number [--data-dir path]

            diagnose 与 config-preview 只读，不修改游戏配置。省略 --player 时屏蔽隐藏区身份。
            """);
        return 0;
    }
    try
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length || !opts.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("参数必须是唯一的 --名称 值 对。");
        }
        string? Option(string key) => opts.GetValueOrDefault("--" + key);
        string Required(string key) => Option(key) ?? throw new ArgumentException($"缺少 --{key}");
        int? Player()
        {
            var text = Option("player");
            if (text is null) return null;
            return text is "1" or "2" ? int.Parse(text) : throw new ArgumentException("--player 需要为1或2。");
        }
        void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
        switch (args[0])
        {
            case "advise":
            {
                var snapshotPath = Path.GetFullPath(Required("snapshot"));
                var outputPath = Path.GetFullPath(Required("out"));
                var environmentPath = Path.GetFullPath(Option("env") ?? EnvironmentModelConfiguration.FindFile()
                    ?? throw new ArgumentException("请用 --env 指定模型配置。"));
                var dataRoot = Option("data-dir") ?? Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
                var protectedPaths = new[] { snapshotPath, environmentPath, Path.Combine(dataRoot, "cards.db"),
                    Path.Combine(dataRoot, "sessions.db"), Path.Combine(dataRoot, "analysis-traces.db") };
                if (protectedPaths.Any(path => Path.GetFullPath(path).Equals(outputPath, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("输出不能覆盖输入、模型配置或数据库。");
                var snapshot = SnapshotCodec.Deserialize(await File.ReadAllTextAsync(snapshotPath));
                var identityTags = new HashSet<string>(["NAME", "PLAYER_NAME", "ENTITY_NAME", "ACCOUNT_ID", "GAME_ACCOUNT_ID", "PlayerName", "EntityName", "AccountId", "GameAccountId"], StringComparer.OrdinalIgnoreCase);
                if (!snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || !snapshot.Quality.HasLocalPerspective || snapshot.IsSpectating ||
                    snapshot.LocalPlayerId is not (1 or 2) || snapshot.Entities.Values.Any(entity =>
                        entity.Tags.Keys.Any(identityTags.Contains) || (entity.Kind == EntityKind.Card &&
                         !(entity.Zone is "PLAY" or "GRAVEYARD" || (entity.ControllerId == snapshot.LocalPlayerId && entity.Zone is "HAND" or "SECRET")) &&
                         (entity.CardId is not null || entity.Tags.Keys.Any(tag => tag is not ("ZONE" or "CONTROLLER" or "ZONE_POSITION" or "ENTITY_ID"))))))
                    throw new InvalidDataException("需要稳定、完整且已过滤隐藏身份与账号信息的玩家视角局面。");
                var build = snapshot.ClientBuild ?? snapshot.DeckTracking?.CardBuild;
                if (string.IsNullOrWhiteSpace(build) || build == "unknown") throw new InvalidDataException("局面没有固定卡库版本。");
                var catalog = new CardCatalog(Path.Combine(dataRoot, "cards.db")); await catalog.InitializeAsync();
                if (await catalog.GetManifestAsync(build) is null) throw new InvalidDataException("缺少局面对应的固定版本卡库。");
                var config = await EnvironmentModelConfiguration.LoadAsync(environmentPath);
                if (config.Settings.Model.Contains("://", StringComparison.Ordinal)) throw new InvalidDataException("模型标识不能包含地址。");
                var traces = new AnalysisTraceStore(Path.Combine(dataRoot, "analysis-traces.db")); await traces.InitializeAsync();
                var totals = await traces.GetBudgetTotalsAsync(snapshot.MatchId);
                var budget = new AnalysisBudget(); budget.SeedMatch(snapshot.MatchId, totals.RequestCount, totals.ChargedTokens);
                var requests = new ConcurrentQueue<ModelRequestTrace>(); var tools = new ConcurrentQueue<ToolExecutionTrace>();
                var coach = new SnapshotCoach(snapshot, JsonNode.Parse(SnapshotCodec.Serialize(snapshot))!.AsObject(), catalog, build,
                    (name, elapsed, succeeded) => tools.Enqueue(new(Guid.NewGuid().ToString("N"), snapshot.MatchId, snapshot.StateVersion,
                        name, elapsed, succeeded ? AnalysisTraceStatus.Completed : AnalysisTraceStatus.Failed)));
                var usages = new ConcurrentQueue<ModelUsage>();
                void Remember(ModelUsage usage)
                {
                    usages.Enqueue(usage);
                    requests.Enqueue(new(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model, usage.Round,
                        usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds,
                        usage.Success ? AnalysisTraceStatus.Completed : AnalysisTraceStatus.Failed, usage.ConservativeCharge)
                        { CardBuild = build, Task = "cli-advise" });
                }
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                StrategyAdvice advice;
                try
                {
                    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                    advice = await new AdviceService(http).AnalyzeAsync(config.Settings, config.ApiKey,
                        new(snapshot.MatchId, snapshot.StateVersion, Option("task") ?? "回合建议", JsonNode.Parse(SnapshotCodec.Serialize(snapshot))!.AsObject()),
                        coach.ExecuteAsync, () => !cancellation.IsCancellationRequested, cancellation.Token, coach.Capabilities(budget, Remember));
                    var result = JsonSerializer.Serialize(advice, jsonOptions);
                    if (!string.IsNullOrEmpty(config.ApiKey) && result.Contains(config.ApiKey, StringComparison.Ordinal))
                        throw new InvalidDataException("模型结果包含不应导出的敏感内容。");
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllTextAsync(temporary, result, cancellation.Token); File.Move(temporary, outputPath, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                finally
                {
                    try { await traces.AppendBatchAsync(requests.ToArray(), tools.ToArray(), CancellationToken.None); }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                var usage = usages.ToArray();
                Print(new { Connected = true, advice.Verification, Actions = advice.Actions.Count, ElapsedMilliseconds = clock.ElapsedMilliseconds,
                    Usage = new { Requests = usage.Length, ChargedTokens = usage.Sum(u => u.ChargedTokens),
                        ReportedTotalTokens = usage.All(u => u.TotalTokens.HasValue) ? usage.Sum(u => u.TotalTokens!.Value) : (long?)null,
                        MatchRequests = budget.Snapshot(snapshot.MatchId).RequestsStarted } });
                return 0;
            }
            case "record":
            case "sessions":
            case "timeline":
            case "historical-context":
            case "stats":
            {
                var dataRoot = Option("data-dir") ?? Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
                var store = new SessionStore(Path.Combine(dataRoot, "sessions.db"));
                await store.InitializeAsync();
                if (args[0] == "record")
                {
                    var path = Path.GetFullPath(Required("log"));
                    var build = ClientBuildMetadata.ReadPowerLogBuild(path) ?? Option("build") ?? "unknown";
                    var perspective = Option("player") ?? "auto";
                    if (perspective is not ("auto" or "1" or "2")) throw new ArgumentException("--player 需要auto、1或2。");
                    var local = perspective == "auto" ? (int?)null : int.Parse(perspective);
                    DeckVersion? deck = null;
                    if (Option("deck-file") is { } deckPath)
                        deck = await store.SaveDeckVersionAsync(Option("deck-name") ?? "导入套牌", DeckCode.Decode(await File.ReadAllTextAsync(deckPath)), build);
                    var engine = new PowerLogStateEngine(local, LogSourceIdentity.Create(path, local ?? 0, deck?.Id), autoDetectPerspective: local is null);
                    var recorder = new LogRecordingPipeline(store, deck?.Id, build);
                    var catalog = new CardCatalog(Path.Combine(dataRoot, "cards.db"));
                    await catalog.InitializeAsync();
                    var tracking = await DeckTrackingEnricher.CreateAsync(catalog, deck, build);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    long lines = 0;
                    using var recordingCancellation = new CancellationTokenSource();
                    ConsoleCancelEventHandler cancelRecording = (_, e) => { e.Cancel = true; recordingCancellation.Cancel(); };
                    Console.CancelKeyPress += cancelRecording;
                    try
                    {
                        await foreach (var line in SharedLogReplay.ReadLinesAsync(path, recordingCancellation.Token))
                        {
                            lines++;
                            if (engine.ProcessLine(line)) recorder.Observe(tracking.Observe(engine.Snapshot()), lines);
                            if (recorder.PendingCount >= 512) await recorder.FlushAsync(recordingCancellation.Token);
                        }
                    }
                    catch (OperationCanceledException) when (recordingCancellation.IsCancellationRequested) { }
                    finally
                    {
                        Console.CancelKeyPress -= cancelRecording;
                        await recorder.CompleteAsync();
                    }
                    Print(new { Lines = lines, recorder.RecordedMatchCount, recorder.UnconfirmedMatchCount, recorder.SpectatedMatchCount,
                        recorder.CurrentSessionId, Build = build, DeckVersion = deck?.Id, ElapsedMilliseconds = clock.ElapsedMilliseconds });
                }
                else if (args[0] == "sessions") Print(await store.ListSessionsAsync());
                else if (args[0] == "timeline") Print(await store.ListDecisionPointsAsync(Required("session"), includeUnstable: true));
                else if (args[0] == "historical-context") Print(await store.GetHistoricalContextAtAsync(Required("session"), long.Parse(Required("event-seq"))) ?? new { Error = "该时点没有记录" } as object);
                else Print(await store.GetStatsAsync(new SessionFilter(CardBuild: Option("build"))));
                return 0;
            }
            case "pool-sync":
            case "pool-import":
            case "deck-describe":
            {
                var dataRoot = Option("data-dir") ?? Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
                var catalog = new CardCatalog(Path.Combine(dataRoot, "cards.db"));
                await catalog.InitializeAsync();
                if (args[0] == "pool-sync")
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    Print(await catalog.SyncStandardPoolAsync(http, Required("build")));
                }
                else if (args[0] == "pool-import")
                {
                    using var input = File.OpenRead(Required("json"));
                    Print(await catalog.ImportPoolRulesAsync(input));
                }
                else Print(await catalog.DescribeDeckAsync(DeckCode.Decode(await File.ReadAllTextAsync(Required("deck-file"))), Required("build")));
                return 0;
            }
            case "model-check":
            {
                var config = await EnvironmentModelConfiguration.LoadAsync(Option("env") ?? EnvironmentModelConfiguration.FindFile()
                    ?? throw new ArgumentException("未找到 .env，请用 --env 指定。"));
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var started = System.Diagnostics.Stopwatch.StartNew();
                var context = new AdviceContext("connection-check", 0,
                    "连接检查：直接返回summary=连接成功，reasons和risks为空数组，alternative为null，不调用工具，不分析对局。", new JsonObject());
                _ = await new AdviceService(http).AnalyzeAsync(config.Settings with { MaxToolRounds = 0, MaxOutputTokens = 512 }, config.ApiKey,
                    context, (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true);
                Print(new { Connected = true, ElapsedMilliseconds = started.ElapsedMilliseconds, SendsGameData = false });
                return 0;
            }
            case "diagnose": Print(HearthstoneDiagnostics.Inspect(Option("game-dir"), Option("config"))); return 0;
            case "config-preview":
                Print(LogConfiguration.CreatePlan(Option("config") ?? HearthstoneDiagnostics.DefaultConfigurationPath)); return 0;
            case "replay":
            {
                var logPath = Path.GetFullPath(Required("log"));
                if (Option("out") is { } destination && Path.GetFullPath(destination).Equals(logPath, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("输出文件不能覆盖原始日志。");
                var engine = new PowerLogStateEngine(Player(), "offline-replay");
                await foreach (var line in SharedLogReplay.ReadLinesAsync(logPath)) engine.ProcessLine(line);
                var snapshot = engine.Snapshot();
                if (Option("out") is { } output) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(snapshot, jsonOptions));
                else Print(snapshot);
                return 0;
            }
            case "watch":
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                using var tailer = new PowerLogTailer();
                var engine = new PowerLogStateEngine(Player());
                await foreach (var batch in tailer.WatchAsync(Required("log-root"), TimeSpan.FromSeconds(1), cancellation.Token))
                {
                    if (batch.ResetReason != LogResetReason.None) engine.ResetSession();
                    foreach (var line in batch.Lines) engine.ProcessLine(line);
                    if (batch.Lines.Count > 0) Print(engine.Snapshot());
                }
                return 0;
            }
            case "deck-decode": Print(DeckCode.Decode(Required("code"))); return 0;
            case "cards-sync":
            case "cards-import":
            case "cards-search":
            {
                var dataRoot = Option("data-dir") ?? Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
                var catalog = new CardCatalog(Path.Combine(dataRoot, "cards.db"));
                await catalog.InitializeAsync();
                if (args[0] == "cards-import")
                {
                    using var file = File.OpenRead(Required("json"));
                    Print(await catalog.ImportAsync(file, Required("build")));
                }
                else if (args[0] == "cards-sync")
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    Print(await catalog.SyncAsync(http, Option("build") ?? "latest"));
                }
                else Print(await catalog.SearchAsync(new CardSearchQuery(Option("query"), Option("class"), Keyword: Option("keyword"),
                    StandardOnly: Option("standard") == "true", StandardPoolOnly: Option("standard-pool") == "true"), Option("build")));
                return 0;
            }
            default: throw new ArgumentException("未知命令，运行 help 查看用法。");
        }
    }
    catch (OperationCanceledException) { return 130; }
    catch (Exception) when (args[0] == "advise")
    { Console.Error.WriteLine("局面分析未完成。请检查过滤快照、固定版本卡库、模型配置、本局预算和输出路径；原始响应与凭据不会输出。"); return 1; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
