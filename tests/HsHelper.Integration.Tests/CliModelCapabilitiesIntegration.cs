using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Storage;

internal static partial class CliHistoryIntegration
{
    public static async Task RunModelCapabilitiesAsync(string root, Action<string> pass)
    {
        if (!OperatingSystem.IsWindows()) return; // These cases exercise the real current-user DPAPI store.
        var cli = typeof(CliHistoryIntegration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CliTestTargetPath").Value!;
        var directory = Path.Combine(root, "cli-model-capabilities");
        var fixture = await Fixture.Create(Path.Combine(directory, "inherited"));
        var savedPath = Path.Combine(fixture.Directory, "model-config.json");
        var store = new ProtectedModelConfigurationStore(savedPath, new WindowsSecretProtector());
        async Task Save(bool tools, ModelResponseFormat format) => await store.SaveAsync(new(
            new ModelSettings(new Uri("https://saved-config.invalid/v1/chat/completions"), "saved-model-unused", 3)
            { EnableTools = tools, ResponseFormat = format }, "SAVED_SYNTHETIC_KEY_UNUSED", "synthetic integration fixture"));

        await Save(false, ModelResponseFormat.JsonSchema);
        var originalSaved = await File.ReadAllBytesAsync(savedPath);
        var connection = await Invoke(cli, fixture.Directory, fixture.Frame, "", [CapabilityFinal("CONNECTED", false)], modelCheck: true);
        Check(connection.ExitCode == 0 && connection.Requests.Count == 1 && !File.Exists(connection.OutputPath), "CLI connection check failed with inherited no-tool schema mode.");
        AssertCapabilityRequest(connection.Requests.Single(), false, "json_schema", false);
        var checkedStatus = JsonNode.Parse(connection.Stdout)!.AsObject();
        Check(checkedStatus["Connected"]?.GetValue<bool>() == true && checkedStatus["SendsGameData"]?.GetValue<bool>() == false &&
            checkedStatus["ToolsEnabled"]?.GetValue<bool>() == false && checkedStatus["ResponseFormat"]?.GetValue<string>() == "JsonSchema" &&
            Context(connection.Requests[0])["Snapshot"] is JsonObject { Count: 0 } &&
            !(await fixture.Traces.ListRequestsAsync(fixture.Frame.MatchId)).Any(), "Connection check sent game context, charged a match or reported a different capability configuration.");
        var advice = await Invoke(cli, fixture.Directory, fixture.Frame, "INHERITED_SCHEMA", [CapabilityFinal("INHERITED_SCHEMA", true)]);
        RequireSuccess(advice); AssertCapabilityRequest(advice.Requests.Single(), false, "json_schema", true);
        var afterReadSaved = await File.ReadAllBytesAsync(savedPath);
        Check(originalSaved.SequenceEqual(afterReadSaved), "Reading model capability settings rewrote the saved configuration.");
        var archive = (await fixture.Archive.ListAsync()).Single(); await AssertTrace(fixture, archive, 1);
        pass("production CLI model-check and advise inherit the same DPAPI no-tool schema settings while connection checks send no game data or match charges");

        await Save(true, ModelResponseFormat.JsonSchema);
        const string omitted = "MODEL_ENABLE_TOOLS=false\nMODEL_RESPONSE_FORMAT=omitted\n";
        connection = await Invoke(cli, fixture.Directory, fixture.Frame, "", [CapabilityFinal("EXPLICIT_OMITTED", false)], capabilityEnvironment: omitted, modelCheck: true);
        Check(connection.ExitCode == 0, "Explicit omitted connection mode failed.");
        AssertCapabilityRequest(connection.Requests.Single(), false, null, false);
        advice = await Invoke(cli, fixture.Directory, fixture.Frame, "EXPLICIT_OMITTED", [CapabilityFinal("EXPLICIT_OMITTED", true)], capabilityEnvironment: omitted);
        RequireSuccess(advice); AssertCapabilityRequest(advice.Requests.Single(), false, null, true);
        pass("actual CLI explicit false and omitted override saved enabled schema settings in both connection and analysis commands");

        await Save(false, ModelResponseFormat.JsonSchema);
        const string enabled = "MODEL_ENABLE_TOOLS=true\nMODEL_RESPONSE_FORMAT=json_object\n";
        connection = await Invoke(cli, fixture.Directory, fixture.Frame, "", [CapabilityFinal("OBJECT_CHECK", false)], capabilityEnvironment: enabled, modelCheck: true);
        Check(connection.ExitCode == 0, "Enabled JSON-object connection check failed.");
        AssertCapabilityRequest(connection.Requests.Single(), true, "json_object", false);
        Check(connection.Requests[0]["tool_choice"]?.GetValue<string>() == "none", "Connection check allowed tool execution.");
        advice = await Invoke(cli, fixture.Directory, fixture.Frame, "EXPLICIT_ENABLED", [Reply.Tool(), CapabilityFinal("EXPLICIT_ENABLED", true)], capabilityEnvironment: enabled);
        RequireSuccess(advice);
        Check(advice.Requests.Count == 2, "Explicit tool re-enabling did not complete the real CLI tool loop.");
        foreach (var request in advice.Requests) AssertCapabilityRequest(request, true, "json_object", true);
        archive = (await fixture.Archive.ListAsync()).Single(record => record.Advice.Summary == "EXPLICIT_ENABLED");
        await AssertTrace(fixture, archive, 2, ToolId);
        pass("actual CLI explicit enabled JSON-object mode restores registered tools and archives both HTTP rounds and the real tool provenance");

        foreach (var (name, reply, format) in new[]
        {
            ("disabled-tool", Reply.Tool(), ModelResponseFormat.Omitted),
            ("invalid-schema", CapabilityFinal("MISSING_REQUIRED_ALTERNATIVE", true, omitAlternative: true), ModelResponseFormat.JsonSchema),
            ("provider-rejection", new Reply("PRIVATE_PROVIDER_BODY_" + FakeKey, 400), ModelResponseFormat.JsonSchema)
        })
        {
            var rejected = await Fixture.Create(Path.Combine(directory, name));
            await new ProtectedModelConfigurationStore(Path.Combine(rejected.Directory, "model-config.json"), new WindowsSecretProtector())
                .SaveAsync(new(new ModelSettings(new Uri("https://saved-config.invalid/v1/chat/completions"), Model)
                { EnableTools = false, ResponseFormat = format }, "SAVED_SYNTHETIC_KEY_UNUSED", "synthetic integration fixture"));
            var result = await Invoke(cli, rejected.Directory, rejected.Frame, "REJECTED_CAPABILITY", [reply]);
            Check(result.ExitCode != 0 && result.Requests.Count == 1 && !File.Exists(result.OutputPath) &&
                (await rejected.Archive.ListAsync()).Count == 0 && (await rejected.Traces.ListToolsAsync(rejected.Frame.MatchId)).Count == 0,
                "Rejected capability response retried, executed tools or produced an advice archive.");
            AssertCapabilityRequest(result.Requests.Single(), false, format == ModelResponseFormat.JsonSchema ? "json_schema" : null, true);
            var requests = await rejected.Traces.ListRequestsAsync(rejected.Frame.MatchId);
            var totals = await rejected.Traces.GetBudgetTotalsAsync(rejected.Frame.MatchId);
            Check(requests.Count == 1 && totals.RequestCount == 1 &&
                (reply.Status == 200 ? requests[0] is { Status: AnalysisTraceStatus.Completed, ChargedTokens: 70 } && totals.ChargedTokens == 70
                    : requests[0].Status == AnalysisTraceStatus.Failed), "Capability protocol failure lost the actual request usage audit.");
            AssertNoSecrets(JsonSerializer.Serialize(requests) + result.Stdout + result.Stderr);
            Check(!result.Stderr.Contains("PRIVATE_PROVIDER_BODY", StringComparison.Ordinal), "Provider refusal body escaped safe CLI diagnostics.");
            pass("production CLI " + name + " fails after one HTTP without fallback tool execution or completed advice and retains the usage audit");
        }
    }

    private static void AssertCapabilityRequest(JsonObject request, bool tools, string? format, bool actions)
    {
        Check(request["model"]?.GetValue<string>() == Model && request.ContainsKey("tools") == tools && request.ContainsKey("tool_choice") == tools,
            "CLI mixed saved endpoint/model fields into environment credentials or sent disabled tool fields.");
        Check(format is null ? !request.ContainsKey("response_format") : request["response_format"]?["type"]?.GetValue<string>() == format,
            "CLI used a different output mode from the merged capability configuration.");
        if (format == "json_schema")
        {
            var schema = request["response_format"]!["json_schema"]!;
            Check(schema["strict"]?.GetValue<bool>() == true && schema["schema"]!["properties"]!.AsObject().ContainsKey("actions") == actions,
                "Connection and action analysis did not use their respective fixed strict response schemas.");
        }
    }

    private static Reply CapabilityFinal(string summary, bool actions, bool omitAlternative = false)
    {
        var content = new JsonObject { ["summary"] = summary, ["reasons"] = new JsonArray("synthetic CLI capability evidence"),
            ["risks"] = new JsonArray(), ["assumptions"] = new JsonArray(), ["unknowns"] = new JsonArray(), ["unsupported_mechanics"] = new JsonArray() };
        if (!omitAlternative) content["alternative"] = null;
        if (actions) content["actions"] = new JsonArray();
        return new(JsonSerializer.Serialize(new { model = Model, choices = new[] { new { finish_reason = "stop",
            message = new { role = "assistant", content = content.ToJsonString() } } }, usage = new { prompt_tokens = 40, completion_tokens = 30, total_tokens = 70 } }));
    }
}
