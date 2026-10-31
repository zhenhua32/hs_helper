using System.Collections.Frozen;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Rules;

internal static class CoachPrivacyIntegration
{
    public static async Task RunAsync(CardCatalog catalog, GameSnapshot original, Action<string> pass)
    {
        var entities = original.Entities.ToDictionary();
        var playerTags = entities[2].Tags.ToDictionary();
        playerTags["USER_ID"] = "123456789"; playerTags["BATTLETAG"] = "987654321"; playerTags["PLAYER_NAME"] = "555444333";
        entities[2] = entities[2] with { Tags = playerTags.ToFrozenDictionary() };
        entities[200] = entities[200] with { CardId = "PRIVATE_HAND_IDENTITY" };
        entities[104] = entities[104] with { CardId = "PRIVATE_DECK_IDENTITY" };
        var heroTags = entities[20].Tags.ToDictionary(); heroTags["HEALTH"] = "1"; heroTags["DAMAGE"] = "0";
        entities[20] = entities[20] with { Tags = heroTags.ToFrozenDictionary() };
        var attackerTags = entities[100].Tags.ToDictionary();
        attackerTags["ZONE"] = "PLAY"; attackerTags["NUM_TURNS_IN_PLAY"] = "1";
        attackerTags["NUM_ATTACKS_THIS_TURN"] = "0"; attackerTags["EXHAUSTED"] = "0";
        entities[3000] = entities[100] with { Id = 3000, Tags = attackerTags.ToFrozenDictionary() };
        var snapshot = original with { Entities = entities.ToFrozenDictionary() };
        var cards = (await catalog.GetByIdsAsync(original.Entities.Values.Select(entity => entity.CardId).OfType<string>().Distinct(), SyntheticFixture.Build))
            .ToDictionary(card => card.Id);
        var context = CompactAdviceContextBuilder.Create(snapshot, SyntheticFixture.Build, AdviceTaskKind.Turn, cards: cards.Values);
        var coach = new SnapshotCoach(snapshot, context.Snapshot, catalog, SyntheticFixture.Build, taskKind: context.TaskKind);
        var actions = new JsonArray(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 100 },
            new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 100, ["target_entity_id"] = 20 });
        var line = await coach.ExecuteAsync("evaluate_line", new JsonObject { ["actions"] = actions }, default);
        var lethal = await coach.ExecuteAsync("find_lethal", new JsonObject { ["maximum_depth"] = 2, ["maximum_nodes"] = 100 }, default);
        Check(line["Verification"]!.GetValue<string>() == "Verified" && line["IsLethal"]!.GetValue<bool>(), "Privacy fixture did not exercise an actual verified line");
        Check(lethal["VerifiedLethalFound"]!.GetValue<bool>() && lethal["Evaluation"] is not null, "Privacy fixture did not exercise nested lethal evaluation results");
        foreach (var result in new[] { context.Snapshot, line, lethal })
            foreach (var forbidden in new[] { "USER_ID", "BATTLETAG", "PLAYER_NAME", "123456789", "987654321", "555444333", "PRIVATE_HAND_IDENTITY", "PRIVATE_DECK_IDENTITY" })
                Check(!result.ToJsonString().Contains(forbidden, StringComparison.Ordinal), "Private input reappeared through a rule-tool result: " + forbidden);
        foreach (var result in new[] { line["Result"]!, lethal["Evaluation"]!["Result"]! })
        {
            var visible = result["Entities"]!.AsArray();
            Check(!visible.Any(entity => entity!["EntityId"]!.GetValue<int>() is 200 or 104) &&
                visible.Any(entity => entity!["EntityId"]!.GetValue<int>() == 100), "Rule-tool projection leaked hidden slots or dropped a visible played entity");
            Check(visible.Single(entity => entity!["EntityId"]!.GetValue<int>() == 2)!["Flags"]!["PLAYER_ID"]!.GetValue<int>() == 1,
                "Privacy projection removed the public side identifier needed to understand the result");
        }
        var local = new LineEvaluator().EvaluateLine(snapshot, [new(ActionKind.PlayCard, 100), new(ActionKind.Attack, 100, 20)], cards);
        Check(local.Result.Entities.Any(entity => entity.Flags.ContainsKey("USER_ID")) &&
            local.Result.Entities.Any(entity => entity.CardId == "PRIVATE_HAND_IDENTITY"), "Tool projection mutated the complete local validation input or result");
        pass("evaluate_line and nested find_lethal results filter hidden slots and numeric identity flags while local validation stays complete");

        var unidentified = snapshot.Entities.ToDictionary();
        var halfTags = new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "HAND", ["CARDTYPE"] = "MINION", ["COST"] = "2",
            ["CRAFTABLE_FIRST_HALF_DBID"] = "130871", ["CRAFTABLE_SECOND_HALF_DBID"] = "118076", ["4572"] = "130871", ["4573"] = "118076" };
        unidentified[3300] = new(3300, null, EntityKind.Card, halfTags.ToFrozenDictionary());
        unidentified[3310] = entities[3000] with { Id = 3310, Tags = entities[3000].Tags.Concat(halfTags.Where(pair => pair.Key is not ("CONTROLLER" or "ZONE" or "CARDTYPE" or "COST"))).ToFrozenDictionary() };
        var unidentifiedFrame = snapshot with { Entities = unidentified.ToFrozenDictionary() };
        var unidentifiedCoach = new SnapshotCoach(unidentifiedFrame, CompactAdviceContextBuilder.Build(unidentifiedFrame, SyntheticFixture.Build), catalog, SyntheticFixture.Build);
        var unidentifiedLine = await unidentifiedCoach.ExecuteAsync("evaluate_line", new() { ["actions"] = actions.DeepClone() }, default);
        var projected = unidentifiedLine["Result"]!["Entities"]!.AsArray();
        var unknownFlags = projected.Single(entity => entity!["EntityId"]!.GetValue<int>() == 3300)!["Flags"]!;
        var knownFlags = projected.Single(entity => entity!["EntityId"]!.GetValue<int>() == 3310)!["Flags"]!;
        foreach (var key in new[] { "CRAFTABLE_FIRST_HALF_DBID", "CRAFTABLE_SECOND_HALF_DBID", "4572", "4573" })
            Check(unknownFlags[key] is null && knownFlags[key] is not null, "Unidentified card disclosed an assembled donor or a visible identity lost its evidence.");
        Check(unknownFlags["COST"]?.GetValue<int>() == 2 && unidentifiedFrame.Entities[3300].Tags.ContainsKey("4572"),
            "Identity filtering removed unrelated observed properties or mutated the local snapshot.");
        pass("rule-tool entity flags exclude assembled donor references for unidentified cards while known public identities retain them");

        var planningContext = CompactAdviceContextBuilder.Create(snapshot, SyntheticFixture.Build, AdviceTaskKind.DeckPlan, cards: cards.Values);
        var planning = new SnapshotCoach(snapshot, planningContext.Snapshot, catalog, SyntheticFixture.Build, taskKind: planningContext.TaskKind);
        var capabilities = planning.Capabilities();
        Check(capabilities.ValidateActions is null && capabilities.Tools.Count == 0, "DeckPlan on a Main snapshot exposed live rule capabilities");
        var forbiddenTools = new (string Name, JsonObject Arguments)[]
        {
            ("evaluate_line", new() { ["actions"] = actions.DeepClone() }),
            ("find_lethal", new() { ["maximum_depth"] = 1, ["maximum_nodes"] = 10 }),
            ("calculate_draw_odds", new() { ["card_id"] = SyntheticFixture.CardId(1), ["draws"] = 1 }),
            ("validate_choice", new() { ["choice_id"] = 1, ["selected_entity_ids"] = new JsonArray(100) }),
            ("evaluate_choice", new() { ["choice_id"] = 1, ["selected_entity_ids"] = new JsonArray(100) }),
            ("evaluate_generation", new() { ["source_entity_id"] = 100 }),
            ("evaluate_wolf_blessing", new() { ["source_entity_id"] = 100 }),
            ("get_recent_events", new())
        };
        foreach (var (name, args) in forbiddenTools) await Throws<ArgumentException>(() => planning.ExecuteAsync(name, args, default));
        Check(JsonNode.DeepEquals(await planning.ExecuteAsync("get_snapshot", new(), default), planningContext.Snapshot), "Planning snapshot is no longer fixed and compact");
        using var handler = new BridgeModelHandler(requestTools: false);
        using var http = new HttpClient(handler);
        await Throws<InvalidDataException>(() => new AdviceService(http).AnalyzeAsync(new(new Uri("https://synthetic.invalid/v1/chat/completions"), "synthetic-fake-model"),
            "synthetic-dummy-key", planningContext, planning.ExecuteAsync, () => true, capabilities: capabilities));
        pass("DeckPlan task disables live calculations and rejects concrete model actions even when its input phase is Main");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
