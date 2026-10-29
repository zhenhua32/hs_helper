using System.Collections.Frozen;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

static class HistoricalStatsChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "historical-stats");
        yield return ("as-of stats never use later results classes or first-player metadata", () => LateMetadata(Path.Combine(root, "late.db")));
        yield return ("as-of stats report candidate overflow instead of truncated samples", () => Overflow(Path.Combine(root, "overflow.db")));
    }
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static GameSnapshot Frame(string id, long sequence, GamePhase phase = GamePhase.Complete,
        string? outcome = null, string? localClass = null, string? opponentClass = null, bool knownFirst = false,
        string? format = null, string? gameType = null)
    {
        EntitySnapshot Entity(int entity, EntityKind kind, params (string Tag, string? Value)[] tags)
            => new(entity, null, kind, tags.Where(t => t.Value is not null).ToFrozenDictionary(t => t.Tag, t => t.Value!));
        var entities = new[]
        {
            Entity(1, EntityKind.Game, ("STATE", phase == GamePhase.Complete ? "COMPLETE" : "RUNNING")),
            Entity(2, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("PLAYSTATE", outcome), ("FIRST_PLAYER", knownFirst ? "1" : null)),
            Entity(3, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "20")),
            Entity(10, EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CLASS", localClass)),
            Entity(20, EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CLASS", opponentClass))
        };
        return new(id, sequence, sequence, 1, 1, 1, phase, entities.ToFrozenDictionary(e => e.Id), new(true, true, true, true, 0, []))
        { Format = format, GameType = gameType };
    }
    private static async Task LateMetadata(string path)
    {
        var store = new SessionStore(path); await store.InitializeAsync();
        var binding = new SessionBinding(null, "stats-asof", 1);
        var lateUnknown = Frame("late", 1);
        var lateKnown = Frame("late", 2, outcome: "WON", localClass: "HUNTER", opponentClass: "MAGE", knownFirst: true, format: "STANDARD", gameType: "GT_RANKED");
        var previous = Frame("previous", 1, outcome: "WON", localClass: "HUNTER", opponentClass: "WARRIOR", knownFirst: true, format: "STANDARD", gameType: "GT_RANKED");
        var current = Frame("current", 1, outcome: "LOST", localClass: "HUNTER", opponentClass: "MAGE", knownFirst: true, format: "STANDARD", gameType: "GT_RANKED");
        var futureInitial = Frame("future", 1, GamePhase.Main);
        var futureEnd = Frame("future", 2, outcome: "CONCEDED", localClass: "HUNTER", opponentClass: "PRIEST", knownFirst: true, format: "STANDARD", gameType: "GT_RANKED");
        var recorder = new SessionRecorder(store, binding);
        foreach (var frame in new[] { previous, lateUnknown, lateKnown, current, futureInitial, futureEnd }) recorder.Observe(frame, frame.EventSequence);
        await recorder.CompleteAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE session_snapshots SET recorded_at=CASE WHEN event_seq=2 THEN $future ELSE $past END;
                UPDATE game_sessions SET started_at=$start,ended_at=CASE WHEN id='future' THEN $future ELSE $past END
                """;
            command.Parameters.AddWithValue("$start", Cutoff.AddHours(-2).ToString("O"));
            command.Parameters.AddWithValue("$past", Cutoff.AddHours(-1).ToString("O"));
            command.Parameters.AddWithValue("$future", Cutoff.AddHours(1).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var filter = new SessionFilter(CardBuild: "stats-asof", Region: "CN", ExcludeSessionId: "current");
        var then = await store.GetStatsAsOfAsync(Cutoff, filter);
        Check(then is { Games: 2, Wins: 1, Losses: 0, Unknown: 1 } && then.Matchups.Any(m => m.OpponentClass == "Unknown" && m.Unknown == 1),
            "As-of stats inferred a later win or opponent class from session metadata.");
        var later = await store.GetStatsAsOfAsync(Cutoff.AddHours(2), filter);
        Check(later is { Games: 3, Wins: 2, Losses: 1, Unknown: 0 }, "Later as-of point lost actually observed result or future completion.");
        var mage = filter with { OpponentClass = "MAGE", PlayerClass = "HUNTER", WentFirst = true, Format = "STANDARD", GameType = "GT_RANKED" };
        Check((await store.GetStatsAsOfAsync(Cutoff, mage)).Games == 0 && (await store.GetStatsAsOfAsync(Cutoff.AddHours(2), mage)) is { Games: 1, Wins: 1 },
            "Dynamic filters used future class/format/first-player data.");
        Check((await store.GetStatsAsync(mage)).Games == 1, "Fixture did not retain final metadata to distinguish historical query.");
        Check((await store.GetStatsAsOfAsync(Cutoff, filter with { EndedBefore = Cutoff.AddHours(-2) })).Games == 0, "Completion-time filter used an after-cutoff observation.");
        var before = (await store.ListDecisionPointsAsync("late"))[0].RecordedAt;
        var duplicate = new SessionRecorder(store, binding); duplicate.Observe(lateUnknown, 1); duplicate.Observe(lateKnown, 2); await duplicate.CompleteAsync();
        Check((await store.ListDecisionPointsAsync("late"))[0].RecordedAt == before && (await store.GetStatsAsOfAsync(Cutoff, filter)) is { Games: 2, Wins: 1, Unknown: 1 },
            "Repeated import rewrote original knowledge times or disclosed later metadata.");
    }
    private static async Task Overflow(string path)
    {
        var store = new SessionStore(path); await store.InitializeAsync();
        var minimal = new GameSnapshot("limit-template", 1, 1, 1, 1, 1, GamePhase.Complete,
            new Dictionary<int, EntitySnapshot>().ToFrozenDictionary(), new(true, true, true, true, 0, []));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS(SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<10001)
                INSERT INTO game_sessions(id,binding_hash,card_build,format,region,started_at,updated_at)
                  SELECT 'limit-'||i,'binding','overflow-asof','STANDARD','CN',$at,$at FROM n;
                INSERT INTO session_snapshots(session_id,event_seq,state_version,turn,phase,is_stable,is_complete,is_decision,recorded_at,snapshot_json)
                  SELECT id,1,1,1,'Complete',1,1,1,$at,replace($json,'limit-template',id) FROM game_sessions;
                """;
            command.Parameters.AddWithValue("$at", Cutoff.AddHours(-1).ToString("O"));
            command.Parameters.AddWithValue("$json", SnapshotCodec.Serialize(minimal));
            await command.ExecuteNonQueryAsync();
        }
        await Throws<InvalidDataException>(() => store.GetStatsAsOfAsync(Cutoff, new(CardBuild: "overflow-asof", Limit: 1)));
        Check((await store.GetStatsAsOfAsync(Cutoff, new(CardBuild: "missing"))).Games == 0, "Immutable binding filter was not applied before candidate bound.");
    }
}
