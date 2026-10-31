using HsHelper.Core;
using System.Collections.Frozen;

var cases = new (string Name, Action Run)[]
{
    ("replay builds distinct entities and current observed costs", ReplayEntities),
    ("snapshot privacy filters hidden identities and attributes", Privacy),
    ("snapshots cannot change when engine changes", SnapshotIsolation),
    ("midgame logs stay incomplete and unknown tags stay null", MissingInformation),
    ("nested block and task boundary control stability", Stability),
    ("new games clear entities and state", GameIsolation),
    ("show change hide updates identity and tags", RevealTransformHide),
    ("numeric tags and bracket references work", NumericTags),
    ("duplicate tags do not advance state or duplicate cards", DuplicateTags),
    ("unresolved references and reset events make state incomplete", InputGaps),
    ("replay is deterministic with fixed session identity", DeterministicReplay),
    ("unknown perspective never reveals private hand", UnknownPerspective),
    ("transform invalidates old dynamic attributes", TransformClearsAttributes),
    ("task boundary during draw is not an actionable snapshot", DrawStepNotStable),
    ("malformed identifiers degrade quality without crashing", MalformedIdentifiers),
    ("player name aliases from metadata resolve without identity export", PlayerNameReferences),
    ("ambiguous player names remain unresolved", AmbiguousPlayerNames),
    ("full entity update bracket records reset and apply observed attributes", FullEntityUpdates),
    ("single unknown human player metadata resolves once", UnknownHumanPlayerAlias),
    ("metadata exposes build format hero classes first player and outcome", MatchMetadata),
    ("choice candidates are complete only after boundary and private to chooser", ChoiceCandidates),
    ("dormant cached attributes do not overwrite current tags", DormantCachedAttributes),
    ("automatic perspective requires exclusive initial private hand and task boundary", AutomaticPerspective),
    ("spectating blocks automatic perspective", SpectatorPerspective),
    ("recent events retain only the selected perspective's private card identities", RecentEventPrivacy),
    ("deck tracker subtracts draws rather than subsequent plays", TrackDrawAndPlay),
    ("known generated shuffles extend the actual deck composition", TrackKnownShuffle),
    ("transformed returns replace original contribution", TrackTransformedReturn),
    ("unknown removals and shuffles never become precise probabilities", TrackUnknownChanges),
    ("late attach and logging gaps keep composition unknown", TrackLateAndGap),
    ("draw probability is hypergeometric without replacement", DrawProbabilityChecks),
    ("dropped event history prevents false exact deck state", TrackingEventHistoryGap),
    ("unchanged entity snapshots are reused without mutating past states", SnapshotValueReuse),
    ("duplicate player identifiers cannot create a complete game or crash alias lookup", DuplicatePlayerIdentifiers),
    ("public plays include each hand-to-play transition and exclude summons and graveyard tokens", PublicPlayTransitions),
    ("opponent play and bounce events retain their public identity across perspective changes", PublicPlayPrivacy),
    ("only an explicit reveal of the same ongoing play resolves an unknown identity", PublicPlayReveal),
    ("later transformations and current entity definitions cannot fill an earlier unknown play", PublicPlayTransform),
    ("returning to hand ends pending reveal correlation", PublicPlayReturn),
    ("public play gaps remain incomplete and cannot correlate across lost events", PublicPlayGaps),
    ("public play records reset across matches and reject rewinds", PublicPlayMatches),
    ("public play snapshots freeze source lists and survive JSON serialization", PublicPlaySerialization),
}.Concat(ChoiceHistoryRegression.Cases()).Concat(TurnHistoryRegression.Cases()).Concat(AssembledTagRegression.Cases()).Concat(QuestTagRegression.Cases()).Concat(SecretSlotPrivacyChecks.Cases()).Concat(WeaponDurabilityChecks.Cases()).Concat(ProjectedChoiceEntityChecks.Cases()).ToArray();
var failures = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} core regression checks passed.");
return failures == 0 ? 0 : 1;

