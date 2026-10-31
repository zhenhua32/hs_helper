using System.Globalization;
using System.Text.Json;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class KnowledgePagingChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "knowledge-paging");
        yield return ("knowledge pages reach an old plan behind two hundred newer notes", () => Across201(Path.Combine(root, "all.db")));
        yield return ("knowledge title search preserves literal percent underscore Chinese quotes and spaces", () => LiteralTitles(Path.Combine(root, "titles.db")));
        yield return ("knowledge title search filters the latest as-of revision without reviving an old match", () => RevisedTitles(Path.Combine(root, "title-revision.db")));
        yield return ("knowledge page scope is applied after latest-version selection", () => RevisedScopes(Path.Combine(root, "scope-revision.db")));
        yield return ("knowledge page tombstones remain historical and do not revive older visible versions", () => Tombstones(Path.Combine(root, "deleted.db")));
        yield return ("knowledge event cutoff removes future current-match anchors before page limits", () => EventBeforeLimit(Path.Combine(root, "event-cutoff.db")));
        yield return ("knowledge unknown event cutoff excludes only same-match anchored entries", () => UnknownEvent(Path.Combine(root, "unknown-event.db")));
        yield return ("knowledge applicability rank uses only known exact class and first-player criteria", () => Applicability(Path.Combine(root, "rank.db")));
        yield return ("knowledge timestamp ties use stable entry ID order across pages and reopen", () => StableTies(Path.Combine(root, "ties.db")));
        yield return ("knowledge browsing at a fixed time is immutable and writes no knowledge revisions", () => ReadOnlyCutoff(Path.Combine(root, "read-only.db")));
        yield return ("knowledge page bounds replace legacy limit and keep maximum offsets safe", () => BoundsAndLegacy(Path.Combine(root, "bounds.db")));
        yield return ("knowledge invalid filters and cancelled pages leave stored history unchanged", () => InvalidAndCancelled(Path.Combine(root, "invalid.db")));
    }

    private static readonly DateTimeOffset Stamp = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private static async Task Across201(string path)
    {
        var (store, clock) = await Open(path);
        var oldest = await Save(store, clock, Plan("最早的套牌计划"));
        var saved = new List<CoachKnowledgeVersion> { oldest };
        for (var index = 0; index < 200; index++) saved.Add(await Save(store, clock, Note("较新笔记" + index, "other-match", index)));
        var actual = new List<string>();
        for (var number = 1; number <= 3; number++)
        {
            var page = await store.ListPageAsOfAsync(clock.Now, pageNumber: number);
            Check(page.PageNumber == number && page.PageSize == 100 && page.HasPrevious == (number > 1) &&
                page.HasMore == (number < 3) && page.Entries.Count == (number == 3 ? 1 : 100), "Page metadata or sentinel row was wrong.");
            actual.AddRange(page.Entries.Select(entry => entry.EntryId));
        }
        Check(actual.SequenceEqual(saved.AsEnumerable().Reverse().Select(entry => entry.EntryId)) && actual.Distinct().Count() == 201 && actual[^1] == oldest.EntryId,
            "An older plan was skipped or rows repeated after the first two hundred entries.");
        var plan = await store.ListPageAsOfAsync(clock.Now, new(Kind: CoachKnowledgeKind.DeckPlan), pageSize: 1);
        Check(plan.Entries.Single() == oldest && !plan.HasMore, "Newer notes occupied the plan query's page before Kind filtering.");
        var empty = await store.ListPageAsOfAsync(clock.Now, pageNumber: 4);
        Check(empty.Entries.Count == 0 && !empty.HasMore && empty.HasPrevious, "A past-end page wrapped to actual entries.");
    }

    private static async Task LiteralTitles(string path)
    {
        var (store, clock) = await Open(path);
        var percent = await Save(store, clock, Plan("甲%乙_丙"));
        var underscore = await Save(store, clock, Plan("甲_乙%丙"));
        await Save(store, clock, Plan("甲X乙Y丙"));
        var chinese = await Save(store, clock, Plan("中文·先手·法师"));
        var spaces = await Save(store, clock, Plan("开局  保留"));
        var quote = await Save(store, clock, Plan("安全' OR 1=1 -- 尾"));
        async Task<IReadOnlyList<CoachKnowledgeVersion>> Search(string value) => (await store.ListPageAsOfAsync(clock.Now,
            new() { TitleContains = value })).Entries;
        Check((await Search("%")).Select(entry => entry.EntryId).ToHashSet().SetEquals([percent.EntryId, underscore.EntryId]), "Percent became a wildcard.");
        Check((await Search("_")).Select(entry => entry.EntryId).ToHashSet().SetEquals([percent.EntryId, underscore.EntryId]), "Underscore became a wildcard.");
        Check((await Search("%乙_")).Single() == percent && (await Search("先手")).Single() == chinese &&
            (await Search("  保")).Single() == spaces && (await Search("' OR 1=1 --")).Single() == quote, "Literal title text was reinterpreted or lost.");
        Check((await Search(" 开局 ")).Count == 0 && (await Search("中文·先手·法师 ")).Count == 0,
            "Search silently trimmed the caller's original title substring.");
        var upper = await Save(store, clock, Plan("Mage策略"));
        Check((await Search("Mage")).Single() == upper && (await Search("mage")).Count == 0, "Title matching accidentally promised case folding.");
    }

    private static async Task RevisedTitles(string path)
    {
        var (store, clock) = await Open(path);
        var first = await Save(store, clock, Plan("旧标题%", "原计划"));
        clock.Advance(); var revised = await store.SaveAsync(Plan("新标题_", "修订后的计划"), first.EntryId, first.Version);
        var oldFilter = new CoachKnowledgeFilter { TitleContains = "旧标题%" };
        Check((await store.ListPageAsOfAsync(first.RecordedAt, oldFilter)).Entries.Single() == first,
            "A later title revision leaked into historical search.");
        Check((await store.ListPageAsOfAsync(revised.RecordedAt, oldFilter)).Entries.Count == 0 &&
            (await store.ListPageAsOfAsync(revised.RecordedAt, new() { TitleContains = "新标题_" })).Entries.Single() == revised,
            "The latest title failed to match or an earlier matching revision was revived.");
        Check((await store.ListPageAsOfAsync(first.RecordedAt.AddTicks(-1))).Entries.Count == 0, "A future-created entry appeared before its first version.");
    }

    private static async Task RevisedScopes(string path)
    {
        var (store, clock) = await Open(path);
        var first = await Save(store, clock, Plan("范围计划") with { OpponentClass = "MAGE", WentFirst = true });
        var general = await Save(store, clock, Plan("通用计划"));
        clock.Advance(); var revised = await store.SaveAsync(Plan("范围计划") with { OpponentClass = "WARRIOR", WentFirst = false }, first.EntryId, first.Version);
        var exact = new CoachKnowledgeFilter("deck-paging", CoachKnowledgeKind.DeckPlan, "MAGE", true, IncludeGeneral: false);
        Check((await store.ListPageAsOfAsync(first.RecordedAt, exact)).Entries.Single() == first &&
            (await store.ListPageAsOfAsync(revised.RecordedAt, exact)).Entries.Count == 0, "Scope search resurrected a superseded matching version.");
        Check((await store.ListPageAsOfAsync(clock.Now, exact with { IncludeGeneral = true })).Entries.Single() == general,
            "A newer mismatched scope crowded out or revived the applicable general entry.");
    }

    private static async Task Tombstones(string path)
    {
        var (store, clock) = await Open(path);
        var first = await Save(store, clock, Plan("删除前计划"));
        clock.Advance(); var deleted = await store.DeleteAsync(first.EntryId, first.Version);
        Check((await store.ListPageAsOfAsync(first.RecordedAt)).Entries.Single() == first &&
            (await store.ListPageAsOfAsync(deleted.RecordedAt)).Entries.Count == 0, "Tombstones leaked backward or revived a preceding live version.");
        var audit = await store.ListPageAsOfAsync(deleted.RecordedAt, new(IncludeDeleted: true) { TitleContains = "删除前" });
        Check(audit.Entries.Single() == deleted && !audit.HasMore, "IncludeDeleted did not expose precisely the latest historical tombstone.");
        clock.Advance(); var restored = await store.SaveAsync(Plan("恢复后的计划"), first.EntryId, deleted.Version);
        Check((await store.ListPageAsOfAsync(clock.Now)).Entries.Single() == restored &&
            (await store.ListPageAsOfAsync(deleted.RecordedAt, new(IncludeDeleted: true))).Entries.Single() == deleted,
            "Restore overwrote deletion history or retained multiple versions as page entries.");
    }

    private static async Task EventBeforeLimit(string path)
    {
        var (store, clock) = await Open(path);
        var eligible = new List<CoachKnowledgeVersion>
        {
            await Save(store, clock, Plan("较早通用计划")),
            await Save(store, clock, Note("事件100", "current-match", 100)),
            await Save(store, clock, Note("无事件锚点", "current-match", null)),
            await Save(store, clock, Note("其它对局事件999", "other-match", 999))
        };
        for (var index = 0; index < 8; index++) await Save(store, clock, Note("未来事件500-" + index, "current-match", 500));
        await Save(store, clock, Note("紧接截止之后", "current-match", 101));
        var filter = new CoachKnowledgeFilter("deck-paging") { EventCutoff = new("current-match", 100) };
        var first = await store.ListPageAsOfAsync(clock.Now, filter, pageSize: 3);
        var second = await store.ListPageAsOfAsync(clock.Now, filter, pageNumber: 2, pageSize: 3);
        Check(first.Entries.Count == 3 && first.HasMore && second.Entries.Count == 1 && !second.HasMore &&
            first.Entries.Concat(second.Entries).SequenceEqual(eligible.AsEnumerable().Reverse()),
            "Future anchored notes occupied page slots or another match's historical anchor was removed.");
        var max = await store.ListPageAsOfAsync(clock.Now, filter with { EventCutoff = new("current-match", long.MaxValue) });
        Check(max.Entries.Count == 13, "A valid maximum event cutoff overflowed or lost rows.");
    }

    private static async Task UnknownEvent(string path)
    {
        var (store, clock) = await Open(path);
        var global = await Save(store, clock, Plan("无对局计划"));
        var unanchored = await Save(store, clock, Note("本局无锚点", "current-match", null));
        await Save(store, clock, Note("本局事件零", "current-match", 0));
        await Save(store, clock, Note("本局事件500", "current-match", 500));
        var other = await Save(store, clock, Note("其它局事件500", "other-match", 500));
        var filter = new CoachKnowledgeFilter { EventCutoff = new("current-match", null) };
        var page = await store.ListPageAsOfAsync(clock.Now, filter);
        Check(page.Entries.SequenceEqual(new[] { other, unanchored, global }) && !page.HasMore,
            "Unknown event was treated as no cutoff or removed unrelated/unanchored entries.");
        Check((await store.ListPageAsOfAsync(clock.Now, filter with { MatchId = "current-match" })).Entries.Single() == unanchored,
            "Event cutoff overrode an explicit match filter.");
    }

    private static async Task Applicability(string path)
    {
        var (store, clock) = await Open(path);
        var general = await Save(store, clock, Plan("较早通用"));
        var exact = await Save(store, clock, Plan("较早精确") with { OpponentClass = "MAGE", WentFirst = true });
        var classOnly = await Save(store, clock, Plan("职业匹配") with { OpponentClass = "MAGE" });
        var firstOnly = await Save(store, clock, Plan("先后手匹配") with { WentFirst = true });
        var newestGeneral = await Save(store, clock, Plan("最新通用"));
        await Save(store, clock, Plan("错误职业") with { OpponentClass = "WARRIOR", WentFirst = true });
        await Save(store, clock, Plan("错误先后手") with { OpponentClass = "MAGE", WentFirst = false });
        var filter = new CoachKnowledgeFilter("deck-paging", OpponentClass: "mage", WentFirst: true) { RankByApplicability = true };
        var ranked = await store.ListPageAsOfAsync(clock.Now, filter, pageSize: 2);
        var next = await store.ListPageAsOfAsync(clock.Now, filter, pageNumber: 2, pageSize: 2);
        Check(ranked.Entries.SequenceEqual(new[] { exact, firstOnly }) && next.Entries.SequenceEqual(new[] { classOnly, newestGeneral }) && next.HasMore,
            "Ranking occurred after LIMIT or treated one exact scope as two matches.");
        var plain = await store.ListPageAsOfAsync(clock.Now, filter with { RankByApplicability = false });
        Check(plain.Entries.SequenceEqual(new[] { newestGeneral, firstOnly, classOnly, exact, general }), "Unranked queries lost ordinary latest-first order.");
        var onlyKnownFirst = await store.ListPageAsOfAsync(clock.Now, new(WentFirst: true) { RankByApplicability = true });
        Check(onlyKnownFirst.Entries[0].Title == "错误职业" && onlyKnownFirst.Entries[1] == firstOnly,
            "An unknown class criterion still contributed to the applicability score.");
    }

    private static async Task StableTies(string path)
    {
        var (store, _) = await Open(path);
        var ids = new[] { "tie-z", "tie-A", "tie-0", "tie-a", "tie-Z" };
        foreach (var id in ids.Reverse()) await Insert(path, new(id, 1, "deck-paging", CoachKnowledgeKind.DeckPlan,
            CoachKnowledgeSource.UserPlan, "同一时刻", "合成分页样本", null, null, null, Stamp, false));
        var expected = ids.Order(StringComparer.Ordinal).ToArray(); var actual = new List<string>();
        for (var number = 1; number <= 3; number++)
        {
            var page = await store.ListPageAsOfAsync(Stamp, pageNumber: number, pageSize: 2);
            actual.AddRange(page.Entries.Select(entry => entry.EntryId));
            Check(page.HasMore == (number < 3), "Timestamp tie sentinel metadata was wrong.");
        }
        Check(actual.SequenceEqual(expected), "Timestamp ties depended on insertion or connection order.");
        var reopened = new CoachKnowledgeStore(path);
        Check((await reopened.ListPageAsOfAsync(Stamp, pageNumber: 2, pageSize: 2)).Entries.Select(entry => entry.EntryId).SequenceEqual(expected.Skip(2).Take(2)),
            "Reopening changed the stable timestamp tie order.");
    }

    private static async Task ReadOnlyCutoff(string path)
    {
        var (store, clock) = await Open(path);
        var old = await Save(store, clock, Plan("旧计划"));
        var newer = await Save(store, clock, Plan("较新计划")); var asOf = clock.Now;
        var before = await Fingerprint(path);
        var page = await store.ListPageAsOfAsync(asOf, pageSize: 1);
        Check(page.AsOf == asOf && page.Entries.Single() == newer, "A page changed its fixed query time.");
        await Throws<NotSupportedException>(() => { ((IList<CoachKnowledgeVersion>)page.Entries).Clear(); return Task.CompletedTask; });
        await store.ListPageAsOfAsync(asOf, pageNumber: 2, pageSize: 1); await store.ListPageAsync(pageSize: 200);
        Check(await Fingerprint(path) == before, "Browsing wrote versions, changed payloads or created a tombstone.");
        clock.Advance(); await store.SaveAsync(Plan("后续修订"), old.EntryId, old.Version);
        await Save(store, clock, Plan("之后新建的计划"));
        var second = await store.ListPageAsOfAsync(page.AsOf, pageNumber: 2, pageSize: 1);
        Check(second.Entries.Single() == old && !second.HasMore && page.Entries.Single() == newer,
            "New saves shifted an anchored page or mutated a previously returned page.");
    }

    private static async Task BoundsAndLegacy(string path)
    {
        var (store, clock) = await Open(path);
        for (var index = 0; index < 4; index++) await Save(store, clock, Plan("边界计划" + index));
        var filter = new CoachKnowledgeFilter(Limit: 1);
        Check((await store.ListAsOfAsync(clock.Now, filter)).Count == 1 &&
            (await store.ListPageAsOfAsync(clock.Now, filter, pageSize: 3)).Entries.Count == 3,
            "Paged size replaced the legacy API's limit or legacy limit truncated a page.");
        var max = await store.ListPageAsOfAsync(clock.Now, pageNumber: int.MaxValue, pageSize: 200);
        Check(max.Entries.Count == 0 && !max.HasMore && max.HasPrevious && max.PageNumber == int.MaxValue && max.PageSize == 200,
            "A large offset overflowed or wrapped into real entries.");
        Check((await store.ListPageAsync(pageSize: 200)).Entries.Count == 4, "The current-page overload changed the query domain.");
        Check((await store.ListPageAsOfAsync(clock.Now, new(Limit: 0), pageSize: 2)).Entries.Count == 2,
            "Paged queries validated or used the ignored legacy Limit.");
    }

    private static async Task InvalidAndCancelled(string path)
    {
        var (store, clock) = await Open(path); await Save(store, clock, Plan("已保存计划"));
        var before = await Fingerprint(path);
        foreach (var (number, size) in new[] { (0, 100), (-1, 100), (1, 0), (1, 201), (int.MinValue, 200) })
            await Throws<ArgumentOutOfRangeException>(() => store.ListPageAsOfAsync(clock.Now, pageNumber: number, pageSize: size));
        foreach (var filter in new CoachKnowledgeFilter[]
        {
            new CoachKnowledgeFilter { TitleContains = new string('题', 201) }, new() { TitleContains = "标题\n尾部" },
            new() { TitleContains = "标题\0尾部" }, new() { TitleContains = "标题\t尾部" }, new() { EventCutoff = new("current-match", -1) },
            new() { EventCutoff = new("https://private.invalid", 0) }, new() { EventCutoff = new("", null) },
            new CoachKnowledgeFilter(Kind: (CoachKnowledgeKind)99), new(OpponentClass: "nickname")
        }) await Throws<ArgumentException>(() => store.ListPageAsOfAsync(clock.Now, filter));
        Check((await store.ListPageAsOfAsync(clock.Now, new() { TitleContains = new string('题', 200) })).Entries.Count == 0,
            "The allowed 200-character search boundary was rejected.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.ListPageAsOfAsync(clock.Now, cancellationToken: cancelled.Token));
        await Throws<OperationCanceledException>(() => store.ListPageAsync(cancellationToken: cancelled.Token));
        Check(await Fingerprint(path) == before, "Rejected or cancelled queries changed stored knowledge.");
    }

    private static CoachKnowledgeDraft Plan(string title, string content = "合成知识分页样本") => new("deck-paging", CoachKnowledgeKind.DeckPlan, title, content);
    private static CoachKnowledgeDraft Note(string title, string match, long? sequence) => new("deck-paging", CoachKnowledgeKind.MatchNote,
        title, "合成对局笔记", MatchId: match) { EventSequence = sequence };
    private static async Task<(CoachKnowledgeStore Store, KnowledgePagingClock Clock)> Open(string path)
    {
        var clock = new KnowledgePagingClock(); var store = new CoachKnowledgeStore(path, clock); await store.InitializeAsync(); return (store, clock);
    }
    private static async Task<CoachKnowledgeVersion> Save(CoachKnowledgeStore store, KnowledgePagingClock clock, CoachKnowledgeDraft draft)
    { clock.Advance(); return await store.SaveAsync(draft); }
    private static async Task<string> Fingerprint(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM coach_knowledge_versions ORDER BY entry_id,version";
        await using var reader = await command.ExecuteReaderAsync(); var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0)); return string.Join("\n", rows);
    }
    private static async Task Insert(string path, CoachKnowledgeVersion entry)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO coach_knowledge_versions(entry_id,version,deck_version_id,kind,opponent_class,went_first,match_id,recorded_at,is_deleted,payload_json)
            VALUES($id,$version,$deck,$kind,NULL,NULL,NULL,$at,0,$json)
            """;
        command.Parameters.AddWithValue("$id", entry.EntryId); command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$deck", entry.DeckVersionId); command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$at", entry.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry)); await command.ExecuteNonQueryAsync();
    }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class KnowledgePagingClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = Stamp;
        public void Advance() => Now = Now.AddSeconds(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
