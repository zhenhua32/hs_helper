using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;
using HsHelper.Ingestion;
using Microsoft.Data.Sqlite;

var root = FindWorkspace();
var temp = Path.Combine(root, ".local", "storage-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var database = Path.Combine(temp, "history.db");
var store = new SessionStore(database);
await store.InitializeAsync();
var checks = new List<(string Name, Func<Task> Run)>
{
    ("immutable deck versions and idempotent save", DeckVersions),
    ("durable snapshots and precise decision selection", DurableSnapshots),
    ("historical queries do not reveal later information", NoFutureInformation),
    ("repeat recording is idempotent and binding stays fixed", DuplicateAndBinding),
    ("conflicting batch rolls back and remains queued", AtomicFailure),
    ("cancellation keeps queued observations for retry", Cancellation),
    ("multi-game switching saves the previous partial checkpoint", MultipleGames),
    ("statistics preserve filters and sample counts", Statistics),
    ("ranked and casual statistics remain separate", ModeStatistics),
    ("historical statistics exclude future completions and the selected match", AsOfStatistics),
    ("retention cascades only eligible sessions and keeps deck versions", Retention),
    ("explicit identity fields are rejected before persistence", IdentityGuard),
    ("choice and filtered recent events survive snapshot serialization", ChoiceRoundTrip),
    ("recorded tracking retains the exact earlier composition", TrackingHistory),
    ("pipeline fixes perspective separately for each match", Pipeline),
    ("schema migration preserves history and rejects future versions", SchemaMigration)
};
checks.AddRange(AnalysisTraceChecks.Cases(database));
checks.AddRange(CoachKnowledgeChecks.Cases(database));
checks.AddRange(AdviceArchiveChecks.Cases(database));
checks.AddRange(HistoricalStatsChecks.Cases(database));
checks.AddRange(ConversationArchiveChecks.Cases(database));
checks.AddRange(ChoiceHistoryStorageChecks.Cases(database));
checks.AddRange(TurnHistoryStorageChecks.Cases(database));
checks.AddRange(SessionPagingChecks.Cases(database));
var completed = 0;
foreach (var check in checks)
{
    await check.Run();
    completed++;
    Console.WriteLine("PASS " + check.Name);
}
Console.WriteLine($"Storage: {completed}/{checks.Count} passed");
if (args.Length == 2 && args[0] == "--log") await RealLogProbe(args[1]);

DeckDefinition Definition(int card = 100) => new(2, new[] { 31 }, new[] { new DeckCard(card, 2) }, Array.Empty<DeckSideboard>());
SessionBinding Binding(string? deck = null, string build = "255790") => new(deck, build, 1, PlayerClass: "HUNTER");

GameSnapshot Snapshot(string id, long sequence, int turn = 1, string? publicEnemy = null, bool stable = true,
    bool completeLog = true, GamePhase phase = GamePhase.Main, string? outcome = null)
{
    Dictionary<string, string> Tags(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
    EntitySnapshot Entity(int entityId, string? cardId, EntityKind kind, Dictionary<string, string> tags)
        => new(entityId, cardId, kind, tags.ToFrozenDictionary());
    var playerTags = Tags(("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("FIRST_PLAYER", "1"),
        ("CURRENT_PLAYER", "1"), ("PLAYSTATE", outcome ?? "PLAYING"));
    var entities = new[]
    {
        Entity(1, null, EntityKind.Game, Tags(("STATE", phase == GamePhase.Complete ? "COMPLETE" : "RUNNING"), ("TURN", turn.ToString()), ("STEP", "MAIN_ACTION"))),
        Entity(2, null, EntityKind.Player, playerTags),
        Entity(3, null, EntityKind.Player, Tags(("PLAYER_ID", "2"), ("HERO_ENTITY", "20"))),
        Entity(10, "HERO_05", EntityKind.Card, Tags(("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CLASS", "HUNTER"), ("HEALTH", "30"), ("DAMAGE", "0"))),
        Entity(20, "HERO_08", EntityKind.Card, Tags(("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CLASS", "MAGE"))),
        Entity(11, "OWN_CARD", EntityKind.Card, Tags(("CONTROLLER", "1"), ("ZONE", "HAND"), ("COST", turn.ToString()))),
        Entity(30, publicEnemy, EntityKind.Card, Tags(("CONTROLLER", "2"), ("ZONE", publicEnemy is null ? "HAND" : "PLAY")))
    };
    return new(id, sequence, sequence, turn, 1, 1, phase, entities.ToFrozenDictionary(e => e.Id),
        new(true, completeLog, stable, true, 0, completeLog ? [] : new[] { "日志缺口" }))
        { Format = "STANDARD", GameType = "GT_RANKED" };
}

async Task DeckVersions()
{
    var first = await store.SaveDeckVersionAsync("猎人样本", Definition(), "255790");
    var duplicate = await store.SaveDeckVersionAsync("猎人样本", Definition(), "255790");
    Check(first.Id == duplicate.Id, "identical deck saves should be idempotent");
    var second = await store.SaveDeckVersionAsync("猎人样本", Definition(101), "255791", first.Id);
    var old = await store.GetDeckVersionAsync(first.Id);
    Check(second.Id != first.Id && second.PreviousVersionId == first.Id, "new version needs separate identity");
    Check(old!.Definition.Cards.Single().DbfId == 100 && old.CardBuild == "255790", "old version was overwritten");
}

async Task DurableSnapshots()
{
    var recorder = new SessionRecorder(store, Binding());
    var before = Snapshot("timeline", 10);
    recorder.Observe(before, 100);
    recorder.Observe(Snapshot("timeline", 20, 2), 200);
    recorder.Observe(Snapshot("timeline", 30, 3, "ENEMY_FUTURE", phase: GamePhase.Complete, outcome: "WON"), 300);
    await recorder.CompleteAsync();
    var reopened = new SessionStore(database);
    await reopened.InitializeAsync();
    var points = await reopened.ListDecisionPointsAsync("timeline");
    Check(points.Count == 3, "stable decision points missing");
    var restored = await reopened.GetDecisionSnapshotAsync(points[0].Id);
    Check(restored is not null && SnapshotCodec.Serialize(restored) == SnapshotCodec.Serialize(before), "FrozenDictionary snapshot round trip changed state");
    Check(restored!.Entities[11].Cost == 1, "dynamic cost missing");
    Check((await reopened.GetSessionAsync("timeline"))!.EndedAt is not null, "ended game not retained");
}

async Task NoFutureInformation()
{
    var snapshot = await store.GetSnapshotAtAsync("timeline", 20);
    Check(snapshot!.Entities[30].CardId is null, "later revealed enemy leaked into old snapshot");
    var events = await store.GetEventsAtAsync("timeline", 20, 1000);
    Check(events.All(e => e.EventSequence <= 20) && events.All(e => !e.DataJson.Contains("ENEMY_FUTURE")), "future event leaked");
    var context = await store.GetHistoricalContextAtAsync("timeline", 20);
    Check(context!.Snapshot.EventSequence == 20 && context.RecentEvents.All(e => e.EventSequence <= 20), "historical context cutoff changed");
    Check(await store.GetSnapshotAtAsync("timeline", 9) is null, "query before first point returned future state");
}

async Task DuplicateAndBinding()
{
    var count = (await store.GetEventsAtAsync("timeline", 30, 1000)).Count;
    var duplicate = new SessionRecorder(store, Binding());
    duplicate.Observe(Snapshot("timeline", 10), 100);
    duplicate.Observe(Snapshot("timeline", 20, 2), 200);
    duplicate.Observe(Snapshot("timeline", 30, 3, "ENEMY_FUTURE", phase: GamePhase.Complete, outcome: "WON"), 300);
    await duplicate.CompleteAsync();
    Check((await store.ListDecisionPointsAsync("timeline")).Count == 3 &&
        (await store.GetEventsAtAsync("timeline", 30, 1000)).Count == count, "repeat import duplicated history");
    var changed = new SessionRecorder(store, Binding(build: "different"));
    changed.Observe(Snapshot("timeline", 10), 100);
    await Throws<InvalidOperationException>(() => changed.FlushAsync());
    Check((await store.GetSessionAsync("timeline"))!.CardBuild == "255790", "binding changed");
}

async Task AtomicFailure()
{
    var eventsBefore = (await store.GetEventsAtAsync("timeline", 30, 1000)).Count;
    var changed = new SessionRecorder(store, Binding());
    changed.Observe(Snapshot("timeline", 15, 7), 150);
    changed.Observe(Snapshot("timeline", 20, 8, "CONFLICT"), 200);
    await Throws<InvalidDataException>(() => changed.FlushAsync());
    Check(changed.PendingCount >= 2, "failed observations were lost");
    Check((await store.GetSnapshotAtAsync("timeline", 15))!.EventSequence == 10, "partial batch survived rollback");
    Check((await store.GetEventsAtAsync("timeline", 30, 1000)).Count == eventsBefore, "event write escaped transaction");
}

async Task Cancellation()
{
    var recorder = new SessionRecorder(store, Binding());
    recorder.Observe(Snapshot("cancel", 1), 1);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await Throws<OperationCanceledException>(() => recorder.FlushAsync(cancellation.Token));
    Check(recorder.PendingCount == 1 && await store.GetSessionAsync("cancel") is null, "cancel committed or lost buffer");
    await recorder.FlushAsync();
    Check(await store.GetSessionAsync("cancel") is not null, "retry did not save buffered observation");
}

async Task MultipleGames()
{
    var recorder = new SessionRecorder(store, Binding());
    recorder.Observe(Snapshot("multi-1", 1, stable: false, completeLog: false), 1);
    recorder.Observe(Snapshot("multi-1", 2, stable: false, completeLog: false), 2);
    recorder.Observe(Snapshot("multi-2", 1), 3);
    await recorder.CompleteAsync();
    var first = await store.GetSnapshotAtAsync("multi-1", 2);
    Check(first!.EventSequence == 2 && !first.Quality.IsComplete && !first.Quality.IsStable, "partial checkpoint lost its quality");
    Check((await store.GetSnapshotAtAsync("multi-2", 1))!.MatchId == "multi-2", "new match inherited prior history");
}

async Task Statistics()
{
    var deck = await store.SaveDeckVersionAsync("统计专用", Definition(199), "stats-build");
    var recorder = new SessionRecorder(store, Binding(deck.Id, "stats-build"));
    recorder.Observe(Snapshot("stats-win", 1, phase: GamePhase.Complete, outcome: "WON"), 1);
    recorder.Observe(Snapshot("stats-loss", 1, phase: GamePhase.Complete, outcome: "LOST"), 2);
    recorder.Observe(Snapshot("stats-unknown", 1, stable: false, completeLog: false), 3);
    await recorder.CompleteAsync();
    var stats = await store.GetStatsAsync(new(CardBuild: "stats-build", DeckVersionId: deck.Id));
    Check(stats.Games == 3 && stats.Wins == 1 && stats.Losses == 1 && stats.Unknown == 1 && stats.WinRate == 0.5,
        $"sample counts wrong: {stats}");
    var empty = await store.GetStatsAsync(new(CardBuild: "absent"));
    Check(empty.Games == 0 && empty.WinRate is null, "missing statistics should be unknown");
    var oldSession = await store.GetSessionAsync("stats-win");
    var newer = await store.SaveDeckVersionAsync("统计专用", Definition(200), "stats-new", deck.Id);
    Check((await store.GetSessionAsync("stats-win"))!.DeckVersionId == deck.Id && newer.Id != oldSession!.DeckVersionId, "new deck changed historical match");
}

async Task AsOfStatistics()
{
    var cutoff = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
    var recorder = new SessionRecorder(store, Binding(build: "asof-stats"));
    foreach (var id in new[] { "asof-past", "asof-future", "asof-current" })
        recorder.Observe(Snapshot(id, 1, phase: GamePhase.Complete, outcome: id == "asof-current" ? "LOST" : "WON"), 1);
    recorder.Observe(Snapshot("asof-ongoing", 1), 1);
    await recorder.CompleteAsync();
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
    {
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE game_sessions SET started_at=$start,ended_at=CASE id
              WHEN 'asof-past' THEN $past WHEN 'asof-current' THEN $past WHEN 'asof-future' THEN $future ELSE NULL END
            WHERE card_build='asof-stats'
            """;
        command.Parameters.AddWithValue("$start", cutoff.AddHours(-2).ToString("O"));
        command.Parameters.AddWithValue("$past", cutoff.AddHours(-1).ToString("O"));
        command.Parameters.AddWithValue("$future", cutoff.AddHours(1).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
    var filter = new SessionFilter(CardBuild: "asof-stats", EndedBefore: cutoff, ExcludeSessionId: "asof-current");
    Check((await store.ListSessionsAsync(filter)).Select(s => s.Id).SequenceEqual(new[] { "asof-past" }), "as-of sessions exposed later completion or current match");
    var stats = await store.GetStatsAsync(filter);
    Check(stats is { Games: 1, Wins: 1, Losses: 0, Unknown: 0 }, "aggregated as-of statistics leaked final result");
    Check((await store.GetStatsAsync(filter with { ExcludeSessionId = null })).Games == 2, "current match exclusion was not applied in SQL");
    var before = (await store.GetSessionAsync("asof-past"))!.EndedAt;
    var duplicate = new SessionRecorder(store, Binding(build: "asof-stats"));
    duplicate.Observe(Snapshot("asof-past", 1, phase: GamePhase.Complete, outcome: "WON"), 1);
    await duplicate.CompleteAsync();
    Check((await store.GetSessionAsync("asof-past"))!.EndedAt == before && (await store.GetStatsAsync(filter)).Games == 1,
        "repeat import changed original completion evidence time");
}

async Task Retention()
{
    var decksBefore = (await store.ListDeckVersionsAsync()).Count;
    var result = await store.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1));
    Check(result.SessionsDeleted >= 3 && result.EventsDeleted > 0 && result.SnapshotsDeleted > 0, "completed history not removed");
    Check(await store.GetSessionAsync("timeline") is null && (await store.GetEventsAtAsync("timeline", 100, 1000)).Count == 0,
        "history child rows not cascaded");
    Check(await store.GetSessionAsync("stats-unknown") is not null, "unfinished game removed without opt-in");
    Check((await store.ListDeckVersionsAsync()).Count == decksBefore, "cleanup removed deck history");
    var all = await store.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1), includeUnfinished: true);
    Check(all.SessionsDeleted > 0 && (await store.ListSessionsAsync()).Count == 0, "explicit unfinished cleanup failed");
}

async Task ModeStatistics()
{
    var recorder = new SessionRecorder(store, Binding(build: "mode-test") with { Format = "Standard" });
    recorder.Observe(Snapshot("mode-ranked", 1, phase: GamePhase.Complete, outcome: "WON") with { Format = "Standard" }, 1);
    recorder.Observe(Snapshot("mode-casual", 1, phase: GamePhase.Complete, outcome: "LOST") with { GameType = "GT_CASUAL" }, 2);
    await recorder.CompleteAsync();
    var ranked = await store.GetStatsAsync(new(CardBuild: "mode-test", GameType: "GT_RANKED", Format: "STANDARD",
        PlayerClass: "hunter", OpponentClass: "mage", WentFirst: true, Region: "cn"));
    Check(ranked.Games == 1 && ranked.Wins == 1 && ranked.WinRate == 1, "casual game leaked into ranked sample");
    var casual = await store.GetStatsAsync(new(CardBuild: "mode-test", GameType: "GT_CASUAL"));
    Check(casual.Games == 1 && casual.Losses == 1 && casual.WinRate == 0, "casual filter failed");
}

async Task SchemaMigration()
{
    var path = Path.Combine(temp, "legacy.db");
    var legacy = new SessionStore(path);
    await legacy.InitializeAsync();
    var recorder = new SessionRecorder(legacy, Binding());
    recorder.Observe(Snapshot("legacy", 1), 1);
    await recorder.CompleteAsync();
    using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
    {
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE game_sessions DROP COLUMN game_type; UPDATE hs_storage_schema SET version=1";
        await command.ExecuteNonQueryAsync();
    }
    await legacy.InitializeAsync();
    Check((await legacy.GetSessionAsync("legacy"))!.CardBuild == "255790" &&
        (await legacy.GetSnapshotAtAsync("legacy", 1))!.Entities[11].CardId == "OWN_CARD", "migration lost history");
    using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
    {
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM hs_storage_schema";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 2, "migration version was not advanced");
        command.CommandText = "UPDATE hs_storage_schema SET version=99";
        await command.ExecuteNonQueryAsync();
    }
    await Throws<InvalidDataException>(() => legacy.InitializeAsync());
}

Task IdentityGuard()
{
    var snapshot = Snapshot("identity", 1);
    var tags = snapshot.Entities[2].Tags.ToDictionary(x => x.Key, x => x.Value);
    tags["PLAYER_NAME"] = "PRIVATE_PLAYER_NAME";
    var entities = snapshot.Entities.ToDictionary(x => x.Key, x => x.Value);
    entities[2] = entities[2] with { Tags = tags.ToFrozenDictionary() };
    var recorder = new SessionRecorder(store, Binding());
    return Throws<InvalidDataException>(() => recorder.ObserveAsync(snapshot with { Entities = entities.ToFrozenDictionary() }, 1));
}

async Task ChoiceRoundTrip()
{
    var sample = Snapshot("choice", 10) with
    {
        ClientBuild = "255790", Format = "STANDARD", PerspectiveSource = "known_hand",
        Choice = new(7, 1, "GENERAL", 11, 1, 1, new[] { new ChoiceOptionSnapshot(40, "KNOWN_CHOICE", 1, 1) }, true),
        RecentEvents = new[] { new ObservedGameEvent(10, "CardDrawn", 11, 1, "OWN_CARD", "DECK", "HAND", "我方抽取一张牌") },
        DeckTracking = new("choice", 10, "deck-version", "255790", 3, new Dictionary<string,int> { ["OWN_CARD"] = 2, ["OTHER_CARD"] = 1 }, 0, true, [])
    };
    var recorder = new SessionRecorder(store, Binding()); recorder.Observe(sample, 90);
    await recorder.CompleteAsync();
    var restored = await store.GetSnapshotAtAsync("choice", 10) ?? throw new InvalidOperationException("choice snapshot missing");
    Check(restored!.Choice?.Options.Single().CardId == "KNOWN_CHOICE" && restored.Choice.IsReady,
        "choice options were not persisted");
    Check(restored.ClientBuild == "255790" && restored.Format == "STANDARD" && restored.PerspectiveSource == "known_hand" &&
        restored.RecentEvents.Single().Kind == "CardDrawn", "snapshot init properties were lost");
    var events = await store.GetEventsAtAsync("choice", 10);
    Check(events.Any(e => e.Kind == "CardDrawn"), "observed normalized event missing");
    Check(restored.DeckTracking is { TotalCards: 3, IsExact: true, DeckVersion: "deck-version" } &&
        restored.DeckTracking.KnownRemaining is FrozenDictionary<string,int> && restored.DeckTracking.KnownRemaining["OWN_CARD"] == 2,
        "deck tracking fields were lost or restored into mutable map");
    await Throws<NotSupportedException>(() =>
    {
        ((IList<string>)restored.DeckTracking!.Warnings).Add("MUTATED");
        return Task.CompletedTask;
    });
    await Throws<NotSupportedException>(() =>
    {
        ((IList<ChoiceOptionSnapshot>)restored.Choice!.Options).Add(new(99, "MUTATED", 1, 2));
        return Task.CompletedTask;
    });
    await Throws<NotSupportedException>(() =>
    {
        ((IList<ObservedGameEvent>)restored.RecentEvents).Clear();
        return Task.CompletedTask;
    });
    await Throws<NotSupportedException>(() =>
    {
        ((IList<string>)restored.Quality.Warnings).Add("MUTATED");
        return Task.CompletedTask;
    });
}

async Task Pipeline()
{
    var pipeline = new LogRecordingPipeline(store, null, "255790");
    var unknown = Snapshot("pipeline-a", 1) with { LocalPlayerId = null, Quality = new(true, false, false, false, 0, ["视角未知"]) };
    pipeline.Observe(unknown, 1);
    Check(pipeline.CurrentSessionId is null && pipeline.UnconfirmedMatchCount == 1, "unconfirmed perspective was guessed");
    pipeline.Observe(Snapshot("pipeline-a", 2), 2);
    pipeline.Observe(Snapshot("pipeline-b", 1) with { LocalPlayerId = 2 }, 3);
    pipeline.Observe(Snapshot("spectating", 1) with { IsSpectating = true }, 4);
    await pipeline.CompleteAsync();
    Check((await store.GetSessionAsync("pipeline-a"))!.LocalPlayerId == 1 &&
        (await store.GetSessionAsync("pipeline-b"))!.LocalPlayerId == 2, "per-game perspective binding failed");
    Check(pipeline.RecordedMatchCount == 2 && pipeline.UnconfirmedMatchCount == 0 && pipeline.SpectatedMatchCount == 1,
        "pipeline counters wrong");
    Check(await store.GetSessionAsync("spectating") is null, "spectated game polluted personal history");
    await Throws<InvalidOperationException>(() => pipeline.ObserveAsync(Snapshot("pipeline-a", 3) with { LocalPlayerId = 2 }, 5));
}

async Task TrackingHistory()
{
    var definition = new DeckDefinition(2, [31], [new(100, 2), new(101, 1)], []);
    var version = await store.SaveDeckVersionAsync("历史记牌样本", definition, "255790");
    var tracker = new DeckTracker(PinnedDeck.Create(version.Id, "255790", 1,
        new Dictionary<string,int> { ["OWN_CARD"] = 2, ["OTHER_CARD"] = 1 }));
    var first = Snapshot("tracking-flow", 1) with { ClientBuild = "255790" };
    var firstEntities = first.Entities.ToDictionary(pair => pair.Key, pair => pair.Value);
    EntitySnapshot Hidden(int id) => new(id, null, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = "1", ["ZONE"] = "DECK" }.ToFrozenDictionary());
    firstEntities[100] = Hidden(100); firstEntities[101] = Hidden(101);
    first = first with { Entities = firstEntities.ToFrozenDictionary() };
    tracker.Observe(first);
    first = first with { DeckTracking = tracker.Snapshot() };
    Check(first.DeckTracking!.IsExact && tracker.CalculateDrawOdds("OWN_CARD", 1).Probability == 0.5,
        "initial tracking fixture is not exact");
    var nextEntities = first.Entities.ToDictionary(pair => pair.Key, pair => pair.Value);
    nextEntities[100] = new(100, "OTHER_CARD", EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = "1", ["ZONE"] = "HAND", ["COST"] = "1" }.ToFrozenDictionary());
    var second = first with { StateVersion = 2, EventSequence = 2, Entities = nextEntities.ToFrozenDictionary(),
        RecentEvents = new[] { new ObservedGameEvent(2, "CardDrawn", 100, 1, "OTHER_CARD", "DECK", "HAND", "我方抽取一张牌") } };
    tracker.Observe(second);
    second = second with { DeckTracking = tracker.Snapshot() };
    Check(second.DeckTracking!.IsExact && tracker.CalculateDrawOdds("OWN_CARD", 1).Probability == 1,
        "later tracking fixture is not exact");
    var recorder = new SessionRecorder(store, Binding(version.Id));
    recorder.Observe(first, 1); recorder.Observe(second, 2);
    await recorder.CompleteAsync();
    var old = (await store.GetHistoricalContextAtAsync("tracking-flow", 1))!.Snapshot.DeckTracking!;
    var latest = (await store.GetHistoricalContextAtAsync("tracking-flow", 2))!.Snapshot.DeckTracking!;
    Check(old.IsExact && old.TotalCards == 2 && old.KnownRemaining["OTHER_CARD"] == 1 &&
        latest.TotalCards == 1 && latest.KnownRemaining.GetValueOrDefault("OTHER_CARD") == 0,
        "later draw changed saved earlier composition");
}

async Task RealLogProbe(string path)
{
    var checkDirectory = Path.Combine(root, ".local", "storage-real-check", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(checkDirectory);
    var checkDb = Path.Combine(checkDirectory, "history.db");
    var checkStore = new SessionStore(checkDb);
    await checkStore.InitializeAsync();
    var pipeline = new LogRecordingPipeline(checkStore, null, ClientBuildMetadata.ReadPowerLogBuild(path) ?? "unknown");
    var engine = new PowerLogStateEngine(sessionId: "storage-real-probe", autoDetectPerspective: true);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    long lineNumber = 0, dirty = 0;
    double coreMilliseconds = 0, recordingMilliseconds = 0, flushMilliseconds = 0;
    await foreach (var line in SharedLogReplay.ReadLinesAsync(path))
    {
        lineNumber++;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = engine.ProcessLine(line);
        var snapshot = changed ? engine.Snapshot() : null;
        coreMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (snapshot is null) continue;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        pipeline.Observe(snapshot, lineNumber);
        recordingMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (++dirty % 512 == 0)
        {
            start = System.Diagnostics.Stopwatch.GetTimestamp();
            await pipeline.FlushAsync();
            flushMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }
    var completeStart = System.Diagnostics.Stopwatch.GetTimestamp();
    await pipeline.CompleteAsync();
    flushMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(completeStart).TotalMilliseconds;
    var sessions = await checkStore.ListSessionsAsync(new(Limit: 10000));
    var points = 0;
    foreach (var session in sessions) points += (await checkStore.ListDecisionPointsAsync(session.Id)).Count;
    watch.Stop();
    Console.WriteLine($"REAL STORAGE: lines={lineNumber}; changed={dirty}; sessions={sessions.Count}; decisions={points}; " +
        $"unknown={pipeline.UnconfirmedMatchCount}; spectated={pipeline.SpectatedMatchCount}; milliseconds={watch.ElapsedMilliseconds}; bytes={new FileInfo(checkDb).Length}; " +
        $"core_ms={coreMilliseconds:F0}; observe_ms={recordingMilliseconds:F0}; flush_ms={flushMilliseconds:F0}");
}

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static string FindWorkspace()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HsHelper.slnx"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Run storage checks from the workspace.");
}
