using System.Collections.Frozen;
using System.Text.Json.Nodes;
using HsHelper.Core;
using HsHelper.Storage;

internal static class ChoiceHistoryStorageChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        yield return ("completed choice evidence survives durable snapshots without changing earlier decisions", () => Durable(database));
        yield return ("choice history round trips freeze nested evidence and accept older snapshots", Codec);
        yield return ("choice history cannot bypass the stored identity field guard", () => Identity(database));
    }

    private static async Task Durable(string database)
    {
        var store = new SessionStore(database);
        var recorder = new SessionRecorder(store, new(null, "255790", 1, PlayerClass: "HUNTER"));
        var frame = Frame();
        recorder.Observe(frame with { StateVersion = 9, EventSequence = 9, ChoiceHistory = null }, 9);
        recorder.Observe(frame, 20);
        await recorder.CompleteAsync();
        var earlier = await store.GetSnapshotAtAsync(frame.MatchId, 9);
        var saved = await store.GetSnapshotAtAsync(frame.MatchId, 20);
        Check(earlier?.ChoiceHistory is null && saved?.ChoiceHistory is { IsComplete: true, EventSequence: 20 },
            "Later selections were lost or became visible in an earlier decision.");
        var entry = saved!.ChoiceHistory!.Entries.Single();
        Check(entry.SourceCardId == "TLC_EVENT_400" && entry.ChosenEntityIds.SequenceEqual([40]) &&
            entry.OptionEntities.Single().Attack == 3 && saved.Choice?.OfferedAtEventSequence == 19,
            "Stored choices lost their source identity, observed component attributes or stage boundary.");
    }

    private static Task Codec()
    {
        var frame = Frame();
        var json = SnapshotCodec.Serialize(frame);
        var saved = SnapshotCodec.Deserialize(json);
        Check(saved.ChoiceHistory?.MatchId == frame.MatchId && saved.ChoiceHistory.Entries.Single().Options.Single().CardId == "COMPONENT",
            "Read-only history properties were omitted by the storage codec.");
        var entry = saved.ChoiceHistory!.Entries.Single();
        RejectMutation(() => ((IList<ChoiceHistoryEntry>)saved.ChoiceHistory.Entries).Clear());
        RejectMutation(() => ((IList<ChoiceOptionSnapshot>)entry.Options).Clear());
        RejectMutation(() => ((IList<EntitySnapshot>)entry.OptionEntities).Clear());
        RejectMutation(() => ((IList<int>)entry.ChosenEntityIds).Clear());
        RejectMutation(() => ((IList<string>)saved.ChoiceHistory.Warnings).Add("changed"));
        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy.Remove("ChoiceHistory");
        legacy["Choice"]!.AsObject().Remove("OfferedAtEventSequence");
        var old = SnapshotCodec.Deserialize(legacy.ToJsonString());
        Check(old.ChoiceHistory is null && old.Choice?.OfferedAtEventSequence == 0,
            "Old snapshots fabricated history or failed to deserialize.");
        var malformed = JsonNode.Parse(json)!;
        malformed["ChoiceHistory"]!["Entries"]![0]!["OptionEntities"]![0]!["Tags"] = null;
        try { SnapshotCodec.Deserialize(malformed.ToJsonString()); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        throw new InvalidOperationException("Malformed nested historical entities entered the stored snapshot.");
    }

    private static Task Identity(string database)
    {
        var recorder = new SessionRecorder(new SessionStore(database), new(null, "255790", 1));
        var frame = Frame("NAME");
        try { recorder.Observe(frame, 20); }
        catch (InvalidDataException)
        {
            Check(recorder.PendingCount == 0, "Rejected private history still entered the recorder queue.");
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Private identity tags entered storage through nested choice evidence.");
    }

    private static GameSnapshot Frame(string? identityTag = null)
    {
        var tags = new Dictionary<string, string> { ["ZONE"] = "SETASIDE", ["CONTROLLER"] = "1", ["ATK"] = "3", ["HEALTH"] = "4", ["DAMAGE"] = "0", ["COST"] = "2" };
        if (identityTag is not null) tags[identityTag] = "PRIVATE_IDENTITY_PROBE";
        var component = new EntitySnapshot(40, "COMPONENT", EntityKind.Card, tags.ToFrozenDictionary());
        var entry = new ChoiceHistoryEntry(7, 1, "GENERAL", 30, "TLC_EVENT_400", 1, 1, 10, 15,
            [new(40, "COMPONENT", 1, 1)], [component], [40], true);
        return new("choice-history-storage", 20, 20, 3, 1, 1, GamePhase.Choice,
            FrozenDictionary<int, EntitySnapshot>.Empty, new(true, true, true, true, 0, []))
        {
            ClientBuild = "255790",
            Choice = new(8, 1, "GENERAL", 30, 1, 1, [new(41, "SECOND", 1, 1)], true) { OfferedAtEventSequence = 19 },
            ChoiceHistory = new("choice-history-storage", 20, 20, [entry], true, 0, [])
        };
    }

    private static void RejectMutation(Action mutate)
    {
        try { mutate(); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Deserialized choice evidence remained mutable.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
