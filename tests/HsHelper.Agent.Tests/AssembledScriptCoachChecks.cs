using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class AssembledScriptCoachChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("assembled line search and final host lazily load a visible half donor absent from all entity identities", LazyDonor);
        yield return ("assembled executable recipe policy cannot accept model supplied halves stats or trigger counts", PolicyAndSchema);
        yield return ("assembled final host refuses a contradictory hand type and unknown attached Battlecry repetition", ConflictingHostEvidence);
    }

    private static async Task LazyDonor()
    {
        var catalog = await Catalog(); var frame = Frame(); var before = JsonSerializer.Serialize(frame);
        Check(frame.Entities.Values.All(e => e.CardId != "TIME_100") && SnapshotCardEffects.VisibleDonorDbfIds(frame, "255790").SequenceEqual([121051]),
            "The lazy donor test accidentally supplied an entity with the donor identity.");
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, new(), catalog, "255790", toolCompleted: observations.Add);
        var result = await coach.ExecuteAsync("evaluate_line", Line(), default);
        Check(result["Verification"]!.ToString() == "Verified" && result["IsLethal"]!.GetValue<bool>() && result["AppliedActions"]!.GetValue<int>() == 3 &&
            result["Result"]!["AvailableMana"]!.GetValue<int>() == 0, "Lazy donor loading did not reach the shared complete action evaluator.");
        var proof = result["Result"]!["AssembledEffects"]!.AsArray().Single()!;
        var target = proof["Targets"]!.AsArray().Single()!;
        Check(proof["MatchId"]!.ToString() == frame.MatchId && proof["StateVersion"]!.GetValue<long>() == frame.StateVersion &&
            proof["EventSequence"]!.GetValue<long>() == frame.EventSequence && proof["CardBuild"]!.ToString() == "255790" &&
            proof["SourceEntityId"]!.GetValue<int>() == 30 && proof["FirstHalfDbfId"]!.GetValue<int>() == 0 &&
            proof["EffectCardId"]!.ToString() == "TIME_100" && proof["ExecutedCount"]!.GetValue<int>() == 1 &&
            target["EntityId"]!.GetValue<int>() == 31 && target["AttackBefore"]!.GetValue<int>() == 1 && target["AttackAfter"]!.GetValue<int>() == 2 &&
            !result["Result"]!["DiscoveryBoundaryReached"]!.GetValue<bool>() && observations.Single().Success,
            "The exact recipe or binding was lost, the source buffed itself, or base Discover returned.");
        var search = await coach.ExecuteAsync("find_lethal", new() { ["maximum_depth"] = 3, ["maximum_nodes"] = 300 }, default);
        Check(search["VerifiedLethalFound"]!.GetValue<bool>() && search["Actions"]!.AsArray().Count == 3,
            "Search did not enumerate a non-Charge hand-buff source needed before Charge lethal.");
        var validation = await coach.Capabilities().ValidateActions!([new("play", 30), new("play", 31), new("attack", 31, 11)], default);
        Check(validation.Status == AdviceValidationStatus.Verified && validation.VerifiedLethal && validation.IsLegal && JsonSerializer.Serialize(frame) == before,
            "Final host validation used a different donor path or rewrote its fixed snapshot.");
        foreach (var unknown in new[] { frame with { ClientBuild = null }, frame with { IsSpectating = true }, frame with { LocalPlayerId = null } })
        {
            Check(SnapshotCardEffects.VisibleDonorDbfIds(unknown, "255790").Count == 0, "Unknown build or perspective supplied private donor IDs.");
            var rejected = await new SnapshotCoach(unknown, new(), catalog, "255790").ExecuteAsync("evaluate_line", Line(), default);
            Check(rejected["Verification"]!.ToString() != "Verified" && !rejected["IsLethal"]!.GetValue<bool>(),
                "An unbound recipe became a verified private action.");
        }
    }

    private static Task PolicyAndSchema()
    {
        foreach (var task in new[] { AdviceTaskKind.Turn, AdviceTaskKind.Choice })
        {
            var policy = AdviceTaskTemplates.Get(task);
            Check(policy.Version == "hunter-v25/" + task.ToString().ToLowerInvariant() && policy.Instructions.Contains("AssembledEffects", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不是对局中已发生的事实", StringComparison.Ordinal) && policy.Instructions.Contains("候选生成池", StringComparison.Ordinal),
                "The policy promoted an execution projection into observed game history or generated eligibility.");
        }
        foreach (var task in new[] { AdviceTaskKind.Review, AdviceTaskKind.Mulligan, AdviceTaskKind.DeckPlan })
            Check(AdviceTaskTemplates.Get(task).Version == (task == AdviceTaskKind.DeckPlan ? "hunter-v8/" : "hunter-v7/") + task.ToString().ToLowerInvariant(), "A task policy lost its current template binding.");
        var tool = SnapshotCoach.Definitions.Single(d => d.Name == "evaluate_line");
        foreach (var key in new[] { "first_half_dbf_id", "second_half_dbf_id", "trigger_count", "attack", "health", "script_key", "donor_card_id" })
        {
            var root = Line(); root[key] = 1;
            Check(!tool.ValidateArguments(root), "The model replaced observed recipe evidence: " + key);
            var action = Line(); action["actions"]![0]![key] = 1;
            Check(!tool.ValidateArguments(action), "An action supplied a forged recipe field: " + key);
        }
        return Task.CompletedTask;
    }

    private static async Task ConflictingHostEvidence()
    {
        var catalog = await Catalog(); var original = Frame();
        var wrongType = original.Entities.ToDictionary(); var tags = wrongType[31].Tags.ToDictionary(); tags["CARDTYPE"] = "SPELL";
        wrongType[31] = wrongType[31] with { Tags = tags.ToFrozenDictionary() };
        var candidates = new List<GameSnapshot> { original with { Entities = wrongType.ToFrozenDictionary() } };
        foreach (var enchantment in new[] { "EDR_100t7e", "EDR_100t7e1" })
        {
            var entities = original.Entities.ToDictionary(); entities[50] = new(50, enchantment, EntityKind.Card,
                Tags(("CARDTYPE", "ENCHANTMENT"), ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("ATTACHED", "30")));
            candidates.Add(original with { Entities = entities.ToFrozenDictionary() });
        }
        foreach (var frame in candidates)
        {
            var before = JsonSerializer.Serialize(frame); var coach = new SnapshotCoach(frame, new(), catalog, "255790");
            var result = await coach.ExecuteAsync("evaluate_line", new() { ["actions"] = new JsonArray(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 }) }, default);
            Check(result["Verification"]!.ToString() != "Verified" && result["AppliedActions"]!.GetValue<int>() == 0 &&
                result["Result"]!["AvailableMana"]!.GetValue<int>() == 5 &&
                result["Result"]!["Entities"]!.AsArray().Single(e => e!["EntityId"]!.GetValue<int>() == 30)!["Zone"]!.ToString() == "HAND",
                "Host execution silently skipped a known hand minion or assumed one Battlecry despite attached repeat evidence.");
            if (result["Result"]!["AssembledEffects"] is JsonArray effects)
                Check(effects.All(e => e!["ExecutedCount"] is null), "Unknown attached repeat evidence was recorded as a definite simulated execution count.");
            var validation = await coach.Capabilities().ValidateActions!([new("play", 30)], default);
            Check(validation.Status != AdviceValidationStatus.Verified && !validation.VerifiedLethal && before == JsonSerializer.Serialize(frame),
                "Final host bypassed an unresolved type or repeat boundary, or changed its source observation.");
        }
    }

    private static JsonObject Line() => new() { ["actions"] = new JsonArray(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 },
        new JsonObject { ["kind"] = "play", ["source_entity_id"] = 31 }, new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 31, ["target_entity_id"] = 11 }) };
    private static GameSnapshot Frame() => new("assembled-coach-lazy-donor", 10, 100, 5, 1, 1, GamePhase.Main, new[]
    {
        new EntitySnapshot(2, null, EntityKind.Player, Tags(("PLAYER_ID", "1"), ("CONTROLLER", "1"), ("HERO_ENTITY", "10"), ("RESOURCES", "5"), ("RESOURCES_USED", "0"), ("NUM_CARDS_IN_HAND", "2"))),
        new EntitySnapshot(3, null, EntityKind.Player, Tags(("PLAYER_ID", "2"), ("CONTROLLER", "2"), ("HERO_ENTITY", "11"))),
        Body(10, "HERO_05", "HERO", "PLAY", 1, 0, 30, 0), Body(11, "HERO_08", "HERO", "PLAY", 2, 0, 2, 0),
        Body(30, "EDR_102", "MINION", "HAND", 1, 5, 4, 4, ("CRAFTABLE_FIRST_HALF_DBID", "0"), ("CRAFTABLE_SECOND_HALF_DBID", "121051")),
        Body(31, "CS2_171", "MINION", "HAND", 1, 1, 1, 1, ("CHARGE", "1"))
    }.ToFrozenDictionary(e => e.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Body(int id, string card, string type, string zone, int owner, int attack, int health, int cost, params (string Key, string Value)[] extra) =>
        new(id, card, EntityKind.Card, Tags(new[] { ("ENTITY_ID", id.ToString()), ("CARDTYPE", type), ("CONTROLLER", owner.ToString()), ("ZONE", zone),
            ("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ("COST", cost.ToString()), ("EXHAUSTED", "0") }.Concat(extra).ToArray()));
    private static FrozenDictionary<string, string> Tags(params (string Key, string Value)[] pairs) => pairs.ToFrozenDictionary(p => p.Key, p => p.Value);
    private static async Task<CardCatalog> Catalog()
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var rows = new Dictionary<string, JsonNode>();
        foreach (var name in new[] { "assembled-scripts-255790.json", "lethal-search-255790.json" })
        {
            var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", name)))!;
            foreach (var row in fixture["cards"]!.AsArray()) rows[row!["id"]!.ToString()] = row.DeepClone();
        }
        var catalog = new CardCatalog(Path.Combine(workspace!.FullName, ".local", "assembled-coach", Guid.NewGuid().ToString("N"), "cards.db"));
        await catalog.InitializeAsync();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new JsonArray(rows.Values.Select(v => v.DeepClone()).ToArray()).ToJsonString()));
        await catalog.ImportAsync(input, "255790", source: "fixture://assembled-coach"); return catalog;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