static string[] Fixture() => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-basic.log"));
static PowerLogStateEngine Replay(int? playerId = 1)
{
    var engine = new PowerLogStateEngine(playerId, "fixture");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    return engine;
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ReplayEntities()
{
    var snapshot = Replay().Snapshot();
    Assert(snapshot.Quality.IsComplete && snapshot.Quality.IsStable, "Expected a complete stable sample");
    Assert(snapshot.Turn == 1 && snapshot.ActivePlayerId == 1 && snapshot.Phase == GamePhase.Main, "Turn or phase wrong");
    Assert(snapshot.Hand.Count() == 2 && snapshot.Entities[10].Cost == 3 && snapshot.Entities[11].Cost == 1, "Instances merged or dynamic costs lost");
}
static void Privacy()
{
    var snapshot = Replay().Snapshot();
    Assert(snapshot.Entities[20].CardId is null && snapshot.Entities[20].Cost is null, "Opponent hidden information leaked");
    Assert(snapshot.Entities[21].CardId is null, "Own hidden deck identity leaked");
    Assert(snapshot.Entities[20].Zone == "HAND" && snapshot.Entities[20].ControllerId == 2, "Public counts unavailable");
}
static void SnapshotIsolation()
{
    var engine = Replay(); var before = engine.Snapshot();
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=COST value=0");
    Assert(before.Entities[10].Cost == 3 && engine.Snapshot().Entities[10].Cost == 0, "Old snapshot mutated");
}
static void MissingInformation()
{
    var engine = new PowerLogStateEngine(1, "midgame");
    engine.ProcessLine("FULL_ENTITY - Creating ID=10 CardID=TEST");
    engine.ProcessLine("tag=ZONE value=PLAY");
    var snapshot = engine.Snapshot();
    Assert(!snapshot.Quality.IsComplete && !snapshot.Quality.IsStable, "Midgame is not a full reconstruction");
    Assert(snapshot.Entities[10].Attack is null && snapshot.Entities[10].RemainingHealth is null, "Unknown attributes became zero");
}
static void Stability()
{
    var engine = Replay();
    engine.ProcessLine("BLOCK_START BlockType=PLAY Entity=10");
    engine.ProcessLine("BLOCK_START BlockType=TRIGGER Entity=11");
    engine.ProcessLine("BLOCK_END");
    Assert(!engine.Snapshot().Quality.IsStable && engine.Snapshot().Quality.OpenBlockCount == 1, "Nested resolution marked stable");
    engine.ProcessLine("BLOCK_END");
    Assert(!engine.Snapshot().Quality.IsStable, "Need explicit task boundary");
    engine.ProcessLine("D 12:00:01.0000000 PowerProcessor.EndCurrentTaskList() - complete");
    Assert(engine.Snapshot().Quality.IsStable, "Task boundary not respected");
}
static void GameIsolation()
{
    var engine = Replay(); var old = engine.Snapshot();
    engine.ProcessLine("CREATE_GAME"); var current = engine.Snapshot();
    Assert(old.MatchId != current.MatchId && current.Entities.Count == 0 && !current.Quality.IsComplete, "Next game inherited state");
    Assert(current.Turn is null && current.ActivePlayerId is null && current.EventSequence == 1, "Next game retained tags");
}
static void RevealTransformHide()
{
    var engine = Replay();
    engine.ProcessLine("SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN id=20 zone=HAND zonePos=1 player=2] CardID=REVEALED");
    engine.ProcessLine("tag=ZONE value=PLAY");
    Assert(engine.Snapshot().Entities[20].CardId == "REVEALED", "Show entity lost");
    engine.ProcessLine("CHANGE_ENTITY - Updating Entity=20 CardID=TRANSFORMED");
    engine.ProcessLine("tag=COST value=2");
    Assert(engine.Snapshot().Entities[20].CardId == "TRANSFORMED" && engine.Snapshot().Entities[20].Cost == 2, "Transform failed");
    engine.ProcessLine("HIDE_ENTITY - Entity=20 tag=ZONE value=DECK");
    Assert(engine.Snapshot().Entities[20].CardId is null && engine.Snapshot().Entities[20].Cost is null, "Hide leaked stale identity");
}
static void NumericTags()
{
    var engine = Replay();
    engine.ProcessLine("TAG_CHANGE Entity=[entityName=Hunter id=10 zone=HAND zonePos=1 cardId=TEST player=1] tag=48 value=0");
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=49 value=1");
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=313 value=777");
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=1518 value=1");
    Assert(engine.Snapshot().Entities[10].Cost == 0 && engine.Snapshot().Entities[10].Zone == "PLAY", "Numeric zero or zone incorrect");
    Assert(engine.Snapshot().Entities[10].GetIntTag("CREATOR") == 777 && engine.Snapshot().Entities[10].GetIntTag("DORMANT") == 1,
        "Numeric source/dormant tags not normalized");
}
static void DuplicateTags()
{
    var engine = Replay(); engine.ProcessLine("TAG_CHANGE Entity=10 tag=COST value=2");
    var version = engine.Snapshot().StateVersion;
    Assert(!engine.ProcessLine("TAG_CHANGE Entity=10 tag=COST value=2"), "Duplicate tag changed state");
    Assert(engine.Snapshot().StateVersion == version && engine.Snapshot().Hand.Count() == 2, "Duplicate changed version or hand");
}
static void InputGaps()
{
    var engine = Replay(); engine.ProcessLine("TAG_CHANGE Entity=UnmappedPlayerName tag=RESOURCES value=3");
    Assert(!engine.Snapshot().Quality.IsComplete, "Unknown reference was silently dropped");
    engine = Replay(); engine.ProcessLine("RESET_GAME");
    Assert(!engine.Snapshot().Quality.IsComplete && !engine.Snapshot().Quality.IsStable, "Reset accepted without rebuilding");
}
static void DeterministicReplay()
{
    var left = Replay().Snapshot(); var right = Replay().Snapshot();
    Assert(left.MatchId == right.MatchId && left.StateVersion == right.StateVersion && left.EventSequence == right.EventSequence, "Replay counters vary");
    Assert(left.Entities[10].Cost == right.Entities[10].Cost && left.Quality.IsStable == right.Quality.IsStable, "Replay results differ");
}
static void UnknownPerspective()
{
    var engine = Replay(null); var snapshot = engine.Snapshot();
    Assert(snapshot.LocalPlayerId is null && !snapshot.Quality.IsComplete && !snapshot.Hand.Any(), "Perspective inferred without evidence");
    Assert(snapshot.Entities[10].CardId is null && snapshot.Entities[10].Cost is null, "Private own hand leaked with no perspective");
    engine.SetLocalPlayerId(1);
    Assert(engine.Snapshot().Entities[10].CardId == "TEST_HUNTER_A", "Explicit perspective not applied");
}
static void TransformClearsAttributes()
{
    var engine = Replay();
    engine.ProcessLine("CHANGE_ENTITY - Updating Entity=10 CardID=TRANSFORMED");
    Assert(engine.Snapshot().Entities[10].Cost is null, "Transform retained old card cost");
    Assert(engine.Snapshot().Entities[10].Zone == "HAND", "Transform lost entity location");
}
static void DrawStepNotStable()
{
    var engine = Replay();
    engine.ProcessLine("TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_DRAW");
    engine.ProcessLine("D 12:00:01.0000000 PowerProcessor.EndCurrentTaskList() - complete");
    Assert(!engine.Snapshot().Quality.IsStable, "Draw animation boundary treated as a player decision");
}
static void MalformedIdentifiers()
{
    var engine = Replay();
    engine.ProcessLine("FULL_ENTITY - Creating ID=99999999999999999999999 CardID=INVALID");
    Assert(!engine.Snapshot().Quality.IsComplete, "Invalid oversized entity was accepted");
}

static void PlayerNameReferences()
{
    var engine = new PowerLogStateEngine(1, "named");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - BuildNumber=255790");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - PlayerID=1, PlayerName=Example Player A");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - PlayerID=2, PlayerName=Example Player B");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    engine.ProcessLine("TAG_CHANGE Entity=Example Player A tag=RESOURCES value=5");
    var snapshot = engine.Snapshot();
    Assert(snapshot.Entities[2].Tags["RESOURCES"] == "5" && snapshot.Quality.IsComplete, "Metadata alias not resolved");
    var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
    Assert(!json.Contains("Example Player", StringComparison.Ordinal), "Player name leaked into snapshot");
    engine.ProcessLine("CREATE_GAME");
    engine.ProcessLine("Player EntityID=2 PlayerID=1");
    engine.ProcessLine("TAG_CHANGE Entity=Example Player A tag=RESOURCES value=9");
    Assert(!engine.Snapshot().Entities[2].Tags.ContainsKey("RESOURCES"), "Prior game alias leaked into new game");
}

