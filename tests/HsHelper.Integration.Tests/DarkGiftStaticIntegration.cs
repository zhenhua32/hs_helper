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

internal static class DarkGiftStaticIntegration
{
    private const string Build = "255790", Charge = "EDR_100t6e", Life = "EDR_100te", Complex = "EDR_100t5e";
    private const string Key = "fixture-dark-gift-secret", PrivateCard = "PRIVATE_DARK_GIFT_HAND";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var beasts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        using var gifts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "dark-gift-static-255790.json")));
        Check(beasts.RootElement.GetProperty("build").GetString() == Build && gifts.RootElement.GetProperty("build").GetString() == Build &&
            gifts.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64 && gifts.RootElement.GetProperty("cardDefsSha256").GetString()?.Length == 64,
            "The native integration definitions lost their pinned public source evidence.");
        var definitions = beasts.RootElement.GetProperty("cards").EnumerateArray().Concat(gifts.RootElement.GetProperty("cards").EnumerateArray())
            .GroupBy(card => card.GetProperty("id").GetString(), StringComparer.Ordinal).Select(group => group.Last().Clone()).ToArray();
        var directory = Path.Combine(root, "dark-gift-static", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);

        var charge = await CreateAsync(directory, definitions, Engine(false));
        var chargeResult = await CompleteAsync(charge, false);
        AssertPositive(chargeResult, false);
        var plainCharge = await CreateAsync(directory, definitions, Engine(false, includeGift: false));
        var chargeControl = await EvaluateAsync(plainCharge, false);
        AssertSameDirectValues(chargeResult.Line, chargeControl, false);
        Check(Coverage(chargeResult.Line, "dark-gift-observed-charge:" + Charge) && !Coverage(chargeControl, "dark-gift-observed-charge:" + Charge),
            "The attached effect was not audited separately from already observed charge.");
        pass("parsed and reopened current charge gift reaches real host validation fake HTTP and immutable archive as a verified lethal without regranting its current flag");

        var life = await CreateAsync(directory, definitions, Engine(true));
        var lifeResult = await CompleteAsync(life, true);
        AssertPositive(lifeResult, true);
        var plainLife = await CreateAsync(directory, definitions, Engine(true, includeGift: false));
        var lifeControl = await EvaluateAsync(plainLife, true);
        AssertSameDirectValues(lifeResult.Line, lifeControl, true);
        Check(Coverage(lifeResult.Line, "dark-gift-observed-lifesteal:" + Life) && !Coverage(lifeControl, "dark-gift-observed-lifesteal:" + Life),
            "The observed lifesteal enchantment was not independently audited.");
        pass("parsed current lifesteal gift preserves attack eight heals only twenty to twenty eight and matches the no-enchantment observed-state control through persistence");

        foreach (var binding in new[] { "owner", "attached", "type", "target-type" })
        {
            var changes = binding switch
            {
                "owner" => new[] { "TAG_CHANGE Entity=50 tag=CONTROLLER value=2" },
                "attached" => new[] { "TAG_CHANGE Entity=50 tag=ATTACHED value=999" },
                "type" => new[] { "TAG_CHANGE Entity=50 tag=CARDTYPE value=SPELL" },
                _ => new[] { "TAG_CHANGE Entity=30 tag=CARDTYPE value=SPELL" }
            };
            var invalid = await CreateAsync(directory, definitions, Engine(false, changes: changes), unresolvedAttachment: binding == "attached");
            var result = await CompleteAsync(invalid, false);
            AssertNotVerified(result, "An explicit " + binding + " conflict acquired static gift coverage.");
        }
        try
        {
            _ = new SnapshotCoach(charge.Frame, CompactAdviceContextBuilder.Build(charge.Frame, "255791"), charge.Catalog, "255791");
            throw new InvalidOperationException("A mismatched build acquired a real host binding.");
        }
        catch (ArgumentException) { }
        pass("actual parser owner attachment type and target-type contradictions plus wrong build cannot become executable or verified lethal in the final archived model answer");

        var missingLifeFlag = await CreateAsync(directory, definitions, Engine(true, grantFlag: false));
        var noFlag = await CompleteAsync(missingLifeFlag, true);
        AssertNotVerified(noFlag, "A lifesteal enchantment inferred a missing current keyword.");
        Check(Entity(noFlag.Line, 11)["RemainingHealth"]?.GetValue<int>() == 20,
            "An unobserved lifesteal flag was applied from the enchantment definition.");
        var missingAttack = await CreateAsync(directory, definitions, Engine(true, observeAttack: false));
        AssertNotVerified(await CompleteAsync(missingAttack, true), "Missing current attack was repaired using the gift's printed bonus.");
        var missingCharge = await CreateAsync(directory, definitions, Engine(false, grantFlag: false));
        var noCharge = await EvaluateAsync(missingCharge, false);
        Check(noCharge["Verification"]?.ToString() != "Verified" && noCharge["IsLethal"]?.GetValue<bool>() == false &&
            noCharge["AppliedActions"]?.GetValue<int>() < 2, "Missing charge was fabricated to permit a fresh hero attack.");
        pass("current flag and stat omissions remain evidence gaps and never manufacture charge access lifesteal healing or the printed three attack twice");

        foreach (var variant in new[] { "dbf", "text", "mechanics", "type", "target-dbf", "target-text" })
        {
            var altered = definitions.Select(card => JsonNode.Parse(card.GetRawText())!.AsObject()).ToArray();
            var enchantment = altered.Single(card => card["id"]?.ToString() == (variant.StartsWith("target-", StringComparison.Ordinal) ? "EDR_227" : Charge));
            if (variant == "dbf") enchantment["dbfId"] = 113810;
            if (variant == "target-dbf") enchantment["dbfId"] = 114021;
            if (variant is "text" or "target-text") enchantment["text"] = "改变伤害并额外触发未知效果。";
            if (variant == "mechanics") enchantment["mechanics"] = new JsonArray("TRIGGER_VISUAL");
            if (variant == "type") enchantment["type"] = "MINION";
            var badDefinitions = altered.Select(card => JsonSerializer.SerializeToElement(card)).ToArray();
            var invalid = await CreateAsync(directory, badDefinitions, Engine(false));
            AssertNotVerified(await CompleteAsync(invalid, false), "A native-looking ID bypassed exact " + variant + " metadata audit.");
        }
        pass("real catalog imports with native IDs but altered DBF text mechanics or type cannot promote tool prefixes or forged model verification into saved executable advice");

        var unknown = await CreateAsync(directory, definitions, Engine(false, gift: Complex));
        var mixed = await CreateAsync(directory, definitions, Engine(false, extraGift: true));
        foreach (var item in new[] { unknown, mixed })
        {
            var result = await CompleteAsync(item, false);
            AssertNotVerified(result, "An unknown or mixed attached gift acquired a complete native line.");
            Check(result.Advice.HostUnsupportedMechanics?.Contains("UNMAPPED_ATTACHED_ENCHANTMENT") == true &&
                result.Archive.Advice.HostUnsupportedMechanics?.Contains("UNMAPPED_ATTACHED_ENCHANTMENT") == true,
                "Unknown attached mechanics were erased by model opinions or archive preparation.");
        }
        pass("actual complex and mixed gift attachments retain the host unsupported boundary while usage source IDs and sanitized tool hashes survive reopening");
    }

    private sealed record Saved(string Directory, CardCatalog Catalog, SessionStore Sessions, GameSnapshot Frame, DecisionPoint Point);
    private sealed record Completed(JsonNode Line, StrategyAdvice Advice, ArchivedAdviceRecord Archive);

    private static async Task<Saved> CreateAsync(string directory, JsonElement[] cards, PowerLogStateEngine engine, bool unresolvedAttachment = false)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var catalog = new CardCatalog(Path.Combine(path, "cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards)))
            await catalog.ImportAsync(input, Build, source: "fixture://fixed-dark-gift-integration");
        var frame = engine.Snapshot();
        Check(frame.Quality.HasLocalPerspective && frame.Quality.IsComplete == !unresolvedAttachment && frame.Quality.IsStable == !unresolvedAttachment &&
            (!unresolvedAttachment || !frame.Entities.ContainsKey(50) && frame.Quality.Warnings.Any(warning => warning.Contains("关联目标未确认", StringComparison.Ordinal))) && frame.ClientBuild == Build &&
            frame.Entities[60].CardId is null && !SnapshotCodec.Serialize(frame).Contains(PrivateCard, StringComparison.Ordinal),
            "The real parser fixture lost its stable local frame or exposed a private opposing hand.");
        var sessions = new SessionStore(Path.Combine(path, "history.db")); await sessions.InitializeAsync();
        var recorder = new SessionRecorder(sessions, new(null, Build, 1)); recorder.Observe(frame, 1); await recorder.CompleteAsync();
        var point = (await sessions.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        var reopened = new SessionStore(Path.Combine(path, "history.db")); await reopened.InitializeAsync();
        var historical = await reopened.GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact parsed decision.");
        Check(historical.Point == point && historical.Context.CardBuild == Build && !historical.HasUnattributedEvents &&
            SnapshotCodec.Serialize(historical.Context.Snapshot) == SnapshotCodec.Serialize(frame), "Recorder/reopen changed the exact enchantment binding or current attributes.");
        return new(path, catalog, reopened, historical.Context.Snapshot, point);
    }

    private static async Task<JsonNode> EvaluateAsync(Saved item, bool life)
    {
        var coach = new SnapshotCoach(item.Frame, CompactAdviceContextBuilder.Build(item.Frame, Build), item.Catalog, Build);
        return await coach.ExecuteAsync("evaluate_line", Arguments(life), default);
    }

    private static async Task<Completed> CompleteAsync(Saved item, bool life, string build = Build)
    {
        var before = SnapshotCodec.Serialize(item.Frame);
        var definitions = await item.Catalog.GetByIdsBatchedAsync(item.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(item.Frame, build, AdviceTaskKind.Turn, cards: definitions)
            with { KnowledgeAsOf = item.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(item.Frame, context.Snapshot, item.Catalog, build, toolCompleted: observations.Add);
        using var handler = new Model(life); using var http = new HttpClient(handler);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://dark-gift-fixture.invalid/v1/chat/completions"), "dark-gift-fixture", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        var observation = observations.Single();
        Check(handler.Calls == 2 && handler.Line is not null && observation is { Success: true, Name: "evaluate_line", ResultHash.Length: 64, ToolCallId: "dark-gift-line", RequestId: not null } &&
            observation.AnalysisRunId == advice.AnalysisRunId && observation.Model == advice.Model && observation.EventSequence == item.Frame.EventSequence &&
            observation.RequestId == usages[0].RequestId && advice.RequestIds.SequenceEqual(usages.Select(usage => usage.RequestId)) &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30 && usage.CardBuild == build && usage.PromptTemplateVersion == "hunter-v18/turn") &&
            budget.Snapshot(item.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            observation.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handler.Line!.ToJsonString()))) &&
            advice.TemplateVersion == "hunter-v18/turn" && before == SnapshotCodec.Serialize(item.Frame),
            "Real tool source/hash usage accounting template binding or immutable snapshot changed across HTTP and final host validation.");
        var draft = AdviceArchiveStore.Prepare(new(item.Frame.MatchId, item.Frame.StateVersion, item.Frame.EventSequence, build, advice.TemplateVersion!, advice.Summary)
        {
            Task = context.Task, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
            Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = [new("evaluate_line", "实际静态附魔链工具结果；边界以主机核验为准。", observation.ResultHash)],
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
            HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
            HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
        }, [Key]);
        var archivePath = Path.Combine(item.Directory, "advice-" + Guid.NewGuid().ToString("N") + ".db");
        var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync(); var saved = await archive.SaveAsync(draft);
        var loaded = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing saved gift advice.");
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) && loaded.Advice.Verification == advice.Verification &&
            loaded.Advice.VerifiedLethal == advice.VerifiedLethal && loaded.Advice.Actions.Count == advice.Actions.Count &&
            loaded.Advice.Evidence.Single().ResultHash == observation.ResultHash && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) &&
            loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.KnowledgeAsOf == item.Point.RecordedAt &&
            !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal) && !handler.Payloads.Any(payload => payload.Contains(PrivateCard, StringComparison.Ordinal)),
            "Prepare/save/reopen lost source-bound validation, tool evidence, history cutoff, actions or redaction.");
        var tracePath = Path.Combine(item.Directory, "trace-" + Guid.NewGuid().ToString("N") + ".db");
        var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usages.Select(usage => new ModelRequestTrace(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model,
            usage.Round, usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = build, Task = context.Task, PromptTemplateVersion = advice.TemplateVersion!, AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence }),
        [new(Guid.NewGuid().ToString("N"), item.Frame.MatchId, item.Frame.StateVersion, observation.Name, observation.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = observation.ResultHash, Model = observation.Model, AnalysisRunId = observation.AnalysisRunId, RequestId = observation.RequestId,
            ToolCallId = observation.ToolCallId, EventSequence = observation.EventSequence }]);
        var totals = await new AnalysisTraceStore(tracePath).GetBudgetTotalsAsync(item.Frame.MatchId);
        var tools = await trace.ListToolsAsync(item.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && tools.Single().ResultHash == observation.ResultHash &&
            tools.Single().RequestId == usages[0].RequestId && tools.Single().AnalysisRunId == advice.AnalysisRunId,
            "Reopened durable usage and tool audit lost paid rounds or exact lineage.");
        return new(handler.Line!, advice, loaded);
    }

    private static void AssertPositive(Completed result, bool life)
    {
        Check(result.Line["Verification"]?.ToString() == "Verified" && result.Line["IsLegal"]?.GetValue<bool>() == true && result.Line["AppliedActions"]?.GetValue<int>() == 2 &&
            result.Line["IsLethal"]?.GetValue<bool>() == !life && result.Advice.Verification == "verified" && result.Advice.Actions.Count == 2 &&
            result.Advice.VerifiedLethal == !life && result.Archive.Advice.Actions.Count == 2 && result.Archive.Advice.VerifiedLethal == !life &&
            Entity(result.Line, 30)["Attack"]?.GetValue<int>() == (life ? 8 : 5) && Entity(result.Line, 11)["RemainingHealth"]?.GetValue<int>() == (life ? 28 : 20) &&
            result.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 6 && (!life || Entity(result.Line, 40)["RemainingHealth"]?.GetValue<int>() == 32),
            "Observed static gift failed the covered original minion play/combat chain or applied an extra stat/keyword effect: " + result.Line.ToJsonString());
    }
    private static void AssertSameDirectValues(JsonNode attached, JsonNode control, bool life)
    {
        Check(control["Verification"]?.ToString() == "Verified" && control["IsLethal"]?.GetValue<bool>() == !life &&
            Entity(attached, 30)["Attack"]?.ToString() == Entity(control, 30)["Attack"]?.ToString() &&
            Entity(attached, 11)["RemainingHealth"]?.ToString() == Entity(control, 11)["RemainingHealth"]?.ToString() &&
            (life ? Entity(attached, 40)["RemainingHealth"]?.ToString() == Entity(control, 40)["RemainingHealth"]?.ToString()
                : !HasEntity(attached, 21) && !HasEntity(control, 21)) &&
            attached["Result"]?["AvailableMana"]?.ToString() == control["Result"]?["AvailableMana"]?.ToString(),
            "Removing the observed enchantment changed current values or exposed duplicate application/payment.");
    }
    private static void AssertNotVerified(Completed result, string error) => Check(result.Line["Verification"]?.ToString() != "Verified" &&
        result.Line["IsLethal"]?.GetValue<bool>() == false && result.Advice.Verification is "conditional" or "unsupported" &&
        result.Advice.Actions.Count == 0 && !result.Advice.VerifiedLethal && result.Archive.Advice.Actions.Count == 0 && !result.Archive.Advice.VerifiedLethal, error);
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity?["EntityId"]?.GetValue<int>() == id)!;
    private static bool HasEntity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Any(entity => entity?["EntityId"]?.GetValue<int>() == id);
    private static bool Coverage(JsonNode line, string expected) => line["RuleCoverage"]!.AsArray().Any(value => value?.ToString() == expected);
    private static JsonObject Arguments(bool life) => new() { ["actions"] = new JsonArray(
        new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 },
        new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = life ? 40 : 21 }) };

    private static PowerLogStateEngine Engine(bool life, bool includeGift = true, string? gift = null, bool grantFlag = true,
        bool observeAttack = true, bool extraGift = false, IEnumerable<string>? changes = null)
    {
        var engine = new PowerLogStateEngine(1, "dark-gift-" + Guid.NewGuid().ToString("N"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1",
            "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=NUM_CARDS_IN_HAND value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Feed("FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER",
            "tag=HEALTH value=30", "tag=DAMAGE value=10", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE",
            "tag=HEALTH value=" + (life ? 30 : 3), "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=30 CardID=EDR_227", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=COST value=4");
        if (observeAttack) Feed("tag=ATK value=" + (life ? 8 : 5));
        Feed("tag=HEALTH value=2", "tag=DAMAGE value=0", "tag=RUSH value=1", "tag=DEATHRATTLE value=1");
        if (grantFlag) Feed(life ? "tag=LIFESTEAL value=1" : "tag=CHARGE value=1");
        if (life) Feed("FULL_ENTITY - Creating ID=40 CardID=EDR_227", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION",
            "tag=ATK value=0", "tag=HEALTH value=40", "tag=DAMAGE value=0", "tag=DEATHRATTLE value=1", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1");
        if (includeGift) Feed("FULL_ENTITY - Creating ID=50 CardID=" + (gift ?? (life ? Life : Charge)), "tag=CONTROLLER value=1", "tag=ZONE value=PLAY",
            "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
        if (extraGift) Feed("FULL_ENTITY - Creating ID=51 CardID=" + Complex, "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
        Feed("FULL_ENTITY - Creating ID=60 CardID=" + PrivateCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION");
        foreach (var line in changes ?? []) Feed(line);
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION",
            "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Model(bool life) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Line { get; private set; }
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); Payloads.Add(text); var payload = JsonNode.Parse(text)!;
            var system = payload["messages"]![0]!["content"]!.GetValue<string>();
            Check(system.Contains("EDR_100t6e", StringComparison.Ordinal) && system.Contains("EDR_100te", StringComparison.Ordinal) &&
                system.Contains("不能再次赋予关键词", StringComparison.Ordinal), "The real model request lost the observed-gift boundary instructions.");
            object message;
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[] { new { id = "dark-gift-line", type = "function",
                function = new { name = "evaluate_line", arguments = Arguments(life).ToJsonString() } } } };
            else
            {
                var result = payload["messages"]!.AsArray().Single(node => node?["role"]?.ToString() == "tool")!;
                Line = JsonNode.Parse(result["content"]!.GetValue<string>())!;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new
                {
                    summary = "DARK_GIFT_MODEL_OPINION " + Key, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                    assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions = Arguments(life)["actions"], verified_lethal = true, verification = "verified", HostUnsupportedMechanics = Array.Empty<string>()
                }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
}
