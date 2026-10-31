using System.Collections.Frozen;
using System.Text.Json.Nodes;
using HsHelper.Core;
using HsHelper.Storage;

internal static class TurnHistoryStorageChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        yield return ("turn evidence survives durable as-of snapshots without filling earlier history", () => Durable(database));
        yield return ("turn evidence codec freezes nested traits and preserves legacy unknown history", Codec);
        yield return ("play-time traits cannot bypass the stored identity field guard", () => Identity(database));
    }
    private static async Task Durable(string database)
    {
        var store = new SessionStore(database); var recorder = new SessionRecorder(store, new(null, "255790", 1));
        var frame = Frame();
        recorder.Observe(frame with { StateVersion = 9, EventSequence = 9, TurnHistory = null }, 9);
        recorder.Observe(frame, 20); await recorder.CompleteAsync();
        Check((await store.GetSnapshotAtAsync(frame.MatchId, 9))!.TurnHistory is null, "A later play filled an earlier decision.");
        var saved = (await store.GetSnapshotAtAsync(frame.MatchId, 20))!.TurnHistory!;
        Check(saved.StateVersion == 20 && saved.EventSequence == 20 && saved.Turns.Single().IsComplete &&
            saved.Turns.Single().Plays.Single().AtPlay.GetIntTag("CARDRACE") == 20, "Stored turn identity/type evidence was lost.");
    }
    private static Task Codec()
    {
        var json = SnapshotCodec.Serialize(Frame()); var saved = SnapshotCodec.Deserialize(json);
        var turn = saved.TurnHistory!.Turns.Single();
        Check(turn.Plays.Single().AtPlay.Tags is FrozenDictionary<string, string> && turn.PlayerId == 1, "Frozen play tags were not restored.");
        Reject(() => ((IList<TurnHistoryEntry>)saved.TurnHistory.Turns).Clear());
        Reject(() => ((IList<TurnPlaySnapshot>)turn.Plays).Clear());
        Reject(() => ((IList<string>)saved.TurnHistory.Warnings).Add("later"));
        var old = JsonNode.Parse(json)!; old.AsObject().Remove("TurnHistory");
        Check(SnapshotCodec.Deserialize(old.ToJsonString()).TurnHistory is null, "An older snapshot fabricated historical turns.");
        var malformed = JsonNode.Parse(json)!; malformed["TurnHistory"]!["Turns"]![0]!["Plays"]![0]!["AtPlay"]!["Tags"] = null;
        try { SnapshotCodec.Deserialize(malformed.ToJsonString()); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        throw new InvalidOperationException("Malformed play-time traits entered a snapshot.");
    }
    private static Task Identity(string database)
    {
        var recorder = new SessionRecorder(new SessionStore(database), new(null, "255790", 1));
        try { recorder.Observe(Frame(true), 20); }
        catch (InvalidDataException) { Check(recorder.PendingCount == 0, "Rejected traits entered the queue."); return Task.CompletedTask; }
        throw new InvalidOperationException("Nested account tags were accepted in play history.");
    }
    private static GameSnapshot Frame(bool identity = false)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "HAND", ["CARDTYPE"] = "MINION", ["CARDRACE"] = "20" };
        if (identity) tags["ACCOUNT_ID"] = "PRIVATE_PROBE";
        var entry = new TurnHistoryEntry(1, 1, 1, 10, true, true,
            [new(2, new(40, "EDR_227", EntityKind.Card, tags.ToFrozenDictionary()))]);
        return new("turn-history-storage", 20, 20, 2, 2, 1, GamePhase.Main,
            FrozenDictionary<int, EntitySnapshot>.Empty, new(true, true, true, true, 0, []))
        { ClientBuild = "255790", TurnHistory = new("turn-history-storage", 20, 20, [entry], true, 0, []) };
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Restored turn evidence remained mutable.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