static void AmbiguousPlayerNames()
{
    var engine = new PowerLogStateEngine(1, "ambiguous");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - PlayerID=1, PlayerName=SameName");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - PlayerID=2, PlayerName=SameName");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    engine.ProcessLine("TAG_CHANGE Entity=SameName tag=RESOURCES value=5");
    Assert(!engine.Snapshot().Quality.IsComplete && engine.Snapshot().Entities[2].Tags["RESOURCES"] == "1", "Ambiguous name assigned to a player");
}

static void FullEntityUpdates()
{
    var engine = Replay();
    engine.ProcessLine("FULL_ENTITY - Updating [entityName=Anonymous cardType=SPELL id=10 zone=HAND zonePos=1 cardId=OLD player=1] CardID=UPDATED_TEST");
    engine.ProcessLine("tag=COST value=0");
    var snapshot = engine.Snapshot();
    Assert(snapshot.Quality.IsComplete && snapshot.Entities[10].CardId == "UPDATED_TEST" && snapshot.Entities[10].Cost == 0,
        "Current full entity update syntax was not parsed");
    Assert(snapshot.Entities[10].Attack is null && snapshot.Entities[10].Zone == "HAND", "Update retained stale attributes or lost location");
}

static void UnknownHumanPlayerAlias()
{
    var engine = new PowerLogStateEngine(1, "placeholder");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - PlayerID=1, PlayerName=UNKNOWN HUMAN PLAYER");
    engine.ProcessLine("D 12:00:00.0000000 GameState.DebugPrintGame() - PlayerID=2, PlayerName=Opponent Example");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    engine.ProcessLine("TAG_CHANGE Entity=Resolved Example tag=RESOURCES value=5");
    Assert(engine.Snapshot().Quality.IsComplete && engine.Snapshot().Entities[2].Tags["RESOURCES"] == "5", "Unique placeholder not bound to resolved player name");
    engine.ProcessLine("TAG_CHANGE Entity=Another Unknown Name tag=RESOURCES value=9");
    Assert(!engine.Snapshot().Quality.IsComplete && engine.Snapshot().Entities[2].Tags["RESOURCES"] == "5", "Placeholder reused for another unknown identity");
}

