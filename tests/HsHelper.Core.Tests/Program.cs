using HsHelper.Core;

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
};
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
    Assert(engine.Snapshot().Entities[10].Cost == 0 && engine.Snapshot().Entities[10].Zone == "PLAY", "Numeric zero or zone incorrect");
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
