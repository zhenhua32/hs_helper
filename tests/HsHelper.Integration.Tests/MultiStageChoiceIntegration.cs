using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class MultiStageChoiceIntegration
{
    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var cards = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in new[] { "beast-hunter-255790.json", "craft-choice-255790.json", "rewind-choice-255790.json" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name)));
            foreach (var card in fixture.RootElement.GetProperty("cards").EnumerateArray()) cards[card.GetProperty("id").GetString()!] = card.Clone();
        }
        var catalog = new CardCatalog(Path.Combine(root, "multi-stage-choice", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cards.Values))))
            await catalog.ImportAsync(input, "255790", source: "fixture://multi-stage-choice");

        var engine = Start("TLC_EVENT_400");
        Feed(engine, Candidate(40, "EDR_227", 2, 3, 4));
        Feed(engine, Offer(7, (40, "EDR_227")));
        var first = await Evaluate(engine.Snapshot(), catalog, 40);
        Check(first["Crafting"]?["Stage"]?.ToString() == "ComponentOne" && first["HandCountAfter"]?.GetValue<int>() == 0 &&
            first["CardOutcomes"]!.AsArray().Count == 0, "Parser's first component became an early hand card.");
        Select(engine, 7, 40);
        Feed(engine, ["TAG_CHANGE Entity=40 tag=COST value=99", "TAG_CHANGE Entity=40 tag=ATK value=99"]);
        Feed(engine, Candidate(60, "EDR_480", 4, 5, 6));
        Feed(engine, Offer(8, (60, "EDR_480")));
        var frame = engine.Snapshot();
        var before = JsonSerializer.Serialize(frame);
        var result = await Evaluate(frame, catalog, 60);
        Check(result["Crafting"] is JsonObject crafted && crafted["Stage"]?.ToString() == "ComponentTwo" &&
            crafted["ResultingCost"]?.GetValue<int>() == 3 && crafted["ResultingAttack"]?.GetValue<int>() == 8 &&
            crafted["ResultingHealth"]?.GetValue<int>() == 10 && crafted["GrantedCostReduction"]?.GetValue<int>() == 3 &&
            result["HandCountAfter"]?.GetValue<int>() == 1 && result["CardOutcomes"]!.AsArray().Count == 0 &&
            result["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && before == JsonSerializer.Serialize(frame),
            "Parser/host craft chain lost frozen first attributes, current second attributes or its preview boundary.");
        pass("synthetic chosen-row replay reaches the craft host with frozen first component and current second-component attributes");

        var compact = CompactAdviceContextBuilder.Build(frame, "255790");
        Check(compact["choice"]?["source_card_id"]?.ToString() == "TLC_EVENT_400" &&
            compact["choice"]?["options"]?[0]?["observed_tags"]?["COST"]?.ToString() == "4" &&
            !compact.ToJsonString().Contains("PRIVATE_UNRELATED", StringComparison.Ordinal) && !compact.ContainsKey("choice_history"),
            "Current choice context lost observed attributes or exported raw private history/unrelated candidates.");
        var history = frame.ChoiceHistory!;
        var future = frame with { ChoiceHistory = new(history.MatchId, history.StateVersion, history.EventSequence + 1,
            history.Entries, true, 0, []) };
        var rejected = await Evaluate(future, catalog, 60);
        Check(rejected["ResolvesEffects"]?.GetValue<bool>() == false && rejected["Crafting"]?["Stage"]?.ToString() == "Unconfirmed",
            "A future choice ledger supplied an old decision's component.");
        var spectated = await Evaluate(frame with { IsSpectating = true }, catalog, 60);
        Check(spectated["ResolvesEffects"]?.GetValue<bool>() == false && spectated["Crafting"] is null && spectated["SourceCardId"] is null,
            "Spectator mode exposed a private craft projection.");
        pass("multi-stage host rejects future ledgers and spectator consequences while the compact context exposes only current safe attributes");

        engine = Start("CORE_EDR_004_2026");
        Feed(engine, Candidate(40, "EDR_227", 3, 3, 4));
        Feed(engine, Candidate(41, "EDR_480", 9, 9, 9));
        Feed(engine, Offer(7, (40, "EDR_227"), (41, "EDR_480")));
        Select(engine, 7, 40);
        Feed(engine, ["TAG_CHANGE Entity=40 tag=ZONE value=HAND", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1"]);
        Feed(engine, Offer(8, (60, "EDR_100t8"), (61, "EDR_100t")));
        var giftFrame = engine.Snapshot();
        var gift = await Evaluate(giftFrame, catalog, 60);
        Check(gift["Rewind"] is JsonObject grant && grant["Stage"]?.ToString() == "DarkGift" &&
            grant["HistoryBound"]?.GetValue<bool>() == true && grant["TargetEntityId"]?.GetValue<int>() == 40 &&
            grant["DestinationAfterGift"]?.ToString() == "DeckTop" && grant["AttackDelta"]?.GetValue<int>() == 4 &&
            grant["HealthDelta"]?.GetValue<int>() == 5 && gift["HandCountAfter"] is null &&
            grant["KindredActive"] is null && gift["CardOutcomes"]!.AsArray().Count == 0,
            "Gift host lost unchosen historical pool definitions, applied to a different Beast or invented hand/Kindred state.");
        pass("Dark Gift host loads the prior offered pool and applies Sweet Dreams only to the actual chosen Beast without inventing hand timing");

        Select(engine, 8, 60);
        Feed(engine, Offer(9, (70, "TIME_000ta"), (71, "TIME_000tb")));
        var timeline = await Evaluate(engine.Snapshot(), catalog, 71);
        Check(timeline["Rewind"]?["RequestsRewind"]?.GetValue<bool>() == true &&
            timeline["Rewind"]?["RestoredMana"] is null && timeline["Rewind"]?["RestoredHandCount"] is null &&
            timeline["HandCountAfter"] is null && timeline["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
            "A parsed rewind button restored an unobserved earlier timeline.");
        pass("parsed timeline buttons request a rewind without fabricating restored resources or later actions");
    }

    private static async Task<JsonNode> Evaluate(GameSnapshot frame, CardCatalog catalog, int selected)
    {
        var coach = new SnapshotCoach(frame, CompactAdviceContextBuilder.Build(frame, "255790"), catalog, "255790");
        return await coach.ExecuteAsync("evaluate_choice", new() { ["choice_id"] = frame.Choice!.Id, ["selected_entity_ids"] = new JsonArray(selected) }, default);
    }

    // Synthetic protocol rows follow the shared Core regression fixture; no live game records.
    private static PowerLogStateEngine Start(string source)
    {
        var engine = new PowerLogStateEngine(1, "multi-stage-" + source);
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED",
            "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=8", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1", "tag=NUM_CARDS_IN_HAND value=0", "tag=MAXHANDSIZE value=10",
            "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0",
            $"FULL_ENTITY - Creating ID=30 CardID={source}", $"tag=CARDTYPE value={(source == "TLC_EVENT_400" ? "SPELL" : "MINION")}", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY",
            "tag=QUEST_PROGRESS value=3", "tag=QUEST_PROGRESS_TOTAL value=3",
            .. Candidate(90, "PRIVATE_UNRELATED", 9, 9, 9), "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static string[] Candidate(int id, string card, int cost, int attack, int health) => [
        $"FULL_ENTITY - Creating ID={id} CardID={card}", "tag=CONTROLLER value=1", "tag=ZONE value=SETASIDE", "tag=CARDTYPE value=MINION",
        $"tag=COST value={cost}", $"tag=ATK value={attack}", $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=CARDRACE value=20"];
    private static IEnumerable<string> Offer(int id, params (int Id, string Card)[] options)
    {
        yield return $"GameState.DebugPrintEntityChoices() - id={id} Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 player=1]";
        for (var i = 0; i < options.Length; i++)
            yield return $"GameState.DebugPrintEntityChoices() - Entities[{i}]=[entityName=Anonymous id={options[i].Id} zone=SETASIDE cardId={options[i].Card} player=1]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static void Select(PowerLogStateEngine engine, int choice, int entity) => Feed(engine, [
        $"GameState.DebugPrintEntitiesChosen() - id={choice} Player=1 EntitiesCount=1", $"GameState.DebugPrintEntitiesChosen() - Entities[0]={entity}",
        "ChoiceCardMgr.OnChoicesShown() - shown"]);
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
