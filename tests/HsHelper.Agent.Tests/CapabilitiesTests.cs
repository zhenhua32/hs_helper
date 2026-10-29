using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;

static class CapabilitiesTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("registered tools use explicit allowlist and cloned schemas", RegisteredTools);
        yield return ("custom argument validators run before execution", RejectCustomArguments);
        yield return ("duplicate and reserved custom tools fail before HTTP", InvalidRegistration);
        yield return ("only locally verified legal actions and lethal are exposed", VerifiedActions);
        yield return ("conditional and unsupported actions are removed", UnverifiedActions);
        yield return ("invalid and illegal actions are rejected", InvalidActions);
        yield return ("action aliases cannot smuggle conflicting entity IDs", MalformedActions);
        yield return ("model verification claims do not grant verified lethal", ModelClaims);
        yield return ("custom tools without rule validation retain strategy restriction", ToolsDoNotEnableActions);
        yield return ("state changing in local action validator discards advice", StaleValidation);
        yield return ("HTTP usage and timings are reported for each round", UsagePerRound);
        yield return ("failed HTTP releases tokens while counting request", FailedBudget);
        yield return ("successful HTTP with missing usage conservatively charges", MissingUsageBudget);
        yield return ("per-match request budget prevents another HTTP round", RequestBudget);
        yield return ("reservations enforce concurrent and aggregate token limits", AtomicBudget);
        yield return ("seeded budgets and oversized actual usage cannot wrap counters", SeedAndOverflow);
        yield return ("local callback failures cannot echo secrets and retain spent usage", SafeCallbacks);
        yield return ("stale or malformed successful responses retain charged usage", ChargedDiscard);
    }

    private static readonly ModelSettings Settings = new(new Uri("https://model.example/v1/chat/completions"), "test-model");
    private static readonly AdviceContext Context = new("budget-match", 7, "turn", new JsonObject { ["turn"] = 4 });
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T ex) { return ex; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static Task<StrategyAdvice> Analyze(FakeHandler handler, AgentCapabilities? capabilities = null,
        Func<string, JsonObject, CancellationToken, Task<JsonNode>>? execute = null, Func<bool>? current = null)
        => new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings, "test-private-key", Context,
            execute ?? ((_, _, _) => Task.FromResult<JsonNode>(new JsonObject())), current ?? (() => true), capabilities: capabilities);
    private static JsonObject Advice(string actions = "[]") => new()
    {
        ["summary"] = "先处理场面", ["reasons"] = new JsonArray("规则计算"), ["risks"] = new JsonArray(),
        ["actions"] = JsonNode.Parse(actions)
    };
    private static string Final(JsonObject? advice = null, long? input = null, long? output = null)
        => Envelope(new JsonObject { ["content"] = (advice ?? Advice()).ToJsonString() }, input, output);
    private static string Calls(string name, string args = "{}", long? input = null, long? output = null)
        => Envelope(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
        { ["id"] = "tool1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } }) }, input, output);
    private static string Envelope(JsonObject message, long? input, long? output)
    {
        var body = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = "stop" }) };
        if (input.HasValue || output.HasValue)
            body["usage"] = new JsonObject { ["prompt_tokens"] = input, ["completion_tokens"] = output, ["total_tokens"] = input + output };
        return body.ToJsonString();
    }
    private static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static AgentToolDefinition Tool(string name = "evaluate_line", Func<JsonObject, bool>? validate = null)
        => new(name, "在当前固定局面验证候选方案", JsonNode.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")!.AsObject(), validate ?? (args => args.Count == 0));
    private static AgentCapabilities Validation(AdviceValidationStatus status, bool legal = true, bool lethal = true)
        => new() { ValidateActions = (_, _) => Task.FromResult(new ActionValidationResult(status, legal, lethal, ["本地质量说明"])) };
    private const string Attack = """[{"kind":"attack","source_entity_id":10,"target_entity_id":20}]""";

    private static async Task RegisteredTools()
    {
        var names = new[] { "evaluate_line", "find_lethal", "calculate_draw_odds", "get_recent_events", "get_personal_stats" };
        foreach (var name in names)
        {
            var invoked = 0;
            var definition = Tool(name);
            var handler = new FakeHandler((round, body) =>
            {
                if (round == 1)
                {
                    var tools = JsonNode.Parse(body)!["tools"]!.AsArray();
                    Check(tools.Count == 3 && tools.Any(t => t!["function"]!["name"]!.GetValue<string>() == name), "Missing registered tool schema.");
                    definition.Parameters["type"] = "array"; // mutation after registration cannot change pinned schema
                    return Reply(Calls(name));
                }
                Check(JsonNode.Parse(body)!["tools"]![2]!["function"]!["parameters"]!["type"]!.GetValue<string>() == "object", "Tool schema was not pinned.");
                return Reply(Final());
            });
            await Analyze(handler, new() { Tools = [definition] }, (called, _, _) =>
            { Check(called == name, "Wrong custom tool."); invoked++; return Task.FromResult<JsonNode>(new JsonObject { ["verified"] = true }); });
            Check(invoked == 1, "Registered tool was not executed once.");
        }
    }
    private static async Task RejectCustomArguments()
    {
        foreach (var validator in new Func<JsonObject, bool>[] { _ => false, _ => throw new Exception("test-private-key") })
        {
            var invoked = false;
            var handler = new FakeHandler((_, _) => Reply(Calls("evaluate_line")));
            var error = await Throws<InvalidDataException>(() => Analyze(handler, new() { Tools = [Tool(validate: validator)] }, (_, _, _) =>
            { invoked = true; return Task.FromResult<JsonNode>(new JsonObject()); }));
            Check(!invoked && !error.ToString().Contains("test-private-key"), "Invalid tool escaped validation or exposed secret.");
        }
    }
    private static async Task InvalidRegistration()
    {
        foreach (var tools in new IReadOnlyList<AgentToolDefinition>[] { [Tool("get_snapshot")], [Tool(), Tool()], [Tool("bad name")] })
        {
            var handler = new FakeHandler((_, _) => Reply(Final()));
            await Throws<ArgumentException>(() => Analyze(handler, new() { Tools = tools }));
            Check(handler.Calls == 0, "Invalid registry reached HTTP.");
        }
    }
    private static async Task VerifiedActions()
    {
        IReadOnlyList<AdviceAction>? captured = null;
        var advice = Advice(Attack); advice["verified_lethal"] = false;
        var result = await Analyze(new FakeHandler((_, _) => Reply(Final(advice))), new()
        { ValidateActions = (actions, _) => { captured = actions; return Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true, true)); } });
        Check(captured is [{ Kind: "attack", SourceEntityId: 10, TargetEntityId: 20 }] && result.Actions.Count == 1 && result.Verification == "verified" && result.VerifiedLethal, "Trusted validator result lost.");
        Check(result.Actions is not List<AdviceAction>, "Returned action list is mutable.");
        var normalized = await Analyze(new FakeHandler((_, _) => Reply(Final(Advice("""[{"Kind":"PlayCard","SourceEntityId":10},{"kind":"hero-power","sourceEntityId":11},{"kind":"end_turn"}]""")))), Validation(AdviceValidationStatus.Verified));
        Check(normalized.Actions.Select(a => a.Kind).SequenceEqual(new[] { "play", "hero_power", "end_turn" }), "Action aliases not normalized.");
    }
    private static async Task UnverifiedActions()
    {
        foreach (var status in new[] { AdviceValidationStatus.Conditional, AdviceValidationStatus.Unsupported })
        {
            var result = await Analyze(new FakeHandler((_, _) => Reply(Final(Advice(Attack)))), Validation(status));
            Check(result.Actions.Count == 0 && !result.VerifiedLethal && result.Risks.Any(r => r.Contains("移除")) && result.Risks.Contains("本地质量说明"), "Unverified actions escaped local gate.");
        }
    }
    private static async Task InvalidActions()
    {
        foreach (var caps in new[] { Validation(AdviceValidationStatus.Invalid), Validation(AdviceValidationStatus.Verified, false), Validation((AdviceValidationStatus)99) })
            await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(Advice(Attack)))), caps));
    }
    private static async Task MalformedActions()
    {
        foreach (var actions in new[] { "{}", """[{"kind":"attack","sourceEntityId":10}]""", """[{"kind":"attack","sourceEntityId":10,"targetEntityId":20,"source_entity_id":11}]""", """[{"kind":"end_turn","sourceEntityId":10}]""", """[{"kind":"click","sourceEntityId":1}]""", """[{"kind":"play","sourceEntityId":-1}]""", """[{"kind":"play","sourceEntityId":10,"verified":true}]""" })
            await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(Advice(actions)))), Validation(AdviceValidationStatus.Verified)));
    }
    private static async Task ModelClaims()
    {
        var advice = Advice(); advice["verified_lethal"] = true; advice["verification"] = "verified";
        var result = await Analyze(new FakeHandler((_, _) => Reply(Final(advice))), Validation(AdviceValidationStatus.Verified));
        Check(!result.VerifiedLethal && result.Verification == "strategy" && result.Actions.Count == 0, "Model promoted its own verification claim.");
    }
    private static async Task ToolsDoNotEnableActions()
        => await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(Advice(Attack)))), new() { Tools = [Tool()] }));
    private static async Task StaleValidation()
    {
        var current = true;
        await Throws<OperationCanceledException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(Advice(Attack)))), new()
        { ValidateActions = (_, _) => { current = false; return Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true)); } }, current: () => current));
    }
    private static async Task UsagePerRound()
    {
        var observed = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var result = await Analyze(new FakeHandler((round, _) => Reply(round == 1 ? Calls("get_snapshot", input: 30, output: 10) : Final(input: 50, output: 15))), new() { Budget = budget, OnUsage = observed.Add });
        Check(result.Usage.Count == 2 && observed.Count == 2 && observed.Select(x => x.TotalTokens).SequenceEqual(new long?[] { 40, 65 }), "Missing per-round usage.");
        Check(observed.All(u => u.Success && u.UsageReported && u.ElapsedMilliseconds >= 0 && u.MatchId == Context.MatchId && u.StateVersion == Context.StateVersion && !u.ConservativeCharge && u.ChargedTokens == u.TotalTokens) && observed.Select(u => u.RequestId).Distinct().Count() == 2, "Usage metadata invalid.");
        Check(budget.Snapshot(Context.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 105, ReservedTokens: 0, InFlightRequests: 0 }, "Usage did not settle budget.");
    }
    private static async Task FailedBudget()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            var usage = new List<ModelUsage>(); var budget = new AnalysisBudget();
            await Throws<HttpRequestException>(() => Analyze(new FakeHandler((_, _) => Reply("secret response", status)), new() { Budget = budget, OnUsage = usage.Add }));
            Check(budget.Snapshot(Context.MatchId) is { RequestsStarted: 1, FailedRequests: 1, ChargedTokens: 0, ReservedTokens: 0, InFlightRequests: 0 } && usage is [{ Success: false, ChargedTokens: 0 }], "Failed request did not release reservation or count request.");
        }
    }
    private static async Task MissingUsageBudget()
    {
        var budget = new AnalysisBudget();
        var result = await Analyze(new FakeHandler((_, _) => Reply(Final())), new() { Budget = budget });
        Check(result.Usage is [{ UsageReported: false, ConservativeCharge: true }] && result.Usage[0].ChargedTokens > Settings.MaxOutputTokens, "Missing usage was free.");
        Check(budget.Snapshot(Context.MatchId).ChargedTokens == result.Usage[0].ChargedTokens && budget.Snapshot(Context.MatchId).ConservativeCharges == 1, "Conservative charge was not retained.");
    }
    private static async Task RequestBudget()
    {
        var budget = new AnalysisBudget(new(MaxRequestsPerMatch: 1));
        var handler = new FakeHandler((_, _) => Reply(Calls("get_snapshot", input: 20, output: 3)));
        var error = await Throws<AnalysisBudgetExceededException>(() => Analyze(handler, new() { Budget = budget }));
        Check(error.Reason == AnalysisBudgetExceededReason.RequestLimit && handler.Calls == 1 && budget.Snapshot(Context.MatchId).ChargedTokens == 23, "Request limit allowed an extra HTTP request.");
    }
    private static async Task AtomicBudget()
    {
        var budget = new AnalysisBudget(new(100, 100, 1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 24).Select(async _ =>
        { await gate.Task; try { return budget.Reserve("race", 60); } catch (AnalysisBudgetExceededException) { return null; } }).ToArray();
        gate.SetResult(); var reservations = await Task.WhenAll(tasks);
        Check(reservations.Count(r => r is not null) == 1 && budget.Snapshot("race") is { RequestsStarted: 1, ReservedTokens: 60, InFlightRequests: 1 }, "Concurrent reservations oversubscribed.");
        reservations.Single(r => r is not null)!.Commit(40);
        using var next = budget.Reserve("race", 60);
        next.Fail(); next.Fail();
        Check(budget.Snapshot("race") is { RequestsStarted: 2, FailedRequests: 1, ChargedTokens: 40, ReservedTokens: 0 }, "Failure release was not idempotent.");
        var error = await Throws<AnalysisBudgetExceededException>(() => Task.Run(() => budget.Reserve("race", 61)));
        Check(error.Reason == AnalysisBudgetExceededReason.TokenLimit, "Token reservation crossed limit.");
        using var otherMatch = budget.Reserve("other-match", 100); // isolation
    }
    private static async Task SeedAndOverflow()
    {
        var budget = new AnalysisBudget(new(5, long.MaxValue, 2)); budget.SeedMatch("persisted", 2, 50);
        using var one = budget.Reserve("persisted", 100); using var two = budget.Reserve("persisted", 100);
        one.Commit(long.MaxValue); two.Commit(long.MaxValue);
        Check(budget.Snapshot("persisted") is { RequestsStarted: 4, ChargedTokens: long.MaxValue, ReservedTokens: 0 }, "Actual usage overflowed or lost persisted spend.");
        var error = await Throws<AnalysisBudgetExceededException>(() => Task.Run(() => budget.Reserve("persisted", 1)));
        Check(error.Reason == AnalysisBudgetExceededReason.TokenLimit, "Overflow reopened budget.");
        await Throws<InvalidOperationException>(() => Task.Run(() => budget.SeedMatch("persisted", 0, 0)));
    }
    private static async Task SafeCallbacks()
    {
        const string secret = "test-private-key";
        var actionError = await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(Advice(Attack)))), new()
        { ValidateActions = (_, _) => throw new InvalidOperationException(secret) }));
        var budget = new AnalysisBudget();
        var usageError = await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(input: 7, output: 2))), new()
        { Budget = budget, OnUsage = _ => throw new InvalidOperationException(secret) }));
        Check(!actionError.ToString().Contains(secret) && !usageError.ToString().Contains(secret) && budget.Snapshot(Context.MatchId).ChargedTokens == 9, "Callback error leaked secret or erased usage.");
    }
    private static async Task ChargedDiscard()
    {
        var budget = new AnalysisBudget(); var changed = false; var observed = new List<ModelUsage>();
        await Throws<OperationCanceledException>(() => Analyze(new FakeHandler((_, _) => { changed = true; return Reply(Final(input: 11, output: 4)); }), new() { Budget = budget, OnUsage = observed.Add }, current: () => !changed));
        Check(observed.Count == 1 && budget.Snapshot(Context.MatchId).ChargedTokens == 15, "Discarded stale response erased spend.");
        await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply("not json")), new() { Budget = budget }));
        Check(budget.Snapshot(Context.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ConservativeCharges: 1 }, "Malformed successful response erased charged request.");
    }
}
