using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

static class ConversationArchiveChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "conversation-sources");
        yield return ("conversation history binds exact deck including unbound and both time axes", () => History(Path.Combine(root, "history.db")));
        yield return ("archived model request and history sources are immutable and durable", () => Sources(Path.Combine(root, "sources.db")));
        yield return ("legacy archive JSON remains unknown-source and hash-idempotent", () => Legacy(Path.Combine(root, "legacy.db")));
        yield return ("request and tool traces preserve optional source links without old hash changes", () => Traces(Path.Combine(root, "traces.db")));
        yield return ("strict knowledge cutoff excludes later live opinions on the same state", () => KnowledgeCutoff(Path.Combine(root, "knowledge-cutoff.db")));
    }
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static AdviceRecordDraft Draft(string summary = "当时的分析", long version = 4, long sequence = 4)
        => new("source-match", version, sequence, "255790", "hunter-v3/turn", summary) { DeckVersionId = "deck-a", UserQuestion = "当时的问题" };
    private static async Task History(string path)
    {
        var clock = new StorageManualClock { Now = Cutoff.AddHours(-1) }; var store = new AdviceArchiveStore(path, clock); await store.InitializeAsync();
        var allowed = new List<ArchivedAdviceRecord>();
        for (var i = 1; i <= 5; i++) { allowed.Add(await store.SaveAsync(Draft("answer-" + i, i, i))); clock.Now = clock.Now.AddMinutes(1); }
        await store.SaveAsync(Draft("OTHER_MATCH") with { MatchId = "other-match" });
        await store.SaveAsync(Draft("OTHER_DECK") with { DeckVersionId = "deck-b" });
        var unbound = await store.SaveAsync(Draft("UNBOUND_DECK") with { DeckVersionId = null });
        await store.SaveAsync(Draft("OTHER_BUILD") with { CardBuild = "255791" });
        await store.SaveAsync(Draft("FUTURE_STATE", 6, 4)); await store.SaveAsync(Draft("FUTURE_EVENT", 4, 6));
        clock.Now = Cutoff.AddHours(1); await store.SaveAsync(Draft("FUTURE_RECORDED"));
        var query = new AdviceConversationQuery("source-match", "deck-a", "255790", 5, 5, Cutoff);
        var history = await store.GetConversationHistoryAsync(query);
        Check(history.IsTruncated && history.Records.Select(r => r.Id).SequenceEqual(allowed.Skip(1).Select(r => r.Id)), "Conversation query crossed a binding/cutoff or returned wrong chronological window.");
        Check((await store.GetConversationHistoryAsync(query with { DeckVersionId = null })).Records.Single().Id == unbound.Id, "Unbound conversation mixed with a fixed deck.");
        Check((await store.GetConversationHistoryAsync(query with { AsOf = allowed[0].RecordedAt })).Records.Single().Id == allowed[0].Id, "Future recording leaked into old conversation cutoff.");
        Check((await store.GetConversationHistoryAsync(query with { MaxTurns = 0 })) is { IsTruncated: false, Records.Count: 0 }, "Explicitly disabled conversation still returned history.");
        await Throws<ArgumentException>(() => store.GetConversationHistoryAsync(query with { MaxTurns = 5 }));
    }
    private static async Task Sources(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        var requestIds = new List<string> { "request-one", "request-two" }; var historyIds = new List<string> { "archive-before" };
        var saved = await store.SaveAsync(Draft() with { Model = "provider/model", AnalysisRunId = "analysis-one", RequestIds = requestIds, ConversationArchiveIds = historyIds });
        requestIds.Clear(); historyIds.Clear();
        var restored = (await new AdviceArchiveStore(path).GetAsync(saved.Id))!;
        Check(restored.Advice is { Model: "provider/model", AnalysisRunId: "analysis-one" } && restored.Advice.RequestIds!.SequenceEqual(new[] { "request-one", "request-two" }) &&
            restored.Advice.ConversationArchiveIds!.Single() == "archive-before", "Archive source pointers were lost or retained mutable caller lists.");
        await Throws<NotSupportedException>(() => { ((IList<string>)restored.Advice.RequestIds!).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<string>)restored.Advice.ConversationArchiveIds!).Clear(); return Task.CompletedTask; });
        foreach (var invalid in new[]
        {
            Draft() with { RequestIds = ["same", "same"] }, Draft() with { AnalysisRunId = "https://private.example" },
            Draft() with { Model = "private\nmodel" }, Draft() with { RequestIds = Enumerable.Range(0, 6).Select(i => "r-" + i).ToArray() },
            Draft() with { ConversationArchiveIds = Enumerable.Range(0, 5).Select(i => "a-" + i).ToArray() }
        }) await Throws<ArgumentException>(() => store.SaveAsync(invalid));
    }
    private static async Task Legacy(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        var original = await store.SaveAsync(Draft(), "legacy-record");
        var json = JsonSerializer.Serialize(original.Advice);
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var property in new[] { "Model", "AnalysisRunId", "RequestIds", "ConversationArchiveIds", "KnowledgeAsOf" })
            Check(!node.ContainsKey(property), "Default source fields changed legacy JSON/hash material.");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE coach_advice_records SET payload_json=$json,content_hash=$hash WHERE id='legacy-record'";
            command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
            await command.ExecuteNonQueryAsync();
        }
        var reopened = new AdviceArchiveStore(path); await reopened.InitializeAsync();
        var old = (await reopened.GetAsync("legacy-record"))!;
        Check(old.Advice is { Model: null, AnalysisRunId: null, RequestIds: null, ConversationArchiveIds: null, KnowledgeAsOf: null }, "Legacy advice invented provenance.");
        var retry = await reopened.SaveAsync(Draft() with { RequestIds = [], ConversationArchiveIds = [] }, "legacy-record");
        Check(retry.RecordedAt == original.RecordedAt && retry.Advice.RequestIds is null, "Legacy retry or empty source normalization changed immutable hash identity.");
    }
    private static async Task Traces(string path)
    {
        var store = new AnalysisTraceStore(path); await store.InitializeAsync();
        var request = new ModelRequestTrace("trace-request", "source-match", 5, "provider/model", 1, 10, 2, 12, 12, 30, AnalysisTraceStatus.Completed)
        { AnalysisRunId = "analysis-one", EventSequence = 5, DeckVersionId = "deck-a" };
        var tool = new ToolExecutionTrace("trace-tool", "source-match", 5, "get_snapshot", 2, AnalysisTraceStatus.Completed)
        { Model = "provider/model", AnalysisRunId = "analysis-one", RequestId = "trace-request", ToolCallId = "call-one", EventSequence = 5 };
        await store.AppendBatchAsync([request], [tool]); await store.AppendBatchAsync([request], [tool]);
        var reopened = new AnalysisTraceStore(path); await reopened.InitializeAsync();
        Check((await reopened.ListRequestsAsync("source-match")).Single() == request && (await reopened.ListToolsAsync("source-match")).Single() == tool &&
            (await reopened.GetBudgetTotalsAsync("source-match")).RequestCount == 1, "Trace provenance was lost or duplicate attribution charged twice.");
        var legacy = request with { RequestId = "legacy-request", AnalysisRunId = null, EventSequence = null, DeckVersionId = null };
        var json = JsonSerializer.Serialize(legacy with { RecordedAt = DateTimeOffset.UnixEpoch });
        Check(!JsonNode.Parse(json)!.AsObject().ContainsKey("AnalysisRunId") && !JsonNode.Parse(json)!.AsObject().ContainsKey("EventSequence"), "Null trace metadata changed legacy receipt hash material.");
        await store.AppendRequestAsync(legacy); await store.AppendRequestAsync(legacy);
        await Throws<ArgumentException>(() => store.AppendToolAsync(tool with { TraceId = "bad-event", EventSequence = -1 }));
        await Throws<ArgumentException>(() => store.AppendRequestAsync(request with { RequestId = "bad-source", AnalysisRunId = "https://private.example" }));
    }
    private static async Task KnowledgeCutoff(string path)
    {
        var clock = new StorageManualClock { Now = Cutoff.AddHours(1) }; var store = new AdviceArchiveStore(path, clock); await store.InitializeAsync();
        var unknown = await store.SaveAsync(Draft("旧资料截止时间未知"));
        var historical = await store.SaveAsync(Draft("同点历史模式回答") with { KnowledgeAsOf = Cutoff.ToOffset(TimeSpan.FromHours(8)) });
        var live = await store.SaveAsync(Draft("同点后来实时回答：已使用新增笔记") with { KnowledgeAsOf = Cutoff.AddTicks(1) });
        var query = new AdviceConversationQuery("source-match", "deck-a", "255790", 4, 4, Cutoff.AddHours(2), MaxKnowledgeAsOf: Cutoff);
        var strict = await store.GetConversationHistoryAsync(query);
        Check(strict.Records.Count == 1 && strict.Records[0].Id == historical.Id && strict.Records[0].Advice.KnowledgeAsOf!.Value.Offset == TimeSpan.Zero,
            "Strict history admitted unknown or even one-tick later knowledge on the same state.");
        Check((await store.GetConversationHistoryAsync(query with { MaxKnowledgeAsOf = Cutoff.AddMinutes(1) })).Records.Select(r => r.Id).ToHashSet().SetEquals(new[] { historical.Id, live.Id }),
            "Widened knowledge scope failed to retain eligible completed answers.");
        Check((await store.GetConversationHistoryAsync(query with { MaxKnowledgeAsOf = null })).Records.Any(r => r.Id == unknown.Id),
            "Legacy unknown-source archive became unreadable outside strict history.");
    }
}