static void MatchMetadata()
{
    var engine = new PowerLogStateEngine(1);
    engine.ProcessLine("GameState.DebugPrintGame() - BuildNumber=255790");
    engine.ProcessLine("GameState.DebugPrintGame() - FormatType=FT_STANDARD");
    engine.ProcessLine("GameState.DebugPrintGame() - GameType=GT_RANKED");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    foreach (var line in new[] { "TAG_CHANGE Entity=2 tag=FIRST_PLAYER value=1", "TAG_CHANGE Entity=2 tag=HERO_ENTITY value=30",
        "TAG_CHANGE Entity=3 tag=HERO_ENTITY value=40", "FULL_ENTITY - Creating ID=30 CardID=HERO_A", "tag=ZONE value=PLAY",
        "tag=CLASS value=HUNTER", "FULL_ENTITY - Creating ID=40 CardID=HERO_B", "tag=ZONE value=PLAY", "tag=CLASS value=MAGE",
        "TAG_CHANGE Entity=2 tag=PLAYSTATE value=WON" }) engine.ProcessLine(line);
    var snapshot = engine.Snapshot();
    Assert(snapshot.ClientBuild == "255790" && snapshot.Format == "Standard", "Build/format lost across CREATE_GAME");
    Assert(snapshot.GameType == "GT_RANKED", "Ranked/casual game type lost across CREATE_GAME");
    Assert(snapshot.FirstPlayerId == 1 && snapshot.LocalOutcome == "Won", "First-player/outcome missing");
    Assert(snapshot.LocalClass == "HUNTER" && snapshot.OpponentClass == "MAGE", "Hero classes not resolved through HERO_ENTITY");
}

static void ChoiceCandidates()
{
    var engine = Replay();
    foreach (var line in new[] { "GameState.DebugPrintEntityChoices() - id=8 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1",
        "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=10 player=1]",
        "GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=80 zone=SETASIDE cardId=OPTION_A player=1]",
        "GameState.DebugPrintEntityChoices() - Entities[1]=[entityName=Anonymous id=81 zone=SETASIDE cardId=OPTION_B player=1]" }) engine.ProcessLine(line);
    Assert(engine.Snapshot().Choice is { IsReady: false, Options.Count: 2 }, "Choice published before list boundary");
    engine.ProcessLine("ChoiceCardMgr.OnChoicesShown() - shown");
    var choice = engine.Snapshot();
    Assert(choice.Phase == GamePhase.Choice && choice.Choice is { IsReady: true, Options.Count: 2 } && choice.Quality.IsStable,
        "Ready choice not actionable");
    Assert(choice.Choice!.Options[0].CardId == "OPTION_A", "Own displayed option identity unavailable");
    engine.SetLocalPlayerId(2);
    Assert(engine.Snapshot().Choice is null, "Another player's choice leaked");
    engine.SetLocalPlayerId(1);
    engine.ProcessLine("GameState.DebugPrintEntitiesChosen() - id=8 Player=1 EntitiesCount=1");
    Assert(engine.Snapshot().Choice is null, "Completed choice remained pending");
}

static void DormantCachedAttributes()
{
    var engine = Replay();
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=ZONE value=PLAY");
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=TAUNT value=0");
    engine.ProcessLine("CACHED_TAG_FOR_DORMANT_CHANGE Entity=[entityName=Anonymous id=10 zone=PLAY player=1] tag=TAUNT value=1");
    engine.ProcessLine("CACHED_TAG_FOR_DORMANT_CHANGE Entity=10 tag=ATK value=9");
    engine.ProcessLine("D 12:00:01.0 PowerProcessor.EndCurrentTaskList() - complete");
    var snapshot = engine.Snapshot();
    Assert(snapshot.Quality.IsComplete && snapshot.Quality.IsStable && snapshot.Entities[10].GetIntTag("TAUNT") == 0 && snapshot.Entities[10].Attack is null,
        "Cached dormant future data overwritten current tags or invalidated complete log");
}

static void AutomaticPerspective()
{
    var engine = new PowerLogStateEngine(autoDetectPerspective: true);
    foreach (var line in Fixture()) engine.ProcessLine(line);
    Assert(engine.Snapshot().LocalPlayerId is null, "Both sides' private identities or non-mulligan phase inferred");
    engine.ProcessLine("TAG_CHANGE Entity=GameEntity tag=STEP value=BEGIN_MULLIGAN");
    engine.ProcessLine("HIDE_ENTITY - Entity=20 tag=ZONE value=HAND");
    Assert(engine.Snapshot().LocalPlayerId is null, "Perspective inferred before complete initial task");
    engine.ProcessLine("D 12:00:01.0 PowerProcessor.EndCurrentTaskList() - complete");
    Assert(engine.Snapshot().LocalPlayerId == 1 && engine.Snapshot().PerspectiveSource == "initial_private_hand", "Exclusive own hand evidence not used");
    engine.SetLocalPlayerId(2);
    Assert(engine.Snapshot().LocalPlayerId == 2 && engine.Snapshot().PerspectiveSource == "manual", "Manual perspective lost priority");
    engine.ProcessLine("TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_ACTION");
    engine.SetLocalPlayerId(null);
    Assert(engine.Snapshot().LocalPlayerId == 1 && engine.Snapshot().PerspectiveSource == "initial_private_hand",
        "Returning to automatic mode lost previously confirmed initial evidence");
}

