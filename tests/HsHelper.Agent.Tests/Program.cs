using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;

var context = new AdviceContext("fixture", 5, "turn", new JsonObject { ["turn"] = 3 });
var settings = new ModelSettings(new Uri("https://model.example/v1/chat/completions"), "configured-model");
var tests = new (string Name, Func<Task> Run)[]
{
    ("tool round preserves pinned result and keeps key out of payload", HappyPath),
    ("stale snapshot makes zero HTTP or tool calls", StaleBeforeRequest),
    ("in-flight stale response is discarded", StaleAfterResponse),
    ("state changing inside a tool stops before another request", StaleAfterTool),
    ("zero-round budget rejects calls without executing a tool", ZeroRoundBudget),
    ("one-round budget makes exactly two HTTP requests and one tool call", OneRoundBudget),
    ("total budget rejects the ninth tool call before execution", TotalCallBudget),
    ("non-JSON provider output returns a safe format error", NonJsonResponse),
    ("invalid provider envelopes are rejected", InvalidEnvelopes),
    ("non-JSON advice and wrongly typed fields are rejected", InvalidAdvice),
    ("action arrays and objects cannot bypass strategy-only output", RejectActions),
    ("malformed tool parameters never reach the callback", InvalidArguments),
    ("missing and duplicate tool IDs are rejected", InvalidCallIds),
    ("unsupported tool reports limitation without executing it", UnsupportedTool),
    ("HTTP error retains status while keeping body and key private", HttpFailure),
    ("transport error cannot echo API key into diagnostics", NetworkFailure),
    ("already canceled requests never reach HTTP", AlreadyCanceled),
    ("invalid settings fail before HTTP", InvalidSettings),
    ("truncated responses are rejected", TruncatedResponse),
    ("invalid credential fails without echoing credential", InvalidCredential),
}.Concat(CapabilitiesTests.Cases()).Concat(CompactContextTests.Cases()).Concat(ConversationTests.Cases()).Concat(AssembledContextChecks.Cases()).Concat(WeaponContextChecks.Cases()).ToArray();
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} Agent regression checks passed (fake HTTP; no external model calls).");
return failed == 0 ? 0 : 1;

