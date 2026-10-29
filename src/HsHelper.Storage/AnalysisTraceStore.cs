using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HsHelper.Storage;

public enum AnalysisTraceStatus { Completed, Failed, Cancelled, Rejected, Conditional, Unsupported }

/// <summary>Allowlisted usage fields only: never pass credentials, endpoints, prompts or provider error text.</summary>
public sealed record ModelRequestTrace(string RequestId, string MatchId, long StateVersion, string Model, int Round,
    long? InputTokens, long? OutputTokens, long? TotalTokens, long ChargedTokens, long ElapsedMilliseconds,
    AnalysisTraceStatus Status, bool ConservativeCharge = false)
{
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public string PromptTemplateVersion { get; init; } = "hunter-v2";
    public string? CardBuild { get; init; }
    public string? Task { get; init; }
}

public sealed record ToolExecutionTrace(string TraceId, string MatchId, long StateVersion, string ToolName,
    long ElapsedMilliseconds, AnalysisTraceStatus Status)
{
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Optional SHA-256 of a sanitized result; raw tool results are never persisted here.</summary>
    public string? ResultHash { get; init; }
}

public sealed record AnalysisBudgetTotals(string MatchId, int RequestCount, long ChargedTokens);
public sealed record AnalysisTraceCleanupResult(int RequestsDeleted, int ToolsDeleted);

