using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Core;
using HsHelper.Storage;

internal static partial class CliHistoryIntegration
{
    public static async Task RunLargeCardsAsync(string root, Action<string> pass)
    {
        var cli = typeof(CliHistoryIntegration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CliTestTargetPath").Value!;
        foreach (var known in new[] { true, false })
        {
            var fixture = await Fixture.Create(Path.Combine(root, "cli-large-cards", known ? "known" : "missing"));
            var entities = fixture.Frame.Entities.Values.ToDictionary(entity => entity.Id);
            for (var index = 0; index < 1001; index++)
                entities.Add(1000 + index, new EntitySnapshot(1000 + index, "CLI_LARGE_PUBLIC_" + index.ToString("D4"), EntityKind.Card,
                    new Dictionary<string, string> { ["ZONE"] = "GRAVEYARD", ["CONTROLLER"] = "2", ["CARDTYPE"] = "MINION" }.ToFrozenDictionary()));
            // This current hand card is deliberately after the thousand historical identities.
            entities.Add(4000, new EntitySnapshot(4000, known ? "CLI_HISTORY_CARD" : "CLI_LARGE_MISSING", EntityKind.Card,
                new Dictionary<string, string> { ["ZONE"] = "HAND", ["CONTROLLER"] = "1", ["CARDTYPE"] = "MINION",
                    ["COST"] = "1", ["ATK"] = "1", ["HEALTH"] = "1", ["DAMAGE"] = "0" }.ToFrozenDictionary()));
            var frame = fixture.Frame with { StateVersion = fixture.Frame.StateVersion + 1, Entities = entities.ToFrozenDictionary() };
            const string toolId = "cli-large-evaluate-line";
            var actions = new[] { new { kind = "play", source_entity_id = 4000, target_entity_id = (int?)null } };
            var tool = new Reply(JsonSerializer.Serialize(new { model = Model, choices = new[] { new { finish_reason = "tool_calls",
                message = new { role = "assistant", tool_calls = new[] { new { id = toolId, type = "function",
                    function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } },
                usage = new { prompt_tokens = 40, completion_tokens = 30, total_tokens = 70 } }));
            var final = new Reply(JsonSerializer.Serialize(new { model = Model, choices = new[] { new { finish_reason = "stop",
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = known ? "LARGE_KNOWN_CARD" : "LARGE_UNKNOWN_CARD",
                    reasons = new[] { "synthetic long match" }, alternative = (string?)null, risks = Array.Empty<string>(),
                    actions = known ? actions : [], assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>() }) } } },
                usage = new { prompt_tokens = 40, completion_tokens = 30, total_tokens = 70 } }));
            var result = await Invoke(cli, fixture.Directory, frame, "LARGE_PUBLIC_HISTORY", [tool, final]);
            RequireSuccess(result);
            Check(result.Requests.Count == 2, "Long-match CLI did not complete the production tool loop.");
            var line = ToolResult(result.Requests[1], toolId);
            var archive = (await fixture.Archive.ListAsync()).Single();
            var requestRecords = await fixture.Traces.ListRequestsAsync(frame.MatchId);
            var tools = await fixture.Traces.ListToolsAsync(frame.MatchId);
            Check(tools is [{ ToolName: "evaluate_line", Status: AnalysisTraceStatus.Completed }] && tools[0].ToolCallId == toolId &&
                tools[0].StateVersion == frame.StateVersion && requestRecords.Count == 2 && requestRecords.All(request => request.ChargedTokens == 70),
                "The long-match tool lost its snapshot binding, request audit or reported usage.");
            if (known)
                Check(line["IsLegal"]?.GetValue<bool>() == true && line["Verification"]?.GetValue<string>() == "Verified" &&
                    archive.Advice is { Verification: "verified", Actions.Count: 1, VerifiedLethal: false } && archive.Advice.Actions[0].SourceEntityId == 4000,
                    "A valid late-batch definition did not reach both the actual rule tool and final host action validator.");
            else
                Check(line["Verification"]?.GetValue<string>() == "Unsupported" && archive.Advice.Actions.Count == 0 && !archive.Advice.VerifiedLethal,
                    "A missing late-batch definition was promoted or turned into a catalog batch exception.");
            AssertNoSecrets(JsonSerializer.Serialize(archive) + JsonSerializer.Serialize(requestRecords) + JsonSerializer.Serialize(tools));
            pass(known
                ? "actual CLI with 1001 public graveyard identities loads the late hand definition for both evaluate_line and final verified action archival"
                : "actual CLI long-match lookup retains a missing late definition as unsupported with no fabricated action or lethal claim");
        }
    }
}
