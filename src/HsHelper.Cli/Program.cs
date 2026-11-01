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
static async Task<ModelConfiguration> LoadCliModelConfigurationAsync(string environmentPath, string dataRoot)
{
    var config = await EnvironmentModelConfiguration.LoadAsync(environmentPath);
    var savedPath = Path.Combine(dataRoot, "model-config.json");
    var saved = File.Exists(savedPath)
        ? await new ProtectedModelConfigurationStore(savedPath, new WindowsSecretProtector()).LoadAsync() : null;
    return EnvironmentModelConfiguration.MergeTuning(config, saved?.Settings);
}

static string ToolEvidenceSummary(CoachToolObservation observation)
{
    if (!observation.Success || observation.ResultJson is null) return "工具未完成，未保存原始结果。";
    var result = JsonNode.Parse(observation.ResultJson);
    string Field(string key) => result?[key]?.ToString() ?? "未知";
    var summary = observation.Name switch
    {
        "get_snapshot" => "已查询本次固定的紧凑玩家视角局面。",
        "get_cards" => "固定版本牌效：" + string.Join("、", (result?["cards"] as JsonArray ?? []).Select(c => c?["Id"]?.ToString() ?? c?["id"]?.ToString() ?? "未知")),
        "get_recent_events" => "仅查询固定事件序号及以前的匿名事件与公开出牌。",
        "calculate_draw_odds" => $"精确条件 {Field("is_exact")}；概率 {Field("probability")}；{Field("reason")}",
        "evaluate_line" => $"验证 {Field("Verification")}；合法 {Field("IsLegal")}；确定斩杀 {Field("IsLethal")}；未覆盖 {Field("UnsupportedMechanics")}",
        "find_lethal" => $"已验证斩杀 {Field("VerifiedLethalFound")}；搜索节点 {Field("ExploredNodes")}；{Field("Coverage")}",
        "validate_choice" => $"候选合法 {Field("IsLegal")}；只验证选择范围，不模拟选择后的效果。",
        "evaluate_choice" => $"选择直接后果 {Field("Verification")}；来源 {Field("SourceCardId")}；不代表已经发生的局面或后续动作。",
        "evaluate_draw" => $"抽牌直接范围；来源 {Field("SourceCardId")}；不生成新手牌实体或证明后续动作。",
        "evaluate_generation" => $"随机生成数量范围；来源 {Field("SourceCardId")}；不选择随机身份或创建新实体。",
        "evaluate_wolf_blessing" => "巨狼祝福各候选直接增益分支；不指定实际命中或证明后续动作。",
        "evaluate_magma_split" => "熔岩猎犬攻击与逐包随机分配的条件分支；区间相关，截断不代表全部结果，不指定命中或证明斩杀。",
        "evaluate_explosive_trap" => "我方已安装爆炸陷阱的当前来袭条件预览；仅列直接触发伤害，不执行对手攻击或给出权威后续局面。",
        "evaluate_play_triggers" => "原生发现、拼装、回溯入口及使用后任务、迅猛龙触发预览；选择和随机效果等待新局面，不证明完整结算或斩杀。",
        "get_entity_effect" => "已查询固定局面的可见实体文字；自定义文字重建不代表牌效脚本已经模拟。",
        "get_matchup_plan" => "已查询固定历史时点之前的用户计划/笔记版本。",
        "get_personal_stats" => $"此前已结束个人样本 {result?["stats"]?["Games"]?.ToString() ?? "未知"} 局，排除当前局。",
        _ => "工具查询已完成。"
    };
    if (result?["error"] is { } error) summary = "工具返回限制：" + error;
    else if (result?["missing"] is { } missing) summary = "查询资料缺失：" + missing;
    return summary.Length <= 900 ? summary : summary[..899] + "…";
}
static async Task<int> MainAsync(string[] args)
{
    var adviceStage = "preflight";
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
            model-check [--env .env] [--data-dir path]
            advise --snapshot path --out advice.json [--env .env] [--data-dir path] [--task turn|mulligan|choice|review|deckplan] [--question text]
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
                    Path.Combine(dataRoot, "sessions.db"), Path.Combine(dataRoot, "analysis-traces.db"),
                    Path.Combine(dataRoot, "knowledge.db"), Path.Combine(dataRoot, "advice-archive.db"), Path.Combine(dataRoot, "model-config.json") };
                if (protectedPaths.Any(path => Path.GetFullPath(path).Equals(outputPath, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("输出不能覆盖输入、模型配置或数据库。");
                var snapshot = SnapshotCodec.Deserialize(await File.ReadAllTextAsync(snapshotPath));
                var identityTags = new HashSet<string>(["NAME", "PLAYER_NAME", "ENTITY_NAME", "ACCOUNT_ID", "GAME_ACCOUNT_ID", "PlayerName", "EntityName", "AccountId", "GameAccountId"], StringComparer.OrdinalIgnoreCase);
                var confirmedLocal = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
                bool PublicSecretClassification(EntitySnapshot entity, KeyValuePair<string, string> tag) =>
                    confirmedLocal.HasValue && entity.Zone == "SECRET" && entity.ControllerId is 1 or 2 && entity.ControllerId != confirmedLocal &&
                    tag.Key is "SECRET" or "QUEST" or "SIDE_QUEST" && tag.Value is "0" or "1" or "unknown";
                if (!snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || !snapshot.Quality.HasLocalPerspective || snapshot.IsSpectating ||
                    snapshot.LocalPlayerId is not (1 or 2) || snapshot.Entities.Values.Any(entity =>
                        entity.Tags.Keys.Any(identityTags.Contains) || (entity.Kind == EntityKind.Card &&
                         !(entity.Zone is "PLAY" or "GRAVEYARD" || (entity.ControllerId == snapshot.LocalPlayerId && entity.Zone is "HAND" or "SECRET") ||
                             ChoiceVisibility.IsProjectedCurrentOptionEntity(snapshot, entity)) &&
                         (entity.CardId is not null || entity.Tags.Any(tag => tag.Key is not ("ZONE" or "CONTROLLER" or "ZONE_POSITION" or "ENTITY_ID") &&
                             !PublicSecretClassification(entity, tag))))))
                    throw new InvalidDataException("需要稳定、完整且已过滤隐藏身份与账号信息的玩家视角局面。");
                var requestStartedAt = DateTimeOffset.UtcNow;
                var sessions = new SessionStore(Path.Combine(dataRoot, "sessions.db")); await sessions.InitializeAsync();
                var point = (await sessions.ListDecisionPointsAsync(snapshot.MatchId, includeUnstable: true))
                    .FirstOrDefault(p => p.EventSequence == snapshot.EventSequence && p.StateVersion == snapshot.StateVersion &&
                        p.IsDecisionPoint && p.IsStable && p.IsComplete);
                var historical = point is null ? null : await sessions.GetHistoricalDecisionAsync(point.Id);
                if (historical is not null && (!historical.Point.IsDecisionPoint || !historical.Point.IsStable || !historical.Point.IsComplete ||
                    SnapshotCodec.Serialize(historical.Context.Snapshot) != SnapshotCodec.Serialize(snapshot))) historical = null;
                string? KnownBuild(string? value) => string.IsNullOrWhiteSpace(value) || value == "unknown" ? null : value;
                var build = KnownBuild(snapshot.ClientBuild) ??
                    (snapshot.DeckTracking is { } candidate && candidate.MatchId == snapshot.MatchId && candidate.StateVersion == snapshot.StateVersion
                        ? KnownBuild(candidate.CardBuild) : null);
                if (historical is not null)
                {
                    var savedBuild = KnownBuild(historical.Context.CardBuild);
                    if (savedBuild is null) historical = null;
                    else
                    {
                        if (KnownBuild(snapshot.ClientBuild) is { } observedBuild && observedBuild != savedBuild ||
                            snapshot.DeckTracking is { } tracked && (tracked.MatchId != snapshot.MatchId || tracked.StateVersion != snapshot.StateVersion ||
                                KnownBuild(tracked.CardBuild) is { } trackingBuild && trackingBuild != savedBuild ||
                                tracked.DeckVersion != historical.Context.DeckVersionId))
                            throw new InvalidDataException("保存决策点的卡库或套牌绑定与快照冲突，不能承接个人历史。");
                        if (historical.Point.RecordedAt > requestStartedAt)
                            throw new InvalidDataException("保存决策点的录制时间晚于本次查询，无法确定历史知识边界。");
                        build = savedBuild;
                    }
                }
                if (string.IsNullOrWhiteSpace(build) || build == "unknown") throw new InvalidDataException("局面没有固定卡库版本。");
                var catalog = new CardCatalog(Path.Combine(dataRoot, "cards.db")); await catalog.InitializeAsync();
                if (await catalog.GetManifestAsync(build) is null) throw new InvalidDataException("缺少局面对应的固定版本卡库。");
                var config = await LoadCliModelConfigurationAsync(environmentPath, dataRoot);
                if (config.Settings.Model.Contains("://", StringComparison.Ordinal)) throw new InvalidDataException("模型标识不能包含地址。");
                var knowledge = new CoachKnowledgeStore(Path.Combine(dataRoot, "knowledge.db")); await knowledge.InitializeAsync();
                var archive = new AdviceArchiveStore(Path.Combine(dataRoot, "advice-archive.db")); await archive.InitializeAsync();
                DeckVersion? deck = historical?.Context.Deck;
                if (historical is null && snapshot.DeckTracking is { } tracking && tracking.MatchId == snapshot.MatchId && tracking.StateVersion == snapshot.StateVersion && tracking.CardBuild == build)
                    deck = await sessions.GetDeckVersionAsync(tracking.DeckVersion);
                var deckVersionId = historical is null ? deck?.Id : historical.Context.DeckVersionId;
                var relevantCards = (await catalog.GetByIdsBatchedAsync(AttachmentVisibility.Project(snapshot).Entities.Values
                    .Where(e => e.Zone == "PLAY" || e.ControllerId == snapshot.LocalPlayerId && e.Zone is "HAND" or "SECRET")
                    .Select(e => e.CardId).OfType<string>()
                    .Concat(PublicHandMemoryVisibility.KnownCardIds(snapshot))
                    .Concat(snapshot.Choice?.Options.Select(o => o.CardId).OfType<string>() ?? []).Distinct(), build)).ToDictionary(c => c.Id);
                if (deck is not null)
                    foreach (var card in await catalog.GetByDbfIdsBatchedAsync(deck.Definition.Cards.Select(c => c.DbfId)
                        .Concat(deck.Definition.Sideboards.SelectMany(s => s.Cards.Select(c => c.DbfId))).Distinct(), build)) relevantCards[card.Id] = card;
                foreach (var card in await SnapshotCardEffects.LoadDonorsAsync(snapshot, build, catalog, relevantCards.Values)) relevantCards[card.Key] = card.Value;
                var taskKind = Option("task") is { } namedTask ? AdviceTaskTemplates.Resolve(namedTask) : snapshot.Phase switch
                { GamePhase.Choice => AdviceTaskKind.Choice, GamePhase.Mulligan => AdviceTaskKind.Mulligan,
                    GamePhase.Complete => AdviceTaskKind.Review, _ => AdviceTaskKind.Turn };
                var context = CompactAdviceContextBuilder.Create(snapshot, build, taskKind, deck?.Definition, relevantCards.Values)
                    with { UserQuestion = Option("question") ?? Option("task"), DeckVersionId = deckVersionId,
                        AsOf = requestStartedAt, KnowledgeAsOf = historical?.Point.RecordedAt };
                if (historical is not null)
                    context = await ConversationHistoryBuilder.AttachAsync(context, archive, deckVersionId, requestStartedAt);
                CoachLibraryContext? library = historical is null ? null : new(sessions, knowledge,
                    new(snapshot.MatchId, snapshot.StateVersion, build, deckVersionId, historical.Point.RecordedAt, snapshot.OpponentClass,
                        snapshot.FirstPlayerId is null ? null : snapshot.FirstPlayerId == snapshot.LocalPlayerId,
                        Format: snapshot.Format ?? "STANDARD", GameType: snapshot.GameType ?? "GT_RANKED", EventSequence: snapshot.EventSequence));
                context.Snapshot["personal_library_available"] = library is not null;
                var traces = new AnalysisTraceStore(Path.Combine(dataRoot, "analysis-traces.db")); await traces.InitializeAsync();
                var totals = await traces.GetBudgetTotalsAsync(snapshot.MatchId);
                var budget = new AnalysisBudget(config.Settings.BudgetLimits); budget.SeedMatch(snapshot.MatchId, totals.RequestCount, totals.ChargedTokens);
                var requests = new ConcurrentQueue<ModelRequestTrace>(); var tools = new ConcurrentQueue<ToolExecutionTrace>();
                var evidence = new ConcurrentQueue<AdviceToolEvidence>();
                var coach = new SnapshotCoach(snapshot, context.Snapshot, catalog, build, library: library, toolCompleted: observation =>
                {
                    tools.Enqueue(new(Guid.NewGuid().ToString("N"), snapshot.MatchId, snapshot.StateVersion, observation.Name,
                        observation.ElapsedMilliseconds, observation.Success ? AnalysisTraceStatus.Completed : AnalysisTraceStatus.Failed)
                        { ResultHash = observation.ResultHash, Model = observation.Model, AnalysisRunId = observation.AnalysisRunId,
                            RequestId = observation.RequestId, ToolCallId = observation.ToolCallId, EventSequence = observation.EventSequence });
                    evidence.Enqueue(new(observation.Name, ToolEvidenceSummary(observation), observation.ResultHash));
                }, taskKind: context.TaskKind, startingDeck: historical is null ? null :
                    StartingDeckContext.FromSavedDeck(snapshot, build, historical.Context.Deck, historical.Context.DeckVersionId));
                var usages = new ConcurrentQueue<ModelUsage>();
                void Remember(ModelUsage usage)
                {
                    usages.Enqueue(usage);
                    requests.Enqueue(new(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model, usage.Round,
                        usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds,
                        usage.Success ? AnalysisTraceStatus.Completed : AnalysisTraceStatus.Failed, usage.ConservativeCharge)
                        { CardBuild = usage.CardBuild ?? build, Task = context.Task, PromptTemplateVersion = usage.PromptTemplateVersion ?? context.TemplateVersion,
                            AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence, DeckVersionId = usage.DeckVersionId });
                }
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                StrategyAdvice advice;
                try
                {
                    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                    adviceStage = "analysis";
                    advice = await new AdviceService(http).AnalyzeAsync(config.Settings, config.ApiKey,
                        context, coach.ExecuteAsync, () => !cancellation.IsCancellationRequested, cancellation.Token, coach.Capabilities(budget, Remember));
                    adviceStage = "archive";
                    var archived = await archive.SaveAsync(new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, build, context.TemplateVersion, advice.Summary)
                    {
                        DeckVersionId = deckVersionId, VisibilityPolicy = context.VisibilityPolicy,
                        Task = context.Task, UserQuestion = context.UserQuestion, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
                        Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
                        LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns,
                        UnsupportedMechanics = advice.UnsupportedMechanics, HostRisks = advice.HostRisks,
                        HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
                        HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated,
                        Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds,
                        ConversationArchiveIds = advice.ConversationArchiveIds, KnowledgeAsOf = advice.KnowledgeAsOf,
                        Actions = advice.Actions.Select(a => new ArchivedAdviceAction(a.Kind, a.SourceEntityId, a.TargetEntityId)).ToArray(), Evidence = evidence.ToArray()
                    }, sensitiveValues: [config.ApiKey, config.Settings.Endpoint.ToString()], cancellationToken: CancellationToken.None);
                    advice = advice with { Summary = archived.Advice.Summary, Reasons = archived.Advice.Reasons.ToArray(),
                        Alternative = archived.Advice.Alternative, Risks = archived.Advice.Risks.ToArray(), UserQuestion = archived.Advice.UserQuestion,
                        LimitationsVersion = archived.Advice.LimitationsVersion, HostLimitationsTruncated = archived.Advice.HostLimitationsTruncated,
                        Assumptions = archived.Advice.Assumptions, Unknowns = archived.Advice.Unknowns,
                        UnsupportedMechanics = archived.Advice.UnsupportedMechanics, HostRisks = archived.Advice.HostRisks,
                        HostAssumptions = archived.Advice.HostAssumptions, HostUnknowns = archived.Advice.HostUnknowns,
                        HostUnsupportedMechanics = archived.Advice.HostUnsupportedMechanics };
                    var result = JsonSerializer.Serialize(advice, jsonOptions);
                    if (!string.IsNullOrEmpty(config.ApiKey) && result.Contains(config.ApiKey, StringComparison.Ordinal))
                        throw new InvalidDataException("模型结果包含不应导出的敏感内容。");
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    adviceStage = "output";
                    var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllTextAsync(temporary, result, cancellation.Token); File.Move(temporary, outputPath, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                finally
                {
                    try { await traces.AppendBatchAsync(requests.ToArray(), tools.ToArray(), CancellationToken.None); }
                    catch { adviceStage = "audit"; throw; }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                var usage = usages.ToArray();
                Print(new { Connected = true, advice.Verification, Actions = advice.Actions.Count, ElapsedMilliseconds = clock.ElapsedMilliseconds,
                    PersonalLibraryAvailable = library is not null, ConversationTurns = advice.ConversationArchiveIds.Count, advice.KnowledgeAsOf,
                    Limitations = new { advice.LimitationsVersion, ModelAssumptions = advice.Assumptions?.Count,
                        ModelUnknowns = advice.Unknowns?.Count, ModelUnsupportedMechanics = advice.UnsupportedMechanics?.Count,
                        HostRisks = advice.HostRisks?.Count, HostAssumptions = advice.HostAssumptions?.Count,
                        HostUnknowns = advice.HostUnknowns?.Count, HostUnsupportedMechanics = advice.HostUnsupportedMechanics?.Count,
                        advice.HostLimitationsTruncated },
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
                var environmentPath = Option("env") ?? EnvironmentModelConfiguration.FindFile()
                    ?? throw new ArgumentException("未找到 .env，请用 --env 指定。");
                var dataRoot = Option("data-dir") ?? Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
                var config = await LoadCliModelConfigurationAsync(environmentPath, dataRoot);
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var started = System.Diagnostics.Stopwatch.StartNew();
                var context = new AdviceContext("connection-check", 0,
                    "连接检查：直接返回summary=连接成功，reasons和risks为空数组，alternative为null，不调用工具，不分析对局。", new JsonObject());
                _ = await new AdviceService(http).AnalyzeAsync(config.Settings with { MaxToolRounds = 0, MaxOutputTokens = 512 }, config.ApiKey,
                    context, (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true);
                Print(new { Connected = true, ElapsedMilliseconds = started.ElapsedMilliseconds, SendsGameData = false,
                    ToolsEnabled = config.Settings.EnableTools, ResponseFormat = config.Settings.ResponseFormat });
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
    catch (Exception error) when (args[0] == "advise")
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = "advice_failed", Stage = adviceStage,
            Kind = error.GetType().Name, HttpStatus = error is HttpRequestException requestError ? (int?)requestError.StatusCode : null,
            Hint = "请检查对应阶段的快照、卡库、模型服务、本局预算或文件路径；原始响应与凭据不会输出。" }));
        return 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
