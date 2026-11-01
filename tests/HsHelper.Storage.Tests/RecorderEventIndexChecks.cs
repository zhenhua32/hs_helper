using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

internal static class RecorderEventIndexChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        yield return ("same-sequence observations append immutable events and preserve exact decision limits", () => SameSequence(Path.Combine(Path.GetDirectoryName(database)!, "recorder-event-index.db")));
    }

    private static async Task SameSequence(string path)
    {
        var store = new SessionStore(path); await store.InitializeAsync();
        var first = Frame("same-sequence-index", 40, 2);
        var gap = first with { StateVersion = 3, Quality = new(true, false, false, true, 0, ["same-sequence-gap"]) };
        var next = gap with
        {
            StateVersion = 4, EventSequence = 41,
            Entities = gap.Entities.Values.Select(entity => entity.Id == 3 ? entity with
            {
                Tags = entity.Tags.Append(new KeyValuePair<string, string>("RESOURCES", "5")).ToFrozenDictionary()
            } : entity).ToFrozenDictionary(entity => entity.Id)
        };
        var other = Frame("same-sequence-new-match", 40, 1);
        var recorder = new SessionRecorder(store, new(null, "255790", 1));
        recorder.Observe(first, 1); await recorder.FlushAsync();
        var point = (await store.ListDecisionPointsAsync(first.MatchId, true)).Single();
        var exactBefore = JsonSerializer.Serialize(await store.GetHistoricalDecisionAsync(point.Id, 1));
        var oldCount = (await store.GetEventsAtAsync(first.MatchId, 40)).Count;
        Check(oldCount > 1, "The initial observation must already own sequence indexes.");
        recorder.Observe(gap, 2); await recorder.FlushAsync();
        var events = await store.GetEventsAtAsync(first.MatchId, 40);
        Check(events.Count == oldCount + 1 && events.Select(entry => entry.EventIndex).SequenceEqual(Enumerable.Range(0, events.Count)) &&
            events.Last().Kind == "QualityChanged", "Later quality markers did not append after existing same-sequence events.");
        Check(JsonSerializer.Serialize(await store.GetHistoricalDecisionAsync(point.Id, 1)) == exactBefore,
            "A later same-sequence event changed an earlier exact decision or consumed its bounded event quota.");
        var gapPoint = (await store.ListDecisionPointsAsync(first.MatchId, true)).Single(entry => entry.StateVersion == 3);
        var gapHistory = (await store.GetHistoricalDecisionAsync(gapPoint.Id, 1))!;
        Check(gapHistory.Context.RecentEvents.Single().Kind == "QualityChanged" && !gapHistory.HasUnattributedEvents &&
            gapHistory.Context.Snapshot.Quality.Warnings.Contains("same-sequence-gap"), "The gap decision lost its attributable later event.");
        recorder.Observe(next, 3); recorder.Observe(other, 4); await recorder.CompleteAsync();
        Check((await store.GetEventsAtAsync(first.MatchId, 41)).Where(entry => entry.EventSequence == 41).Min(entry => entry.EventIndex) == 0 &&
            (await store.GetEventsAtAsync(other.MatchId, 40)).Min(entry => entry.EventIndex) == 0,
            "A new event sequence or match inherited the previous index offset.");
        var beforeReplay = await Rows(path);
        var replay = new SessionRecorder(store, new(null, "255790", 1));
        foreach (var (frame, line) in new[] { (first, 1L), (gap, 2L), (next, 3L), (other, 4L) })
        {
            replay.Observe(frame, line); await replay.FlushAsync();
        }
        await replay.CompleteAsync();
        Check(await Rows(path) == beforeReplay && JsonSerializer.Serialize(await store.GetHistoricalDecisionAsync(point.Id, 1)) == exactBefore,
            "Replaying identical same-sequence observations changed immutable event rows, snapshots or recorded times.");
    }

    private static GameSnapshot Frame(string match, long sequence, long version) => new(match, version, sequence, 1, 1, 1, GamePhase.Main,
        new[]
        {
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            new EntitySnapshot(40, null, EntityKind.Card, new Dictionary<string,string> { ["ZONE"] = "HAND", ["CONTROLLER"] = "2" }.ToFrozenDictionary())
        }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };

    private static async Task<string> Rows(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); var result = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT session_id||':'||event_seq||':'||event_index||':'||source_line||':'||observed_state_version||':'||kind||':'||data_json FROM session_events ORDER BY session_id,event_seq,event_index",
            "SELECT id||':'||session_id||':'||event_seq||':'||state_version||':'||recorded_at||':'||hex(snapshot_json) FROM session_snapshots ORDER BY id"
        })
        {
            command.CommandText = sql; await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }
        return string.Join("\n", result);
    }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
}