static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
static JsonObject Tool(string id = "c1", string name = "get_snapshot", string args = "{}") => new()
{
    ["id"] = id, ["type"] = "function",
    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
};
static string Calls(params JsonObject[] tools) => new JsonObject
{
    ["choices"] = new JsonArray(new JsonObject
    {
        ["message"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(tools.Select(t => (JsonNode)t).ToArray()) }
    })
}.ToJsonString();
static JsonObject Advice() => new()
{
    ["summary"] = "保留资源", ["reasons"] = new JsonArray("场面压力有限"),
    ["alternative"] = null, ["risks"] = new JsonArray("对手手牌未知")
};
static string Final(string? content = null, string finishReason = "stop") => new JsonObject
{
    ["choices"] = new JsonArray(new JsonObject
    {
        ["finish_reason"] = finishReason,
        ["message"] = new JsonObject { ["content"] = content ?? Advice().ToJsonString() }
    })
}.ToJsonString();
static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
    => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static async Task<T> Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T ex) { return ex; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
Task<StrategyAdvice> Analyze(FakeHandler handler, Func<string, JsonObject, CancellationToken, Task<JsonNode>>? tool = null,
    Func<bool>? current = null, ModelSettings? config = null, string key = "dummy-test-key", CancellationToken token = default)
    => new AdviceService(new HttpClient(handler)).AnalyzeAsync(config ?? settings, key, context,
        tool ?? ((_, _, _) => Task.FromResult<JsonNode>(new JsonObject())), current ?? (() => true), token);

async Task HappyPath()
{
    var handler = new FakeHandler((n, _) => Reply(n == 1 ? Calls(Tool()) : Final()));
    var toolCount = 0;
    var result = await Analyze(handler, (name, args, _) =>
    {
        toolCount++; Check(name == "get_snapshot" && args.Count == 0, "Wrong tool request.");
        return Task.FromResult<JsonNode>(new JsonObject { ["result"] = "fixture-result" });
    });
    Check(result.MatchId == "fixture" && result.StateVersion == 5 && result.Verification == "strategy", "Result lost pinned context.");
    Check(result.Summary == "保留资源" && result.Reasons.Length == 1 && handler.Calls == 2 && toolCount == 1, "Wrong tool flow.");
    Check(handler.Requests[1].Body.Contains("fixture-result") && handler.Requests[1].Body.Contains("tool_call_id"), "Tool result missing.");
    Check(handler.Requests.All(r => r.Authorization == "Bearer dummy-test-key" && !r.Body.Contains("dummy-test-key")), "Credential placement incorrect.");
}
async Task StaleBeforeRequest()
{
    var handler = new FakeHandler((_, _) => Reply(Final())); var invoked = false;
    await Throws<OperationCanceledException>(() => Analyze(handler, (_, _, _) => { invoked = true; return Task.FromResult<JsonNode>(new JsonObject()); }, () => false));
    Check(handler.Calls == 0 && !invoked, "Stale state reached provider or tool.");
}
async Task StaleAfterResponse()
{
    var changed = false;
    var handler = new FakeHandler((_, _) => { changed = true; return Reply(Final()); });
    await Throws<OperationCanceledException>(() => Analyze(handler, current: () => !changed));
    Check(handler.Calls == 1, "Unexpected retry after stale response.");
}
async Task StaleAfterTool()
{
    var changed = false; var invoked = 0;
    var handler = new FakeHandler((_, _) => Reply(Calls(Tool())));
    await Throws<OperationCanceledException>(() => Analyze(handler, (_, _, _) =>
    {
        invoked++; changed = true; return Task.FromResult<JsonNode>(new JsonObject());
    }, () => !changed));
    Check(handler.Calls == 1 && invoked == 1, "Stale tool output caused another request.");
}
async Task ZeroRoundBudget()
{
    var handler = new FakeHandler((_, _) => Reply(Calls(Tool()))); var invoked = 0;
    await Throws<InvalidDataException>(() => Analyze(handler, (_, _, _) =>
    {
        invoked++; return Task.FromResult<JsonNode>(new JsonObject());
    }, config: settings with { MaxToolRounds = 0 }));
    Check(handler.Calls == 1 && invoked == 0, "Zero-round budget executed a tool or retried.");
    Check(JsonNode.Parse(handler.Requests[0].Body)!["tool_choice"]!.GetValue<string>() == "none", "Zero-round request permitted tools.");
}
async Task OneRoundBudget()
{
    var handler = new FakeHandler((n, _) => Reply(Calls(Tool($"c{n}")))); var invoked = 0;
    await Throws<InvalidDataException>(() => Analyze(handler, (_, _, _) =>
    {
        invoked++; return Task.FromResult<JsonNode>(new JsonObject { ["result"] = "round-one" });
    }, config: settings with { MaxToolRounds = 1 }));
    Check(handler.Calls == 2 && invoked == 1, "Round budget did not bound HTTP/tool calls exactly.");
    Check(JsonNode.Parse(handler.Requests[0].Body)!["tool_choice"]!.GetValue<string>() == "auto", "First round disabled tools.");
    Check(JsonNode.Parse(handler.Requests[1].Body)!["tool_choice"]!.GetValue<string>() == "none", "Final round permitted tools.");
    Check(handler.Requests[1].Body.Contains("round-one"), "Final request lost first tool result.");
}
async Task TotalCallBudget()
{
    var handler = new FakeHandler((n, _) => Reply(n == 1
        ? Calls(Enumerable.Range(0, 8).Select(i => Tool($"c{i}")).ToArray()) : Calls(Tool("ninth"))));
    var invoked = 0;
    await Throws<InvalidDataException>(() => Analyze(handler, (_, _, _) =>
    {
        invoked++; return Task.FromResult<JsonNode>(new JsonObject());
    }, config: settings with { MaxToolRounds = 3 }));
    Check(handler.Calls == 2 && invoked == 8, "Total budget was not enforced before ninth call.");
}
async Task NonJsonResponse()
{
    const string secret = "private-test-key";
    var handler = new FakeHandler((_, _) => Reply($"<html>{secret}</html>"));
    var error = await Throws<InvalidDataException>(() => Analyze(handler, key: secret));
    Check(handler.Calls == 1 && !error.ToString().Contains(secret), "Malformed response leaked provider content.");
}
async Task InvalidEnvelopes()
{
    foreach (var body in new[] { "{}", "[]", "{\"choices\":[]}", "{\"choices\":[7]}", "{\"choices\":[{\"message\":3}]}" })
    {
        var handler = new FakeHandler((_, _) => Reply(body));
        await Throws<InvalidDataException>(() => Analyze(handler));
        Check(handler.Calls == 1, "Malformed envelope retried.");
    }
}
async Task InvalidAdvice()
{
    foreach (var text in new[] { "not JSON", "[]", "{}", "{\"summary\":1}", "{\"summary\":\"ok\",\"reasons\":[4]}", "{\"summary\":\"ok\",\"risks\":\"bad\"}" })
        await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(text)))));
}
async Task RejectActions()
{
    foreach (JsonNode actions in new JsonNode[] { new JsonArray(new JsonObject { ["play"] = 10 }), new JsonObject { ["play"] = 10 }, JsonValue.Create("play 10")! })
    {
        var advice = Advice(); advice["actions"] = actions;
        var handler = new FakeHandler((_, _) => Reply(Final(advice.ToJsonString())));
        await Throws<InvalidDataException>(() => Analyze(handler));
        Check(handler.Calls == 1, "Invalid actions retried.");
    }
}
async Task InvalidArguments()
{
    foreach (var (name, args) in new[]
    {
        ("get_snapshot", "[]"), ("get_snapshot", "bad JSON"), ("get_snapshot", "{\"live\":true}"),
        ("get_cards", "{}"), ("get_cards", "{\"card_ids\":\"bad\"}"), ("get_cards", "{\"card_ids\":[2]}"),
        ("get_cards", "{\"card_ids\":[\"\"]}"),
        ("get_cards", new JsonObject { ["card_ids"] = new JsonArray(Enumerable.Range(0, 31).Select(i => (JsonNode)JsonValue.Create($"CARD_{i}")!).ToArray()) }.ToJsonString())
    })
    {
        var invoked = 0;
        var handler = new FakeHandler((_, _) => Reply(Calls(Tool(name: name, args: args))));
        await Throws<InvalidDataException>(() => Analyze(handler, (_, _, _) => { invoked++; return Task.FromResult<JsonNode>(new JsonObject()); }));
        Check(invoked == 0 && handler.Calls == 1, "Malformed arguments reached callback.");
    }
}
async Task InvalidCallIds()
{
    foreach (var calls in new[] { Calls(Tool(id: "")), Calls(Tool(id: "same"), Tool(id: "same")) })
        await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(calls))));
}
async Task UnsupportedTool()
{
    var invoked = 0;
    var handler = new FakeHandler((n, _) => Reply(n == 1 ? Calls(Tool(name: "pretend_simulator")) : Final()));
    await Analyze(handler, (_, _, _) => { invoked++; return Task.FromResult<JsonNode>(new JsonObject()); });
    Check(invoked == 0 && handler.Calls == 2 && handler.Requests[1].Body.Contains("error"), "Unknown tool escaped allowlist.");
}
async Task HttpFailure()
{
    const string secret = "http-private-key";
    var handler = new FakeHandler((_, _) => Reply($"{{\"error\":\"{secret}\"}}", HttpStatusCode.Unauthorized));
    var error = await Throws<HttpRequestException>(() => Analyze(handler, key: secret));
    Check(handler.Calls == 1 && error.StatusCode == HttpStatusCode.Unauthorized && error.Message.Contains("401") && !error.ToString().Contains(secret), "HTTP error leaked response or lost status.");
}
async Task NetworkFailure()
{
    const string secret = "transport-private-key";
    var handler = new FakeHandler((_, _) => throw new HttpRequestException($"Authorization: Bearer {secret}"));
    var error = await Throws<HttpRequestException>(() => Analyze(handler, key: secret));
    Check(handler.Calls == 1 && !error.ToString().Contains(secret) && error.InnerException is null, "Transport exception leaked credential.");
}
async Task AlreadyCanceled()
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    var handler = new FakeHandler((_, _) => Reply(Final()));
    await Throws<OperationCanceledException>(() => Analyze(handler, token: cancellation.Token));
    Check(handler.Calls == 0, "Canceled request reached HTTP.");
}
async Task InvalidSettings()
{
    foreach (var config in new[]
    {
        settings with { MaxToolRounds = 5 }, settings with { TimeoutSeconds = 0 }, settings with { MaxOutputTokens = 99 },
        settings with { Endpoint = new Uri("http://remote.example/api") }, settings with { Endpoint = new Uri("relative", UriKind.Relative) }
    })
    {
        var handler = new FakeHandler((_, _) => Reply(Final()));
        await Throws<ArgumentException>(() => Analyze(handler, config: config));
        Check(handler.Calls == 0, "Invalid settings reached HTTP.");
    }
}
async Task TruncatedResponse()
{
    foreach (var reason in new[] { "length", "content_filter" })
        await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Final(finishReason: reason)))));
}
async Task InvalidCredential()
{
    const string secret = "invalid-private-key\nvalue";
    var handler = new FakeHandler((_, _) => Reply(Final()));
    var error = await Throws<ArgumentException>(() => Analyze(handler, key: secret));
    Check(handler.Calls == 0 && !error.ToString().Contains("invalid-private-key"), "Invalid key leaked or reached HTTP.");
}

sealed record RecordedRequest(string Body, string? Authorization);
sealed class FakeHandler(Func<int, string, HttpResponseMessage> reply) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<RecordedRequest> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        Requests.Add(new RecordedRequest(body, request.Headers.Authorization?.ToString()));
        return reply(++Calls, body);
    }
}
