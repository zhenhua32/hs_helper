using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class MagmaSplitIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_MAGMA_INTEGRATION_PROBE";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        var catalog = new CardCatalog(Path.Combine(root, "magma-split", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://magma-split-integration");
        var engine = Start();
        var frame = engine.Snapshot();
        var before = JsonSerializer.Serialize(frame);
        var observations = new List<CoachToolObservation>();
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Turn);
        var coach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, toolCompleted: observations.Add);
        var direct = await Preview(coach);
        var split = direct["Split"]!;
        Check(direct["AttackIsLegal"]?.GetValue<bool>() != false && direct["AppliedActions"]?.GetValue<int>() == 1 &&
            split["TriggerActive"]?.GetValue<bool>() == true && split["ResolvesBranches"]?.GetValue<bool>() == true &&
            split["IsEnumerationComplete"]?.GetValue<bool>() == true &&
            split["RequestedPackets"]?.GetValue<long>() == 5 && split["DamagePerPacket"]?.GetValue<long>() == 1 &&
            split["Branches"]!.AsArray().Count > 1 && split["Targets"]!.AsArray().Select(t => t!["EntityId"]!.GetValue<int>()).Order().SequenceEqual([21, 40]) &&
            direct["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && direct["HasAuthoritativeNextState"]?.GetValue<bool>() == false &&
            observations.Single() is { Name: "evaluate_magma_split", Success: true, ResultHash.Length: 64 } &&
            before == JsonSerializer.Serialize(frame), "Parsed combat did not reach real independent split branches or mutated its input snapshot: " + direct.ToJsonString());
        var heroRange = split["Targets"]!.AsArray().Single(target => target!["EntityId"]!.GetValue<int>() == 21)!;
        var minionRange = split["Targets"]!.AsArray().Single(target => target!["EntityId"]!.GetValue<int>() == 40)!;
        Check(Range(heroRange["Damage"], 2, 5) && Range(heroRange["RemainingHealth"], 25, 28) &&
            Range(minionRange["Damage"], 0, 3) && Range(minionRange["RemainingHealth"], 0, 3) &&
            split["Branches"]!.AsArray().All(branch => branch!["IsComplete"]!.GetValue<bool>() && branch["PacketsApplied"]!.GetValue<long>() == 5 &&
                branch["PacketsRemaining"]!.GetValue<long>() == 0 && branch["Targets"]!.AsArray().Sum(target => target!["RemainingHealth"]!.GetValue<long>()) == 28),
            "The split did not calculate correlated five-packet damage with dead-minion removal: " + split.ToJsonString());
        var silencedBody = await Preview(Coach(Start(targetSilenced: true).Snapshot(), catalog));
        Check(silencedBody["Split"]?["IsEnumerationComplete"]?.GetValue<bool>() == true && silencedBody["Split"]?["Targets"]!.AsArray().Count == 2,
            "An explicitly silenced body could not form a separate complete multi-target positive control.");
        pass("parsed FIR953 combat fully enumerates native Macaw and silenced-body targets without replacing the observed snapshot");

        var line = await coach.ExecuteAsync("evaluate_line", Actions(followup: true), default);
        var result = line["Result"]!;
        Check(line["Verification"]?.ToString() == "Unsupported" && line["AppliedActions"]?.GetValue<int>() == 1 &&
            result["MagmaSplitBoundaryReached"]?.GetValue<bool>() == true && result["MagmaSplitPreviews"]!.AsArray().Count == 1 &&
            Entity(result, 21)["RemainingHealth"]?.GetValue<int>() == 30 && Entity(result, 40)["RemainingHealth"]?.GetValue<int>() == 3 &&
            Entity(result, 30)["RemainingHealth"]?.GetValue<int>() == 7 && result["TurnEnded"]?.GetValue<bool>() == false &&
            line["IsLethal"]?.GetValue<bool>() == false && line["UnsupportedMechanics"]!.AsArray().Any(x => x?.ToString() == "AWAIT_MAGMA_SPLIT_SNAPSHOT"),
            "A random branch became the current board, or the following action crossed the unresolved split boundary.");
        pass("line projection stops after the ordinary attack exchange and blocks the next action before random split resolution");

        foreach (var extra in new[] { "random_target_entity_id", "selected_target_entity_ids", "packets", "damage_per_packet", "branch_id" })
        {
            var args = new JsonObject { ["source_entity_id"] = 30, ["target_entity_id"] = 40, [extra] = 21 };
            await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_magma_split", args, default));
        }
        var definition = SnapshotCoach.Definitions.Single(d => d.Name == "evaluate_magma_split");
        Check(!definition.ValidateArguments(new() { ["source_entity_id"] = 30 }) &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 30, ["target_entity_id"] = 0 }),
            "The split schema accepted an absent or invalid ordinary attack target.");
        pass("magma tool accepts only the ordinary source and attacked target and rejects model-defined packets targets or branches");

        foreach (var denied in new[] { Start("tag=EXHAUSTED value=1"), Start("tag=FROZEN value=1"), Start("tag=4573 value=97376"),
            Start(otherTaunt: true) })
        {
            var deniedResult = await Preview(Coach(denied.Snapshot(), catalog));
            Check((deniedResult["error"] is not null || deniedResult["AppliedActions"]?.GetValue<int>() == 0) && deniedResult["Split"] is null &&
                deniedResult["AttackIsLegal"]?.GetValue<bool>() != true,
                "Direct split preview bypassed exhaustion freeze custom source or the ordinary taunt validator.");
        }
        foreach (var hidden in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } } })
        {
            var hiddenResult = await Preview(Coach(hidden, catalog));
            Check(hiddenResult["Split"] is null && (hiddenResult["error"] is not null || hiddenResult["AppliedActions"]?.GetValue<int>() == 0) &&
                !hiddenResult.ToJsonString().Contains(Probe, StringComparison.Ordinal), "An unconfirmed perspective acquired split preview data.");
        }
        Check(frame.Entities[90].CardId is null && !direct.ToJsonString().Contains(Probe, StringComparison.Ordinal) &&
            !line.ToJsonString().Contains(Probe, StringComparison.Ordinal) && !line["Result"]!["Entities"]!.AsArray().Any(e => e!["EntityId"]?.GetValue<int>() == 90),
            "Private opposing entities entered the split or line projection.");
        pass("direct magma preview reuses ordinary combat legality and excludes private and spectator identities");

        var pendingEngine = Start(targetHealth: 5, targetExtra: "tag=REBORN value=1");
        var pending = await Preview(Coach(pendingEngine.Snapshot(), catalog));
        Check(pending["Split"]?["PriorResolutionPending"]?.GetValue<bool>() == true &&
            pending["Split"]?["RequiresObservation"]?.GetValue<bool>() == true &&
            pending["Split"]?["ResolvesBranches"]?.GetValue<bool>() == false && pending["Split"]!["Branches"]!.AsArray().Count == 0,
            "A just-killed reborn minion was removed and its unresolved return converted into an exact hero-only split.");
        pass("unresolved ordinary-combat reborn stops split enumeration before assuming an empty enemy board");

        var conflictingEntities = frame.Entities.ToDictionary();
        var conflictingTags = conflictingEntities[30].Tags.ToDictionary(); conflictingTags["SILENCED"] = "1"; conflictingTags["188"] = "0";
        conflictingEntities[30] = conflictingEntities[30] with { Tags = conflictingTags.ToFrozenDictionary() };
        var conflicting = await Coach(frame with { Entities = conflictingEntities.ToFrozenDictionary() }, catalog)
            .ExecuteAsync("evaluate_line", Actions(followup: true), default);
        Check(conflicting["Verification"]?.ToString() != "Verified" && conflicting["Result"]?["TurnEnded"]?.GetValue<bool>() == false &&
            conflicting["AppliedActions"]?.GetValue<int>() <= 1,
            "An explicit silenced alias conflict became a certainly suppressed trigger and allowed the following end turn.");
        foreach (var (id, name, alias) in new[] { (21, "IMMUNE", "240"), (30, "LIFESTEAL", "685") })
        {
            var soleHero = Start(targetHealth: 1).Snapshot(); var entities = soleHero.Entities.ToDictionary(); var tags = entities[id].Tags.ToDictionary();
            tags[name] = "1"; tags[alias] = "0"; entities[id] = entities[id] with { Tags = tags.ToFrozenDictionary() };
            var rejected = await Coach(soleHero with { Entities = entities.ToFrozenDictionary() }, catalog)
                .ExecuteAsync("evaluate_line", Actions(followup: true), default);
            Check(rejected["Verification"]?.ToString() != "Verified" && rejected["Result"]?["TurnEnded"]?.GetValue<bool>() == false &&
                rejected["AppliedActions"]?.GetValue<int>() <= 1 && rejected["Result"]?["MagmaSplitBoundaryReached"]?.GetValue<bool>() == true,
                "A sole-hero fast path erased the explicit " + name + "/" + alias + " contradiction: " + rejected.ToJsonString());
        }
        pass("raw contradictory silence immunity and lifesteal evidence cannot bypass the split boundary and verify a following action");

        var targetConflictEntities = frame.Entities.ToDictionary();
        var targetConflictTags = targetConflictEntities[40].Tags.ToDictionary(); targetConflictTags["SILENCED"] = "1"; targetConflictTags["188"] = "0";
        targetConflictEntities[40] = targetConflictEntities[40] with { CardId = "EDR_227", Tags = targetConflictTags.ToFrozenDictionary() };
        var targetConflict = await Preview(Coach(frame with { Entities = targetConflictEntities.ToFrozenDictionary() }, catalog));
        Check(targetConflict["AppliedActions"]?.GetValue<int>() == 1 && targetConflict["Split"] is not null &&
            targetConflict["Split"]?["IsEnumerationComplete"]?.GetValue<bool>() != true,
            "Unknown target silence removed its printed deathrattle and made later split packets exact.");
        pass("contradictory target silence never removes a printed deathrattle to complete an unsupported split branch");

        using var model = new BranchCombiningModel();
        using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://magma-fixture.invalid/v1/chat/completions"), "magma-fixture"),
            "magma-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(model.Calls == 2 && model.CheckedPreview && advice.Actions.Count == 0 && !advice.VerifiedLethal && advice.Risks.Length > 0 &&
            before == JsonSerializer.Serialize(frame), "Model-selected split branches or a post-boundary end turn survived host action validation.");
        pass("fake model cannot promote conditional magma branches into executable followup actions or verified lethal");
    }

    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog) => new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build);
    private static Task<JsonNode> Preview(SnapshotCoach coach) => coach.ExecuteAsync("evaluate_magma_split", new() { ["source_entity_id"] = 30, ["target_entity_id"] = 40 }, default);
    private static JsonObject Actions(bool followup) => new() { ["actions"] = followup ? new JsonArray(
        new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 40 },
        new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) : new JsonArray(
        new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 40 }) };
    private static JsonNode Entity(JsonNode result, int id) => result["Entities"]!.AsArray().Single(e => e!["EntityId"]!.GetValue<int>() == id)!;
    private static bool Range(JsonNode? range, long minimum, long maximum) =>
        range?["Minimum"]?.GetValue<long>() == minimum && range?["Maximum"]?.GetValue<long>() == maximum;
    private static PowerLogStateEngine Start(string? sourceExtra = null, bool otherTaunt = false, int targetHealth = 8, string? targetExtra = null, bool targetSilenced = false)
    {
        var engine = new PowerLogStateEngine(1, "magma-integration");
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME",
            "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1",
            "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            "tag=NUM_CARDS_IN_HAND value=0", "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Character(11, "HERO_05", 1, "HERO", 0, 30), .. Character(21, "HERO_08", 2, "HERO", 0, 30),
            .. Character(30, "FIR_953", 1, "MINION", 5, 8), "tag=CARDRACE value=20", "tag=RUSH value=1"]);
        if (sourceExtra is not null) Feed(engine, [sourceExtra]);
        Feed(engine, Character(40, "CORE_UNG_912", 2, "MINION", 1, targetHealth));
        Feed(engine, [$"tag=SILENCED value={(targetSilenced ? 1 : 0)}"]);
        if (targetExtra is not null) Feed(engine, [targetExtra]);
        if (otherTaunt) Feed(engine, [.. Character(41, "CORE_UNG_912", 2, "MINION", 0, 2), "tag=TAUNT value=1"]);
        Feed(engine, [$"FULL_ENTITY - Creating ID=90 CardID={Probe}", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION",
            "tag=ATK value=321", "tag=HEALTH value=654", "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static string[] Character(int id, string card, int owner, string type, int attack, int health) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", $"tag=CARDTYPE value={type}",
            $"tag=ATK value={attack}", $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0",
            "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1"];
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<JsonNode>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class BranchCombiningModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedPreview { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains(Probe, StringComparison.Ordinal), "Private magma probe entered the model request.");
            var body = JsonNode.Parse(text)!;
            object message;
            if (Calls == 1)
            {
                Check(text.Contains("evaluate_magma_split", StringComparison.Ordinal), "Magma preview tool was not offered to the model.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "magma-preview", type = "function",
                    function = new { name = "evaluate_magma_split", arguments = "{\"source_entity_id\":30,\"target_entity_id\":40}" } } } };
            }
            else
            {
                var tool = body["messages"]!.AsArray().Single(m => m?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(result["Split"]?["Branches"]!.AsArray().Count > 1 && result["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
                    "The model did not receive independent conditional split branches.");
                CheckedPreview = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "选择最有利溅射并继续结束回合。",
                    reasons = new[] { "刻意把条件分支当实际结算" }, risks = Array.Empty<string>(), actions = new object[] {
                        new { kind = "attack", source_entity_id = 30, target_entity_id = 40 }, new { kind = "end_turn", source_entity_id = 2 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
