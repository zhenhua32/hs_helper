using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class HunterHistoryIntegration
{
    private const string Build = "255790";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        if (workspace is null) throw new InvalidOperationException("Could not locate pinned rule fixtures.");
        var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var fixtureName in new[] { "beast-hunter-255790.json", "rewind-choice-255790.json", "imbue-malorne-255790.json" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", fixtureName)));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Integration fixture is not version pinned.");
            foreach (var card in fixture.RootElement.GetProperty("cards").EnumerateArray())
                definitions[card.GetProperty("id").GetString()!] = card.Clone();
        }
        var catalog = new CardCatalog(Path.Combine(root, "hunter-history", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definitions.Values))))
            await catalog.ImportAsync(input, Build, source: "fixture://hunter-history-integration");

        var normal = RaptorChoice(definitions, extraFriendlyTurn: false);
        var normalBefore = JsonSerializer.Serialize(normal);
        var nativeKindred = await Choice(normal, catalog, 60);
        var sourcePlay = normal.TurnHistory!.Turns.Last().Plays.Single(play => play.AtPlay.Id == 30);
        Check(normal.Quality.IsComplete && normal.Quality.IsStable && sourcePlay.AtPlay.Zone == "HAND" &&
            sourcePlay.AtPlay.CardId == "CORE_EDR_004_2026" &&
            nativeKindred["Rewind"]?["KindredActive"]?.GetValue<bool>() == true &&
            nativeKindred["Rewind"]?["KindredCostReduction"]?.GetValue<int>() == 1 &&
            nativeKindred["Rewind"]?["KindredSourcePlaySequence"]?.GetValue<long>() == sourcePlay.Sequence &&
            nativeKindred["Rewind"]?["ResultingCost"] is null && nativeKindred["CardOutcomes"]!.AsArray().Count == 0 &&
            nativeKindred["CanEvaluateFollowupActions"]?.GetValue<bool>() == false &&
            normalBefore == JsonSerializer.Serialize(normal),
            "Parser-to-host Raptor choice lost its actual hand PLAY or native Kindred grant boundary.");
        pass("parsed own turn windows and an actual hand PLAY reach the Raptor choice host with a native one-mana Kindred grant");

        var extra = RaptorChoice(definitions, extraFriendlyTurn: true);
        var extraResult = await Choice(extra, catalog, 60);
        Check(extra.TurnHistory!.IsComplete && extra.TurnHistory.Turns.Select(turn => turn.PlayerId).SequenceEqual([1, 1, 1]) &&
            extra.TurnHistory.Turns[0].Plays.Count == 1 && extra.TurnHistory.Turns[1].Plays.Count == 0 &&
            extraResult["Rewind"]?["KindredActive"]?.GetValue<bool>() == false &&
            extraResult["Rewind"]?["KindredCostReduction"]?.GetValue<int>() == 0 &&
            extraResult["Rewind"]?["KindredPreviousOwnTurnStart"]?.GetValue<long>() == extra.TurnHistory.Turns[1].StartedAtEventSequence,
            "Kindred used turn minus two instead of the immediately preceding actual friendly turn.");
        pass("parsed extra friendly turns make Kindred inspect the last own turn rather than turn minus two");

        var history = normal.TurnHistory!;
        var future = normal with
        {
            TurnHistory = new(history.MatchId, history.StateVersion, history.EventSequence + 1,
                history.Turns, history.IsComplete, history.TruncatedThroughSequence, history.Warnings)
        };
        var futureResult = await Choice(future, catalog, 60);
        var watching = await Choice(normal with { IsSpectating = true }, catalog, 60);
        Check(futureResult["Rewind"]?["KindredActive"] is null && futureResult["Rewind"]?["KindredCostReduction"] is null &&
            futureResult["Rewind"]?["KindredMatchingPlaySequence"] is null &&
            watching["ResolvesEffects"]?.GetValue<bool>() == false && watching["Rewind"] is null && watching["SourceCardId"] is null,
            "A future turn ledger or spectator perspective supplied private Kindred evidence.");
        pass("the choice host rejects future turn evidence and suppresses spectator Kindred consequences");

        var malorneEngine = Start("malorne");
        Begin(malorneEngine, 1, 1);
        Feed(malorneEngine, ["TAG_CHANGE Entity=2 tag=3527 value=4"]);
        Feed(malorneEngine, Minion(definitions, 30, "EDR_888", "HAND"));
        Play(malorneEngine, 30);
        Feed(malorneEngine, Minion(definitions, 60, "EDR_480", "SETASIDE"));
        Feed(malorneEngine, Offer(7, "EDR_480"));
        var malorneFrame = malorneEngine.Snapshot();
        var malorne = await Choice(malorneFrame, catalog, 60);
        Check(malorneFrame.Entities[2].GetIntTag("3527") == 4 &&
            malorne["Malorne"]?["Imbues"]?["Count"]?.GetValue<int>() == 4 &&
            malorne["Malorne"]?["ImbueConditionMet"]?.GetValue<bool>() == true &&
            malorne["Malorne"]?["GrantedSetCost"]?.GetValue<int>() == 1 &&
            malorne["Malorne"]?["ResultingCost"] is null && malorne["HandCountAfter"]?.GetValue<int>() == 1 &&
            malorne["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
            "Parsed cumulative Imbues did not reach Malorne as set-cost-one, or became an authoritative current cost.");
        pass("parsed Player tag3527 and a Malorne Wild God offer reach the host as a set-cost-one grant with unknown final cost");

        var drawEngine = Start("draw");
        Begin(drawEngine, 1, 1);
        Feed(drawEngine, ["TAG_CHANGE Entity=2 tag=3527 value=2", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1",
            "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_DECK value=3"]);
        Feed(drawEngine, Minion(definitions, 30, "FIR_921", "HAND"));
        for (var index = 0; index < 3; index++)
            Feed(drawEngine, [$"FULL_ENTITY - Creating ID={1000 + index} CardID=", "tag=CONTROLLER value=1", "tag=ZONE value=DECK"]);
        Feed(drawEngine, ["PowerProcessor.EndCurrentTaskList() - complete"]);
        var drawFrame = drawEngine.Snapshot();
        var drawBefore = JsonSerializer.Serialize(drawFrame);
        var drawCoach = Coach(drawFrame, catalog);
        var draw = await drawCoach.ExecuteAsync("evaluate_draw", new() { ["source_entity_id"] = 30 }, default);
        Check(drawFrame.Phase == GamePhase.Main && drawFrame.Quality.IsComplete && drawFrame.Quality.IsStable &&
            drawFrame.Entities[30].Zone == "HAND" && draw["ResolvesEffects"]?.GetValue<bool>() == true &&
            Exact(draw["RequestedDraws"], 2) && Exact(draw["CardsRemovedFromDeck"], 2) &&
            Exact(draw["CardsAddedToHand"], 2) && Exact(draw["CardsBurned"], 0) &&
            draw["CanEvaluateFollowupActions"]?.GetValue<bool>() == false &&
            drawBefore == JsonSerializer.Serialize(drawFrame) && draw["Entities"] is null && draw["CardOutcomes"] is null,
            "Parsed FIR_921/count-two did not yield a bounded direct two-card preview or invented drawn entities.");
        pass("parsed FIR921 hand source and cumulative count two reach evaluate_draw without drawing identities or mutating the frame");

        var line = await drawCoach.ExecuteAsync("evaluate_line", new()
        {
            ["actions"] = new JsonArray(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 },
                new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 })
        }, default);
        var projected = line["Result"]!;
        Check(line["Verification"]?.ToString() == "Unsupported" && line["AppliedActions"]?.GetValue<int>() == 1 &&
            projected["DrawBoundaryReached"]?.GetValue<bool>() == true && projected["TurnEnded"]?.GetValue<bool>() == false &&
            projected["ImbueCountBefore"]?.GetValue<int>() == 2 && projected["ImbueCountAfter"]?.GetValue<int>() == 2 &&
            projected["DrawPreviews"]!.AsArray().Count == 1 && Exact(projected["DrawPreviews"]?[0]?["RequestedDraws"], 2) &&
            line["UnsupportedMechanics"]!.AsArray().Any(item => item?.ToString() == "AWAIT_DRAW_SNAPSHOT") &&
            projected["Entities"]!.AsArray().All(item => drawFrame.Entities.ContainsKey(item!["EntityId"]!.GetValue<int>())) &&
            projected["Entities"]!.AsArray().All(item => item?["Zone"]?.ToString() != "HAND") &&
            drawBefore == JsonSerializer.Serialize(drawFrame),
            "The host crossed an unresolved draw boundary into end_turn or fabricated a new hand entity.");
        pass("evaluate_line stops after the source play at the draw boundary and leaves the submitted end_turn unapplied");
    }

    private static GameSnapshot RaptorChoice(IReadOnlyDictionary<string, JsonElement> definitions, bool extraFriendlyTurn)
    {
        var engine = Start(extraFriendlyTurn ? "kindred-extra" : "kindred-normal");
        Begin(engine, 1, 1);
        Feed(engine, Minion(definitions, 40, "EDR_227", "HAND"));
        Play(engine, 40);
        End(engine);
        Begin(engine, 2, extraFriendlyTurn ? 1 : 2);
        End(engine);
        Begin(engine, 3, 1);
        Feed(engine, Minion(definitions, 30, "CORE_EDR_004_2026", "HAND"));
        Play(engine, 30);
        Feed(engine, Minion(definitions, 60, "EDR_227", "SETASIDE"));
        Feed(engine, Offer(7, "EDR_227"));
        return engine.Snapshot();
    }

    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog) =>
        new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build);
    private static Task<JsonNode> Choice(GameSnapshot frame, CardCatalog catalog, int selected) =>
        Coach(frame, catalog).ExecuteAsync("evaluate_choice",
            new() { ["choice_id"] = frame.Choice!.Id, ["selected_entity_ids"] = new JsonArray(selected) }, default);
    private static bool Exact(JsonNode? range, long value) =>
        range?["Minimum"]?.GetValue<long>() == value && range?["Maximum"]?.GetValue<long>() == value;

    // Protocol rows are generated solely for this test; source definitions come from fixed public fixtures above.
    private static PowerLogStateEngine Start(string name)
    {
        var engine = new PowerLogStateEngine(1, "hunter-history-" + name);
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED",
            "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1",
            "tag=HERO_ENTITY value=11", "tag=HERO_POWER_ENTITY value=12", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0",
            "tag=TEMP_RESOURCES value=0", "tag=MAXHANDSIZE value=10", "tag=NUM_CARDS_IN_HAND value=0", "tag=NUM_CARDS_IN_DECK value=0",
            "tag=FATIGUE value=0", "tag=CANT_DRAW value=0", "tag=CANT_BE_FATIGUED value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Hero(11, "HERO_05", 1, 3), .. Hero(21, "HERO_08", 2, 4),
            "FULL_ENTITY - Creating ID=12 CardID=HERO_05bp", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY",
            "tag=CARDTYPE value=HERO_POWER", "tag=COST value=2", "tag=EXHAUSTED value=0"]);
        return engine;
    }
    private static string[] Hero(int id, string card, int owner, int heroClass) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO",
            $"tag=CLASS value={heroClass}", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0"];
    private static IEnumerable<string> Minion(IReadOnlyDictionary<string, JsonElement> definitions, int id, string cardId, string zone)
    {
        var card = definitions[cardId];
        Check(card.GetProperty("type").GetString() == "MINION", "Synthetic observed source must have a fixed minion definition.");
        yield return $"FULL_ENTITY - Creating ID={id} CardID={cardId}";
        yield return "tag=CONTROLLER value=1";
        yield return $"tag=ZONE value={zone}";
        yield return "tag=CARDTYPE value=MINION";
        foreach (var (property, tag) in new[] { ("cost", "COST"), ("attack", "ATK"), ("health", "HEALTH") })
            yield return $"tag={tag} value={card.GetProperty(property).GetInt32()}";
        yield return "tag=DAMAGE value=0";
        if (card.TryGetProperty("race", out var race)) yield return $"tag=CARDRACE value={race.GetString()}";
    }
    private static void Begin(PowerLogStateEngine engine, int turn, int player) => Feed(engine,
        [$"TAG_CHANGE Entity=1 tag=TURN value={turn}", $"TAG_CHANGE Entity=2 tag=CURRENT_PLAYER value={(player == 1 ? 1 : 0)}",
            $"TAG_CHANGE Entity=3 tag=CURRENT_PLAYER value={(player == 2 ? 1 : 0)}", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"]);
    private static void End(PowerLogStateEngine engine) => Feed(engine,
        ["TAG_CHANGE Entity=1 tag=STEP value=MAIN_END", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_CLEANUP", "PowerProcessor.EndCurrentTaskList() - complete"]);
    private static void Play(PowerLogStateEngine engine, int entity) => Feed(engine,
        [$"TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1", $"BLOCK_START BlockType=PLAY Entity={entity}",
            $"TAG_CHANGE Entity={entity} tag=ZONE value=PLAY", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=0", "BLOCK_END",
            "PowerProcessor.EndCurrentTaskList() - complete"]);
    private static IEnumerable<string> Offer(int id, string card)
    {
        yield return $"GameState.DebugPrintEntityChoices() - id={id} Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 player=1]";
        yield return $"GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=60 zone=SETASIDE cardId={card} player=1]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