static void SpectatorPerspective()
{
    var engine = new PowerLogStateEngine(autoDetectPerspective: true);
    engine.ProcessLine("Begin Spectating");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    engine.ProcessLine("TAG_CHANGE Entity=GameEntity tag=STEP value=BEGIN_MULLIGAN");
    engine.ProcessLine("HIDE_ENTITY - Entity=20 tag=ZONE value=HAND");
    engine.ProcessLine("D 12:00:01.0 PowerProcessor.EndCurrentTaskList() - complete");
    Assert(engine.Snapshot().IsSpectating && engine.Snapshot().LocalPlayerId is null, "Spectator log inferred own player");
}

static void RecentEventPrivacy()
{
    var engine = Replay();
    Assert(engine.Snapshot().RecentEvents.Any(value => value.CardId == "TEST_HUNTER_A"), "Own observed identity lost");
    Assert(!engine.Snapshot().RecentEvents.Any(value => value.CardId == "SHOULD_NOT_LEAK"), "Opponent hidden history leaked");
    engine.SetLocalPlayerId(2);
    Assert(!engine.Snapshot().RecentEvents.Any(value => value.CardId == "TEST_HUNTER_A"), "Old private perspective leaked after switch");
}

static PinnedDeck TestDeck() => PinnedDeck.Create("v1", "255790", 1,
    new Dictionary<string, int> { ["A"] = 2, ["B"] = 1 });
static EntitySnapshot TrackingEntity(int id, string? card, string zone, bool generated = false)
{
    var tags = new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = "1" };
    if (generated) tags["CREATOR"] = "777";
    return new(id, card, EntityKind.Card, tags.ToFrozenDictionary(StringComparer.Ordinal));
}
static GameSnapshot TrackingState(long sequence, IEnumerable<EntitySnapshot> entities, int turn = 1,
    bool complete = true, IReadOnlyList<ObservedGameEvent>? events = null) =>
    new("tracked", sequence, sequence, turn, 1, 1, GamePhase.Main,
        entities.ToFrozenDictionary(entity => entity.Id), new(true, complete, complete, true, 0, []))
    { ClientBuild = "255790", RecentEvents = events ?? [] };
static EntitySnapshot[] BaselineEntities() => [TrackingEntity(1, "A", "HAND"), TrackingEntity(2, null, "DECK"), TrackingEntity(3, null, "DECK")];

static void TrackDrawAndPlay()
{
    var tracker = new DeckTracker(TestDeck());
    tracker.Observe(TrackingState(1, BaselineEntities()));
    Assert(tracker.Snapshot().IsExact && tracker.Snapshot().KnownRemaining["A"] == 1 && tracker.Snapshot().TotalCards == 2, "Initial hand not removed from deck");
    var drawn = new[] { TrackingEntity(1, "A", "HAND"), TrackingEntity(2, "A", "HAND"), TrackingEntity(3, null, "DECK") };
    tracker.Observe(TrackingState(2, drawn));
    Assert(tracker.Snapshot().KnownRemaining.GetValueOrDefault("A") == 0 && tracker.CalculateDrawOdds("B", 1).Probability == 1, "Draw did not reduce actual deck");
    tracker.Observe(TrackingState(3, [TrackingEntity(1, "A", "PLAY"), drawn[1], drawn[2]]));
    Assert(tracker.Snapshot().TotalCards == 1 && tracker.Snapshot().KnownRemaining["B"] == 1, "Playing an already drawn card subtracted twice");
}

static void TrackKnownShuffle()
{
    var tracker = new DeckTracker(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities()));
    tracker.Observe(TrackingState(2, BaselineEntities().Append(TrackingEntity(4, "G", "HAND", true))));
    tracker.Observe(TrackingState(3, BaselineEntities().Append(TrackingEntity(4, null, "DECK", true))));
    var result = tracker.Snapshot();
    Assert(result.IsExact && result.TotalCards == 3 && result.KnownRemaining["G"] == 1, "Known generated shuffle omitted or conflated with original deck");
}

