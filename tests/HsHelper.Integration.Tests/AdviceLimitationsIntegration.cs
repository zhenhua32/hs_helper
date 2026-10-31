using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

internal static class AdviceLimitationsIntegration
{
    private const string Build = "255790";
    private const string PrivateProbe = "PRIVATE_LIMITATIONS_HAND_PROBE";
    private const string ModelAssumption = "模型意见：假设对方没有额外响应";
    private const string ModelUnknown = "模型意见：不知道对手手牌";
    private const string ModelUnsupported = "模型意见：未核对其他牌效";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "advice-limitations", Guid.NewGuid().ToString("N"));
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db"));
        await catalog.InitializeAsync();
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Unpinned limitations fixture.");
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://advice-limitations");
        var archivePath = Path.Combine(directory, "advice.db");
        var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync();

        var conditional = Frame(secret: true);
        var conditionalCoach = Coach(conditional, catalog);
        var conditionalValidation = await conditionalCoach.Capabilities().ValidateActions!([Attack()], default);
        Check(conditionalValidation.Status == AdviceValidationStatus.Conditional && conditionalValidation.Assumptions is { Count: > 0 } &&
            conditionalValidation.UnsupportedMechanics?.Contains("OPPONENT_SECRET") == true && conditionalValidation.Unknowns is null,
            "Real conditional line did not classify its exact assumption and unsupported mechanism.");
        var conditionalAdvice = await Analyze(conditional, conditionalCoach, [Attack()], Line(Attack()));
        Check(conditionalAdvice.Verification == "conditional" && conditionalAdvice.Actions.Count == 0 && !conditionalAdvice.VerifiedLethal &&
            Same(conditionalAdvice.HostAssumptions, conditionalValidation.Assumptions) &&
            Same(conditionalAdvice.HostUnsupportedMechanics, conditionalValidation.UnsupportedMechanics) &&
            conditionalAdvice.HostUnknowns is null && conditionalAdvice.HostRisks!.Contains("OPPONENT_SECRET") &&
            conditionalAdvice.Risks.Contains("OPPONENT_SECRET") && !conditionalAdvice.HostLimitationsTruncated,
            "Fake model opinions erased a real selected line restriction or upgraded its actions/lethal.");
        await RoundTrip(archive, archivePath, conditional, conditionalAdvice);
        pass("real conditional SnapshotCoach validation keeps selected-line assumptions and mechanisms separate from model opinions through fake HTTP and reopened archive");

        var assembled = Frame(customSource: true);
        var assembledCoach = Coach(assembled, catalog);
        var assembledValidation = await assembledCoach.Capabilities().ValidateActions!([Attack()], default);
        var assembledAdvice = await Analyze(assembled, assembledCoach, [Attack()], Line(Attack()));
        Check(assembledValidation.Status == AdviceValidationStatus.Unsupported &&
            assembledAdvice.Verification == "unsupported" && assembledAdvice.Actions.Count == 0 && !assembledAdvice.VerifiedLethal &&
            assembledAdvice.HostUnsupportedMechanics?.Contains("CUSTOM_ASSEMBLED_SOURCE") == true &&
            Same(assembledAdvice.HostAssumptions, assembledValidation.Assumptions) && assembledAdvice.HostUnknowns is null &&
            !assembledAdvice.HostUnsupportedMechanics.Contains(ModelUnsupported) && !assembledAdvice.HostAssumptions!.Contains(ModelAssumption),
            "Assembled source lost the host restriction or copied model opinions into host categories.");
        await RoundTrip(archive, archivePath, assembled, assembledAdvice);
        pass("unsupported assembled native-effect proposals retain their exact host reason and cannot acquire executable actions or lethal after persistence");

        var choice = ChoiceFrame(); var choiceCoach = Coach(choice, catalog);
        var choiceValidation = await choiceCoach.Capabilities().ValidateActions!([new("choose", 80)], default);
        var choiceAdvice = await Analyze(choice, choiceCoach, [new("choose", 80)],
            new("evaluate_choice", new() { ["choice_id"] = 77, ["selected_entity_ids"] = new JsonArray(80) }));
        Check(choiceValidation.Status == AdviceValidationStatus.Verified && choiceAdvice.Verification == "verified" &&
            choiceAdvice.Actions is [{ Kind: "choose", SourceEntityId: 80 }] && !choiceAdvice.VerifiedLethal &&
            Same(choiceAdvice.HostAssumptions, choiceValidation.Assumptions) && choiceAdvice.HostAssumptions is { Count: > 0 } &&
            choiceAdvice.HostUnsupportedMechanics is null && choiceAdvice.HostUnknowns is null,
            "A legal choice lost its selection-only assumption or claimed its card effects/follow-up/lethal were verified.");
        await RoundTrip(archive, archivePath, choice, choiceAdvice);
        pass("verified choice actions retain the actual selection-only host assumption and leave unavailable mechanism and unknown categories unprovided");

        var clean = Frame(); var observations = new List<CoachToolObservation>();
        var cleanCoach = Coach(clean, catalog, observations.Add);
        var candidate = await cleanCoach.ExecuteAsync("evaluate_line", Line(new AdviceAction("play", 31)).Arguments, default);
        Check(candidate["Verification"]?.ToString() == "Unsupported" &&
            candidate["UnsupportedMechanics"]!.AsArray().Any(item => item?.ToString() == "UNKNOWN_CURRENT_COST"),
            "Unrelated candidate did not exercise a real unsupported rule path.");
        var verified = await Analyze(clean, cleanCoach, [Attack()], Line(new AdviceAction("play", 31)));
        Check(observations.Count == 2 && observations.All(item => item is { Name: "evaluate_line", Success: true, ResultHash.Length: 64 }) &&
            verified.Verification == "verified" && verified.Actions is [{ Kind: "attack", SourceEntityId: 30, TargetEntityId: 20 }] &&
            verified.VerifiedLethal && verified.HostAssumptions is { Count: 0 } && verified.HostUnsupportedMechanics is { Count: 0 } &&
            verified.HostUnknowns is null && verified.HostRisks is { Count: 0 } &&
            !JsonSerializer.Serialize(verified).Contains("UNKNOWN_CURRENT_COST", StringComparison.Ordinal),
            "An unrelated unsupported tool candidate contaminated the final verified selected action, or valid lethal gating regressed.");
        await RoundTrip(archive, archivePath, clean, verified);
        pass("an unsupported queried candidate does not contaminate the final covered attack or its host categories and genuine verified lethal remains intact");

        var missing = new CardCatalog(Path.Combine(directory, "no-manifest.db")); await missing.InitializeAsync();
        var missingAdvice = await Analyze(clean, Coach(clean, missing), [Attack()]);
        Check(missingAdvice.Verification == "unsupported" && missingAdvice.Actions.Count == 0 && !missingAdvice.VerifiedLethal &&
            missingAdvice.HostAssumptions is null && missingAdvice.HostUnknowns is null &&
            missingAdvice.HostUnsupportedMechanics is [{ } reason] && reason.Contains("缺少对应版本卡库", StringComparison.Ordinal),
            "The missing-catalog early exit was not explicitly classified as unsupported.");
        await RoundTrip(archive, archivePath, clean, missingAdvice);
        pass("missing build catalog is an explicit host unsupported restriction rather than an invented unknown or verified action");

        var mixed = await Analyze(choice, choiceCoach, [new("choose", 80), new("end_turn", 2)]);
        Check(mixed.Verification == "unsupported" && mixed.Actions.Count == 0 && !mixed.VerifiedLethal &&
            mixed.HostUnsupportedMechanics is [{ } mixedReason] && mixedReason.Contains("混合选择", StringComparison.Ordinal) &&
            mixed.HostAssumptions is null && mixed.HostUnknowns is null,
            "The mixed choice/action early exit lost its explicit unsupported category.");
        await RoundTrip(archive, archivePath, choice, mixed);
        pass("mixed choose and later actions keep the explicit unsupported host category without inheriting unrelated choice assumptions");

        var planning = clean with { Phase = GamePhase.Unknown };
        var planningCoach = Coach(planning, catalog);
        Check(planningCoach.Capabilities().ValidateActions is null, "Deck planning unexpectedly enabled runtime action validation.");
        var strategy = await Analyze(planning, planningCoach, []);
        Check(strategy.Verification == "strategy" && strategy.Actions.Count == 0 && !strategy.VerifiedLethal &&
            strategy.HostRisks is null && strategy.HostAssumptions is null && strategy.HostUnknowns is null &&
            strategy.HostUnsupportedMechanics is null && strategy.LimitationsVersion == 1,
            "An unvalidated planning answer invented empty host evidence or verified runtime actions.");
        await RoundTrip(archive, archivePath, planning, strategy);
        pass("strategy-only planning preserves unprovided host classifications instead of presenting them as an empty completed check");
    }

    private static SnapshotCoach Coach(GameSnapshot snapshot, CardCatalog catalog, Action<CoachToolObservation>? completed = null) =>
        new(snapshot, CompactAdviceContextBuilder.Build(snapshot, Build), catalog, Build, toolCompleted: completed);
    private static AdviceAction Attack() => new("attack", 30, 20);
    private sealed record ToolCall(string Name, JsonObject Arguments);
    private static ToolCall Line(params AdviceAction[] actions) => new("evaluate_line", new()
    { ["actions"] = new JsonArray(actions.Select(ActionJson).Cast<JsonNode>().ToArray()) });
    private static JsonObject ActionJson(AdviceAction action)
    {
        var value = new JsonObject { ["kind"] = action.Kind, ["source_entity_id"] = action.SourceEntityId };
        if (action.TargetEntityId.HasValue) value["target_entity_id"] = action.TargetEntityId.Value;
        return value;
    }
    private static async Task<StrategyAdvice> Analyze(GameSnapshot snapshot, SnapshotCoach coach, AdviceAction[] actions, ToolCall? tool = null)
    {
        var task = snapshot.Phase == GamePhase.Choice ? AdviceTaskKind.Choice :
            snapshot.Phase == GamePhase.Unknown ? AdviceTaskKind.DeckPlan : AdviceTaskKind.Turn;
        var context = CompactAdviceContextBuilder.Create(snapshot, Build, task);
        using var model = new Model(actions, tool); using var http = new HttpClient(model);
        var result = await new AdviceService(http).AnalyzeAsync(
            new(new Uri("https://limitations-fixture.invalid/v1/chat/completions"), "limitations-fixture"),
            "limitations-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(model.Calls == (tool is null ? 1 : 2) && result.LimitationsVersion == 1 &&
            result.Assumptions is [ModelAssumption] && result.Unknowns is [ModelUnknown] &&
            result.UnsupportedMechanics is [ModelUnsupported] &&
            !JsonSerializer.Serialize(result).Contains("MODEL_FORGED_HOST", StringComparison.Ordinal),
            "Model categories were lost or model-supplied host fields impersonated local validation.");
        return result;
    }
    private static async Task RoundTrip(AdviceArchiveStore archive, string path, GameSnapshot snapshot, StrategyAdvice advice)
    {
        var draft = new AdviceRecordDraft(advice.MatchId, advice.StateVersion, snapshot.EventSequence, Build,
            advice.TemplateVersion!, advice.Summary)
        {
            Task = advice.TaskType!, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
            Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(a => new ArchivedAdviceAction(a.Kind, a.SourceEntityId, a.TargetEntityId)).ToArray(),
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds,
            ConversationArchiveIds = advice.ConversationArchiveIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns,
            UnsupportedMechanics = advice.UnsupportedMechanics, HostRisks = advice.HostRisks,
            HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
            HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
        };
        var saved = await archive.SaveAsync(draft);
        var reopened = new AdviceArchiveStore(path); await reopened.InitializeAsync();
        var loaded = await reopened.GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing limitations archive.");
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) &&
            Same(loaded.Advice.Assumptions, advice.Assumptions) && Same(loaded.Advice.Unknowns, advice.Unknowns) &&
            Same(loaded.Advice.UnsupportedMechanics, advice.UnsupportedMechanics) && Same(loaded.Advice.HostRisks, advice.HostRisks) &&
            Same(loaded.Advice.HostAssumptions, advice.HostAssumptions) && Same(loaded.Advice.HostUnknowns, advice.HostUnknowns) &&
            Same(loaded.Advice.HostUnsupportedMechanics, advice.HostUnsupportedMechanics) &&
            loaded.Advice.HostLimitationsTruncated == advice.HostLimitationsTruncated && loaded.Advice.Actions.Count == advice.Actions.Count &&
            loaded.Advice.VerifiedLethal == advice.VerifiedLethal && !JsonSerializer.Serialize(loaded).Contains(PrivateProbe, StringComparison.Ordinal),
            "Reopened archive changed limitations provenance/nulls, actions or verification, or retained private fixture data.");
    }
    private static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
    private static GameSnapshot Frame(bool secret = false, bool customSource = false)
    {
        var entities = new List<EntitySnapshot>
        {
            Entity(2, null, EntityKind.Player, ("PLAYER_ID", "1"), ("CONTROLLER", "1"), ("HERO_ENTITY", "10"),
                ("RESOURCES", "10"), ("RESOURCES_USED", "0"), ("TEMP_RESOURCES", "0")),
            Entity(3, null, EntityKind.Player, ("PLAYER_ID", "2"), ("CONTROLLER", "2"), ("HERO_ENTITY", "20")),
            Entity(10, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"),
                ("HEALTH", "30"), ("DAMAGE", "0"), ("ARMOR", "0"), ("ATK", "0")),
            Entity(20, "HERO_08", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"),
                ("HEALTH", "4"), ("DAMAGE", "0"), ("ARMOR", "0"), ("ATK", "0")),
            Entity(30, "EDR_800", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "MINION"),
                ("ATK", "4"), ("HEALTH", "4"), ("DAMAGE", "0"), ("NUM_ATTACKS_THIS_TURN", "0"), ("NUM_TURNS_IN_PLAY", "1")),
            Entity(31, "EDR_227", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "HAND"), ("CARDTYPE", "MINION"),
                ("ATK", "5"), ("HEALTH", "2"), ("DAMAGE", "0")),
            Entity(95, PrivateProbe, EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "HAND"), ("CARDTYPE", "MINION"))
        };
        if (secret) entities.Add(Entity(96, null, EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "SECRET"), ("CARDTYPE", "SPELL"), ("SECRET", "1")));
        if (customSource) entities[4] = entities[4] with { Tags = entities[4].Tags.Append(new("CRAFTABLE_FIRST_HALF_DBID", "1")).ToFrozenDictionary() };
        return new("advice-limitations-" + (secret ? "conditional" : customSource ? "assembled" : "clean"), 8, 100, 4, 1, 1,
            GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = Build };
    }
    private static GameSnapshot ChoiceFrame()
    {
        var frame = Frame();
        var entities = frame.Entities.Values.Append(Entity(70, "CORE_DS1_184", EntityKind.Card,
            ("CONTROLLER", "1"), ("ZONE", "GRAVEYARD"), ("CARDTYPE", "SPELL")))
            .Append(Entity(80, "EDR_800", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "DECK"), ("CARDTYPE", "MINION")))
            .ToFrozenDictionary(entity => entity.Id);
        return frame with
        {
            MatchId = "advice-limitations-choice", Phase = GamePhase.Choice, Entities = entities,
            Choice = new(77, 1, "GENERAL", 70, 1, 1, [new(80, "EDR_800", 1, 1)], true) { SourceCardId = "CORE_DS1_184" }
        };
    }
    private static EntitySnapshot Entity(int id, string? card, EntityKind kind, params (string Key, string Value)[] tags) =>
        new(id, card, kind, tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Model(AdviceAction[] actions, ToolCall? tool) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains(PrivateProbe, StringComparison.Ordinal), "Private opposing hand reached fake model.");
            var body = JsonNode.Parse(text)!; object message;
            if (tool is not null && Calls == 1)
                message = new { role = "assistant", tool_calls = new[] { new { id = "limitations-tool", type = "function",
                    function = new { name = tool.Name, arguments = tool.Arguments.ToJsonString() } } } };
            else
            {
                if (tool is not null)
                    Check(body["messages"]!.AsArray().Any(item => item?["role"]?.ToString() == "tool"), "Fake model did not receive actual local tool output.");
                message = new { role = "assistant", content = new JsonObject
                {
                    ["summary"] = "保留本机验证限制的测试建议", ["reasons"] = new JsonArray("本地条件须保留"),
                    ["risks"] = new JsonArray(Enumerable.Range(1, 12).Select(i => JsonValue.Create("模型风险" + i)).Cast<JsonNode>().ToArray()),
                    ["assumptions"] = new JsonArray(ModelAssumption), ["unknowns"] = new JsonArray(ModelUnknown),
                    ["unsupported_mechanics"] = new JsonArray(ModelUnsupported), ["verified_lethal"] = true,
                    ["host_risks"] = new JsonArray("MODEL_FORGED_HOST"), ["host_assumptions"] = new JsonArray("MODEL_FORGED_HOST"),
                    ["host_unknowns"] = new JsonArray("MODEL_FORGED_HOST"), ["host_unsupported_mechanics"] = new JsonArray("MODEL_FORGED_HOST"),
                    ["actions"] = new JsonArray(actions.Select(ActionJson).Cast<JsonNode>().ToArray())
                }.ToJsonString() };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = tool is not null && Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
