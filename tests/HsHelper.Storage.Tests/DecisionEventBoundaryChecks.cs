using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class DecisionEventBoundaryChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "decision-event-boundary");
        yield return ("exact decisions exclude a later public reveal at the same sequence", () => ExactVersions(Path.Combine(root, "exact.db")));
        yield return ("decision state filtering precedes the bounded event limit", () => FilterBeforeLimit(Path.Combine(root, "limit.db")));
        yield return ("exact decisions retain original binding time and immutable data", () => BindingAndFreeze(Path.Combine(root, "binding.db")));
        yield return ("late same-sequence writes cannot pollute repeated concurrent decision reads", () => ConcurrentLateWrite(Path.Combine(root, "concurrent.db")));
        yield return ("schema two migration preserves legacy payloads hashes and unknown provenance", () => Migration(Path.Combine(root, "migration.db")));
        yield return ("legacy same-sequence events are omitted with a gap while earlier events remain", () => LegacyGap(Path.Combine(root, "legacy-gap.db")));
        yield return ("new events after migration receive actual observation state provenance", () => FreshProvenance(Path.Combine(root, "fresh.db")));
        yield return ("replay preserves original event provenance and conflicting batches stay queued", () => Replay(Path.Combine(root, "replay.db")));
        yield return ("recorders reject state version rollback without changing valid queued history", () => MonotonicVersions(Path.Combine(root, "monotonic.db")));
        yield return ("invalid cancelled and missing exact-decision queries do not write data", () => InvalidAndCancelled(Path.Combine(root, "invalid.db")));
        yield return ("cleanup removes exact decisions without falling back to another session", () => Cleanup(Path.Combine(root, "cleanup.db")));
        yield return ("future history schema four is rejected without rewriting existing data", () => FutureSchema(Path.Combine(root, "future.db")));
    }

    private static async Task ExactVersions(string path)
    {
        var store = await Open(path); var (recorder, earlier, point) = await Earlier(store, "exact-source");
        var initial = await store.GetHistoricalDecisionAsync(point.Id);
        var later = Reveal(earlier); recorder.Observe(later, 41); await recorder.CompleteAsync();
        var actual = (await store.GetHistoricalDecisionAsync(point.Id))!;
        var latestPoint = (await store.ListDecisionPointsAsync(earlier.MatchId, true)).Single(row => row.StateVersion == 3);
        var latest = (await store.GetHistoricalDecisionAsync(latestPoint.Id))!;
        Check(actual.Point == point && actual.Context.Snapshot.StateVersion == 2 && actual.Context.Snapshot.Entities[30].CardId is null &&
            !actual.HasUnattributedEvents && actual.Context.RecentEvents.All(entry => entry.EventSequence < 40),
            "A later same-sequence public card or event entered an earlier exact decision.");
        Check(JsonSerializer.Serialize(initial) == JsonSerializer.Serialize(actual) && latest.Context.Snapshot.Entities[30].CardId == "PUBLIC_LATER_CARD" &&
            latest.Context.RecentEvents.Any(entry => entry.EventSequence == 40 && entry.CardId == "PUBLIC_LATER_CARD") && !latest.HasUnattributedEvents,
            "Strict reading changed an old decision or permanently deleted an attributable newer event.");
        var legacyLookup = (await store.GetHistoricalContextAtAsync(earlier.MatchId, 41))!;
        Check(legacyLookup.Snapshot.EventSequence == 40 && legacyLookup.Snapshot.StateVersion == 3 &&
            legacyLookup.RecentEvents.Any(entry => entry.CardId == "PUBLIC_LATER_CARD"), "The existing sequence-only API semantics changed.");
    }

    private static async Task FilterBeforeLimit(string path)
    {
        var store = await Open(path); var (recorder, earlier, point) = await Earlier(store, "bounded-source");
        var before = (await store.GetHistoricalDecisionAsync(point.Id, 3))!;
        var additions = Enumerable.Range(100, 120).Select(id => Entity(id, "PUBLIC_ADDITION_" + id, "PLAY", 2));
        recorder.Observe(Reveal(earlier) with { Entities = Reveal(earlier).Entities.Values.Concat(additions).ToFrozenDictionary(entity => entity.Id) }, 41);
        await recorder.CompleteAsync();
        var old = (await store.GetHistoricalDecisionAsync(point.Id, 3))!;
        Check(old.Context.RecentEvents.Count == 3 && JsonSerializer.Serialize(old) == JsonSerializer.Serialize(before),
            "Future same-sequence events consumed the old decision's bounded event quota.");
        var latest = (await store.ListDecisionPointsAsync(earlier.MatchId, true)).Last();
        var recent = (await store.GetHistoricalDecisionAsync(latest.Id, 3))!.Context.RecentEvents;
        Check(recent.Count == 3 && recent.All(entry => entry.EventSequence == 40) && recent.Select(entry => entry.EventIndex).SequenceEqual(recent.Select(entry => entry.EventIndex).Order()),
            "Eligible event limits or chronological event-index ordering were not stable.");
    }

    private static async Task BindingAndFreeze(string path)
    {
        var store = await Open(path);
        var deck = await store.SaveDeckVersionAsync("原套牌", new(2, [31], [new(100, 1)], [new(100, [new(101, 1)])]), "255790");
        var (recorder, earlier, point) = await Earlier(store, "bound-source", deck.Id);
        recorder.Observe(Reveal(earlier), 41); await recorder.CompleteAsync();
        var before = await Fingerprint(path);
        var result = (await new SessionStore(path).GetHistoricalDecisionAsync(point.Id))!;
        Check(result.Point.RecordedAt == point.RecordedAt && result.Context is { SessionId: "bound-source", CardBuild: "255790" } &&
            result.Context.DeckVersionId == deck.Id && result.Context.Deck?.Id == deck.Id && result.Context.Snapshot.StateVersion == 2,
            "The exact decision borrowed a newer state, time, build or deck.");
        await Throws<NotSupportedException>(() => { ((IList<SessionEvent>)result.Context.RecentEvents).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<DeckCard>)result.Context.Deck!.Definition.Cards).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<int>)result.Context.Deck!.Definition.Heroes).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<DeckSideboard>)result.Context.Deck!.Definition.Sideboards).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<DeckCard>)result.Context.Deck!.Definition.Sideboards[0].Cards).Clear(); return Task.CompletedTask; });
        Check(result.Context.Snapshot.Entities is FrozenDictionary<int, EntitySnapshot> && await Fingerprint(path) == before,
            "Reading exact decision data changed persistence or returned mutable snapshot entities.");
        var (_, _, unboundPoint) = await Earlier(store, "unbound-source");
        var unbound = (await store.GetHistoricalDecisionAsync(unboundPoint.Id))!;
        Check(unbound.Context.DeckVersionId is null && unbound.Context.Deck is null, "An unbound session inherited another session's deck.");
    }

    private static async Task ConcurrentLateWrite(string path)
    {
        var store = await Open(path); var (recorder, earlier, point) = await Earlier(store, "concurrent-source");
        var baseline = JsonSerializer.Serialize(await store.GetHistoricalDecisionAsync(point.Id));
        var reads = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
                Check(JsonSerializer.Serialize(await new SessionStore(path).GetHistoricalDecisionAsync(point.Id)) == baseline,
                    "A concurrent later write changed the exact older view.");
        })).ToArray();
        await Task.Run(async () => { recorder.Observe(Reveal(earlier), 41); await recorder.CompleteAsync(); });
        await Task.WhenAll(reads);
        Check(JsonSerializer.Serialize(await store.GetHistoricalDecisionAsync(point.Id)) == baseline,
            "Reopening after the writer exposed later same-sequence events.");
    }

    private static async Task Migration(string path)
    {
        var store = await Open(path); var (recorder, earlier, _) = await Earlier(store, "migrated-source");
        recorder.Observe(Reveal(earlier), 41); await recorder.CompleteAsync();
        await MakeSchemaTwo(path);
        var before = await Fingerprint(path); var oldColumns = await Columns(path);
        Check(!oldColumns.Contains("observed_state_version"), "Migration fixture retained the new column instead of a real schema-two table.");
        var reopened = new SessionStore(path); await reopened.InitializeAsync();
        Check(await Scalar(path, "SELECT version FROM hs_storage_schema") == 3 && (await Columns(path)).Contains("observed_state_version") &&
            await Scalar(path, "SELECT COUNT(*) FROM session_events WHERE observed_state_version IS NOT NULL") == 0 &&
            await Fingerprint(path) == before, "Migration rewrote old payload JSON, compressed snapshots, binding hashes, IDs or attributed old events.");
        await reopened.InitializeAsync();
        Check(await Fingerprint(path) == before && await Scalar(path, "SELECT version FROM hs_storage_schema") == 3,
            "Repeated initialization rewrote migrated history.");
    }

    private static async Task LegacyGap(string path)
    {
        var store = await Open(path); var (recorder, earlier, point) = await Earlier(store, "legacy-source");
        recorder.Observe(Reveal(earlier), 41); await recorder.CompleteAsync();
        await MakeSchemaTwo(path); await store.InitializeAsync();
        var older = (await store.GetHistoricalDecisionAsync(point.Id, 1))!;
        var latestPoint = (await store.ListDecisionPointsAsync(earlier.MatchId, true)).Last();
        var latest = (await store.GetHistoricalDecisionAsync(latestPoint.Id))!;
        Check(older.HasUnattributedEvents && older.Context.RecentEvents.Count == 1 && older.Context.RecentEvents[0].EventSequence < 40 &&
            latest.HasUnattributedEvents && latest.Context.RecentEvents.All(entry => entry.EventSequence < 40),
            "An old unattributed same-sequence event leaked or its gap disappeared under a small event limit.");
        var first = (await store.ListDecisionPointsAsync(earlier.MatchId, true)).First();
        Check((await store.GetHistoricalDecisionAsync(first.Id)) is { HasUnattributedEvents: true, Context.RecentEvents.Count: 0 },
            "A legacy first decision invented same-sequence provenance.");
        Check((await store.GetEventsAtAsync(earlier.MatchId, 40)).Any(entry => entry.CardId == "PUBLIC_LATER_CARD"),
            "Strict-view filtering deleted immutable legacy events from the sequence-only history.");
    }

    private static async Task FreshProvenance(string path)
    {
        var store = await Open(path); var frame = Frame("fresh-source", 1, 1);
        var recorder = Recorder(store); recorder.Observe(frame, 1); await recorder.FlushAsync();
        await MakeSchemaTwo(path); await store.InitializeAsync();
        var earlier = frame with { EventSequence = 40, StateVersion = 2 }; recorder.Observe(earlier, 40); await recorder.FlushAsync();
        recorder.Observe(Reveal(earlier), 41); await recorder.CompleteAsync();
        Check(await Scalar(path, "SELECT COUNT(*) FROM session_events WHERE event_seq=40 AND observed_state_version=3") == 1 &&
            await Scalar(path, "SELECT COUNT(*) FROM session_events WHERE event_seq=1 AND observed_state_version IS NULL") > 0,
            "A fresh post-migration event lost its actual state version or old rows were backfilled.");
        var points = await store.ListDecisionPointsAsync(frame.MatchId, true);
        var old = (await store.GetHistoricalDecisionAsync(points.Single(point => point.StateVersion == 2).Id))!;
        var latest = (await store.GetHistoricalDecisionAsync(points.Single(point => point.StateVersion == 3).Id))!;
        Check(!old.HasUnattributedEvents && !latest.HasUnattributedEvents && old.Context.RecentEvents.All(entry => entry.EventSequence < 40) &&
            latest.Context.RecentEvents.Any(entry => entry.CardId == "PUBLIC_LATER_CARD"), "An earlier-sequence legacy row caused a false current gap or new attributable events stayed hidden.");
    }

    private static async Task Replay(string path)
    {
        var store = await Open(path); var first = Frame("replay-source", 1, 1); var recorder = Recorder(store);
        recorder.Observe(first, 1); await recorder.CompleteAsync();
        var point = (await store.ListDecisionPointsAsync(first.MatchId)).Single(); var before = await Provenance(path);
        var repeated = Recorder(store); repeated.Observe(first, 999); await repeated.CompleteAsync();
        Check(await Provenance(path) == before && (await store.ListDecisionPointsAsync(first.MatchId)).Single() == point,
            "Idempotent replay changed the original event provenance or decision time.");
        await MakeSchemaTwo(path); await store.InitializeAsync(); var migrated = await Provenance(path);
        var legacyReplay = Recorder(store); legacyReplay.Observe(first, 1000); await legacyReplay.CompleteAsync();
        Check(await Provenance(path) == migrated, "Replaying legacy rows backfilled provenance from a later observation.");
        var conflicting = Recorder(store); conflicting.Observe(Reveal(first), 2000);
        await Throws<InvalidDataException>(() => conflicting.FlushAsync());
        Check(conflicting.PendingCount == 1 && await Provenance(path) == migrated &&
            (await store.GetHistoricalDecisionAsync(point.Id))!.Context.Snapshot.Entities[30].CardId is null,
            "A conflicting replay overwrote immutable events or lost its queued batch.");
    }

    private static async Task MonotonicVersions(string path)
    {
        var store = await Open(path); var frame = Frame("monotonic-source", 40, 3); var recorder = Recorder(store);
        recorder.Observe(frame, 40); var queued = recorder.PendingCount;
        await Throws<InvalidOperationException>(() => { recorder.Observe(Reveal(frame) with { StateVersion = 2 }, 41); return Task.CompletedTask; });
        await Throws<InvalidOperationException>(() => { recorder.Observe(frame with { EventSequence = 41, StateVersion = 2 }, 41); return Task.CompletedTask; });
        Check(recorder.PendingCount == queued, "Rejected version rollback changed the queued valid observations.");
        recorder.Observe(frame, 40); Check(recorder.PendingCount == queued, "Same-state duplicate stopped being idempotent.");
        recorder.Observe(frame with { EventSequence = 41 }, 41); await recorder.CompleteAsync();
        Check((await store.ListDecisionPointsAsync(frame.MatchId, true)).Count == 2, "Non-decreasing same-version sequence progression was rejected.");
        recorder.Observe(Frame("new-match-reset", 1, 1), 42); await recorder.CompleteAsync();
        Check(await store.GetSessionAsync("new-match-reset") is not null, "A new match inherited the previous match's version floor.");
    }

    private static async Task InvalidAndCancelled(string path)
    {
        var store = await Open(path); var (_, _, point) = await Earlier(store, "invalid-source"); var before = await Fingerprint(path);
        foreach (var id in new long[] { 0, -1, long.MinValue }) await Throws<ArgumentOutOfRangeException>(() => store.GetHistoricalDecisionAsync(id));
        foreach (var limit in new[] { 0, -1, 10001 }) await Throws<ArgumentOutOfRangeException>(() => store.GetHistoricalDecisionAsync(point.Id, limit));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.GetHistoricalDecisionAsync(point.Id, cancellationToken: cancelled.Token));
        Check(await store.GetHistoricalDecisionAsync(long.MaxValue) is null &&
            (await store.GetHistoricalDecisionAsync(point.Id, 10000))!.Point == point && await Fingerprint(path) == before,
            "A missing, rejected or cancelled exact query changed stored history or fell back to another decision.");
    }

    private static async Task Cleanup(string path)
    {
        var store = await Open(path); var (_, _, point) = await Earlier(store, "cleaned-source");
        var (_, _, other) = await Earlier(store, "other-source");
        await using (var connection = await Connect(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE game_sessions SET started_at=$old,ended_at=$old WHERE id='cleaned-source'";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddYears(-1).ToString("O", CultureInfo.InvariantCulture)); await command.ExecuteNonQueryAsync();
        }
        var result = await store.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-1));
        Check(result.SessionsDeleted == 1 && result.EventsDeleted > 0 && result.SnapshotsDeleted == 2 &&
            await store.GetHistoricalDecisionAsync(point.Id) is null && (await store.GetHistoricalDecisionAsync(other.Id))!.Point == other,
            "Cleanup retained an orphan decision or exact lookup fell back to another surviving session.");
    }

    private static async Task FutureSchema(string path)
    {
        var store = await Open(path); await Earlier(store, "future-schema-source");
        await using (var connection = await Connect(path))
        await using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE hs_storage_schema SET version=4"; await command.ExecuteNonQueryAsync(); }
        var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        await Throws<InvalidDataException>(() => new SessionStore(path).InitializeAsync());
        Check(await Scalar(path, "SELECT version FROM hs_storage_schema") == 4 &&
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) == before, "Future-schema rejection rewrote its version or database bytes.");
    }

    private static async Task<(SessionRecorder Recorder, GameSnapshot Earlier, DecisionPoint Point)> Earlier(SessionStore store, string match, string? deck = null)
    {
        var recorder = Recorder(store, deck); var initial = Frame(match, 1, 1); recorder.Observe(initial, 1);
        var earlier = initial with { EventSequence = 40, StateVersion = 2 }; recorder.Observe(earlier, 40); await recorder.FlushAsync();
        return (recorder, earlier, (await store.ListDecisionPointsAsync(match, true)).Single(point => point.StateVersion == 2));
    }
    private static SessionRecorder Recorder(SessionStore store, string? deck = null) => new(store, new(deck, "255790", 1, PlayerClass: "HUNTER"));
    private static GameSnapshot Reveal(GameSnapshot frame) => frame with
    {
        StateVersion = 3,
        Entities = frame.Entities.Values.Where(entity => entity.Id != 30).Append(Entity(30, "PUBLIC_LATER_CARD", "PLAY", 2)).ToFrozenDictionary(entity => entity.Id)
    };
    private static GameSnapshot Frame(string match, long sequence, long version) => new(match, version, sequence, 1, 1, 1, GamePhase.Main,
        new[]
        {
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["HERO_ENTITY"] = "11", ["CURRENT_PLAYER"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["HERO_ENTITY"] = "21" }.ToFrozenDictionary()),
            Entity(11, "HERO_05", "PLAY", 1), Entity(21, "HERO_08", "PLAY", 2), Entity(30, null, "HAND", 2)
        }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790", Format = "STANDARD", GameType = "GT_RANKED" };
    private static EntitySnapshot Entity(int id, string? card, string zone, int owner) => new(id, card, EntityKind.Card,
        new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString(CultureInfo.InvariantCulture) }.ToFrozenDictionary());
    private static async Task<SessionStore> Open(string path) { var store = new SessionStore(path); await store.InitializeAsync(); return store; }
    private static async Task<SqliteConnection> Connect(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString()); await connection.OpenAsync(); return connection; }
    private static async Task MakeSchemaTwo(string path)
    {
        await using var connection = await Connect(path); await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE session_events DROP COLUMN observed_state_version; UPDATE hs_storage_schema SET version=2"; await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> Scalar(string path, string sql)
    { await using var connection = await Connect(path); await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture); }
    private static async Task<string[]> Columns(string path)
    {
        await using var connection = await Connect(path); await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA table_info(session_events)";
        var columns = new List<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) columns.Add(reader.GetString(1)); return columns.ToArray();
    }
    private static async Task<string> Fingerprint(string path)
    {
        await using var connection = await Connect(path); await using var command = connection.CreateCommand(); var rows = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT id||':'||binding_hash||':'||card_build||':'||COALESCE(deck_version_id,'') FROM game_sessions ORDER BY id",
            "SELECT session_id||':'||event_seq||':'||event_index||':'||kind||':'||data_json FROM session_events ORDER BY session_id,event_seq,event_index",
            "SELECT id||':'||session_id||':'||event_seq||':'||state_version||':'||recorded_at||':'||hex(snapshot_json) FROM session_snapshots ORDER BY id",
            "SELECT id||':'||version_key||':'||definition_json FROM saved_deck_versions ORDER BY id"
        })
        {
            command.CommandText = sql; await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        }
        return string.Join("\n", rows);
    }
    private static async Task<string> Provenance(string path)
    {
        await using var connection = await Connect(path); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id||':'||event_seq||':'||event_index||':'||source_line||':'||COALESCE(observed_state_version,'NULL')||':'||data_json FROM session_events ORDER BY session_id,event_seq,event_index";
        var rows = new List<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) rows.Add(reader.GetString(0)); return string.Join("\n", rows);
    }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
}
