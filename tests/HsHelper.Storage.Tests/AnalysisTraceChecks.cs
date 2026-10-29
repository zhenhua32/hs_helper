using HsHelper.Storage;
using Microsoft.Data.Sqlite;

static class AnalysisTraceChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        yield return ("anonymous usage and tool traces persist alongside session storage", () => Durable(database));
        yield return ("usage trace deduplication and conflicts are transactional", () => Transactional(database));
        yield return ("concurrent duplicate usage charges exactly once", () => Concurrent(database));
        yield return ("trace cleanup preserves budget and idempotent receipts", () => Cleanup(database));
        yield return ("trace validation and cancellation preserve persisted history", () => Validation(database));
        yield return ("trace schema rejects future versions and token ledger saturates safely", () => SchemaAndOverflow(database));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static ModelRequestTrace Request(string id, string match, long charge = 21) => new(id, match, 8,
        "provider/test-model", 1, 15, 6, 21, charge, 125, AnalysisTraceStatus.Completed)
    { CardBuild = "255790", Task = "回合建议" };

    private static async Task Durable(string database)
    {
        var store = new AnalysisTraceStore(database); await store.InitializeAsync();
        var request = Request("durable-request", "trace-durable");
        var failure = request with { RequestId = "durable-failure", Status = AnalysisTraceStatus.Failed,
            InputTokens = null, OutputTokens = null, TotalTokens = null, ChargedTokens = 0, Round = 2 };
        var tool = new ToolExecutionTrace("durable-tool", "trace-durable", 8, "evaluate_line", 3, AnalysisTraceStatus.Conditional)
        { ResultHash = new string('A', 64) };
        await store.AppendBatchAsync([request, failure], [tool]);
        var reopened = new AnalysisTraceStore(database); await reopened.InitializeAsync();
        var requests = await reopened.ListRequestsAsync("trace-durable");
        Check(requests.Count == 2 && requests.Contains(request) && requests.Contains(failure), "Reopening lost usage or provenance metadata.");
        Check((await reopened.ListToolsAsync("trace-durable")).Single() == tool, "Tool timing/status/hash was lost.");
        Check(await reopened.GetBudgetTotalsAsync("trace-durable") == new AnalysisBudgetTotals("trace-durable", 2, 21), "Failed request count or spend was lost.");
        Check(await reopened.GetBudgetTotalsAsync("unknown-match") == new AnalysisBudgetTotals("unknown-match", 0, 0), "Missing match spend was not zero.");
        Check((await new SessionStore(database).ListSessionsAsync()).Count > 0, "Independent trace schema disturbed sessions.");
    }
    private static async Task Transactional(string database)
    {
        var store = new AnalysisTraceStore(database); var original = Request("atomic-original", "trace-atomic");
        await store.AppendRequestAsync(original);
        await store.AppendRequestAsync(original with { RecordedAt = original.RecordedAt.AddMinutes(2) });
        Check((await store.ListRequestsAsync("trace-atomic")).Count == 1 &&
            (await store.GetBudgetTotalsAsync("trace-atomic")).RequestCount == 1, "Duplicate request was charged twice.");
        var preceding = Request("atomic-preceding", "trace-atomic");
        await Throws<InvalidDataException>(() => store.AppendBatchAsync([preceding, original with { ChargedTokens = 999 }], []));
        Check((await store.ListRequestsAsync("trace-atomic")).Count == 1 &&
            (await store.GetBudgetTotalsAsync("trace-atomic")).ChargedTokens == 21, "Conflicting batch partially committed.");
        await store.AppendRequestAsync(preceding);
        Check((await store.GetBudgetTotalsAsync("trace-atomic")).RequestCount == 2, "Failed transaction left a phantom receipt.");
    }
    private static async Task Concurrent(string database)
    {
        var request = Request("concurrent-request", "trace-concurrent");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => new AnalysisTraceStore(database).AppendRequestAsync(request))));
        var store = new AnalysisTraceStore(database);
        Check(await store.GetBudgetTotalsAsync("trace-concurrent") == new AnalysisBudgetTotals("trace-concurrent", 1, 21), "Concurrent append charged duplicate usage.");
    }
    private static async Task Cleanup(string database)
    {
        var store = new AnalysisTraceStore(database);
        var old = Request("cleanup-old", "trace-cleanup", 9000) with
        { RecordedAt = DateTimeOffset.UtcNow.AddDays(-90), InputTokens = null, OutputTokens = null, TotalTokens = null, ConservativeCharge = true };
        var current = Request("cleanup-current", "trace-cleanup");
        var oldTool = new ToolExecutionTrace("cleanup-tool-old", "trace-cleanup", 8, "get_personal_stats", 5, AnalysisTraceStatus.Completed)
        { RecordedAt = old.RecordedAt };
        await store.AppendBatchAsync([old, current], [oldTool]);
        var cleanup = await store.CleanupBeforeAsync(DateTimeOffset.UtcNow.AddDays(-30));
        Check(cleanup == new AnalysisTraceCleanupResult(1, 1), "Cleanup removed recent trace details.");
        Check((await store.ListRequestsAsync("trace-cleanup")).Single() == current && (await store.ListToolsAsync("trace-cleanup")).Count == 0, "Cleanup did not remove old details.");
        await store.AppendRequestAsync(old with { RecordedAt = DateTimeOffset.UtcNow });
        await store.AppendToolAsync(oldTool with { RecordedAt = DateTimeOffset.UtcNow });
        Check(await store.GetBudgetTotalsAsync("trace-cleanup") == new AnalysisBudgetTotals("trace-cleanup", 2, 9021) &&
            (await store.ListRequestsAsync("trace-cleanup")).Count == 1 && (await store.ListToolsAsync("trace-cleanup")).Count == 0,
            "Cleanup reset budget or forgot idempotent receipts.");
    }
    private static async Task Validation(string database)
    {
        var store = new AnalysisTraceStore(database);
        foreach (var invalid in new[]
        {
            Request("invalid", "trace-invalid") with { Model = "https://secret.example/private" },
            Request("invalid", "trace-invalid") with { MatchId = "player nickname" },
            Request("invalid", "trace-invalid") with { ChargedTokens = -1 },
            Request("invalid", "trace-invalid") with { Status = AnalysisTraceStatus.Failed },
            Request("invalid", "trace-invalid") with { PromptTemplateVersion = "private\nvalue" },
            Request("invalid", "trace-invalid") with { Round = 0 }
        }) await Throws<ArgumentException>(() => store.AppendRequestAsync(invalid));
        await Throws<ArgumentException>(() => store.AppendToolAsync(new("invalid", "trace-invalid", 1, "https://private.example", 1, AnalysisTraceStatus.Completed)));
        await Throws<ArgumentException>(() => store.AppendToolAsync(new("invalid", "trace-invalid", 1, "evaluate_line", 1, AnalysisTraceStatus.Completed) { ResultHash = "raw result text" }));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => store.AppendRequestAsync(Request("cancelled", "trace-invalid"), cancellation.Token));
        Check((await store.GetBudgetTotalsAsync("trace-invalid")).RequestCount == 0, "Rejected or cancelled append changed ledger.");
    }
    private static async Task SchemaAndOverflow(string database)
    {
        var store = new AnalysisTraceStore(database);
        await store.AppendBatchAsync([Request("max-charge", "trace-max", long.MaxValue), Request("extra-charge", "trace-max", 1)], []);
        Check((await store.GetBudgetTotalsAsync("trace-max")).ChargedTokens == long.MaxValue, "Durable ledger token count overflowed.");
        var futureDatabase = Path.Combine(Path.GetDirectoryName(database)!, "future-analysis.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = futureDatabase, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE analysis_trace_schema(version INTEGER NOT NULL); INSERT INTO analysis_trace_schema VALUES(99)";
            await command.ExecuteNonQueryAsync();
        }
        await Throws<InvalidDataException>(() => new AnalysisTraceStore(futureDatabase).InitializeAsync());
    }
}
