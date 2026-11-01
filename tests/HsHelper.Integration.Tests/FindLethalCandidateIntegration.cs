using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

internal static class FindLethalCandidateIntegration
{
    private const string Build = "255790", Key = "fixture-find-lethal-key", PrivateCard = "PRIVATE_LETHAL_IDENTITY";
    private const string ChargeGift = "EDR_100t6e", UnknownGift = "EDR_100t5e";
    private const string Template = "hunter-v20/turn";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var fixtures = Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures");
        var cards = new List<JsonElement>();
        foreach (var name in new[] { "beast-hunter", "dark-gift-static", "play-trigger-line", "quest-progress", "terror-end-turn", "lethal-search" })
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, name + "-255790.json")));
            Check(document.RootElement.GetProperty("build").GetString() == Build &&
                document.RootElement.GetProperty("source").GetString() == "https://api.hearthstonejson.com/v1/255790/zhCN/cards.json",
                "Lethal candidates must use complete public records from the pinned build.");
            cards.AddRange(document.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var definitions = cards.GroupBy(card => card.GetProperty("id").GetString(), StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var directory = Path.Combine(root, "find-lethal-candidates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);

        foreach (var scenario in new[] { CoinArcane(), CoinPower() })
        {
            var result = await CompleteAsync(await CreateAsync(directory, definitions, scenario), scenario);
            AssertFound(result, scenario);
            Check(result.Search["Evaluation"]!["RuleCoverage"]!.AsArray().Any(value => value?.ToString() == "coin-temporary-mana"),
                "The found lethal skipped the resource-enabling coin step.");
        }
        pass("parsed saved and reopened coin into Arcane Shot or Steady Shot is found by the real tool and revalidated before source-bound advice and trace persistence");

        var charge = Charge();
        var charged = await CompleteAsync(await CreateAsync(directory, definitions, charge), charge);
        AssertFound(charged, charge);
        Check(charged.Search["Evaluation"]!["RuleCoverage"]!.AsArray().Any(value => value?.ToString() == "dark-gift-observed-charge:" + ChargeGift) &&
            Entity(charged.Search, 30)["Attack"]?.GetValue<int>() == 8 && Entity(charged.Search, 30)["Flags"]?["CHARGE"]?.GetValue<int>() == 1,
            "Search regranted charge or changed the already observed DINO attack instead of auditing the attachment.");
        var native = new Scenario("native-charge", "CS2_171", false, false, 1, 1, 1, 1, 1, true);
        AssertFound(await CompleteAsync(await CreateAsync(directory, definitions, native), native), native);
        pass("observed non-Rush DINO charge with the actual static gift and native Stonetusk Boar both produce fresh hand play then hero attack lethal through the production host");

        var combination = Charge() with { Name = "coin-charge", Coin = true, Mana = 7 };
        var combined = await CompleteAsync(await CreateAsync(directory, definitions, combination), combination);
        AssertFound(combined, combination);
        Check(combined.Advice.Actions.Count == 3 && combined.Advice.Actions[0] is { Kind: "play", SourceEntityId: 31 } &&
            combined.Advice.Actions[1] is { Kind: "play", SourceEntityId: 30 } && combined.Advice.Actions[2] is { Kind: "attack", SourceEntityId: 30, TargetEntityId: 21 },
            "The combined search did not preserve coin payment before minion play and the same entity's attack.");
        pass("the real bounded search combines coin with observed charge and archives only the returned order after final host reevaluation without mutating the historical frame");

        foreach (var scenario in new[]
        {
            Charge() with { Name = "missing-current-charge", CurrentCharge = null },
            Charge() with { Name = "missing-current-cost", Cost = null },
            Charge() with { Name = "missing-current-attack", Attack = null },
            CoinArcane() with { Name = "missing-coin-cost", ObserveCoinCost = false }
        }) AssertRejected(await CompleteAsync(await CreateAsync(directory, definitions, scenario), scenario), scenario.Name);
        var cannotAttackHero = Charge() with { Name = "cannot-attack-heroes", CantAttackHeroes = true };
        await AssertFinalDeniedAsync(await CreateAsync(directory, definitions, cannotAttackHero), cannotAttackHero);
        pass("missing current charge cost attack or coin cost and an observed hand attack restriction cannot be repaired into a searched lethal or model-forged executable final answer");

        foreach (var variant in new[] { "missing-target", "gift-dbf", "target-text", "coin-type", "coin-text", "coin-dbf", "coin-raw-mechanics" })
        {
            var altered = definitions.Select(card => JsonNode.Parse(card.GetRawText())!.AsObject()).ToList();
            var scenario = variant.StartsWith("coin-", StringComparison.Ordinal) ? CoinArcane() : Charge();
            if (variant == "missing-target") altered.RemoveAll(card => card["id"]?.ToString() == "DINO_421");
            if (variant == "gift-dbf") altered.Single(card => card["id"]?.ToString() == ChargeGift)["dbfId"] = 113810;
            if (variant == "target-text") altered.Single(card => card["id"]?.ToString() == "DINO_421")["text"] = "未审计的额外使用效果。";
            if (variant == "coin-type") altered.Single(card => card["id"]?.ToString() == "GAME_005")["type"] = "MINION";
            if (variant == "coin-text") altered.Single(card => card["id"]?.ToString() == "GAME_005")["text"] = "不能获得法力水晶。";
            if (variant == "coin-dbf") altered.Single(card => card["id"]?.ToString() == "GAME_005")["dbfId"] = 1747;
            if (variant == "coin-raw-mechanics") altered.Single(card => card["id"]?.ToString() == "GAME_005")["mechanics"] = new JsonArray("TRIGGER_VISUAL");
            scenario = scenario with { Name = variant };
            var records = altered.Select(card => JsonSerializer.SerializeToElement(card)).ToArray();
            AssertRejected(await CompleteAsync(await CreateAsync(directory, records, scenario), scenario), variant);
        }
        pass("a missing catalog identity and native-looking IDs with changed gift DBF target text or coin type cannot authorize the candidates or the final archived actions");

        foreach (var scenario in new[]
        {
            Charge() with { Name = "unknown-gift", Gift = UnknownGift },
            Charge() with { Name = "opposing-secret", OpposingSecret = true },
            Charge() with { Name = "raptor-pending", Raptor = true }
        })
        {
            var result = await CompleteAsync(await CreateAsync(directory, definitions, scenario), scenario);
            AssertRejected(result, scenario.Name);
            Check(result.Advice.HostUnsupportedMechanics?.Count > 0 && result.Archive.Advice.HostUnsupportedMechanics?.Count > 0,
                "The real host's unresolved boundary disappeared from saved model advice: " + scenario.Name);
        }
        pass("unknown attached effects opposing secrets and Raptor after-play randomness prevent search and final validation from crossing an unobserved resolution boundary");

        var privateSource = Charge() with { Name = "private-hand", PrivateSource = true };
        var masked = await CreateAsync(directory, definitions, privateSource);
        Check(masked.Frame.Entities[30].CardId is null, "The parser exposed the opponent's hand identity.");
        await AssertFinalDeniedAsync(masked, privateSource);
        var wrongBuild = Charge() with { Name = "unaudited-build", ClientBuild = "255791" };
        AssertRejected(await CompleteAsync(await CreateAsync(directory, definitions, wrongBuild), wrongBuild), wrongBuild.Name);
        try
        {
            _ = new SnapshotCoach(masked.Frame, CompactAdviceContextBuilder.Build(masked.Frame, "255791"), masked.Catalog, "255791");
            throw new InvalidOperationException("A mismatched host build was accepted.");
        }
        catch (ArgumentException) { }
        pass("private hand identities remain masked in search HTTP tool evidence and storage while mismatched or unaudited card builds never yield verified native lethal");
    }

    private sealed record Scenario(string Name, string? HandCard, bool Coin, bool Power, int Mana, int EnemyHealth,
        int? Cost, int? Attack, int? Health, bool? CurrentCharge, string? Gift = null, bool ObserveCoinCost = true,
        bool OpposingSecret = false, bool Raptor = false, bool PrivateSource = false, string ClientBuild = Build, bool CantAttackHeroes = false);
    private static Scenario CoinArcane() => new("coin-arcane", "CORE_DS1_185", true, false, 0, 2, 1, null, null, null);
    private static Scenario CoinPower() => new("coin-power", null, true, true, 1, 2, null, null, null, null);
    private static Scenario Charge() => new("observed-charge", "DINO_421", false, false, 8, 8, 8, 8, 8, true, ChargeGift);
    private sealed record Saved(string Directory, CardCatalog Catalog, GameSnapshot Frame, DecisionPoint Point);
    private sealed record Completed(JsonNode Search, StrategyAdvice Advice, ArchivedAdviceRecord Archive);

    private static async Task<Saved> CreateAsync(string directory, JsonElement[] cards, Scenario scenario)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var catalog = new CardCatalog(Path.Combine(path, "cards.db")); await catalog.InitializeAsync();
        using (var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards)))
            await catalog.ImportAsync(stream, scenario.ClientBuild, source: "fixture://fixed-find-lethal-candidates");
        var frame = Engine(scenario).Snapshot();
        Check(frame.Quality is { IsStable: true, IsComplete: true, HasLocalPerspective: true } && frame.ClientBuild == scenario.ClientBuild &&
            frame.Entities[60].CardId is null && !SnapshotCodec.Serialize(frame).Contains(PrivateCard, StringComparison.Ordinal),
            "The parser fixture is not a stable complete confirmed frame or failed to mask a private identity.");
        var sessions = new SessionStore(Path.Combine(path, "history.db")); await sessions.InitializeAsync();
        var recorder = new SessionRecorder(sessions, new(null, scenario.ClientBuild, 1)); recorder.Observe(frame, 1); await recorder.CompleteAsync();
        var point = (await sessions.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        var reopened = new SessionStore(Path.Combine(path, "history.db")); await reopened.InitializeAsync();
        var exact = await reopened.GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact stored search frame.");
        Check(exact.Point == point && exact.Context.CardBuild == scenario.ClientBuild && !exact.HasUnattributedEvents &&
            SnapshotCodec.Serialize(exact.Context.Snapshot) == SnapshotCodec.Serialize(frame), "Persistence changed the current candidate identity resources or event boundary.");
        return new(path, catalog, exact.Context.Snapshot, point);
    }

    private static async Task<Completed> CompleteAsync(Saved item, Scenario scenario)
    {
        var before = SnapshotCodec.Serialize(item.Frame);
        var records = await item.Catalog.GetByIdsBatchedAsync(item.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), scenario.ClientBuild);
        var context = CompactAdviceContextBuilder.Create(item.Frame, scenario.ClientBuild, AdviceTaskKind.Turn, cards: records)
            with { KnowledgeAsOf = item.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(item.Frame, context.Snapshot, item.Catalog, scenario.ClientBuild, toolCompleted: observations.Add);
        using var model = new Model(scenario); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://lethal-fixture.invalid/v1/chat/completions"), "lethal-fixture", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        var observed = observations.Single();
        Check(model.Calls == 2 && model.Search is not null && observed is { Name: "find_lethal", Success: true, ResultHash.Length: 64, ToolCallId: "search-lethal", RequestId: not null } &&
            observed.AnalysisRunId == advice.AnalysisRunId && observed.Model == advice.Model && observed.EventSequence == item.Frame.EventSequence &&
            observed.RequestId == usages[0].RequestId && advice.RequestIds.SequenceEqual(usages.Select(usage => usage.RequestId)) &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30 && usage.CardBuild == scenario.ClientBuild && usage.PromptTemplateVersion == Template) &&
            budget.Snapshot(item.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            observed.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.Search!.ToJsonString()))) &&
            advice.TemplateVersion == Template && before == SnapshotCodec.Serialize(item.Frame),
            "Search tool lineage hash model usage template binding or immutable snapshot changed: " + scenario.Name);
        Check(!model.Payloads.Any(payload => payload.Contains(PrivateCard, StringComparison.Ordinal)), "A private identity reached model HTTP.");
        Check(model.Search!["IsExhaustive"]?.GetValue<bool>() == false, "A bounded search claimed exhaustive coverage.");
        var draft = AdviceArchiveStore.Prepare(new(item.Frame.MatchId, item.Frame.StateVersion, item.Frame.EventSequence,
            scenario.ClientBuild, advice.TemplateVersion!, advice.Summary)
        {
            Task = context.Task, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
            Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = [new("find_lethal", "有界候选搜索；最终动作另由固定快照主机重新核验。", observed.ResultHash)],
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
            HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
            HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
        }, [Key]);
        var archivePath = Path.Combine(item.Directory, "advice.db"); var store = new AdviceArchiveStore(archivePath); await store.InitializeAsync();
        var saved = await store.SaveAsync(draft); var loaded = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Lost saved search advice.");
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) && loaded.Advice.Verification == advice.Verification &&
            loaded.Advice.VerifiedLethal == advice.VerifiedLethal && loaded.Advice.Actions.Count == advice.Actions.Count &&
            loaded.Advice.Evidence.Single().ResultHash == observed.ResultHash && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) &&
            loaded.Advice.Model == advice.Model && loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.KnowledgeAsOf == item.Point.RecordedAt &&
            loaded.Advice.MatchId == item.Frame.MatchId && loaded.Advice.StateVersion == item.Frame.StateVersion &&
            loaded.Advice.EventSequence == item.Frame.EventSequence && loaded.Advice.CardBuild == scenario.ClientBuild &&
            !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal) && !JsonSerializer.Serialize(loaded).Contains(PrivateCard, StringComparison.Ordinal),
            "Sanitized archived search advice lost exact source validation or evidence on reopen.");
        await AssertTraceAsync(item, scenario, usages, observed, advice.AnalysisRunId!, context.Task);
        return new(model.Search!, advice, loaded);
    }

    private static async Task AssertFinalDeniedAsync(Saved item, Scenario scenario)
    {
        var before = SnapshotCodec.Serialize(item.Frame);
        var context = CompactAdviceContextBuilder.Create(item.Frame, scenario.ClientBuild, AdviceTaskKind.Turn)
            with { KnowledgeAsOf = item.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(item.Frame, context.Snapshot, item.Catalog, scenario.ClientBuild, toolCompleted: observations.Add);
        using var model = new Model(scenario); using var http = new HttpClient(model);
        var rejected = false;
        try
        {
            await new AdviceService(http).AnalyzeAsync(new(new Uri("https://lethal-fixture.invalid/v1/chat/completions"), "lethal-fixture", 1),
                Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        }
        catch (InvalidDataException) { rejected = true; }
        var observed = observations.Single();
        Check(rejected && model.Calls == 2 && model.Search?["VerifiedLethalFound"]?.GetValue<bool>() == false && model.Search["Actions"]!.AsArray().Count == 0 &&
            observed is { Name: "find_lethal", Success: true, ResultHash.Length: 64, ToolCallId: "search-lethal" } &&
            observed.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.Search.ToJsonString()))) &&
            observed.RequestId == usages[0].RequestId && observed.AnalysisRunId == usages[0].AnalysisRunId &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30) &&
            budget.Snapshot(item.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            !model.Payloads.Any(payload => payload.Contains(PrivateCard, StringComparison.Ordinal)) &&
            model.Search["Evaluation"] is null && before == SnapshotCodec.Serialize(item.Frame) && !File.Exists(Path.Combine(item.Directory, "advice.db")),
            "An invalid final action escaped hard rejection, exposed an identity, or discarded already paid usage: " + scenario.Name);
        await AssertTraceAsync(item, scenario, usages, observed, usages[0].AnalysisRunId!, context.Task);
    }

    private static async Task AssertTraceAsync(Saved item, Scenario scenario, List<ModelUsage> usages, CoachToolObservation observed, string runId, string task)
    {
        var tracePath = Path.Combine(item.Directory, "trace.db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usages.Select(usage => new ModelRequestTrace(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model,
            usage.Round, usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = scenario.ClientBuild, Task = task, PromptTemplateVersion = Template, AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence }),
        [new(Guid.NewGuid().ToString("N"), item.Frame.MatchId, item.Frame.StateVersion, observed.Name, observed.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = observed.ResultHash, Model = observed.Model, AnalysisRunId = observed.AnalysisRunId, RequestId = observed.RequestId,
            ToolCallId = observed.ToolCallId, EventSequence = observed.EventSequence }]);
        var reopened = new AnalysisTraceStore(tracePath); var totals = await reopened.GetBudgetTotalsAsync(item.Frame.MatchId);
        var requests = await reopened.ListRequestsAsync(item.Frame.MatchId); var tools = await reopened.ListToolsAsync(item.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && requests.Count == 2 && requests.All(request => request.AnalysisRunId == runId &&
            request.EventSequence == item.Frame.EventSequence && request.CardBuild == scenario.ClientBuild && request.Task == task &&
            request.PromptTemplateVersion == Template && request.MatchId == item.Frame.MatchId && request.StateVersion == item.Frame.StateVersion) &&
            tools.Single().ResultHash == observed.ResultHash && tools.Single().RequestId == usages[0].RequestId && tools.Single().ToolCallId == "search-lethal" &&
            tools.Single().AnalysisRunId == runId && tools.Single().EventSequence == item.Frame.EventSequence,
            "Reopened request budget and tool audit lost the actual two paid rounds or their source linkage.");
    }

    private static void AssertFound(Completed result, Scenario scenario)
    {
        Check(result.Search["VerifiedLethalFound"]?.GetValue<bool>() == true && result.Search["ExploredNodes"]?.GetValue<int>() > 0 &&
            result.Search["Evaluation"]?["Verification"]?.ToString() == "Verified" && result.Search["Evaluation"]?["IsLegal"]?.GetValue<bool>() == true &&
            result.Search["Evaluation"]?["IsLethal"]?.GetValue<bool>() == true && result.Advice.Verification == "verified" && result.Advice.VerifiedLethal &&
            result.Archive.Advice.Verification == "verified" && result.Archive.Advice.VerifiedLethal &&
            JsonNode.DeepEquals(Actions(result.Search), ExpectedActions(scenario)) && result.Advice.Actions.Count == ExpectedActions(scenario).Count &&
            result.Search["Evaluation"]?["Result"]?["AvailableMana"]?.GetValue<int>() == 0,
            "A real known lethal was not found and independently verified in the expected resource order: " + scenario.Name + " " + result.Search.ToJsonString());
    }

    private static void AssertRejected(Completed result, string name) => Check(result.Search["VerifiedLethalFound"]?.GetValue<bool>() == false &&
        result.Search["Evaluation"] is null && result.Search["Actions"]?.AsArray().Count == 0 && !result.Advice.VerifiedLethal &&
        result.Advice.Verification is "conditional" or "unsupported" && result.Advice.Actions.Count == 0 &&
        !result.Archive.Advice.VerifiedLethal && result.Archive.Advice.Actions.Count == 0,
        "A gap or unknown boundary acquired a searched or model-forged verified lethal: " + name + " " + result.Search.ToJsonString());
    private static JsonNode Entity(JsonNode search, int id) => search["Evaluation"]!["Result"]!["Entities"]!.AsArray().Single(entity => entity?["EntityId"]?.GetValue<int>() == id)!;
    private static JsonArray Actions(JsonNode search) => new(search["Actions"]!.AsArray().Select(action => (JsonNode)new JsonObject
    {
        ["kind"] = action!["Kind"]!.ToString() switch { "PlayCard" => "play", "Attack" => "attack", "HeroPower" => "hero_power", _ => throw new InvalidOperationException("Unexpected search action.") },
        ["source_entity_id"] = action["SourceEntityId"]!.GetValue<int>(), ["target_entity_id"] = action["TargetEntityId"]?.DeepClone()
    }).ToArray());
    private static JsonArray ExpectedActions(Scenario scenario)
    {
        var actions = new JsonArray();
        if (scenario.Coin) actions.Add(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 31, ["target_entity_id"] = null });
        if (scenario.Power) actions.Add(new JsonObject { ["kind"] = "hero_power", ["source_entity_id"] = 12, ["target_entity_id"] = null });
        else if (scenario.HandCard == "CORE_DS1_185") actions.Add(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30, ["target_entity_id"] = 21 });
        else
        {
            actions.Add(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30, ["target_entity_id"] = null });
            actions.Add(new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 21 });
        }
        return actions;
    }

    private static PowerLogStateEngine Engine(Scenario scenario)
    {
        var engine = new PowerLogStateEngine(1, "lethal-candidate-" + Guid.NewGuid().ToString("N"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=" + scenario.ClientBuild + " FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1",
            "tag=HERO_ENTITY value=11", "tag=RESOURCES value=" + scenario.Mana, "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            "tag=NUM_CARDS_IN_HAND value=" + ((scenario.Coin ? 1 : 0) + (scenario.HandCard is null || scenario.PrivateSource ? 0 : 1)),
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Feed("FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER",
            "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE",
            "tag=HEALTH value=" + scenario.EnemyHealth, "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        if (scenario.Power) Feed("FULL_ENTITY - Creating ID=12 CardID=HERO_05bp", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER",
            "tag=COST value=2", "tag=EXHAUSTED value=0");
        if (scenario.Coin)
        {
            Feed("FULL_ENTITY - Creating ID=31 CardID=GAME_005", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=SPELL");
            if (scenario.ObserveCoinCost) Feed("tag=COST value=0");
        }
        if (scenario.HandCard is not null)
        {
            Feed("FULL_ENTITY - Creating ID=30 CardID=" + scenario.HandCard, "tag=CONTROLLER value=" + (scenario.PrivateSource ? 2 : 1), "tag=ZONE value=HAND",
                "tag=CARDTYPE value=" + (scenario.HandCard == "CORE_DS1_185" ? "SPELL" : "MINION"));
            if (scenario.Cost.HasValue) Feed("tag=COST value=" + scenario.Cost);
            if (scenario.Attack.HasValue) Feed("tag=ATK value=" + scenario.Attack);
            if (scenario.Health.HasValue) Feed("tag=HEALTH value=" + scenario.Health, "tag=DAMAGE value=0");
            if (scenario.CurrentCharge.HasValue) Feed("tag=CHARGE value=" + (scenario.CurrentCharge.Value ? 1 : 0));
            if (scenario.CantAttackHeroes) Feed("tag=CANT_ATTACK_HEROES value=1");
            if (scenario.HandCard == "DINO_421") Feed("tag=TAUNT value=1", "tag=ELUSIVE value=1", "tag=DEATHRATTLE value=1", "tag=CARDRACE value=BEAST");
        }
        if (scenario.Gift is not null) Feed("FULL_ENTITY - Creating ID=50 CardID=" + scenario.Gift, "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
        if (scenario.Raptor) Feed("FULL_ENTITY - Creating ID=40 CardID=EDR_849", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION",
            "tag=ATK value=2", "tag=HEALTH value=1", "tag=DAMAGE value=0", "tag=CARDRACE value=BEAST", "tag=EXHAUSTED value=1", "tag=NUM_TURNS_IN_PLAY value=1");
        Feed("FULL_ENTITY - Creating ID=60 CardID=" + PrivateCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION");
        if (scenario.OpposingSecret) Feed("FULL_ENTITY - Creating ID=61 CardID=" + PrivateCard, "tag=CONTROLLER value=2", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1");
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Model(Scenario scenario) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Search { get; private set; }
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); Payloads.Add(text); var payload = JsonNode.Parse(text)!;
            object message;
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[] { new { id = "search-lethal", type = "function",
                function = new { name = "find_lethal", arguments = "{\"maximum_depth\":6,\"maximum_nodes\":5000}" } } } };
            else
            {
                var tool = payload["messages"]!.AsArray().Single(node => node?["role"]?.ToString() == "tool")!;
                Search = JsonNode.Parse(tool["content"]!.GetValue<string>())!;
                var actions = Search["VerifiedLethalFound"]?.GetValue<bool>() == true ? Actions(Search) : ExpectedActions(scenario);
                message = new { role = "assistant", content = JsonSerializer.Serialize(new
                {
                    summary = "LETHAL_MODEL_OPINION " + Key, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                    assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions, verified_lethal = true, verification = "verified", HostUnsupportedMechanics = Array.Empty<string>()
                }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
}
