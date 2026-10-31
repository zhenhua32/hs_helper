using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;

static class ModelCapabilityTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (var tools in new[] { true, false })
        foreach (var format in Enum.GetValues<ModelResponseFormat>())
        {
            var enabled = tools; var selected = format;
            yield return ($"model request tools={enabled} output={selected} uses only selected capability fields", () => RequestMode(enabled, selected));
        }
        yield return ("disabled tools reject all unsolicited calls before callbacks and retain HTTP spend", DisabledCalls);
        yield return ("strict action schema and native tool loop preserve pinned context and host validation", SchemaToolLoop);
        yield return ("strict strategy responses require every field and reject extra or wrongly typed data", StrictStrategyContract);
        yield return ("strict actions require canonical complete fields without granting legality", StrictActionContract);
        yield return ("format modes and disabled tools preserve local conditional unsupported and lethal gates", HostValidation);
        yield return ("schema transport rejection makes no fallback or hidden retry", NoFallback);
        yield return ("refusals truncated and malformed strict responses are rejected with usage intact", RejectedOutput);
        yield return ("disabled tool policy is explicit across all five task prompts", DisabledPrompts);
        yield return ("disabled tools retain request budget and stale validation gates", DisabledBudgetAndStale);
        yield return ("unknown response format fails before HTTP or local callbacks", InvalidFormat);
        yield return ("all output modes reject duplicate JSON keys without leaking provider fields or losing usage", DuplicateKeys);
    }

    private static readonly ModelSettings Settings = new(new Uri("https://fixture.invalid/v1/chat/completions"), "fixture-model", MaxToolRounds: 4);
    private static readonly AdviceContext Context = new("capability-match", 7, "turn", new JsonObject { ["turn"] = 4 })
    { CardBuild = "255790", EventSequence = 40, KnowledgeAsOf = DateTimeOffset.Parse("2026-10-01T01:02:03Z") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static Task<StrategyAdvice> Analyze(FakeHandler handler, ModelSettings? settings = null, AgentCapabilities? capabilities = null,
        Func<string, JsonObject, CancellationToken, Task<JsonNode>>? execute = null, Func<bool>? current = null, AdviceContext? context = null)
        => new AdviceService(new HttpClient(handler)).AnalyzeAsync(settings ?? Settings, "fake-private-key", context ?? Context,
            execute ?? ((_, _, _) => Task.FromResult<JsonNode>(new JsonObject())), current ?? (() => true), capabilities: capabilities);
    private static JsonObject Advice(bool actions = false)
    {
        var result = new JsonObject
        {
            ["summary"] = "先核对当前资源", ["reasons"] = new JsonArray("当前固定局面"), ["alternative"] = null,
            ["risks"] = new JsonArray(), ["assumptions"] = new JsonArray(), ["unknowns"] = new JsonArray("未知的对手手牌"),
            ["unsupported_mechanics"] = new JsonArray()
        };
        if (actions) result["actions"] = new JsonArray();
        return result;
    }
    private static JsonObject WithEndTurn()
    {
        var advice = Advice(true);
        advice["actions"] = new JsonArray(new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2, ["target_entity_id"] = null });
        return advice;
    }
    private static string Envelope(JsonObject message, string finish = "stop") => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = finish }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 11, ["completion_tokens"] = 7, ["total_tokens"] = 18 }
    }.ToJsonString();
    private static string Final(JsonObject? advice = null) => Envelope(new JsonObject { ["content"] = (advice ?? Advice()).ToJsonString() });
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static AgentToolDefinition Tool() => new("evaluate_line", "检查固定局面候选", new JsonObject
    { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false }, x => x.Count == 0);
    private static JsonObject Call(string name) => new()
    {
        ["id"] = "call-1", ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = name == "get_cards" ? "{\"card_ids\":[\"TEST_CARD\"]}" : "{}" }
    };

    private static async Task RequestMode(bool enabled, ModelResponseFormat format)
    {
        var handler = new FakeHandler((_, _) => Reply(Final()));
        var result = await Analyze(handler, Settings with { EnableTools = enabled, ResponseFormat = format });
        Check(handler.Calls == 1 && result.Verification == "strategy" && result.Usage is [{ ChargedTokens: 18 }], "Final answer changed request count, verification or usage.");
        var request = JsonNode.Parse(handler.Requests.Single().Body)!.AsObject();
        Check(request.ContainsKey("tools") == enabled && request.ContainsKey("tool_choice") == enabled, "Disabled capability fields were sent or enabled fields were omitted.");
        if (enabled) Check(request["tool_choice"]!.GetValue<string>() == "auto", "Enabled request lost its configured rounds.");
        Check(request.ContainsKey("response_format") == (format != ModelResponseFormat.Omitted), "Output mode did not control field omission.");
        if (format == ModelResponseFormat.JsonObject)
            Check(request["response_format"]!.AsObject().Count == 1 && request["response_format"]!["type"]!.GetValue<string>() == "json_object", "JSON object request added schema or wrong mode.");
        if (format == ModelResponseFormat.JsonSchema) AssertSchema(request, false);
        Check(!handler.Requests.Single().Body.Contains("fake-private-key", StringComparison.Ordinal), "Request body included a credential.");
    }

    private static void AssertSchema(JsonObject request, bool actions)
    {
        var format = request["response_format"]!.AsObject();
        var definition = format["json_schema"]!.AsObject();
        Check(format["type"]!.GetValue<string>() == "json_schema" && definition["strict"]!.GetValue<bool>(), "Strict schema transport contract missing.");
        Check(!string.IsNullOrWhiteSpace(definition["name"]!.GetValue<string>()), "Schema name missing.");
        var schema = definition["schema"]!.AsObject();
        var properties = schema["properties"]!.AsObject();
        string[] expected = ["summary", "reasons", "alternative", "risks", "assumptions", "unknowns", "unsupported_mechanics"];
        Check(properties.Select(x => x.Key).Order().SequenceEqual((actions ? expected.Append("actions") : expected).Order()), "Schema exposes host fields or omitted model fields.");
        AssertClosed(schema);
        Check(properties["alternative"]!["type"]!.AsArray().Any(x => x!.GetValue<string>() == "null"), "Alternative cannot explicitly be null.");
        if (actions)
        {
            var item = properties["actions"]!["items"]!.AsObject();
            var action = item["properties"]!.AsObject();
            Check(action.Select(x => x.Key).Order().SequenceEqual(new[] { "kind", "source_entity_id", "target_entity_id" }.Order()), "Action fields are not canonical.");
            Check(action["target_entity_id"]!["type"]!.AsArray().Any(x => x!.GetValue<string>() == "null"), "Target cannot explicitly be null.");
            Check(action["kind"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["play", "attack", "hero_power", "end_turn", "choose"]), "Action aliases or unsupported operations were advertised.");
        }
    }
    private static void AssertClosed(JsonObject schema)
    {
        if (schema["properties"] is JsonObject fields)
        {
            Check(schema["type"]!.GetValue<string>() == "object" && !schema["additionalProperties"]!.GetValue<bool>(), "Nested object allows undeclared fields.");
            Check(schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).Order().SequenceEqual(fields.Select(x => x.Key).Order()), "Not all object fields are required.");
            foreach (var property in fields) AssertClosed(property.Value!.AsObject());
        }
        if (schema["items"] is JsonObject item) AssertClosed(item);
    }

    private static async Task DisabledCalls()
    {
        foreach (var name in new[] { "get_snapshot", "get_cards", "evaluate_line", "unregistered" })
        {
            var executed = 0; var observed = 0; var budget = new AnalysisBudget(); var usage = new List<ModelUsage>();
            var handler = new FakeHandler((_, _) => Reply(Envelope(new JsonObject { ["tool_calls"] = new JsonArray(Call(name)) })));
            var error = await Throws<InvalidDataException>(() => Analyze(handler, Settings with { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema },
                new() { Tools = [Tool()], Budget = budget, OnUsage = usage.Add, OnToolInvocation = _ => observed++ },
                (_, _, _) => { executed++; return Task.FromResult<JsonNode>(new JsonObject()); }));
            Check(handler.Calls == 1 && executed == 0 && observed == 0, "Disabled tools reached a callback or another HTTP request.");
            Check(usage is [{ Success: true, ChargedTokens: 18 }] && budget.Snapshot(Context.MatchId) is { CompletedRequests: 1, ChargedTokens: 18, ReservedTokens: 0, InFlightRequests: 0 }, "Rejected protocol response lost successful HTTP spend.");
            Check(!error.ToString().Contains("fake-private-key", StringComparison.Ordinal), "Protocol rejection exposed provider content.");
        }
        var message = new JsonObject { ["content"] = Advice().ToJsonString(), ["tool_calls"] = new JsonArray() };
        await Analyze(new FakeHandler((_, _) => Reply(Envelope(message))), Settings with { EnableTools = false });
    }

    private static async Task SchemaToolLoop()
    {
        var invoked = 0; var validated = 0; var observations = new List<AgentToolInvocation>();
        var handler = new FakeHandler((round, body) =>
        {
            var request = JsonNode.Parse(body)!.AsObject(); AssertSchema(request, true);
            Check(request["tool_choice"]!.GetValue<string>() == (round == 1 ? "auto" : "none"), "Schema mode altered final tool-round gating.");
            if (round == 1) return Reply(Envelope(new JsonObject { ["tool_calls"] = new JsonArray(Call("get_cards")) }));
            Check(body.Contains("pinned-effect", StringComparison.Ordinal) && body.Contains("255790", StringComparison.Ordinal), "Next request lost pinned tool evidence or context.");
            return Reply(Final(WithEndTurn()));
        });
        var advice = await Analyze(handler, Settings with { MaxToolRounds = 1, ResponseFormat = ModelResponseFormat.JsonSchema },
            new() { OnToolInvocation = observations.Add, ValidateActions = (actions, _) =>
            {
                validated++; Check(actions is [{ Kind: "end_turn", SourceEntityId: 2, TargetEntityId: null }], "Explicit null target or canonical action was changed.");
                return Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true, false));
            } }, (_, _, _) => { invoked++; return Task.FromResult<JsonNode>(new JsonObject { ["effect"] = "pinned-effect" }); });
        Check(handler.Calls == 2 && invoked == 1 && validated == 1 && advice.Verification == "verified" && !advice.VerifiedLethal, "Schema promoted formatting to a lethal claim or bypassed callbacks.");
        Check(observations is [{ MatchId: "capability-match", StateVersion: 7, EventSequence: 40 }] && observations[0].KnowledgeAsOf == Context.KnowledgeAsOf, "Tool audit lost fixed historical scope.");
    }

    private static async Task StrictStrategyContract()
    {
        var invalid = new List<JsonObject>();
        foreach (var name in Advice().Select(x => x.Key)) { var item = Advice(); item.Remove(name); invalid.Add(item); }
        var extra = Advice(); extra["HostUnknowns"] = new JsonArray(); invalid.Add(extra);
        var action = Advice(true); invalid.Add(action);
        var wrong = Advice(); wrong["alternative"] = 1; invalid.Add(wrong);
        wrong = Advice(); wrong["summary"] = null; invalid.Add(wrong);
        wrong = Advice(); wrong["risks"] = null; invalid.Add(wrong);
        wrong = Advice(); wrong["reasons"] = new JsonArray((JsonNode?)null); invalid.Add(wrong);
        wrong = Advice(); wrong["unknowns"] = new JsonArray(Enumerable.Range(0, 13).Select(x => (JsonNode?)JsonValue.Create("unknown-" + x)).ToArray()); invalid.Add(wrong);
        foreach (var item in invalid)
        {
            var handler = new FakeHandler((_, _) => Reply(Final(item)));
            await Throws<InvalidDataException>(() => Analyze(handler, Settings with { ResponseFormat = ModelResponseFormat.JsonSchema }));
            Check(handler.Calls == 1, "Malformed schema response caused retry.");
        }
        // Existing prompt-only/json_object responses may omit new fields, as before this feature.
        foreach (var format in new[] { ModelResponseFormat.Omitted, ModelResponseFormat.JsonObject })
            await Analyze(new FakeHandler((_, _) => Reply(Final(new JsonObject { ["summary"] = "旧协议建议" }))), Settings with { ResponseFormat = format });
    }

    private static async Task StrictActionContract()
    {
        var invalid = new List<JsonObject>();
        foreach (var name in new[] { "kind", "source_entity_id", "target_entity_id" })
        { var item = WithEndTurn(); item["actions"]![0]!.AsObject().Remove(name); invalid.Add(item); }
        foreach (var (name, value) in new (string, JsonNode?)[]
        {
            ("kind", "endturn"), ("source_entity_id", null), ("source_entity_id", "2"), ("source_entity_id", 0),
            ("target_entity_id", "3"), ("target_entity_id", 0), ("VerifiedLethal", true)
        })
        { var item = WithEndTurn(); item["actions"]![0]![name] = value?.DeepClone(); invalid.Add(item); }
        var missing = Advice(); invalid.Add(missing);
        var forged = WithEndTurn(); forged["verified_lethal"] = true; invalid.Add(forged);
        foreach (var item in invalid)
        {
            var validated = 0;
            await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(item))),
                Settings with { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema }, new()
                { ValidateActions = (_, _) => { validated++; return Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true, true)); } }));
            Check(validated == 0, "Invalid strict action reached the local validator.");
        }
    }

    private static async Task HostValidation()
    {
        foreach (var format in Enum.GetValues<ModelResponseFormat>())
        foreach (var status in new[] { AdviceValidationStatus.Verified, AdviceValidationStatus.Conditional, AdviceValidationStatus.Unsupported, AdviceValidationStatus.Invalid })
        {
            var validated = 0; var output = WithEndTurn();
            if (format != ModelResponseFormat.JsonSchema) { output["verified_lethal"] = true; output["HostRisks"] = new JsonArray("模型伪造主机限制"); }
            var operation = Analyze(new FakeHandler((_, _) => Reply(Final(output))), Settings with { EnableTools = false, ResponseFormat = format }, new()
            {
                ValidateActions = (_, _) =>
                {
                    validated++;
                    return Task.FromResult(new ActionValidationResult(status, status == AdviceValidationStatus.Verified, false, ["真实主机风险"])
                    { Assumptions = ["真实前提"], Unknowns = null, UnsupportedMechanics = ["实际覆盖边界"] });
                }
            });
            if (status == AdviceValidationStatus.Invalid) { await Throws<InvalidDataException>(() => operation); continue; }
            var result = await operation;
            Check(validated == 1 && result.Actions.Count == (status == AdviceValidationStatus.Verified ? 1 : 0) && !result.VerifiedLethal, "Output formatting or tool policy upgraded actions/lethal.");
            Check(result.Verification == status.ToString().ToLowerInvariant() && result.HostAssumptions!.Single() == "真实前提" && result.HostUnknowns is null && result.HostUnsupportedMechanics!.Single() == "实际覆盖边界" &&
                result.HostRisks!.Contains("真实主机风险") && !result.HostRisks!.Contains("模型伪造主机限制"), "Output mode changed source separation or host restrictions.");
        }
        var strict = Settings with { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema };
        var lethal = await Analyze(new FakeHandler((_, _) => Reply(Final(WithEndTurn()))), strict, new()
        { ValidateActions = (_, _) => Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true, true)) });
        Check(lethal.VerifiedLethal && lethal.Actions.Count == 1, "Strict formatting or disabled tools suppressed an actual host-verified result.");
        await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(WithEndTurn()))), strict, new()
        { ValidateActions = (_, _) => Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, false, true)) }));
    }

    private static async Task NoFallback()
    {
        foreach (var enabled in new[] { true, false })
        {
            var usage = new List<ModelUsage>(); var budget = new AnalysisBudget();
            var handler = new FakeHandler((_, _) => Reply("provider says fake-private-key", HttpStatusCode.BadRequest));
            var error = await Throws<HttpRequestException>(() => Analyze(handler, Settings with { EnableTools = enabled, ResponseFormat = ModelResponseFormat.JsonSchema }, new() { OnUsage = usage.Add, Budget = budget }));
            Check(handler.Calls == 1 && error.StatusCode == HttpStatusCode.BadRequest && !error.ToString().Contains("fake-private-key", StringComparison.Ordinal), "Schema rejection retried, downgraded or echoed provider body.");
            Check(usage is [{ Success: false, ChargedTokens: 0 }] && budget.Snapshot(Context.MatchId) is { FailedRequests: 1, ChargedTokens: 0, ReservedTokens: 0 }, "HTTP failure did not retain request accounting.");
        }
    }

    private static async Task RejectedOutput()
    {
        var outputs = new List<string>
        {
            Envelope(new JsonObject { ["content"] = Advice().ToJsonString(), ["refusal"] = "fake-private-key" }),
            Envelope(new JsonObject { ["content"] = Advice().ToJsonString(), ["refusal"] = new JsonObject() }),
            Envelope(new JsonObject { ["content"] = Advice().ToJsonString() }, "length"),
            Envelope(new JsonObject { ["content"] = Advice().ToJsonString() }, "content_filter"),
            Envelope(new JsonObject { ["content"] = "fake-private-key invalid JSON" }),
            Envelope(new JsonObject { ["content"] = "{\"fake-private-key\":1,\"fake-private-key\":2}" })
        };
        foreach (var body in outputs)
        {
            var usage = new List<ModelUsage>(); var handler = new FakeHandler((_, _) => Reply(body));
            var error = await Throws<InvalidDataException>(() => Analyze(handler, Settings with { ResponseFormat = ModelResponseFormat.JsonSchema }, new() { OnUsage = usage.Add }));
            Check(handler.Calls == 1 && usage is [{ Success: true, ChargedTokens: 18 }] && !error.ToString().Contains("fake-private-key", StringComparison.Ordinal), "Rejected strict output produced retries, leaked body or lost usage.");
        }
    }

    private static async Task DisabledPrompts()
    {
        foreach (var kind in Enum.GetValues<AdviceTaskKind>())
        foreach (var actions in new[] { true, false })
        {
            var context = Context with { TaskKind = kind, Snapshot = new JsonObject
            { ["local_player_id"] = 1, ["choice"] = new JsonObject { ["confirmed"] = true, ["player_id"] = 1, ["options"] = new JsonArray("visible") } } };
            var handler = new FakeHandler((_, body) =>
            {
                var system = JsonNode.Parse(body)!["messages"]![0]!["content"]!.GetValue<string>();
                Check(system.Contains("本次模型工具已禁用", StringComparison.Ordinal) && system.Contains("不得调用工具或编造工具结果", StringComparison.Ordinal) &&
                    !system.Contains("缺失卡牌效果时调用get_cards", StringComparison.Ordinal) && !system.Contains("卡牌效果须查get_cards", StringComparison.Ordinal), "Disabled policy still requires unavailable card tools.");
                return Reply(Final(Advice(actions)));
            });
            await Analyze(handler, Settings with { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema },
                actions ? new() { ValidateActions = (_, _) => throw new Exception("No actions proposed.") } : null, context: context);
        }
    }

    private static async Task DisabledBudgetAndStale()
    {
        var handler = new FakeHandler((_, _) => Reply(Final()));
        var settings = Settings with { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema };
        var budget = new AnalysisBudget(new(MaxRequestsPerMatch: 1)); var capabilities = new AgentCapabilities { Budget = budget };
        await Analyze(handler, settings, capabilities);
        await Throws<AnalysisBudgetExceededException>(() => Analyze(handler, settings, capabilities));
        Check(handler.Calls == 1 && budget.Snapshot(Context.MatchId).ChargedTokens == 18, "No-tools mode bypassed a saved request budget.");
        var current = true; var usage = new List<ModelUsage>();
        await Throws<OperationCanceledException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(WithEndTurn()))), settings, new()
        {
            OnUsage = usage.Add,
            ValidateActions = (_, _) => { current = false; return Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true, true)); }
        }, current: () => current));
        Check(usage is [{ Success: true, ChargedTokens: 18 }], "Stale validation lost HTTP use.");
    }

    private static async Task InvalidFormat()
    {
        var handler = new FakeHandler((_, _) => Reply(Final()));
        await Throws<ArgumentException>(() => Analyze(handler, Settings with { ResponseFormat = (ModelResponseFormat)99 }));
        Check(handler.Calls == 0, "Invalid output format reached HTTP.");
    }

    private static async Task DuplicateKeys()
    {
        const string fakeField = "fake-provider-private-field";
        var duplicate = $"\"{fakeField}\":1,\"{fakeField}\":2";
        foreach (var format in Enum.GetValues<ModelResponseFormat>())
        foreach (var shape in new[] { "root", "action", "response", "response_nested", "arguments" })
        {
            var content = WithEndTurn().ToJsonString();
            if (shape == "root") content = content[..^1] + "," + duplicate + "}";
            if (shape == "action") content = content.Replace("\"target_entity_id\":null", "\"target_entity_id\":null," + duplicate, StringComparison.Ordinal);
            var message = new JsonObject { ["content"] = content };
            if (shape == "arguments")
            {
                var call = Call("get_snapshot"); call["function"]!["arguments"] = "{" + duplicate + "}";
                message = new JsonObject { ["tool_calls"] = new JsonArray(call) };
            }
            var body = Envelope(message);
            if (shape == "response") body = body[..^1] + "," + duplicate + "}";
            if (shape == "response_nested") body = body.Replace("\"prompt_tokens\":11", "\"prompt_tokens\":11," + duplicate, StringComparison.Ordinal);
            var handler = new FakeHandler((_, _) => Reply(body));
            var usage = new List<ModelUsage>(); var budget = new AnalysisBudget(); var validated = 0; var invoked = 0;
            var error = await Throws<InvalidDataException>(() => Analyze(handler, Settings with { ResponseFormat = format }, new()
            {
                Budget = budget, OnUsage = usage.Add, OnToolInvocation = _ => invoked++,
                ValidateActions = (_, _) => { validated++; return Task.FromResult(new ActionValidationResult(AdviceValidationStatus.Verified, true, true)); }
            }, (_, _, _) => { invoked++; return Task.FromResult<JsonNode>(new JsonObject()); }));
            Check(handler.Calls == 1 && validated == 0 && invoked == 0 && !error.ToString().Contains(fakeField, StringComparison.Ordinal),
                $"Duplicate {shape} key leaked, retried or reached a callback in {format}.");
            var charged = budget.Snapshot(Context.MatchId);
            Check(usage is [{ Success: true }] && charged is { CompletedRequests: 1, ReservedTokens: 0, InFlightRequests: 0 } && charged.ChargedTokens == usage[0].ChargedTokens,
                "Invalid JSON response bypassed successful HTTP accounting.");
            if (shape is "response" or "response_nested")
                Check(usage[0].ConservativeCharge && usage[0].ChargedTokens > Settings.MaxOutputTokens && usage[0].TotalTokens is null,
                    "Unusable response envelope was allowed to provide authoritative usage or escaped conservative billing.");
            else Check(!usage[0].ConservativeCharge && usage[0].ChargedTokens == 18, "Duplicate content/arguments discarded valid reported usage.");
        }
    }
}
