using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class ArchivePagingChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "archive-paging");
        yield return ("archive pages reach all 205 records without duplicates or sentinel leaks", () => Across205(Path.Combine(root, "across.db")));
        yield return ("archive timestamp ties retain ordinal ID order across pages and reopen", () => StableTies(Path.Combine(root, "ties.db")));
        yield return ("archive binding filters exclude newer mismatches before page limits", () => BindingFilters(Path.Combine(root, "bindings.db")));
        yield return ("archive task filter is exact and distinguishes empty unknown and absent tasks", () => ExactTasks(Path.Combine(root, "tasks.db")));
        yield return ("archive text search uses only literal summary or question text", () => LiteralText(Path.Combine(root, "text.db")));
        yield return ("archive effective cutoff is the earlier inclusive UTC time boundary", () => EffectiveCutoff(Path.Combine(root, "cutoff.db")));
        yield return ("archive fixed cutoff pages do not drift after newer inserts", () => FrozenPages(Path.Combine(root, "frozen.db")));
        yield return ("archive pages deeply freeze records without retaining caller collection aliases", () => DeepReadOnly(Path.Combine(root, "immutable.db")));
        yield return ("archive browsing changes neither database bytes nor schema", () => ReadOnlyDatabase(Path.Combine(root, "read-only.db")));
        yield return ("archive page bounds ignore legacy limits and use safe long offsets", () => BoundsAndLegacy(Path.Combine(root, "bounds.db")));
        yield return ("archive invalid filters and cancellation preserve persisted records", () => InvalidAndCancelled(Path.Combine(root, "invalid.db")));
        yield return ("archive paging does not change conversation binding or chronological history", () => ConversationUnaffected(Path.Combine(root, "conversation.db")));
    }

    private static readonly DateTimeOffset Stamp = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private static async Task Across205(string path)
    {
        var (store, clock) = await Open(path);
        var records = new List<ArchivedAdviceRecord>();
        for (var i = 0; i < 205; i++) records.Add(await Save(store, clock, Draft("分页样本 " + i)));
        var ids = new List<string>();
        for (var number = 1; number <= 3; number++)
        {
            var page = await store.ListPageAsOfAsync(clock.Now, pageNumber: number);
            Check(page.PageNumber == number && page.PageSize == 100 && page.HasPrevious == (number > 1) &&
                page.HasMore == (number < 3) && page.Records.Count == (number == 3 ? 5 : 100), "Page metadata or bounded sentinel count was wrong.");
            ids.AddRange(page.Records.Select(record => record.Id));
        }
        Check(ids.SequenceEqual(records.AsEnumerable().Reverse().Select(record => record.Id)) && ids.Distinct().Count() == 205,
            "An older archived analysis was skipped, repeated, or reordered across pages.");
        var empty = await store.ListPageAsOfAsync(clock.Now, pageNumber: 4);
        Check(empty.Records.Count == 0 && !empty.HasMore && empty.HasPrevious, "A past-end page wrapped or invented records.");
        var exact = await store.ListPageAsOfAsync(clock.Now, pageNumber: 41, pageSize: 5);
        Check(exact.Records.Count == 5 && !exact.HasMore, "Exactly one full final page was falsely marked as truncated.");
    }

    private static async Task StableTies(string path)
    {
        var (store, clock) = await Open(path);
        string[] ids = ["tie-z", "tie-a", "tie-09", "tie-01", "tie-B", "tie-b", "tie-Y"];
        foreach (var id in ids) await Save(store, clock, Draft(id), id);
        await using (var connection = await Connect(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE coach_advice_records SET recorded_at=$at";
            command.Parameters.AddWithValue("$at", Stamp.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        var expected = ids.Order(StringComparer.Ordinal).ToArray(); var actual = new List<string>();
        for (var number = 1; number <= 4; number++)
        {
            var page = await store.ListPageAsOfAsync(Stamp, pageNumber: number, pageSize: 2);
            actual.AddRange(page.Records.Select(record => record.Id));
            Check(page.HasMore == (number < 4), "Timestamp-tie sentinel used the wrong ordering.");
        }
        Check(actual.SequenceEqual(expected), "Equal timestamps used insertion order or a different ID collation.");
        var reopened = new AdviceArchiveStore(path);
        Check((await reopened.ListPageAsOfAsync(Stamp, pageNumber: 2, pageSize: 2)).Records.Select(record => record.Id)
            .SequenceEqual(expected.Skip(2).Take(2)), "Reopening changed stable tie pagination.");
    }

    private static async Task BindingFilters(string path)
    {
        var (store, clock) = await Open(path);
        var allowed = await Save(store, clock, Draft("唯一完整匹配"));
        var mismatches = new AdviceRecordDraft[]
        {
            Draft("其他对局") with { MatchId = "other-match" }, Draft("其他局面") with { StateVersion = 9 },
            Draft("未来事件") with { EventSequence = 16 }, Draft("其他卡库") with { CardBuild = "255791" },
            Draft("其他套牌") with { DeckVersionId = "other-deck" }, Draft("未绑定套牌") with { DeckVersionId = null }
        };
        foreach (var draft in mismatches) await Save(store, clock, draft);
        var filter = new AdviceArchiveFilter("paging-match", 8, 15, "255790", "paging-deck");
        var page = await store.ListPageAsOfAsync(clock.Now, filter, pageSize: 1);
        Check(page.Records.Single().Id == allowed.Id && !page.HasMore, "Mismatched bindings occupied the page before filtering.");
        async Task<string[]> Ids(AdviceArchiveFilter scope) => (await store.ListPageAsOfAsync(clock.Now, scope)).Records.Select(record => record.Advice.Summary).ToArray();
        Check((await Ids(filter with { MatchId = null })).Contains("其他对局") &&
            (await Ids(filter with { StateVersion = null })).Contains("其他局面") &&
            (await Ids(filter with { ThroughEventSequence = null })).Contains("未来事件") &&
            (await Ids(filter with { CardBuild = null })).Contains("其他卡库") &&
            (await Ids(filter with { DeckVersionId = null })).Contains("未绑定套牌"), "An absent archive filter retained an unintended binding constraint.");
        Check((await store.ListAsync(filter)).Select(record => record.Id).SequenceEqual([allowed.Id]), "Legacy binding semantics diverged from the equivalent paged query.");
    }

    private static async Task ExactTasks(string path)
    {
        var (store, clock) = await Open(path);
        var empty = await Save(store, clock, Draft("旧版空任务") with { Task = "" });
        var review = await Save(store, clock, Draft("复盘任务") with { Task = "局面复盘" });
        var unknown = await Save(store, clock, Draft("未知但合法任务") with { Task = "future-task-v99" });
        await Save(store, clock, Draft("前缀相同任务") with { Task = "局面复盘扩展" });
        var missing = await Save(store, clock, Draft("旧JSON无任务"));
        var nullTask = await Save(store, clock, Draft("旧JSON空值任务"));
        await using (var connection = await Connect(path))
        foreach (var (record, remove) in new[] { (missing, true), (nullTask, false) })
        {
            var payload = JsonSerializer.SerializeToNode(record.Advice)!.AsObject();
            if (remove) payload.Remove("Task"); else payload["Task"] = null;
            var json = payload.ToJsonString();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE coach_advice_records SET payload_json=$json,content_hash=$hash WHERE id=$id";
            command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
            await command.ExecuteNonQueryAsync();
        }
        async Task<AdviceArchivePage> Query(string? task) => await store.ListPageAsOfAsync(clock.Now, new() { Task = task }, pageSize: 1);
        var emptyPage = await store.ListPageAsOfAsync(clock.Now, new() { Task = "" });
        Check(emptyPage.Records.Select(record => record.Id).ToHashSet().SetEquals([empty.Id, missing.Id, nullTask.Id]) && (await Query("")).HasMore,
            "Empty task became no filter or excluded a legacy absent/null/empty task.");
        Check((await Query("局面复盘")).Records.Single().Id == review.Id && (await Query("future-task-v99")).Records.Single().Id == unknown.Id,
            "Exact task filtering used prefixes or a fixed task enum.");
        Check((await Query(" 局面复盘")).Records.Count == 0 && (await Query("FUTURE-TASK-V99")).Records.Count == 0,
            "Task matching trimmed or folded the original query.");
        Check((await Query(null)).HasMore && (await store.ListPageAsOfAsync(clock.Now)).Records.Count == 6,
            "Absent task did not preserve all tasks.");
        Check((await store.ListAsync(new() { Task = "" })).Select(record => record.Id).SequenceEqual(emptyPage.Records.Select(record => record.Id)),
            "Legacy Task filtering did not preserve exact empty-task semantics.");
    }

    private static async Task LiteralText(string path)
    {
        var (store, clock) = await Open(path);
        var summary = await Save(store, clock, Draft("中文甲%乙_丙  留牌 Mage") with { UserQuestion = null });
        var question = await Save(store, clock, Draft("问题包含查询词") with { UserQuestion = "是否保留%_和' OR 1=1 --？" });
        await Save(store, clock, Draft("其他字段") with { Task = "OnlyOther", Reasons = ["OnlyOther"], Risks = ["OnlyOther"], Alternative = "OnlyOther" });
        await Save(store, clock, Draft("普通甲X乙Y丙") with { UserQuestion = "不匹配" });
        async Task<AdviceArchivePage> Query(string? text) => await store.ListPageAsOfAsync(clock.Now, new() { TextContains = text });
        Check((await Query("%")).Records.Select(record => record.Id).ToHashSet().SetEquals([summary.Id, question.Id]) &&
            (await Query("_")).Records.Count == 2, "Percent or underscore became SQL wildcards.");
        Check((await Query("甲%乙_")).Records.Single().Id == summary.Id && (await Query("中文")).Records.Single().Id == summary.Id &&
            (await Query("' OR 1=1 --")).Records.Single().Id == question.Id, "Literal text or a quote was reinterpreted instead of parameterized.");
        Check((await Query("  留")).Records.Single().Id == summary.Id && (await Query(" 中文")).Records.Count == 0 &&
            (await Query("Mage")).Records.Single().Id == summary.Id && (await Query("mage")).Records.Count == 0,
            "Search trimmed spaces or accidentally folded case.");
        Check((await Query("OnlyOther")).Records.Count == 0 && (await Query("")).Records.Count == 4 && (await Query(null)).Records.Count == 4,
            "Text search scanned non-searchable fields or an empty query removed records.");
        Check((await store.ListPageAsOfAsync(clock.Now, new() { Task = "局面复盘", TextContains = "甲%" })).Records.Single().Id == summary.Id,
            "Task and text filters were not combined.");
    }

    private static async Task EffectiveCutoff(string path)
    {
        var (store, clock) = await Open(path);
        var first = await Save(store, clock, Draft("第一个时点"));
        var second = await Save(store, clock, Draft("第二个时点"));
        await Save(store, clock, Draft("未来记录"));
        var asOf = second.RecordedAt.ToOffset(TimeSpan.FromHours(8));
        var filterCutoff = first.RecordedAt.ToOffset(TimeSpan.FromHours(-5));
        var earlierFilter = await store.ListPageAsOfAsync(asOf, new(RecordedBefore: filterCutoff));
        Check(earlierFilter.Records.Single().Id == first.Id && earlierFilter.AsOf == first.RecordedAt && earlierFilter.AsOf.Offset == TimeSpan.Zero,
            "RecordedBefore was not the earlier inclusive UTC boundary.");
        var earlierArgument = await store.ListPageAsOfAsync(first.RecordedAt, new(RecordedBefore: second.RecordedAt));
        Check(earlierArgument.Records.Single().Id == first.Id && earlierArgument.AsOf == first.RecordedAt,
            "A later filter cutoff weakened the explicit as-of time.");
        Check((await store.ListPageAsOfAsync(asOf)).Records.Select(record => record.Id).SequenceEqual([second.Id, first.Id]) &&
            (await store.ListPageAsOfAsync(first.RecordedAt.AddTicks(-1))).Records.Count == 0, "As-of equality included a future record or excluded its exact boundary.");
        var defaultPage = await store.ListPageAsync(new(RecordedBefore: filterCutoff));
        Check(defaultPage.AsOf == first.RecordedAt && defaultPage.AsOf.Offset == TimeSpan.Zero && defaultPage.Records.Single().Id == first.Id,
            "The current-page convenience API ignored its effective cutoff.");
    }

    private static async Task FrozenPages(string path)
    {
        var (store, clock) = await Open(path); var records = new List<ArchivedAdviceRecord>();
        for (var i = 0; i < 5; i++) records.Add(await Save(store, clock, Draft("固定分页 " + i)));
        var first = await store.ListPageAsOfAsync(clock.Now, pageSize: 2);
        await Save(store, clock, Draft("稍后插入的分析"));
        var second = await store.ListPageAsOfAsync(first.AsOf, pageNumber: 2, pageSize: 2);
        var third = await store.ListPageAsOfAsync(second.AsOf, pageNumber: 3, pageSize: 2);
        Check(first.Records.Concat(second.Records).Concat(third.Records).Select(record => record.Id)
            .SequenceEqual(records.AsEnumerable().Reverse().Select(record => record.Id)) && second.AsOf == first.AsOf && third.AsOf == first.AsOf &&
            !third.HasMore, "An insertion moved a fixed-cutoff offset or duplicated a page boundary.");
        var reopened = new AdviceArchiveStore(path);
        Check((await reopened.ListPageAsOfAsync(first.AsOf, pageNumber: 2, pageSize: 2)).Records.Select(record => record.Id)
            .SequenceEqual(second.Records.Select(record => record.Id)), "Reopening moved the immutable browsing cutoff.");
        Check((await store.ListPageAsOfAsync(clock.Now, pageSize: 200)).Records.Count == 6,
            "An explicit refresh cutoff did not expose the later analysis.");
    }

    private static async Task DeepReadOnly(string path)
    {
        var (store, clock) = await Open(path);
        var reasons = new List<string> { "原依据" }; var risks = new List<string> { "原风险" };
        var actions = new List<ArchivedAdviceAction> { new("attack", 11, 21) };
        var evidence = new List<AdviceToolEvidence> { new("evaluate_line", "原证据", new string('A', 64)) };
        var requests = new List<string> { "request-original" }; var sources = new List<string> { "archive-original" };
        var saved = await Save(store, clock, Draft("深只读分析") with
        {
            Reasons = reasons, Risks = risks, Actions = actions, Evidence = evidence, RequestIds = requests,
            ConversationArchiveIds = sources, Verification = "verified"
        });
        reasons.Clear(); risks[0] = "后改"; actions.Clear(); evidence.Clear(); requests.Clear(); sources.Clear();
        var page = await store.ListPageAsOfAsync(clock.Now); var record = page.Records.Single(); var before = JsonSerializer.Serialize(record);
        Check(record.Id == saved.Id && record.Advice.Reasons.Single() == "原依据" && record.Advice.Risks.Single() == "原风险" &&
            record.Advice.Actions.Count == 1 && record.Advice.Evidence.Single().Summary == "原证据" &&
            record.Advice.RequestIds!.Single() == "request-original" && record.Advice.ConversationArchiveIds!.Single() == "archive-original",
            "Persisted page retained caller-owned collection aliases.");
        await Throws<NotSupportedException>(() => { ((IList<ArchivedAdviceRecord>)page.Records).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<string>)record.Advice.Reasons).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<string>)record.Advice.Risks)[0] = "变更"; return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<ArchivedAdviceAction>)record.Advice.Actions).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<AdviceToolEvidence>)record.Advice.Evidence).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<string>)record.Advice.RequestIds!).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<string>)record.Advice.ConversationArchiveIds!).Clear(); return Task.CompletedTask; });
        var replaced = record with { Advice = record.Advice with { Summary = "调用方副本" } };
        Check(replaced.Advice.Summary != record.Advice.Summary && JsonSerializer.Serialize((await store.ListPageAsOfAsync(clock.Now)).Records.Single()) == before,
            "Mutating a caller record copy changed the next page or persisted archive.");
    }

    private static async Task ReadOnlyDatabase(string path)
    {
        var (store, clock) = await Open(path);
        await Save(store, clock, Draft("数据库只读检查")); await Save(store, clock, Draft("第二条只读检查"));
        var before = await DatabaseFingerprint(path);
        await store.ListPageAsOfAsync(clock.Now, pageSize: 1);
        await store.ListPageAsOfAsync(clock.Now, new() { Task = "局面复盘", TextContains = "不存在" }, pageSize: 200);
        await store.ListPageAsync(pageNumber: 2, pageSize: 1);
        await new AdviceArchiveStore(path).ListPageAsOfAsync(clock.Now);
        Check(await DatabaseFingerprint(path) == before, "Read-only archive browsing altered database bytes, rows, or schema.");
    }

    private static async Task BoundsAndLegacy(string path)
    {
        var (store, clock) = await Open(path);
        for (var i = 0; i < 105; i++) await Save(store, clock, Draft("边界样本 " + i));
        Check((await store.ListAsync()).Count == 100 && (await store.ListAsync(new(Limit: 1000))).Count == 105 &&
            (await store.ListAsync(new(Limit: 1))).Count == 1, "Legacy list default or explicit limits changed.");
        foreach (var limit in new[] { 0, -1, int.MaxValue })
        {
            var page = await store.ListPageAsOfAsync(clock.Now, new(Limit: limit), pageSize: 3);
            Check(page.Records.Count == 3 && page.PageSize == 3 && page.HasMore, "An ignored legacy limit constrained or rejected paged retrieval.");
        }
        var all = await store.ListPageAsOfAsync(clock.Now, new(Limit: 1), pageSize: 200);
        Check(all.Records.Count == 105 && !all.HasMore, "Page size did not replace the old list limit.");
        var far = await store.ListPageAsOfAsync(clock.Now, pageNumber: int.MaxValue, pageSize: 200);
        Check(far.PageNumber == int.MaxValue && far.Records.Count == 0 && !far.HasMore && far.HasPrevious,
            "The maximum page offset overflowed or wrapped into real rows.");
        foreach (var invalid in new[] { 0, 1001, int.MaxValue }) await Throws<ArgumentException>(() => store.ListAsync(new(Limit: invalid)));
    }

    private static async Task InvalidAndCancelled(string path)
    {
        var (store, clock) = await Open(path); await Save(store, clock, Draft("保留的归档"));
        var before = await DatabaseFingerprint(path);
        foreach (var (number, size) in new[] { (0, 100), (-1, 100), (1, 0), (1, 201), (int.MinValue, 200) })
            await Throws<ArgumentOutOfRangeException>(() => store.ListPageAsOfAsync(clock.Now, pageNumber: number, pageSize: size));
        var invalids = new AdviceArchiveFilter[]
        {
            new(StateVersion: -1), new(ThroughEventSequence: -1), new(MatchId: ""), new(CardBuild: "https://invalid.example"), new(DeckVersionId: "bad\tdeck"),
            new() { Task = new string('任', 81) }, new() { Task = "任务\n" }, new() { Task = "任务\0" },
            new() { TextContains = new string('查', 201) }, new() { TextContains = "查询\t" }, new() { TextContains = "查询\0" }
        };
        foreach (var filter in invalids)
        {
            await Throws<ArgumentException>(() => store.ListPageAsOfAsync(clock.Now, filter));
            await Throws<ArgumentException>(() => store.ListAsync(filter));
        }
        Check((await store.ListPageAsOfAsync(clock.Now, new() { Task = new string('任', 80), TextContains = new string('查', 200) })).Records.Count == 0,
            "Valid maximum search lengths were rejected.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.ListPageAsOfAsync(clock.Now, cancellationToken: cancelled.Token));
        await Throws<OperationCanceledException>(() => store.ListPageAsync(cancellationToken: cancelled.Token));
        await Throws<OperationCanceledException>(() => store.ListAsync(cancellationToken: cancelled.Token));
        Check(await DatabaseFingerprint(path) == before, "Invalid or cancelled reads modified stored archive state.");
    }

    private static async Task ConversationUnaffected(string path)
    {
        var (store, clock) = await Open(path); var allowed = new List<ArchivedAdviceRecord>();
        for (var i = 1; i <= 3; i++) allowed.Add(await Save(store, clock, Draft("历史回答 " + i) with
        { StateVersion = i, EventSequence = i, Task = i == 1 ? "" : "局面复盘", KnowledgeAsOf = Stamp }));
        var query = new AdviceConversationQuery("paging-match", "paging-deck", "255790", 3, 3, clock.Now, MaxTurns: 2, MaxKnowledgeAsOf: Stamp);
        await Save(store, clock, Draft("未来局面") with { StateVersion = 4, EventSequence = 4, KnowledgeAsOf = Stamp });
        await Save(store, clock, Draft("其他套牌") with { DeckVersionId = "other-deck", KnowledgeAsOf = Stamp });
        var before = await store.GetConversationHistoryAsync(query);
        await store.ListPageAsOfAsync(clock.Now, new() { Task = "", TextContains = "历史" }, pageSize: 1);
        await store.ListPageAsOfAsync(clock.Now, new() { Task = "未知任务", TextContains = "无" });
        var after = await store.GetConversationHistoryAsync(query);
        Check(before.IsTruncated && after.IsTruncated && before.Records.Select(record => record.Id).SequenceEqual(allowed.Skip(1).Select(record => record.Id)) &&
            JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after), "Paged filters changed conversation chronological order, binding, cutoff, or truncation.");
        Check((await store.ListPageAsOfAsync(query.AsOf)).Records.Select(record => record.Id).SequenceEqual(allowed.AsEnumerable().Reverse().Select(record => record.Id)),
            "Archive browsing incorrectly inherited the conversation's two-turn quota or chronological order.");
    }

    private static AdviceRecordDraft Draft(string summary) => new("paging-match", 8, 15, "255790", "hunter-v13/turn", summary)
    { DeckVersionId = "paging-deck", Task = "局面复盘", UserQuestion = "原问题", Verification = "strategy" };
    private static async Task<(AdviceArchiveStore Store, ArchivePagingClock Clock)> Open(string path)
    {
        var clock = new ArchivePagingClock(); var store = new AdviceArchiveStore(path, clock); await store.InitializeAsync(); return (store, clock);
    }
    private static async Task<ArchivedAdviceRecord> Save(AdviceArchiveStore store, ArchivePagingClock clock, AdviceRecordDraft draft, string? id = null)
    { clock.Advance(); return await store.SaveAsync(draft, id); }
    private static async Task<SqliteConnection> Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); return connection;
    }
    private static async Task<string> DatabaseFingerprint(string path)
    {
        string schema; string rows;
        await using (var connection = await Connect(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type||':'||name||':'||COALESCE(sql,'') FROM sqlite_master ORDER BY type,name";
            var values = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) values.Add(reader.GetString(0));
            schema = string.Join("\n", values); values.Clear();
            command.CommandText = "SELECT id||':'||recorded_at||':'||content_hash||':'||payload_json FROM coach_advice_records ORDER BY id";
            await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) values.Add(reader.GetString(0));
            rows = string.Join("\n", values);
        }
        return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) + "\n" + schema + "\n" + rows;
    }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class ArchivePagingClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = Stamp;
        public void Advance() => Now = Now.AddSeconds(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
