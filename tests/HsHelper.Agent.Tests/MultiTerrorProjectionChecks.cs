using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class MultiTerrorProjectionChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("model branch summaries retain correlated witnesses and consume only their own invocation", SummaryAndLocal);
        yield return ("failed and cancelled model tools cannot leave a summary mode or stale audit source on local tools", FailedInvocation);
    }

    private static async Task SummaryAndLocal()
    {
        var catalog = await Catalog(); var frame = Frame(); var original = JsonSerializer.Serialize(frame);
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, new(), catalog, "255790", toolCompleted: observations.Add);
        var full = await coach.ExecuteAsync("evaluate_line", EndTurn(), default);
        var fullPreview = Preview(full); var fullBranches = fullPreview["Branches"]!.AsArray();
        Check(fullBranches.Count == 256 && fullPreview["BranchProjection"] is null,
            "The local tool lost its complete retained high-branch calculation.");
        var capabilities = coach.Capabilities();
        capabilities.OnToolInvocation!(Invocation(frame, "model-line", "evaluate_line"));
        var model = await coach.ExecuteAsync("evaluate_line", EndTurn(), default);
        var view = Preview(model); var projection = view["BranchProjection"]!;
        Check(view["Branches"]!.AsArray().Count == 8 && projection["TotalBranches"]!.GetValue<int>() == fullBranches.Count &&
            projection["ShownBranches"]!.GetValue<int>() == 8 && projection["MaximumShownBranches"]!.GetValue<int>() == 8 &&
            projection["AllBranchesIncluded"]!.GetValue<bool>() == false &&
            JsonNode.DeepEquals(view["SourceEntityIds"], fullPreview["SourceEntityIds"]) &&
            JsonNode.DeepEquals(view["IsEnumerationComplete"], fullPreview["IsEnumerationComplete"]),
            "The model summary concealed omitted results or replaced computational completeness with a display claim.");
        foreach (var branch in view["Branches"]!.AsArray())
        {
            var matching = fullBranches.Single(candidate => candidate!["Id"]!.ToString() == branch!["Id"]!.ToString());
            Check(JsonNode.DeepEquals(matching, branch), "A summary split stages, combats, survivors or healing between correlated branches.");
        }
        Check(observations[1] is { ToolCallId: "model-line", Success: true } &&
            JsonNode.DeepEquals(JsonNode.Parse(observations[1].ResultJson!), model),
            "The model tool's archived result differs from the returned bounded view.");
        var localAgain = await coach.ExecuteAsync("evaluate_line", EndTurn(), default);
        Check(JsonNode.DeepEquals(localAgain, full) && observations[2].ToolCallId is null,
            "An inherited async context reused a completed model invocation for a local result or audit.");
        capabilities.OnToolInvocation!(Invocation(frame, "model-snapshot", "get_snapshot"));
        var snapshot = await coach.ExecuteAsync("get_snapshot", new(), default);
        Check(snapshot["match_id"]!.ToString() == frame.MatchId && observations[3].ToolCallId == "model-snapshot",
            "A non-line model tool lost its original response or invocation binding.");
        var afterSnapshot = await coach.ExecuteAsync("evaluate_line", EndTurn(), default);
        Check(JsonNode.DeepEquals(afterSnapshot, full) && observations[4].ToolCallId is null && JsonSerializer.Serialize(frame) == original,
            "A prior model snapshot enabled summary mode later or a tool modified the fixed frame.");
        var validation = await capabilities.ValidateActions!([new("end_turn", 2)], default);
        Check(validation.Status == AdviceValidationStatus.Conditional && !validation.VerifiedLethal,
            "A model display summary changed authoritative final action validation.");
        var fullBytes = Encoding.UTF8.GetByteCount(full.ToJsonString());
        var modelBytes = Encoding.UTF8.GetByteCount(model.ToJsonString());
        Check(fullBytes > 1_000_000 && modelBytes < 128 * 1024, "The realistic large result no longer tests bounded model transport.");
        Console.WriteLine($"Multi Terror tool bytes: local={fullBytes}, model={modelBytes}, retained={fullBranches.Count}, shown=8");
    }

    private static async Task FailedInvocation()
    {
        var catalog = await Catalog(); var frame = Frame(); var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, new(), catalog, "255790", toolCompleted: observations.Add);
        var capabilities = coach.Capabilities();
        foreach (var cancel in new[] { false, true })
        {
            var id = cancel ? "cancelled-model" : "failed-model";
            capabilities.OnToolInvocation!(Invocation(frame, id, "evaluate_line"));
            using var cancellation = new CancellationTokenSource(); if (cancel) cancellation.Cancel();
            try
            {
                await coach.ExecuteAsync("evaluate_line", cancel ? EndTurn() : new(), cancellation.Token);
                throw new InvalidOperationException("A rejected tool unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (cancel) { }
            catch (ArgumentException) when (!cancel) { }
            Check(observations.Last() is { Success: false, ResultJson: null, ResultHash: null } && observations.Last().ToolCallId == id,
                "Rejected model execution lost its failure audit or fabricated a result.");
            var local = await coach.ExecuteAsync("evaluate_line", EndTurn(), default);
            Check(Preview(local)["Branches"]!.AsArray().Count == 256 && Preview(local)["BranchProjection"] is null && observations.Last().ToolCallId is null,
                "A failed or cancelled model invocation contaminated a later direct local call.");
        }
    }

    private static JsonObject EndTurn() => new() { ["actions"] = new JsonArray(new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) };
    private static AgentToolInvocation Invocation(GameSnapshot frame, string id, string name) => new("fixture-model", "fixture-run", "fixture-request", id, name,
        frame.MatchId, frame.StateVersion, 1, frame.EventSequence);
    private static JsonNode Preview(JsonNode result) => result["Result"]!["TerrorEndTurnPreviews"]!.AsArray().Single()!;

    private static async Task<CardCatalog> Catalog()
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "The model view lost its fixed-build source.");
        var catalog = new CardCatalog(Path.Combine(workspace.FullName, ".local", "multi-terror-model-view", Guid.NewGuid().ToString("N"), "cards.db"));
        await catalog.InitializeAsync();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText()));
        await catalog.ImportAsync(input, "255790", source: "fixture://multi-terror-model-view"); return catalog;
    }

    private static GameSnapshot Frame()
    {
        var entities = new List<EntitySnapshot>
        {
            new(2, null, EntityKind.Player, Tags(("PLAYER_ID", "1"), ("CONTROLLER", "1"), ("HERO_ENTITY", "10"), ("RESOURCES", "10"), ("RESOURCES_USED", "0"))),
            new(3, null, EntityKind.Player, Tags(("PLAYER_ID", "2"), ("CONTROLLER", "2"), ("HERO_ENTITY", "11"))),
            new(10, "HERO_01", EntityKind.Card, Tags(("CARDTYPE", "HERO"), ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("HEALTH", "30"), ("DAMAGE", "10"), ("ARMOR", "0"), ("ATK", "0"))),
            new(11, "HERO_02", EntityKind.Card, Tags(("CARDTYPE", "HERO"), ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("HEALTH", "30"), ("DAMAGE", "0"), ("ARMOR", "0"), ("ATK", "0")))
        };
        foreach (var (id, attack, health) in new[] { (30, 1, 65), (31, 2, 48), (32, 3, 33) })
            entities.Add(new(id, "CORE_TTN_866", EntityKind.Card, Tags(("CARDTYPE", "MINION"), ("CONTROLLER", "1"), ("ZONE", "PLAY"),
                ("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ("DIVINE_SHIELD", "1"))));
        for (var index = 0; index < 7; index++)
            entities.Add(new(21 + index, "CS2_172", EntityKind.Card, Tags(("CARDTYPE", "MINION"), ("CONTROLLER", "2"), ("ZONE", "PLAY"),
                ("ATK", (1 << index).ToString()), ("HEALTH", "100"), ("DAMAGE", "0"))));
        return new("multi-terror-model-view", 10, 100, 5, 1, 1, GamePhase.Main,
            entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static FrozenDictionary<string, string> Tags(params (string Name, string Value)[] tags) => tags.ToFrozenDictionary(pair => pair.Name, pair => pair.Value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