static void TrackTransformedReturn()
{
    var tracker = new DeckTracker(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities()));
    tracker.Observe(TrackingState(2, [TrackingEntity(1, "G", "HAND"), TrackingEntity(2, null, "DECK"), TrackingEntity(3, null, "DECK")],
        events: [new(2, "CardChanged", 1, 1, "G", null, null, "卡牌发生变形")]));
    tracker.Observe(TrackingState(3, [TrackingEntity(1, null, "DECK"), TrackingEntity(2, null, "DECK"), TrackingEntity(3, null, "DECK")]));
    var result = tracker.Snapshot();
    Assert(result.IsExact && result.KnownRemaining["A"] == 1 && result.KnownRemaining["G"] == 1 && result.TotalCards == 3,
        "Transformed return incorrectly restored original identity");
}

static void TrackUnknownChanges()
{
    var tracker = new DeckTracker(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities()));
    tracker.Observe(TrackingState(2, BaselineEntities().Append(TrackingEntity(4, null, "DECK", true))));
    Assert(!tracker.Snapshot().IsExact && tracker.Snapshot().UnknownCount == 1 && tracker.CalculateDrawOdds("A", 1).Probability is null,
        "Unknown shuffle generated a precise probability");
    tracker = new(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities()));
    tracker.Observe(TrackingState(2, [TrackingEntity(1, "A", "HAND"), TrackingEntity(2, null, "REMOVEDFROMGAME"), TrackingEntity(3, null, "DECK")]));
    Assert(!tracker.Snapshot().IsExact && tracker.Snapshot().UnknownCount == 1, "Unknown removed card guessed as a particular original");
}

static void TrackLateAndGap()
{
    var tracker = new DeckTracker(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities(), turn: 5));
    Assert(!tracker.Snapshot().IsExact && tracker.Snapshot().UnknownCount == 2, "Midgame attachment invented a starting deck mapping");
    tracker = new(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities()));
    tracker.Observe(TrackingState(2, BaselineEntities(), complete: false));
    tracker.Observe(TrackingState(3, BaselineEntities()));
    Assert(!tracker.Snapshot().IsExact, "Input gap silently recovered to precise composition");
    try { tracker.Observe(TrackingState(2, BaselineEntities())); throw new Exception("Rewind accepted"); }
    catch (InvalidOperationException) { }
}

static void DrawProbabilityChecks()
{
    var expected = 1 - (28.0 * 27 * 26) / (30.0 * 29 * 28);
    Assert(Math.Abs(DrawProbability.AtLeastOne(30, 2, 3) - expected) < 1e-12, "Hypergeometric probability incorrect");
    Assert(DrawProbability.AtLeastOne(0, 0, 0) == 0 && DrawProbability.AtLeastOne(5, 1, 5) == 1,
        "Empty or guaranteed sampling boundary incorrect");
    try { DrawProbability.AtLeastOne(3, 1, 4); throw new Exception("Fatigue treated as normal draw"); }
    catch (ArgumentOutOfRangeException) { }
}

static void TrackingEventHistoryGap()
{
    var tracker = new DeckTracker(TestDeck()); tracker.Observe(TrackingState(1, BaselineEntities()));
    tracker.Observe(TrackingState(100, BaselineEntities()) with { EventHistoryTruncatedThrough = 50 });
    Assert(!tracker.Snapshot().IsExact && tracker.CalculateDrawOdds("A", 1).Probability is null,
        "A hidden transformation could have been lost from the bounded history");
}

static void SnapshotValueReuse()
{
    var engine = Replay();
    var first = engine.Snapshot();
    var repeated = engine.Snapshot();
    Assert(ReferenceEquals(first.Entities[10], repeated.Entities[10]), "Unchanged entity was re-frozen");
    engine.ProcessLine("TAG_CHANGE Entity=10 tag=COST value=0");
    var changed = engine.Snapshot();
    Assert(!ReferenceEquals(first.Entities[10], changed.Entities[10]) && ReferenceEquals(first.Entities[11], changed.Entities[11]),
        "Dirty entity caching refreshed unrelated values or missed the changed value");
    Assert(first.Entities[10].Cost == 3 && changed.Entities[10].Cost == 0, "Cached mutable tags changed a past snapshot");
    engine.SetLocalPlayerId(2);
    var alternate = engine.Snapshot();
    Assert(alternate.Entities[10].CardId is null && alternate.Entities[10].Cost is null && first.Entities[10].CardId is not null,
        "Visibility cache reused a private snapshot for the wrong perspective");
    engine.SetLocalPlayerId(1);
    Assert(ReferenceEquals(changed.Entities[10], engine.Snapshot().Entities[10]), "Visible cache not reused after switching back");
}

static void DuplicatePlayerIdentifiers()
{
    var engine = new PowerLogStateEngine(1);
    engine.ProcessLine("GameState.DebugPrintGame() - PlayerID=1, PlayerName=Anonymous Player");
    foreach (var line in Fixture()) engine.ProcessLine(line);
    engine.ProcessLine("TAG_CHANGE Entity=3 tag=PLAYER_ID value=1");
    engine.ProcessLine("TAG_CHANGE Entity=Anonymous Player tag=RESOURCES value=7");
    Assert(!engine.Snapshot().Quality.IsComplete && !engine.Snapshot().Quality.IsStable,
        "Duplicate player IDs were accepted or alias lookup crashed");
}

