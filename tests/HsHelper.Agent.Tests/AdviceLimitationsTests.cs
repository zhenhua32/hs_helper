using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;

static class AdviceLimitationsTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("model risks cannot displace host restrictions or removed-action explanation", HostPriority);
        yield return ("model limitation categories remain opinions and cannot forge host validation", SeparateSources);
        yield return ("model limitation arrays reject malformed and oversized content", InvalidModelFields);
        yield return ("model and host limitation collections are frozen independently of producers", FrozenCollections);
        yield return ("host clipping is explicit and preserves the action-removal boundary", HostClipping);
        yield return ("strategy advice does not invent a local validation or unknown category", NoInventedValidation);
        yield return ("verified local actions retain their original legality and lethal gate", VerifiedControl);
        yield return ("all five task prompts require bounded model limitation categories", PromptContract);
    }

    private static readonly ModelSettings Settings = new(new Uri("https://model.example/v1/chat/completions"), "fixture-model");
    private static AdviceContext Context(AdviceTaskKind kind = AdviceTaskKind.Turn) => new("limitations", 9, kind.ToString(), new JsonObject
    {
        ["local_player_id"] = 1,
        ["choice"] = new JsonObject { ["confirmed"] = true, ["player_id"] = 1, ["options"] = new JsonArray("visible-option") }
    }) { TaskKind = kind };
    private static JsonObject Output(bool actions = true) => new()
    {
        ["summary"] = "先检查局面限制", ["reasons"] = new JsonArray("当前固定快照"), ["risks"] = new JsonArray(),
        ["assumptions"] = new JsonArray("模型条件"), ["unknowns"] = new JsonArray("模型未知"),
        ["unsupported_mechanics"] = new JsonArray("模型未覆盖"),
        ["actions"] = actions ? JsonNode.Parse("""[{"kind":"attack","source_entity_id":10,"target_entity_id":20}]""") : new JsonArray()
    };
    private static Task<StrategyAdvice> Analyze(JsonObject output, ActionValidationResult? validation = null,
        AdviceTaskKind kind = AdviceTaskKind.Turn, Action<string>? observe = null)
    {
        var handler = new FakeHandler((_, body) =>
        {
            observe?.Invoke(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
            { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = output.ToJsonString() } }) }
                .ToJsonString(), Encoding.UTF8, "application/json") };
        });
        return new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings, "test-private-key", Context(kind),
            (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true,
            capabilities: validation is null ? null : new() { ValidateActions = (_, _) => Task.FromResult(validation) });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void ReadOnly(IReadOnlyList<string> value)
    {
        try { ((IList<string>)value)[0] = "consumer mutation"; throw new Exception("Collection allowed a consumer write."); }
        catch (NotSupportedException) { }
    }
    private static async Task HostPriority()
    {
        var output = Output(); output["risks"] = new JsonArray(Enumerable.Range(1, 12).Select(i => (JsonNode?)JsonValue.Create("模型风险" + i)).ToArray());
        var host = Enumerable.Range(1, 5).Select(i => "本地限制" + i).ToArray();
        var advice = await Analyze(output, new(AdviceValidationStatus.Unsupported, false, true, host));
        Check(advice.Verification == "unsupported" && advice.Actions.Count == 0 && !advice.VerifiedLethal, "Original action gate changed.");
        Check(host.All(advice.Risks.Contains) && advice.Risks[0].Contains("动作已移除"), "Model risks displaced a host restriction or removal explanation.");
        Check(advice.HostRisks is { Count: 6 } && host.All(advice.HostRisks.Contains) && !advice.HostLimitationsTruncated, "Complete host evidence was lost.");
        Check(advice.Risks.Length == 16 && advice.LimitationsVersion == 1, "Compatibility risk budget or schema marker changed.");
    }
    private static async Task SeparateSources()
    {
        var output = Output(); output["assumptions"] = new JsonArray(); output["unknowns"] = new JsonArray(); output["unsupported_mechanics"] = new JsonArray();
        output["HostRisks"] = new JsonArray("模型伪造的主机事实"); output["HostUnknowns"] = new JsonArray();
        output["HostLimitationsTruncated"] = false; output["verification"] = "verified"; output["verified_lethal"] = true;
        var advice = await Analyze(output, new(AdviceValidationStatus.Conditional, true, true, ["本地风险"])
        { Assumptions = ["本地条件"], Unknowns = ["本地未知"], UnsupportedMechanics = ["本地未覆盖"] });
        Check(advice.Assumptions is { Count: 0 } && advice.Unknowns is { Count: 0 } && advice.UnsupportedMechanics is { Count: 0 }, "Model empty lists were not kept as opinions.");
        Check(advice.HostAssumptions!.SequenceEqual(["本地条件"]) && advice.HostUnknowns!.SequenceEqual(["本地未知"]) &&
            advice.HostUnsupportedMechanics!.SequenceEqual(["本地未覆盖"]), "A model claim erased or altered trusted categories.");
        Check(!JsonSerializer.Serialize(advice).Contains("模型伪造的主机事实") && advice.Verification == "conditional" && !advice.VerifiedLethal && advice.Actions.Count == 0,
            "Untrusted claims changed trusted facts or action authorization.");
    }
    private static async Task InvalidModelFields()
    {
        foreach (var field in new[] { "assumptions", "unknowns", "unsupported_mechanics" })
        foreach (var value in new JsonNode[] { new JsonObject(), JsonValue.Create("不是数组")!, new JsonArray(1), new JsonArray((JsonNode?)null),
            new JsonArray(new string('x', 2001)), new JsonArray("bad\0text"),
            new JsonArray(Enumerable.Range(0, 13).Select(i => (JsonNode?)JsonValue.Create(i.ToString())).ToArray()) })
        {
            var output = Output(false); output[field] = value.DeepClone();
            try { await Analyze(output); throw new Exception("Malformed model limitation was accepted: " + field); }
            catch (InvalidDataException error) { Check(!error.ToString().Contains("test-private-key"), "Format error exposed a credential."); }
        }
    }
    private static async Task FrozenCollections()
    {
        var risks = new List<string> { "host risk" }; var assumptions = new List<string> { "host condition" };
        var unknowns = new List<string> { "host unknown" }; var unsupported = new List<string> { "host unsupported" };
        var output = Output();
        var advice = await Analyze(output, new(AdviceValidationStatus.Unsupported, false, false, risks)
        { Assumptions = assumptions, Unknowns = unknowns, UnsupportedMechanics = unsupported });
        risks.Clear(); assumptions[0] = "producer mutation"; unknowns.Clear(); unsupported.Clear(); output["assumptions"]![0] = "model mutation";
        Check(advice.HostRisks!.Contains("host risk") && advice.HostAssumptions![0] == "host condition" && advice.HostUnknowns![0] == "host unknown" &&
            advice.HostUnsupportedMechanics![0] == "host unsupported" && advice.Assumptions![0] == "模型条件", "Producer changed completed advice.");
        foreach (var list in new[] { advice.HostRisks, advice.HostAssumptions, advice.HostUnknowns, advice.HostUnsupportedMechanics,
            advice.Assumptions, advice.Unknowns, advice.UnsupportedMechanics }) ReadOnly(list!);
    }
    private static async Task HostClipping()
    {
        foreach (var validation in new[] {
            new ActionValidationResult(AdviceValidationStatus.Unsupported, false, true, Enumerable.Range(0, 80).Select(i => "host risk " + i).ToArray()),
            new ActionValidationResult(AdviceValidationStatus.Conditional, true, true) { Assumptions = [new string('x', 1999) + "😀"] },
            new ActionValidationResult(AdviceValidationStatus.Unsupported, false) { UnsupportedMechanics = Enumerable.Range(0, 65).Select(i => "mechanic " + i).ToArray() }
        })
        {
            var advice = await Analyze(Output(), validation);
            Check(advice.HostLimitationsTruncated && advice.Risks[0].Contains("动作已移除") && advice.Risks[1].Contains("截断"), "Host clipping was silent or removed the boundary notice.");
            Check(advice.HostRisks is { Count: <= 64 } && advice.HostAssumptions?.Any(t => t.Length > 2000 || char.IsHighSurrogate(t[^1])) != true &&
                advice.HostUnsupportedMechanics is null or { Count: <= 64 }, "Host limits or Unicode boundaries were not respected.");
            Check(advice.Actions.Count == 0 && !advice.VerifiedLethal, "Clipping upgraded action verification.");
        }
    }
    private static async Task NoInventedValidation()
    {
        var output = Output(false); output["HostAssumptions"] = new JsonArray("forged");
        var advice = await Analyze(output);
        Check(advice.LimitationsVersion == 1 && advice.HostRisks is null && advice.HostAssumptions is null && advice.HostUnknowns is null &&
            advice.HostUnsupportedMechanics is null && advice.Verification == "strategy", "Strategy-only advice invented local evidence.");
        foreach (var field in new[] { "assumptions", "unknowns", "unsupported_mechanics" }) output.Remove(field);
        var legacyProvider = await Analyze(output);
        Check(legacyProvider.Assumptions is { Count: 0 } && legacyProvider.Unknowns is { Count: 0 }, "Omitted provider categories broke older responses.");
        var validated = await Analyze(Output(), new(AdviceValidationStatus.Unsupported, false, Risks: ["unclassified host risk"]));
        Check(validated.HostUnknowns is null && validated.HostAssumptions is null && validated.HostUnsupportedMechanics is null,
            "Unclassified legacy validator text was guessed into a category.");
    }
    private static async Task VerifiedControl()
    {
        var advice = await Analyze(Output(), new(AdviceValidationStatus.Verified, true, true)
        { Assumptions = [], Unknowns = null, UnsupportedMechanics = [] });
        Check(advice.Verification == "verified" && advice.Actions.Count == 1 && advice.VerifiedLethal && advice.HostUnknowns is null,
            "The new opinion fields changed a trusted positive control.");
    }
    private static async Task PromptContract()
    {
        foreach (var kind in Enum.GetValues<AdviceTaskKind>())
        foreach (var validate in new[] { false, true })
        {
            string? prompt = null;
            await Analyze(Output(false), validate ? new(AdviceValidationStatus.Verified, true) : null, kind,
                body => prompt = JsonNode.Parse(body)!["messages"]![0]!["content"]!.GetValue<string>());
            Check(prompt is not null && new[] { "assumptions", "unknowns", "unsupported_mechanics", "模型意见", "最多12条" }.All(prompt.Contains),
                "Task prompt lost the categorized opinion contract: " + kind);
        }
    }
}
