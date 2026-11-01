using System.Collections.Frozen;
using System.Text.Json.Nodes;
using HsHelper.Core;
using HsHelper.Storage;

internal static class DirectHandRevealStorageChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        yield return ("closed history-display evidence survives exact durable snapshots without filling earlier decisions", () => Durable(database));
        yield return ("direct hand evidence roundtrips as readonly provenance without inventing a return or current source", Codec);
        yield return ("missing direct history evidence remains unknown rather than being repaired from old hand rows", MissingEvidence);
    }

    private static async Task Durable(string database)
    {
        var frame = Frame(); var recorder = new SessionRecorder(new SessionStore(database), new(null, "255790", 1));
        var before = frame with { StateVersion = 9, EventSequence = 9, PublicHandMemory = null };
        var beforeJson = SnapshotCodec.Serialize(before);
        recorder.Observe(before, 9); recorder.Observe(frame, 20);
        recorder.Observe(frame with { StateVersion = 21, PublicHandMemory = null }, 21);
        await recorder.CompleteAsync();
        var reopened = new SessionStore(database); await reopened.InitializeAsync();
        var points = await reopened.ListDecisionPointsAsync(frame.MatchId, includeUnstable: true);
        var old = await reopened.GetDecisionSnapshotAsync(points.Single(point => point.StateVersion == 9).Id);
        var shown = await reopened.GetDecisionSnapshotAsync(points.Single(point => point.StateVersion == 20).Id);
        var later = await reopened.GetDecisionSnapshotAsync(points.Single(point => point.StateVersion == 21).Id);
        Check(SnapshotCodec.Serialize(old!) == beforeJson && old!.PublicHandMemory is null && later!.PublicHandMemory is null,
            "Confirmation or later withdrawal backfilled or replaced an exact earlier state.");
        var entry = PublicHandMemoryVisibility.Confirmed(shown!)!.Entries.Single();
        Check(entry.FromZone == "HAND" && entry.EnteredHandAtSequence is null && entry.PublicAtSequence == 10 &&
            entry.DirectReveal is { ShowAtSequence: 7, HistoryTargetAtSequence: 8, ConfirmedAtSequence: 10, SourcePublicAtSequence: 6 } &&
            shown!.Entities[40].CardId is null && shown.Entities[40].Cost is null,
            "Durable history lost closed evidence or converted it into an actual hand arrival/current attribute.");
    }

    private static Task Codec()
    {
        var frame = Frame(); var json = SnapshotCodec.Serialize(frame); var copy = SnapshotCodec.Deserialize(json);
        var entry = copy.PublicHandMemory!.Entries.Single();
        Check(SnapshotCodec.Serialize(copy) == json && entry.DirectReveal == frame.PublicHandMemory!.Entries.Single().DirectReveal &&
            PublicHandMemoryVisibility.Confirmed(copy) is not null && !copy.Entities.ContainsKey(50),
            "Source evidence depended on a current source entity, omitted get-only data or changed canonical bytes.");
        try { ((IList<PublicHandMemoryEntry>)copy.PublicHandMemory.Entries).Clear(); }
        catch (NotSupportedException)
        {
            Check(PublicHandMemoryVisibility.KnownCardIds(copy).SequenceEqual(["DIRECT_PUBLIC", "PUBLIC_SOURCE"]),
                "Historical public source and target definitions were lost or hidden entities were inserted.");
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Restored direct evidence remained mutable.");
    }

    private static Task MissingEvidence()
    {
        var json = SnapshotCodec.Serialize(Frame());
        foreach (var path in new[] { "DirectReveal", "SourcePublicAtSequence", "ConfirmedAtSequence", "ClientBuild" })
        {
            var node = JsonNode.Parse(json)!; var entry = node["PublicHandMemory"]!["Entries"]![0]!.AsObject();
            if (path == "DirectReveal") entry.Remove(path); else entry["DirectReveal"]!.AsObject().Remove(path);
            var restored = SnapshotCodec.Deserialize(node.ToJsonString()); var before = SnapshotCodec.Serialize(restored);
            Check(PublicHandMemoryVisibility.Confirmed(restored) is null && restored.PublicHandMemory!.Entries.Single().EnteredHandAtSequence is null,
                "A missing closed-proof field fabricated public eligibility or a hand return: " + path);
            Check(PublicHandMemoryVisibility.KnownCardIds(restored).Count == 0 && SnapshotCodec.Serialize(restored) == before,
                "Deriving an invalid proof exposed card definitions or mutated the original stored value.");
        }
        return Task.CompletedTask;
    }

    private static GameSnapshot Frame()
    {
        var rows = new[]
        {
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            new EntitySnapshot(40, null, EntityKind.Card, new Dictionary<string,string> { ["ZONE"] = "HAND", ["CONTROLLER"] = "2" }.ToFrozenDictionary())
        };
        var frame = new GameSnapshot("direct-hand-reveal-storage", 20, 20, 3, 1, 1, GamePhase.Main,
            rows.ToFrozenDictionary(row => row.Id), new(true, true, true, true, 0, []))
            { ClientBuild = "255790", PerspectiveSource = "manual" };
        var proof = new PublicHandDirectRevealEvidence("255790", 7, 8, 5, 10, 50, "PUBLIC_SOURCE", 1, "SPELL", "GRAVEYARD", "POWER", 6);
        return frame with { PublicHandMemory = new(frame.MatchId, 20, 20, 1, frame.PerspectiveSource,
            [new(40, "DIRECT_PUBLIC", 2, "HAND", 10, null) { DirectReveal = proof }], true, [], true, PublicHandMemoryVisibility.CurrentContinuityPolicy) };
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
