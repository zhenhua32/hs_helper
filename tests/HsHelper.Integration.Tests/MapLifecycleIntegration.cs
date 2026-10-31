using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class MapLifecycleIntegration
{
    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        Check(workspace is not null, "Could not locate fixed Map definitions.");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "map-followup-255790.json")));
        var cards = fixture.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(
            raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
            raw.GetProperty("text").GetString()!, raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!,
            raw.GetProperty("type").GetString()!, raw.GetProperty("cost").GetInt32(),
            raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
        var rules = new LineEvaluator(); var engine = InitialOffer(); var initial = engine.Snapshot();
        Check(initial.Quality.IsComplete && initial.Quality.IsStable && initial.TurnHistory is { IsComplete: true } &&
            initial.TurnHistory.Turns.Single().Plays.Single().AtPlay is { Id: 30, CardId: "TLC_824", Zone: "HAND" },
            "Parser did not preserve the actual Map HAND PLAY boundary before its first offer.");
        var firstProjection = rules.EvaluateChoice(initial, [60], cards, 1);
        Check(firstProjection.Map is { Stage: "InitialChoice", ResolvesEffects: true, AddedToHand: true, HandCountAfter: 1 } &&
            firstProjection.CardOutcomes is [{ OptionEntityId: 60, CardId: "EDR_227", AddedToHand: true, ResultingCost: null }] &&
            initial.Entities[60].Zone == "SETASIDE" && initial.Choice?.Options.Count == 3,
            "An initial real three-card offer did not project exactly its selected one card without modifying the snapshot.");
        pass("parsed actual Map HAND play and first three-option offer project one selected odd-Attack Beast");

        ChooseFirst(engine); var hand = engine.Snapshot(); var before = JsonSerializer.Serialize(hand);
        var evidence = MapFollowupRules.Evaluate(hand, 30, cards);
        Check(evidence is { Stage: "AwaitingSelectedCardPlay", InitialSelectedEntityId: 60, InitialSelectedCardId: "EDR_227" } &&
            hand.ChoiceHistory!.Entries.Single().ChosenEntityIds.SequenceEqual([60]) && hand.Entities[60].Zone == "HAND",
            "Actual EntitiesChosen and the same candidate's SETASIDE-to-HAND transition did not bind the first selection.");
        var prediction = rules.EvaluateLine(hand, [new(ActionKind.PlayCard, 60), new(ActionKind.EndTurn, 2)], cards);
        Check(prediction.AppliedActions == 1 && prediction.Result.MapFollowupBoundaryReached && !prediction.Result.TurnEnded &&
            prediction.Result.MapPlayPreviews is [{ MapSourceEntityId: 30, PlayedEntityId: 60, InitialChoiceId: 1,
                OriginalChosenCardId: "EDR_227", PlayedCardId: "EDR_227", IdentityMatch: true, RequiresObservation: true }] &&
            prediction.UnsupportedMechanics.Contains("AWAIT_MAP_FOLLOWUP_SNAPSHOT") &&
            prediction.Result.Entities.Count == hand.Entities.Count && before == JsonSerializer.Serialize(hand) &&
            JsonSerializer.Serialize(engine.Snapshot().TurnHistory) == JsonSerializer.Serialize(hand.TurnHistory),
            "Predicting the first selected play crossed the Map boundary, created a card/offer, or rewrote real TurnHistory.");
        pass("parsed actual first selection binds simulated successful use to a Map wait boundary without fabricating actual PLAY history");

        var cloneEngine = InitialOffer(); ChooseFirst(cloneEngine);
        Feed(cloneEngine, Card(70, "EDR_227", 1, "HAND", "MINION", 4, 5, 2));
        Feed(cloneEngine, ["TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=2", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var clone = rules.EvaluateLine(cloneEngine.Snapshot(), [new(ActionKind.PlayCard, 70)], cards);
        Check(clone.AppliedActions == 1 && clone.Result.MapPlayPreviews.Count == 0 && !clone.Result.MapFollowupBoundaryReached &&
            clone.Result.Entities.Single(entity => entity.EntityId == 60).Zone == "HAND", "A parsed same-CardId clone was treated as the Map's chosen entity.");
        var expiredEngine = InitialOffer(); ChooseFirst(expiredEngine);
        Feed(expiredEngine, ["TAG_CHANGE Entity=1 tag=STEP value=MAIN_END", "TAG_CHANGE Entity=1 tag=TURN value=2",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var expiredFrame = expiredEngine.Snapshot(); var expired = rules.EvaluateLine(expiredFrame, [new(ActionKind.PlayCard, 60)], cards);
        Check(MapFollowupRules.Evaluate(expiredFrame, 30, cards) is { Stage: "ConditionNotMet", FollowupConditionMet: false } &&
            expired.AppliedActions == 1 && expired.Result.MapPlayPreviews.Count == 0 && !expired.Result.MapFollowupBoundaryReached,
            "A subsequent actual extra own turn rearmed the old Map selection.");
        pass("parsed same-name clone and subsequent real extra own turn cannot reuse the original Map first-card condition");

        Feed(engine, ["BLOCK_START BlockType=PLAY Entity=[entityName=Anonymous id=60 zone=HAND cardId=EDR_227 player=1] EffectCardId= EffectIndex=0 Target=0",
            "TAG_CHANGE Entity=60 tag=ZONE value=PLAY", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=0",
            "TAG_CHANGE Entity=2 tag=RESOURCES_USED value=5", "BLOCK_END", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var played = engine.Snapshot(); var waiting = MapFollowupRules.Evaluate(played, 30, cards);
        Check(played.TurnHistory!.Turns.Single().Plays.Count == 2 && played.Choice is null &&
            waiting is { Stage: "AwaitingFollowupOffer", FollowupConditionMet: true, ResolvesEffects: false, AddedToHand: null, RemainingOptions.Count: 0 } &&
            waiting.InitialSelectedPlayedAtEventSequence == played.TurnHistory.Turns.Single().Plays.Last().Sequence,
            "Actual first-card PLAY created an unobserved followup choice or failed to bind the real played entity.");
        pass("actual parsed selected-card HAND play proves the original-turn condition while absent followup offers create no candidates or hand");

        Feed(engine, Offer(2, [(61, "CORE_UNG_912"), (62, "EDR_480")]));
        var followup = engine.Snapshot(); var followupBefore = JsonSerializer.Serialize(followup);
        var secondProjection = rules.EvaluateChoice(followup, [61], cards, 2);
        Check(secondProjection.Map is { Stage: "FollowupChoice", FollowupConditionMet: true, InitialSelectedEntityId: 60,
                SelectedOptionEntityId: 61, AddedToHand: true, HandCountBefore: 0, HandCountAfter: 1 } &&
            secondProjection.Map.RemainingOptions.Select(option => option.EntityId).Order().SequenceEqual([61, 62]) &&
            secondProjection.CardOutcomes is [{ OptionEntityId: 61, CardId: "CORE_UNG_912", AddedToHand: true, ResultingCost: null }] &&
            secondProjection.HandCountAfter == 1 && !secondProjection.CanEvaluateFollowupActions &&
            followupBefore == JsonSerializer.Serialize(followup) && !JsonSerializer.Serialize(secondProjection).Contains("PRIVATE_MAP_", StringComparison.Ordinal),
            "The real two-option followup rediscovered a pool, reissued the first card, leaked private identity or granted more than one card.");
        pass("parsed real second offer is exactly the two original remaining entities and its selected one enters only a direct hand preview");

        Feed(engine, ["GameState.DebugPrintEntitiesChosen() - id=2 Player=1 EntitiesCount=1",
            "GameState.DebugPrintEntitiesChosen() - Entities[0]=61", "TAG_CHANGE Entity=61 tag=ZONE value=HAND",
            "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var consumed = engine.Snapshot(); var consumedEvidence = MapFollowupRules.Evaluate(consumed, 30, cards);
        Check(consumedEvidence is { Stage: "FollowupAlreadyChosen", ResolvesEffects: false, AddedToHand: null } && consumed.Hand.Count() == 1 &&
            consumed.ChoiceHistory!.Entries.Count == 2 && rules.EvaluateChoice(consumed, [61], cards, 2).CardOutcomes.Count == 0,
            "A completed real second selection could be consumed again.");
        pass("actual parsed second EntitiesChosen consumes one Map followup exactly once and stale choices cannot issue another hand card");
    }

    private static PowerLogStateEngine InitialOffer()
    {
        var engine = new PowerLogStateEngine(1, "map-lifecycle-fixture");
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1",
            "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=MAXHANDSIZE value=10", "tag=NUM_CARDS_IN_HAND value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21"]);
        Feed(engine, Card(11, "HERO_05", 1, "PLAY", "HERO", 0, 0, 30));
        Feed(engine, Card(21, "HERO_08", 2, "PLAY", "HERO", 0, 0, 30));
        Feed(engine, Card(30, "TLC_824", 1, "HAND", "SPELL", 1, 0, 0));
        Feed(engine, Card(60, "EDR_227", 1, "SETASIDE", "MINION", 4, 5, 2));
        Feed(engine, Card(61, "CORE_UNG_912", 1, "SETASIDE", "MINION", 1, 1, 2));
        Feed(engine, Card(62, "EDR_480", 1, "SETASIDE", "MINION", 9, 9, 9));
        Feed(engine, Card(90, "PRIVATE_MAP_OPPONENT", 2, "HAND", "MINION", 1, 1, 1));
        Feed(engine, ["TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete",
            "BLOCK_START BlockType=PLAY Entity=[entityName=Anonymous id=30 zone=HAND cardId=TLC_824 player=1] EffectCardId= EffectIndex=0 Target=0",
            "TAG_CHANGE Entity=30 tag=ZONE value=GRAVEYARD", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=0",
            "TAG_CHANGE Entity=2 tag=RESOURCES_USED value=1", "BLOCK_END", "PowerProcessor.EndCurrentTaskList() - complete"]);
        Feed(engine, Offer(1, [(60, "EDR_227"), (61, "CORE_UNG_912"), (62, "EDR_480")])); return engine;
    }
    private static void ChooseFirst(PowerLogStateEngine engine) => Feed(engine,
        ["GameState.DebugPrintEntitiesChosen() - id=1 Player=1 EntitiesCount=1", "GameState.DebugPrintEntitiesChosen() - Entities[0]=60",
            "TAG_CHANGE Entity=60 tag=ZONE value=HAND", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1", "PowerProcessor.EndCurrentTaskList() - complete"]);
    private static IEnumerable<string> Offer(int id, IReadOnlyList<(int Entity, string Card)> options)
    {
        yield return $"GameState.DebugPrintEntityChoices() - id={id} Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 zone=GRAVEYARD cardId=TLC_824 player=1]";
        for (var index = 0; index < options.Count; index++)
            yield return $"GameState.DebugPrintEntityChoices() - Entities[{index}]=[entityName=Anonymous id={options[index].Entity} zone=SETASIDE cardId={options[index].Card} player=1]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static IEnumerable<string> Card(int id, string card, int owner, string zone, string type, int cost, int attack, int health)
    {
        yield return $"FULL_ENTITY - Creating ID={id} CardID={card}"; yield return $"tag=CONTROLLER value={owner}";
        yield return $"tag=ZONE value={zone}"; yield return $"tag=CARDTYPE value={type}"; yield return $"tag=COST value={cost}";
        yield return $"tag=ATK value={attack}"; yield return $"tag=HEALTH value={health}"; yield return "tag=DAMAGE value=0";
        if (type == "HERO") { yield return "tag=ARMOR value=0"; yield return $"tag=CLASS value={(owner == 1 ? "HUNTER" : "MAGE")}"; }
        if (type == "MINION") yield return "tag=CARDRACE value=BEAST";
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