static ObservedGameEvent PlayEvent(long sequence, string kind, int entityId, string? cardId = null,
    string? from = null, string? to = null, int? controller = 2) =>
    new(sequence, kind, entityId, controller, cardId, from, to, "匿名公开事件");

static void PublicPlayTransitions()
{
    var tracker = new PublicPlayTracker();
    var events = new[]
    {
        PlayEvent(1, "ZoneChanged", 20, "A", "HAND", "PLAY"),
        PlayEvent(2, "ZoneChanged", 30, "TOKEN", "SETASIDE", "PLAY"),
        PlayEvent(3, "ZoneChanged", 30, "TOKEN", "PLAY", "GRAVEYARD"),
        PlayEvent(4, "ZoneChanged", 21, "DISCARDED", "HAND", "GRAVEYARD"),
        PlayEvent(5, "ZoneChanged", 20, "A", "PLAY", "HAND"),
        PlayEvent(6, "ZoneChanged", 20, "A", "HAND", "PLAY"),
        PlayEvent(7, "ZoneChanged", 10, "OWN", "HAND", "PLAY", 1)
    };
    var snapshot = TrackingState(7, [], events: events);
    tracker.Observe(snapshot); tracker.Observe(snapshot);
    var result = tracker.Snapshot();
    Assert(result.IsComplete && result.Plays.Count == 3 && result.Plays.Count(play => play.EntityId == 20) == 2,
        "A repeated entity was collapsed or a non-play zone transition counted");
    Assert(result.Plays.Select(play => play.Sequence).SequenceEqual([1L, 6L, 7L]) && result.Plays[^1].ControllerId == 1,
        "Play order, event deduplication, or controller-at-play was lost");
}

static void PublicPlayPrivacy()
{
    var engine = Replay();
    var tracker = new PublicPlayTracker();
    tracker.Observe(engine.Snapshot());
    engine.ProcessLine("SHOW_ENTITY - Updating Entity=20 CardID=PUBLIC_A");
    Assert(!engine.Snapshot().RecentEvents.Any(value => value.CardId == "PUBLIC_A"), "An unplayed opponent hand reveal leaked");
    tracker.Observe(engine.Snapshot());
    engine.ProcessLine("TAG_CHANGE Entity=20 tag=ZONE value=PLAY");
    tracker.Observe(engine.Snapshot());
    var played = tracker.Snapshot();
    Assert(played.Plays.Single().CardId == "PUBLIC_A", "Opponent hand-to-play identity was filtered as private");
    engine.ProcessLine("HIDE_ENTITY - Entity=20 tag=ZONE value=HAND");
    tracker.Observe(engine.Snapshot());
    Assert(tracker.Snapshot().Plays.Single().CardId == "PUBLIC_A" && engine.Snapshot().Entities[20].CardId is null,
        "Returning to a hidden hand erased public history or exposed the current hidden card");
    engine.SetLocalPlayerId(2);
    var switched = engine.Snapshot();
    Assert(switched.RecentEvents.Any(value => value.CardId == "PUBLIC_A") &&
        !switched.RecentEvents.Any(value => value.CardId == "TEST_HUNTER_A"), "Public/private event ownership changed incorrectly");
}

static void PublicPlayReveal()
{
    var tracker = new PublicPlayTracker();
    tracker.Observe(TrackingState(1, [], events: [PlayEvent(1, "ZoneChanged", 20, from: "HAND", to: "PLAY")]));
    var before = tracker.Snapshot();
    Assert(before.Plays.Single().CardId is null && !before.IsComplete, "Unknown play became an invented identity");
    tracker.Observe(TrackingState(2, [], events: [PlayEvent(2, "CardRevealed", 20, "A")]));
    var after = tracker.Snapshot();
    Assert(after.Plays.Single().CardId == "A" && after.IsComplete && before.Plays.Single().CardId is null,
        "Explicit same-play reveal did not resolve or mutated an earlier snapshot");
    tracker.Observe(TrackingState(3, [], events: [PlayEvent(3, "CardChanged", 20, "TRANSFORMED")]));
    Assert(tracker.Snapshot().Plays.Single().CardId == "A", "Known historical identity was changed by later transformation");
}

static void PublicPlayTransform()
{
    var tracker = new PublicPlayTracker();
    tracker.Observe(TrackingState(1, [TrackingEntity(20, "FUTURE_ENTITY_DEFINITION", "PLAY")],
        events: [PlayEvent(1, "ZoneChanged", 20, from: "HAND", to: "PLAY")]));
    Assert(tracker.Snapshot().Plays.Single().CardId is null, "Current entity definition backfilled unknown history");
    tracker.Observe(TrackingState(3, [], events:
        [PlayEvent(2, "CardChanged", 20, "B"), PlayEvent(3, "CardRevealed", 20, "B")]));
    Assert(tracker.Snapshot().Plays.Single().CardId is null, "A transformed definition was mistaken for the originally played card");
}

