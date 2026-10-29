using HsHelper.Storage;
using Microsoft.Data.Sqlite;

static class CoachKnowledgeChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "knowledge");
        yield return ("user plan versions retain immutable as-of content across reopening", () => Versions(Path.Combine(root, "versions.db")));
        yield return ("knowledge applicability filters never resurrect superseded scope", () => Scopes(Path.Combine(root, "scopes.db")));
        yield return ("knowledge deletion is a historical tombstone", () => Tombstones(Path.Combine(root, "tombstones.db")));
        yield return ("knowledge revisions reject stale edits and binding changes", () => Concurrency(Path.Combine(root, "concurrency.db")));
        yield return ("knowledge timestamps remain ordered through clock rollback", () => ClockRollback(Path.Combine(root, "clock.db")));
        yield return ("key-turn note anchors remain fixed across revisions and tombstones", () => Anchors(Path.Combine(root, "anchors.db")));
        yield return ("knowledge validation cancellation and future schemas preserve history", () => Invalid(Path.Combine(root, "invalid.db")));
    }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static CoachKnowledgeDraft Plan(string content = "对快攻优先争夺场面")
        => new("deck-a", CoachKnowledgeKind.DeckPlan, "猎人基本计划", content);

    private static async Task Versions(string path)
    {
        var clock = new StorageManualClock(); var store = new CoachKnowledgeStore(path, clock); await store.InitializeAsync();
        var initial = await store.SaveAsync(Plan());
        var revised = await store.SaveAsync(Plan("面对慢速对手保留后续资源"), initial.EntryId, initial.Version);
        Check(initial.Version == 1 && revised.Version == 2 && revised.RecordedAt > initial.RecordedAt && initial.Source == CoachKnowledgeSource.UserPlan, "Revision/source metadata missing.");
        Check((await store.GetAsOfAsync(initial.EntryId, initial.RecordedAt)) == initial, "Later plan text leaked into old as-of query.");
        Check(await store.GetAsOfAsync(initial.EntryId, initial.RecordedAt.AddTicks(-1)) is null, "Future-created plan appeared before creation.");
        clock.Now = clock.Now.AddHours(1);
        var third = await store.SaveAsync(Plan("再次修订的计划"), initial.EntryId, revised.Version);
        var reopened = new CoachKnowledgeStore(path, clock); await reopened.InitializeAsync();
        Check((await reopened.ListAsOfAsync(initial.RecordedAt)).Single() == initial && (await reopened.ListAsync()).Single() == third, "Reopening changed historical knowledge.");
        var duplicate = await reopened.SaveAsync(Plan("再次修订的计划"), initial.EntryId, third.Version);
        Check(duplicate == third, "Identical save created a spurious revision.");
        await Throws<NotSupportedException>(() => { ((IList<CoachKnowledgeVersion>)reopened.ListAsync().GetAwaiter().GetResult()).Clear(); return Task.CompletedTask; });
    }
    private static async Task Scopes(string path)
    {
        var store = new CoachKnowledgeStore(path, new StorageManualClock()); await store.InitializeAsync();
        var general = await store.SaveAsync(Plan());
        var mage = await store.SaveAsync(Plan("法师先手计划") with { OpponentClass = "mage", WentFirst = true });
        await store.SaveAsync(Plan("法师后手计划") with { OpponentClass = "MAGE", WentFirst = false });
        await store.SaveAsync(Plan("战士计划") with { OpponentClass = "WARRIOR", WentFirst = true });
        await store.SaveAsync(Plan("其他套牌") with { DeckVersionId = "deck-b" });
        var note = await store.SaveAsync(new("deck-a", CoachKnowledgeKind.MatchNote, "对局笔记", "这局过早交出资源", "MAGE", true, "match-a"));
        Check(note.Source == CoachKnowledgeSource.UserNote, "User note source missing.");
        var filter = new CoachKnowledgeFilter("deck-a", CoachKnowledgeKind.DeckPlan, "MAGE", true);
        Check((await store.ListAsync(filter)).Select(v => v.EntryId).ToHashSet().SetEquals(new[] { general.EntryId, mage.EntryId }), "Applicable general/specific plans were filtered incorrectly.");
        Check((await store.ListAsync(filter with { IncludeGeneral = false })).Single().EntryId == mage.EntryId, "Exact-scope query included generic plans.");
        Check((await store.ListAsync(new(MatchId: "match-a"))).Single() == note && (await store.ListAsync(new(MatchId: "other-match"))).Count == 0, "Notes crossed match binding.");
        await store.SaveAsync(Plan("改为战士的范围") with { OpponentClass = "WARRIOR", WentFirst = true }, mage.EntryId, mage.Version);
        Check((await store.ListAsync(filter)).Single().EntryId == general.EntryId, "Filtering resurrected an old matching revision.");
        Check((await store.ListAsOfAsync(mage.RecordedAt, filter)).Any(v => v.EntryId == mage.EntryId), "Scope revision erased earlier applicability.");
    }
    private static async Task Tombstones(string path)
    {
        var store = new CoachKnowledgeStore(path, new StorageManualClock()); await store.InitializeAsync();
        var initial = await store.SaveAsync(Plan()); var deleted = await store.DeleteAsync(initial.EntryId, initial.Version);
        Check(deleted is { IsDeleted: true, Version: 2 } && (await store.ListAsync()).Count == 0, "Delete did not create a tombstone.");
        Check((await store.GetAsOfAsync(initial.EntryId, initial.RecordedAt)) == initial && await store.GetAsOfAsync(initial.EntryId, deleted.RecordedAt) is null, "Tombstone leaked backward or was ignored.");
        Check(await store.GetAsOfAsync(initial.EntryId, deleted.RecordedAt, includeDeleted: true) == deleted &&
            (await store.ListAsync(new(IncludeDeleted: true))).Single() == deleted, "Deleted version was not inspectable.");
        Check(await store.DeleteAsync(initial.EntryId, deleted.Version) == deleted, "Repeated deletion was not idempotent.");
        var restored = await store.SaveAsync(Plan("用户恢复此计划"), initial.EntryId, deleted.Version);
        Check(restored is { Version: 3, IsDeleted: false } && await store.GetAsOfAsync(initial.EntryId, deleted.RecordedAt) is null, "Restoring a plan rewrote deleted history.");
    }
    private static async Task Concurrency(string path)
    {
        var store = new CoachKnowledgeStore(path); await store.InitializeAsync(); var initial = await store.SaveAsync(Plan());
        var saves = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { return await new CoachKnowledgeStore(path).SaveAsync(Plan("竞争修订" + i), initial.EntryId, 1); }
            catch (InvalidOperationException) { return null; }
        })));
        var latest = saves.Single(v => v is not null)!;
        Check(latest.Version == 2 && saves.Count(v => v is not null) == 1, "Concurrent edits silently overwrote a revision.");
        foreach (var change in new[] { Plan() with { DeckVersionId = "deck-b" }, Plan() with { Kind = CoachKnowledgeKind.MatchNote }, Plan() with { MatchId = "another-match" } })
            await Throws<InvalidOperationException>(() => store.SaveAsync(change, initial.EntryId, latest.Version));
        await Throws<InvalidOperationException>(() => store.SaveAsync(Plan("旧编辑器保存"), initial.EntryId, initial.Version));
        Check((await store.ListAsync()).Single() == latest, "Rejected edit changed latest knowledge.");
    }
    private static async Task ClockRollback(string path)
    {
        var clock = new StorageManualClock(); var store = new CoachKnowledgeStore(path, clock); await store.InitializeAsync();
        var first = await store.SaveAsync(Plan()); clock.Now = clock.Now.AddYears(-1);
        var futureEntry = await store.SaveAsync(Plan("系统时钟回退后的新计划"));
        var futureRevision = await store.SaveAsync(Plan("系统时钟回退后的修订"), first.EntryId, first.Version);
        Check(futureEntry.RecordedAt > first.RecordedAt && futureRevision.RecordedAt > futureEntry.RecordedAt &&
            (await store.ListAsOfAsync(first.RecordedAt)).Single() == first, "Clock rollback disclosed future-created or revised knowledge.");
    }
    private static async Task Invalid(string path)
    {
        var store = new CoachKnowledgeStore(path); await store.InitializeAsync();
        foreach (var invalid in new[] { Plan() with { OpponentClass = "nickname" }, Plan() with { Kind = (CoachKnowledgeKind)99 }, Plan() with { Title = "" }, Plan() with { DeckVersionId = "https://private.example" } })
            await Throws<ArgumentException>(() => store.SaveAsync(invalid));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => store.SaveAsync(Plan(), cancellationToken: cancellation.Token));
        Check((await store.ListAsync()).Count == 0, "Invalid/cancelled knowledge reached storage.");
        var future = path + ".future";
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = future, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE coach_knowledge_schema(version INTEGER NOT NULL);INSERT INTO coach_knowledge_schema VALUES(99)";
        await command.ExecuteNonQueryAsync();
        await Throws<InvalidDataException>(() => new CoachKnowledgeStore(future).InitializeAsync());
    }
    private static async Task Anchors(string path)
    {
        var store = new CoachKnowledgeStore(path, new StorageManualClock()); await store.InitializeAsync();
        var draft = new CoachKnowledgeDraft("deck-a", CoachKnowledgeKind.MatchNote, "关键回合", "在此回合过早投入后续资源", MatchId: "match-a") { EventSequence = 20 };
        var initial = await store.SaveAsync(draft);
        var revised = await store.SaveAsync(draft with { Content = "复盘补充：可以先保留资源" }, initial.EntryId, initial.Version);
        Check(initial.EventSequence == 20 && revised.EventSequence == 20 && (await store.GetAsOfAsync(initial.EntryId, initial.RecordedAt)) == initial, "Note revision moved the original key-turn anchor.");
        await Throws<InvalidOperationException>(() => store.SaveAsync(draft with { EventSequence = 21 }, initial.EntryId, revised.Version));
        await Throws<InvalidOperationException>(() => store.SaveAsync(draft with { MatchId = "match-b" }, initial.EntryId, revised.Version));
        await Throws<ArgumentException>(() => store.SaveAsync(draft with { MatchId = null }));
        await Throws<ArgumentException>(() => store.SaveAsync(draft with { EventSequence = -1 }));
        var deleted = await store.DeleteAsync(initial.EntryId, revised.Version);
        Check(deleted.EventSequence == 20 && (await store.GetAsOfAsync(initial.EntryId, revised.RecordedAt))!.EventSequence == 20, "Deletion erased historical note anchor.");
    }
}

sealed class StorageManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
