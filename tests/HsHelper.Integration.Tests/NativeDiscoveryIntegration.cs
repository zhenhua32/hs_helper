using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class NativeDiscoveryIntegration
{
    private const string Build = "255790";
    private const string PrivateProbe = "PRIVATE_NATIVE_DISCOVERY";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var cards = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in new[] { "beast-hunter-255790.json", "discovery-play-255790.json" })
        {
            using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name)));
            Check(input.RootElement.GetProperty("build").GetString() == Build, "Unpinned native discovery fixture.");
            foreach (var card in input.RootElement.GetProperty("cards").EnumerateArray()) cards[card.GetProperty("id").GetString()!] = card.Clone();
        }
        var catalog = new CardCatalog(Path.Combine(root, "native-discovery", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cards.Values))))
            await catalog.ImportAsync(input, Build, source: "fixture://native-discovery");

        var engine = Start(2); var frame = engine.Snapshot(); var before = JsonSerializer.Serialize(frame);
        var trace = new List<CoachToolObservation>(); var coach = Coach(frame, catalog, trace);
        var direct = await coach.ExecuteAsync("evaluate_play_triggers", new() { ["source_entity_id"] = 30 }, default);
        Check(direct["Discovery"] is JsonObject preview && preview["Kind"]?.ToString() == "DeckDiscover" &&
            preview["DeckCountBeforeEffect"]?.GetValue<int>() == 2 && preview["RequiresChoice"]?.GetValue<bool>() == true &&
            !direct["HasAuthoritativeNextState"]!.GetValue<bool>() && !direct.ToJsonString().Contains(PrivateProbe, StringComparison.Ordinal),
            "Parser's own Tracking did not reach the native entry tool or leaked a private identity.");
        var line = await coach.ExecuteAsync("evaluate_line", PlayAndEnd(), default);
        Check(line["AppliedActions"]?.GetValue<int>() == 1 && line["Verification"]?.ToString() == "Unsupported" &&
            line["Result"]?["DiscoveryBoundaryReached"]?.GetValue<bool>() == true && line["Result"]?["TurnEnded"]?.GetValue<bool>() == false &&
            line["Result"]?["AvailableMana"]?.GetValue<int>() == 9 &&
            line["Result"]!["DiscoveryPreviews"]!.AsArray().Single()!["SourceCardId"]?.ToString() == "CORE_DS1_184" &&
            line["Result"]!["Entities"]!.AsArray().All(entity => entity!["EntityId"]!.GetValue<int>() != 30 &&
                frame.Entities.ContainsKey(entity["EntityId"]!.GetValue<int>()) && entity["Zone"]?.ToString() != "DECK") &&
            line["IsLethal"]?.GetValue<bool>() == false && JsonSerializer.Serialize(frame) == before &&
            trace.Count == 2 && trace.All(item => item is { Success: true, ResultHash.Length: 64, ResultJson: not null } &&
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.ResultJson))) == item.ResultHash),
            "The real host native play did not pay/leave hand, crossed the choice boundary, created entities or mutated its original parser frame.");
        pass("synthetic Parser Tracking reaches native entry and actual evaluate_line payment while its following action waits for a real offer");

        Feed(engine, ["BLOCK_START BlockType=PLAY Entity=[entityName=Tracking id=30 zone=HAND zonePos=1 cardId=CORE_DS1_184 player=1] EffectCardId= EffectIndex=0 Target=0",
            "TAG_CHANGE Entity=30 tag=ZONE value=GRAVEYARD", "TAG_CHANGE Entity=2 tag=RESOURCES_USED value=1",
            "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=0", "BLOCK_END",
            "GameState.DebugPrintEntityChoices() - id=7 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1",
            "GameState.DebugPrintEntityChoices() - Source=[entityName=Tracking id=30 player=1]",
            "GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=80 zone=DECK cardId=EDR_227 player=1]",
            "GameState.DebugPrintEntityChoices() - Entities[1]=[entityName=Anonymous id=81 zone=DECK cardId=EDR_480 player=1]",
            "ChoiceCardMgr.OnChoicesShown() - shown"]);
        var choiceFrame = engine.Snapshot(); var choiceBefore = JsonSerializer.Serialize(choiceFrame);
        Check(choiceFrame.Choice is { IsReady: true, SourceEntityId: 30, SourceCardId: "CORE_DS1_184" } && choiceFrame.Phase == GamePhase.Choice,
            "Synthetic real offer did not bind the actually used Tracking source.");
        var choice = await Coach(choiceFrame, catalog).ExecuteAsync("evaluate_choice", new()
            { ["choice_id"] = 7, ["selected_entity_ids"] = new JsonArray(80) }, default);
        Check(choice["ResolvesEffects"]?.GetValue<bool>() == true && choice["IsLegal"]?.GetValue<bool>() == true &&
            choice["CardOutcomes"]!.AsArray().Count == 1 && choice["CardOutcomes"]![0]!["CardId"]?.ToString() == "EDR_227" &&
            choice["CardOutcomes"]![0]!["Origin"]?.ToString() == "Deck" && choice["CardOutcomes"]![0]!["AddedToHand"]?.GetValue<bool>() == true &&
            choice["CardOutcomes"]![0]!["ResultingCost"] is null && choice["DirectDeckChange"]?["RemovedCopies"]?.GetValue<int>() == 1 &&
            choice["HandCountBefore"]?.GetValue<int>() == 0 && choice["HandCountAfter"]?.GetValue<int>() == 1 &&
            choice["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && JsonSerializer.Serialize(choiceFrame) == choiceBefore &&
            !choice.ToJsonString().Contains(PrivateProbe, StringComparison.Ordinal),
            "Actual Parser offer did not reach the fixed choice consequence, filled a final cost, leaked identity or changed its snapshot.");
        pass("actual Tracking choice rows bind the played source and selected deck card without fabricated final cost or post-choice frame");

        foreach (var count in new int?[] { 0, null })
        {
            var observed = Start(count).Snapshot(); var result = await Coach(observed, catalog).ExecuteAsync("evaluate_line", PlayAndEnd(), default);
            var native = result["Result"]!["DiscoveryPreviews"]!.AsArray().Single()!;
            Check(result["Result"]!["DrawPreviews"]!.AsArray().Count == 0 && Find(result, 10)["RemainingHealth"]?.GetValue<int>() == 30 &&
                Find(result, 2)["Flags"]?["FATIGUE"]?.GetValue<int>() == 3, "Tracking scope was replaced with a draw or direct fatigue.");
            if (count == 0)
                Check(result["AppliedActions"]?.GetValue<int>() == 2 && result["Result"]?["TurnEnded"]?.GetValue<bool>() == true &&
                    native["DeckCountBeforeEffect"]?.GetValue<int>() == 0 && native["RequiresChoice"]?.GetValue<bool>() == false,
                    "Explicit empty-deck count forced a nonexistent choice.");
            else Check(result["AppliedActions"]?.GetValue<int>() == 1 && result["Result"]?["TurnEnded"]?.GetValue<bool>() == false &&
                native["DeckCountBeforeEffect"] is null && native["RequiresChoice"] is null && result["Result"]?["DiscoveryBoundaryReached"]?.GetValue<bool>() == true,
                "No created deck entities plus no count became a confirmed empty deck.");
        }
        pass("Parser explicit empty Tracking scope permits a no-draw continuation while absent count keeps the choice boundary unknown");
    }

    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog, List<CoachToolObservation>? trace = null) =>
        new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build, toolCompleted: trace is null ? null : trace.Add);
    private static JsonObject PlayAndEnd() => new() { ["actions"] = new JsonArray(
        new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 }, new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) };
    private static JsonNode Find(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]!.GetValue<int>() == id)!;
    private static PowerLogStateEngine Start(int? deckCount)
    {
        var engine = new PowerLogStateEngine(1, "native-tracking-" + (deckCount?.ToString() ?? "unknown"));
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME",
            "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=10",
            "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=NUM_CARDS_IN_HAND value=1", "tag=MAXHANDSIZE value=10", "tag=FATIGUE value=3"]);
        if (deckCount.HasValue) engine.ProcessLine("tag=NUM_CARDS_IN_DECK value=" + deckCount.Value);
        Feed(engine, ["Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=11"]);
        Feed(engine, Card(10, "HERO_05", "PLAY", "HERO", 1)); Feed(engine, Card(11, "HERO_08", "PLAY", "HERO", 2));
        Feed(engine, Card(30, "CORE_DS1_184", "HAND", "SPELL", 1)); engine.ProcessLine("tag=COST value=1");
        if (deckCount > 0)
        {
            Feed(engine, Card(80, "EDR_227", "DECK", "MINION", 1));
            Feed(engine, Card(81, "EDR_480", "DECK", "MINION", 1));
        }
        Feed(engine, Card(90, PrivateProbe, "HAND", "MINION", 2));
        engine.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }
    private static IEnumerable<string> Card(int id, string card, string zone, string type, int owner)
    {
        yield return $"FULL_ENTITY - Creating ID={id} CardID={card}"; yield return $"tag=CONTROLLER value={owner}";
        yield return $"tag=ZONE value={zone}"; yield return $"tag=CARDTYPE value={type}";
        if (type == "HERO")
        { yield return "tag=HEALTH value=30"; yield return "tag=DAMAGE value=0"; yield return "tag=ARMOR value=0"; yield return "tag=ATK value=0"; }
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