/// <summary>
/// Anonymous model/tool audit and durable per-match spend. Uses its own schema and has no Agent dependency.
/// Removing old trace details deliberately retains the small budget ledger, so cleanup cannot reset a match budget.
/// </summary>
public sealed class AnalysisTraceStore
{
    private readonly string _connectionString;
    public AnalysisTraceStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = path, ForeignKeys = true, Pooling = false, DefaultTimeout = 15 }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_trace_schema(version INTEGER NOT NULL);
            INSERT INTO analysis_trace_schema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM analysis_trace_schema);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "SELECT version FROM analysis_trace_schema";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("当前应用不支持此分析审计数据库版本。");
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_requests(
              id TEXT PRIMARY KEY,match_id TEXT NOT NULL,state_version INTEGER NOT NULL,recorded_at TEXT NOT NULL,
              charged_tokens INTEGER NOT NULL,status INTEGER NOT NULL,payload_json TEXT NOT NULL,content_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS analysis_tools(
              id TEXT PRIMARY KEY,match_id TEXT NOT NULL,state_version INTEGER NOT NULL,recorded_at TEXT NOT NULL,
              status INTEGER NOT NULL,payload_json TEXT NOT NULL,content_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS analysis_budget(
              match_id TEXT PRIMARY KEY,request_count INTEGER NOT NULL,charged_tokens INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS analysis_trace_receipts(
              kind TEXT NOT NULL,id TEXT NOT NULL,content_hash TEXT NOT NULL,PRIMARY KEY(kind,id));
            INSERT OR IGNORE INTO analysis_trace_receipts SELECT 'analysis_requests',id,content_hash FROM analysis_requests;
            INSERT OR IGNORE INTO analysis_trace_receipts SELECT 'analysis_tools',id,content_hash FROM analysis_tools;
            CREATE INDEX IF NOT EXISTS analysis_requests_match ON analysis_requests(match_id,recorded_at,id);
            CREATE INDEX IF NOT EXISTS analysis_tools_match ON analysis_tools(match_id,recorded_at,id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public Task AppendRequestAsync(ModelRequestTrace trace, CancellationToken cancellationToken = default)
        => AppendBatchAsync([trace], [], cancellationToken);
    public Task AppendToolAsync(ToolExecutionTrace trace, CancellationToken cancellationToken = default)
        => AppendBatchAsync([], [trace], cancellationToken);

    public async Task AppendBatchAsync(IEnumerable<ModelRequestTrace> requests, IEnumerable<ToolExecutionTrace> tools,
        CancellationToken cancellationToken = default)
    {
        var requestBatch = requests.ToArray(); var toolBatch = tools.ToArray();
        if (requestBatch.Length + toolBatch.Length > 1000) throw new ArgumentException("单批分析审计记录不得超过1000条。");
        foreach (var trace in requestBatch) Validate(trace);
        foreach (var trace in toolBatch) Validate(trace);
        cancellationToken.ThrowIfCancellationRequested();
        if (requestBatch.Length == 0 && toolBatch.Length == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var trace in requestBatch)
        {
            var added = await InsertAsync(connection, transaction, "analysis_requests", trace.RequestId, trace.MatchId,
                trace.StateVersion, trace.RecordedAt, trace.Status, JsonSerializer.Serialize(trace),
                Hash(JsonSerializer.Serialize(trace with { RecordedAt = DateTimeOffset.UnixEpoch })), trace.ChargedTokens, cancellationToken);
            if (!added) continue;
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO analysis_budget VALUES($match,1,$charge)
                ON CONFLICT(match_id) DO UPDATE SET
                  request_count=MIN(2147483647,request_count+1),
                  charged_tokens=CASE WHEN $charge>9223372036854775807-charged_tokens
                    THEN 9223372036854775807 ELSE charged_tokens+$charge END;
                """;
            Add(command, "$match", trace.MatchId); Add(command, "$charge", trace.ChargedTokens);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var trace in toolBatch)
            await InsertAsync(connection, transaction, "analysis_tools", trace.TraceId, trace.MatchId,
                trace.StateVersion, trace.RecordedAt, trace.Status, JsonSerializer.Serialize(trace),
                Hash(JsonSerializer.Serialize(trace with { RecordedAt = DateTimeOffset.UnixEpoch })), null, cancellationToken);
        transaction.Commit();
    }

    public async Task<AnalysisBudgetTotals> GetBudgetTotalsAsync(string matchId, CancellationToken cancellationToken = default)
    {
        Identifier(matchId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_count,charged_tokens FROM analysis_budget WHERE match_id=$match";
        Add(command, "$match", matchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(matchId, reader.GetInt32(0), reader.GetInt64(1)) : new(matchId, 0, 0);
    }

    public Task<IReadOnlyList<ModelRequestTrace>> ListRequestsAsync(string matchId, int limit = 1000,
        CancellationToken cancellationToken = default) => ListAsync<ModelRequestTrace>("analysis_requests", matchId, limit, cancellationToken);
    public Task<IReadOnlyList<ToolExecutionTrace>> ListToolsAsync(string matchId, int limit = 1000,
        CancellationToken cancellationToken = default) => ListAsync<ToolExecutionTrace>("analysis_tools", matchId, limit, cancellationToken);

    public async Task<AnalysisTraceCleanupResult> CleanupBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        Add(command, "$cutoff", Timestamp(cutoff));
        command.CommandText = "DELETE FROM analysis_requests WHERE recorded_at<$cutoff";
        var requests = await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM analysis_tools WHERE recorded_at<$cutoff";
        var tools = await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit(); return new(requests, tools);
    }

    private static async Task<bool> InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string id, string matchId, long stateVersion, DateTimeOffset recordedAt, AnalysisTraceStatus status,
        string payload, string hash, long? charge, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO analysis_trace_receipts VALUES($kind,$id,$hash) ON CONFLICT(kind,id) DO NOTHING";
        Add(command, "$kind", table); Add(command, "$id", id); Add(command, "$hash", hash);
        if (await command.ExecuteNonQueryAsync(token) == 0)
        {
            command.CommandText = "SELECT content_hash FROM analysis_trace_receipts WHERE kind=$kind AND id=$id";
            if (!string.Equals(await command.ExecuteScalarAsync(token) as string, hash, StringComparison.Ordinal))
                throw new InvalidDataException("相同分析记录标识对应了不同内容，整批写入已取消。");
            return false;
        }
        command.CommandText = "INSERT INTO " + table + "(id,match_id,state_version,recorded_at,status,payload_json,content_hash" +
            (charge.HasValue ? ",charged_tokens" : "") + ") VALUES($id,$match,$version,$at,$status,$payload,$hash" +
            (charge.HasValue ? ",$charge" : "") + ") ON CONFLICT(id) DO NOTHING";
        Add(command, "$match", matchId); Add(command, "$version", stateVersion);
        Add(command, "$at", Timestamp(recordedAt)); Add(command, "$status", (int)status);
        Add(command, "$payload", payload);
        if (charge.HasValue) Add(command, "$charge", charge.Value);
        if (await command.ExecuteNonQueryAsync(token) != 0) return true;
        command.CommandText = "SELECT content_hash FROM " + table + " WHERE id=$id";
        if (!string.Equals(await command.ExecuteScalarAsync(token) as string, hash, StringComparison.Ordinal))
            throw new InvalidDataException("相同分析记录标识对应了不同内容，整批写入已取消。");
        return false;
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string table, string matchId, int limit, CancellationToken token)
    {
        Identifier(matchId);
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM " + table + " WHERE match_id=$match ORDER BY recorded_at,id LIMIT $limit";
        Add(command, "$match", matchId); Add(command, "$limit", limit);
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))
            ?? throw new InvalidDataException("分析审计记录无效。"));
        return result.AsReadOnly();
    }

    private static void Validate(ModelRequestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Identifier(trace.RequestId); Identifier(trace.MatchId); Text(trace.Model, 200); Text(trace.PromptTemplateVersion, 80);
        if (trace.CardBuild is not null) Identifier(trace.CardBuild);
        if (trace.Task is not null) Text(trace.Task, 80);
        if (trace.StateVersion < 0 || trace.Round is < 1 or > 5 || trace.InputTokens < 0 || trace.OutputTokens < 0 ||
            trace.TotalTokens < 0 || trace.ChargedTokens < 0 || trace.ElapsedMilliseconds < 0 || !Enum.IsDefined(trace.Status) ||
            (trace.Status != AnalysisTraceStatus.Completed && trace.ChargedTokens != 0))
            throw new ArgumentException("分析用量或状态无效。");
    }
    private static void Validate(ToolExecutionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Identifier(trace.TraceId); Identifier(trace.MatchId);
        if (!Regex.IsMatch(trace.ToolName ?? "", "^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant) ||
            trace.StateVersion < 0 || trace.ElapsedMilliseconds < 0 || !Enum.IsDefined(trace.Status) ||
            (trace.ResultHash is not null && !Regex.IsMatch(trace.ResultHash, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("工具审计字段无效。仅接受工具标识、状态与脱敏结果哈希。");
    }
    private static void Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$", RegexOptions.CultureInvariant) || value.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("分析审计需要匿名标识，不能使用地址或自由文本。");
    }
    private static void Text(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("分析审计元数据格式无效，不能包含地址或控制字符。");
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