static void PublicPlayReturn()
{
    var tracker = new PublicPlayTracker();
    tracker.Observe(TrackingState(1, [], events: [PlayEvent(1, "ZoneChanged", 20, from: "HAND", to: "PLAY")]));
    tracker.Observe(TrackingState(3, [], events:
        [PlayEvent(2, "ZoneChanged", 20, from: "PLAY", to: "HAND"), PlayEvent(3, "CardRevealed", 20, "PRIVATE_HAND")]));
    Assert(tracker.Snapshot().Plays.Single().CardId is null, "A later hand reveal backfilled an earlier play");
    tracker.Observe(TrackingState(5, [], events:
        [PlayEvent(4, "ZoneChanged", 20, from: "HAND", to: "PLAY"), PlayEvent(5, "CardRevealed", 20, "B")]));
    Assert(tracker.Snapshot().Plays.Count == 2 && tracker.Snapshot().Plays[0].CardId is null &&
        tracker.Snapshot().Plays[1].CardId == "B", "Reveal correlation did not stay within its current play");
}

static void PublicPlayGaps()
{
    var tracker = new PublicPlayTracker();
    tracker.Observe(TrackingState(1, [], events: [PlayEvent(1, "ZoneChanged", 20, from: "HAND", to: "PLAY")]));
    tracker.Observe(TrackingState(5, [], events: [PlayEvent(5, "CardRevealed", 20, "AFTER_GAP")]) with { EventHistoryTruncatedThrough = 3 });
    Assert(!tracker.Snapshot().IsComplete && tracker.Snapshot().Plays.Single().CardId is null &&
        tracker.Snapshot().Warnings.Any(warning => warning.Contains("缺口")), "Dropped events still allowed an exact ledger or unsafe reveal correlation");
    tracker.Observe(TrackingState(6, []));
    Assert(!tracker.Snapshot().IsComplete, "Sparse-observation gap silently recovered");
    var late = new PublicPlayTracker();
    late.Observe(TrackingState(100, [], events: [PlayEvent(100, "ZoneChanged", 40, "KNOWN", "HAND", "PLAY")]) with { EventHistoryTruncatedThrough = 80 });
    Assert(!late.Snapshot().IsComplete && late.Snapshot().Plays.Single().CardId == "KNOWN", "Late attachment hid the missing earlier play history");
}

static void PublicPlayMatches()
{
    var tracker = new PublicPlayTracker();
    tracker.Observe(TrackingState(2, [], events: [PlayEvent(2, "ZoneChanged", 20, "A", "HAND", "PLAY")]));
    try { tracker.Observe(TrackingState(1, [])); throw new Exception("Rewind was accepted"); }
    catch (InvalidOperationException) { }
    tracker.Observe(TrackingState(3, [], complete: false));
    tracker.Observe(TrackingState(4, []));
    Assert(!tracker.Snapshot().IsComplete, "An input-quality gap recovered without new game start");
    tracker.Observe(TrackingState(1, [], events: [PlayEvent(1, "ZoneChanged", 20, "NEW", "HAND", "PLAY")]) with { MatchId = "another-match" });
    Assert(tracker.Snapshot().IsComplete && tracker.Snapshot().Plays.Count == 1 && tracker.Snapshot().Plays[0].CardId == "NEW",
        "Match reset retained prior records, unresolved identities, or missing-history status");
}

static void PublicPlaySerialization()
{
    var plays = new List<PlayedCardRecord> { new(1, 20, "A", 2) };
    var warnings = new List<string>();
    var snapshot = new PublicPlayTrackingSnapshot("fixture", 1, plays, true, warnings);
    plays[0] = plays[0] with { CardId = "FUTURE" }; warnings.Add("later");
    Assert(snapshot.Plays[0].CardId == "A" && snapshot.Warnings.Count == 0, "Snapshot retained mutable source collections");
    try { ((IList<PlayedCardRecord>)snapshot.Plays)[0] = new(1, 20, "BAD", 2); throw new Exception("Public list allowed mutation"); }
    catch (NotSupportedException) { }
    var options = new System.Text.Json.JsonSerializerOptions { IgnoreReadOnlyProperties = true };
    var json = System.Text.Json.JsonSerializer.Serialize(snapshot, options);
    var restored = System.Text.Json.JsonSerializer.Deserialize<PublicPlayTrackingSnapshot>(json, options)!;
    Assert(restored.MatchId == "fixture" && restored.Plays.Single() == new PlayedCardRecord(1, 20, "A", 2), "Public ledger JSON roundtrip failed: " + json);
    try { ((IList<PlayedCardRecord>)restored.Plays).Clear(); throw new Exception("Restored ledger allowed mutation"); }
    catch (NotSupportedException) { }
}
