using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HsHelper.Storage;

public sealed record ArchivedAdviceAction(string Kind, int? SourceEntityId, int? TargetEntityId = null);
public sealed record AdviceToolEvidence(string ToolName, string Summary, string? ResultHash = null);

/// <summary>Only final structured advice and brief tool evidence belong here; no HTTP messages or prompts.</summary>
public sealed record AdviceRecordDraft(string MatchId, long StateVersion, long EventSequence, string CardBuild,
    string PromptTemplateVersion, string Summary)
{
    public string? DeckVersionId { get; init; }
    public string Task { get; init; } = "";
    public string? UserQuestion { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public string? Alternative { get; init; }
    public IReadOnlyList<string> Risks { get; init; } = [];
    public string Verification { get; init; } = "strategy";
    public bool VerifiedLethal { get; init; }
    public IReadOnlyList<ArchivedAdviceAction> Actions { get; init; } = [];
    public IReadOnlyList<AdviceToolEvidence> Evidence { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AnalysisRunId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? RequestIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? ConversationArchiveIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? KnowledgeAsOf { get; init; }
}

public sealed record ArchivedAdviceRecord(string Id, DateTimeOffset RecordedAt, AdviceRecordDraft Advice);
public sealed record AdviceArchiveFilter(string? MatchId = null, long? StateVersion = null,
    long? ThroughEventSequence = null, string? CardBuild = null, string? DeckVersionId = null,
    DateTimeOffset? RecordedBefore = null, int Limit = 100);
public sealed record AdviceConversationQuery(string MatchId, string? DeckVersionId, string CardBuild,
    long StateVersion, long EventSequence, DateTimeOffset AsOf, int MaxTurns = 4, DateTimeOffset? MaxKnowledgeAsOf = null);
public sealed record AdviceHistoryResult(IReadOnlyList<ArchivedAdviceRecord> Records, bool IsTruncated);

/// <summary>Immutable completed advice, with exact state/build/template binding and sanitized allowlisted fields.</summary>
public sealed class AdviceArchiveStore
{
    private readonly CoachLocalDatabase _database;
    private readonly TimeProvider _clock;
    public AdviceArchiveStore(string databasePath, TimeProvider? clock = null)
        => (_database, _clock) = (new(databasePath), clock ?? TimeProvider.System);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        await CoachLocalDatabase.CheckSchemaAsync(command, "advice_archive_schema", cancellationToken);
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS coach_advice_records(
              id TEXT PRIMARY KEY,match_id TEXT NOT NULL,state_version INTEGER NOT NULL,event_sequence INTEGER NOT NULL,
              card_build TEXT NOT NULL,prompt_template_version TEXT NOT NULL,deck_version_id TEXT,recorded_at TEXT NOT NULL,
              content_hash TEXT NOT NULL,payload_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS coach_advice_state ON coach_advice_records(match_id,state_version,event_sequence,recorded_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken); transaction.Commit();
    }

    public async Task<ArchivedAdviceRecord> SaveAsync(AdviceRecordDraft draft, string? recordId = null,
        IReadOnlyCollection<string>? sensitiveValues = null, CancellationToken cancellationToken = default)
    {
        draft = Prepare(draft, sensitiveValues);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(draft))));
        recordId ??= hash;
        CoachLocalDatabase.Identifier(recordId);
        await using var connection = await _database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT content_hash,payload_json,recorded_at FROM coach_advice_records WHERE id=$id";
        CoachLocalDatabase.Add(command, "$id", recordId);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != hash) throw new InvalidDataException("已归档建议不可覆盖，相同标识对应了不同内容。");
                return new(recordId, DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), Freeze(Read(reader.GetString(1))));
            }
        var at = await CoachLocalDatabase.NextRecordedAtAsync(command, "coach_advice_records", _clock, cancellationToken);
        command.CommandText = "INSERT INTO coach_advice_records VALUES($id,$match,$version,$sequence,$build,$template,$deck,$at,$hash,$json)";
        CoachLocalDatabase.Add(command, "$match", draft.MatchId); CoachLocalDatabase.Add(command, "$version", draft.StateVersion);
        CoachLocalDatabase.Add(command, "$sequence", draft.EventSequence); CoachLocalDatabase.Add(command, "$build", draft.CardBuild);
        CoachLocalDatabase.Add(command, "$template", draft.PromptTemplateVersion); CoachLocalDatabase.Add(command, "$deck", draft.DeckVersionId);
        CoachLocalDatabase.Add(command, "$at", CoachLocalDatabase.Timestamp(at)); CoachLocalDatabase.Add(command, "$hash", hash);
        CoachLocalDatabase.Add(command, "$json", JsonSerializer.Serialize(draft));
        await command.ExecuteNonQueryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return new(recordId, at, draft);
    }

    public async Task<ArchivedAdviceRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        CoachLocalDatabase.Identifier(id);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT recorded_at,payload_json FROM coach_advice_records WHERE id=$id";
        CoachLocalDatabase.Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(id, DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), Freeze(Read(reader.GetString(1)))) : null;
    }

    public async Task<IReadOnlyList<ArchivedAdviceRecord>> ListAsync(AdviceArchiveFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new();
        if (filter.Limit is < 1 or > 1000 || filter.StateVersion < 0 || filter.ThroughEventSequence < 0)
            throw new ArgumentException("建议归档筛选条件无效。");
        foreach (var id in new[] { filter.MatchId, filter.CardBuild, filter.DeckVersionId })
            if (id is not null) CoachLocalDatabase.Identifier(id);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var clauses = new List<string>();
        void Where(string column, string parameter, object? value, string comparison = "=")
        {
            if (value is null) return;
            clauses.Add(column + comparison + parameter); CoachLocalDatabase.Add(command, parameter, value);
        }
        Where("match_id", "$match", filter.MatchId); Where("state_version", "$version", filter.StateVersion);
        Where("event_sequence", "$sequence", filter.ThroughEventSequence, "<=");
        Where("card_build", "$build", filter.CardBuild); Where("deck_version_id", "$deck", filter.DeckVersionId);
        Where("recorded_at", "$at", filter.RecordedBefore.HasValue ? CoachLocalDatabase.Timestamp(filter.RecordedBefore.Value) : null, "<=");
        command.CommandText = "SELECT id,recorded_at,payload_json FROM coach_advice_records" +
            (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses)) + " ORDER BY recorded_at DESC,id LIMIT $limit";
        CoachLocalDatabase.Add(command, "$limit", filter.Limit);
        var result = new List<ArchivedAdviceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), Freeze(Read(reader.GetString(2)))));
        return result.AsReadOnly();
    }

    /// <summary>MaxKnowledgeAsOf enables strict history: unknown or later knowledge scopes are excluded.</summary>
    public async Task<AdviceHistoryResult> GetConversationHistoryAsync(AdviceConversationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        CoachLocalDatabase.Identifier(query.MatchId); CoachLocalDatabase.Identifier(query.CardBuild);
        if (query.DeckVersionId is not null) CoachLocalDatabase.Identifier(query.DeckVersionId);
        if (query.StateVersion < 0 || query.EventSequence < 0 || query.MaxTurns is < 0 or > 4 || query.MaxKnowledgeAsOf > query.AsOf)
            throw new ArgumentException("连续追问历史的边界或轮数无效。");
        cancellationToken.ThrowIfCancellationRequested();
        if (query.MaxTurns == 0) return new(Array.Empty<ArchivedAdviceRecord>(), false);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,recorded_at,payload_json FROM coach_advice_records
            WHERE match_id=$match AND card_build=$build AND state_version<=$version AND event_sequence<=$sequence
              AND recorded_at<=$at AND
            """ + " " + (query.DeckVersionId is null ? "deck_version_id IS NULL" : "deck_version_id=$deck") +
            (query.MaxKnowledgeAsOf.HasValue ? " AND json_type(payload_json,'$.KnowledgeAsOf')='text' AND json_extract(payload_json,'$.KnowledgeAsOf')<=$knowledgeAt" : "") +
            " ORDER BY recorded_at DESC,id DESC LIMIT $limit";
        CoachLocalDatabase.Add(command, "$match", query.MatchId); CoachLocalDatabase.Add(command, "$build", query.CardBuild);
        CoachLocalDatabase.Add(command, "$version", query.StateVersion); CoachLocalDatabase.Add(command, "$sequence", query.EventSequence);
        CoachLocalDatabase.Add(command, "$at", CoachLocalDatabase.Timestamp(query.AsOf));
        if (query.DeckVersionId is not null) CoachLocalDatabase.Add(command, "$deck", query.DeckVersionId);
        if (query.MaxKnowledgeAsOf.HasValue) CoachLocalDatabase.Add(command, "$knowledgeAt", CoachLocalDatabase.Timestamp(query.MaxKnowledgeAsOf.Value));
        CoachLocalDatabase.Add(command, "$limit", query.MaxTurns + 1);
        var records = new List<ArchivedAdviceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(new(reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), Freeze(Read(reader.GetString(2)))));
        var truncated = records.Count > query.MaxTurns;
        if (truncated) records.RemoveAt(records.Count - 1);
        records.Reverse(); return new(records.AsReadOnly(), truncated);
    }

    /// <summary>
    /// Validates, redacts and deeply freezes a complete advice record without opening or writing any database.
    /// Queue only this returned value, so malformed content cannot permanently block durable retries.
    /// </summary>
    public static AdviceRecordDraft Prepare(AdviceRecordDraft draft, IReadOnlyCollection<string>? sensitiveValues = null)
    {
        var secrets = sensitiveValues;
        ArgumentNullException.ThrowIfNull(draft);
        CoachLocalDatabase.Identifier(draft.MatchId); CoachLocalDatabase.Identifier(draft.CardBuild);
        CoachLocalDatabase.Identifier(draft.PromptTemplateVersion);
        if (draft.DeckVersionId is not null) CoachLocalDatabase.Identifier(draft.DeckVersionId);
        if (draft.Model is not null && (string.IsNullOrWhiteSpace(draft.Model) || draft.Model.Length > 200 ||
            draft.Model.Any(char.IsControl) || draft.Model.Contains("://", StringComparison.Ordinal)))
            throw new ArgumentException("归档模型标识无效。");
        if (draft.AnalysisRunId is not null) CoachLocalDatabase.Identifier(draft.AnalysisRunId);
        var requestIds = ProvenanceIds(draft.RequestIds, 5);
        var conversationIds = ProvenanceIds(draft.ConversationArchiveIds, 4);
        if (draft.StateVersion < 0 || draft.EventSequence < 0 || draft.Reasons is null || draft.Risks is null ||
            draft.Actions is null || draft.Evidence is null || draft.Reasons.Count > 12 || draft.Risks.Count > 16 ||
            draft.Actions.Count > 30 || draft.Evidence.Count > 32 || draft.Task is null || draft.Task.Length > 80 ||
            draft.Verification is not ("strategy" or "verified" or "conditional" or "unsupported") ||
            (draft.Actions.Count > 0 && draft.Verification != "verified") ||
            (draft.VerifiedLethal && (draft.Verification != "verified" || draft.Actions.Count == 0)))
            throw new ArgumentException("归档建议的局面绑定或验证状态无效。");
        if (secrets is { Count: > 32 } || secrets?.Any(s => s is null || s.Length > 8192) == true)
            throw new ArgumentException("敏感字段过滤参数无效。");
        var sensitive = secrets?.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray() ?? [];
        var actions = draft.Actions.ToArray();
        foreach (var action in actions)
            if (action is null || action.Kind is not ("play" or "attack" or "hero_power" or "end_turn" or "choose") ||
                (action.Kind != "end_turn" && action.SourceEntityId is not > 0) ||
                (action.TargetEntityId.HasValue && action.TargetEntityId <= 0) ||
                (action.Kind == "attack" && action.TargetEntityId is null) ||
                (action.Kind == "end_turn" && (action.SourceEntityId.HasValue || action.TargetEntityId.HasValue)))
                throw new ArgumentException("归档动作结构无效。");
        var evidence = draft.Evidence.Select(value =>
        {
            if (value is null || !Regex.IsMatch(value.ToolName ?? "", "^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant) ||
                (value.ResultHash is not null && !Regex.IsMatch(value.ResultHash, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)))
                throw new ArgumentException("工具依据需要有效名称与脱敏摘要，可选哈希应为SHA256。");
            return value with { Summary = Text(value.Summary, 1000, sensitive), ResultHash = value.ResultHash?.ToUpperInvariant() };
        }).ToArray();
        return draft with
        {
            Summary = Text(draft.Summary, 2000, sensitive), Task = Text(draft.Task, 80, sensitive, allowEmpty: true),
            UserQuestion = draft.UserQuestion is null ? null : Text(draft.UserQuestion, 1000, sensitive, allowEmpty: true),
            Reasons = Array.AsReadOnly(draft.Reasons.Select(s => Text(s, 2000, sensitive)).ToArray()),
            Alternative = draft.Alternative is null ? null : Text(draft.Alternative, 2000, sensitive, allowEmpty: true),
            Risks = Array.AsReadOnly(draft.Risks.Select(s => Text(s, 2000, sensitive)).ToArray()),
            Actions = Array.AsReadOnly(actions), Evidence = Array.AsReadOnly(evidence),
            RequestIds = requestIds, ConversationArchiveIds = conversationIds, KnowledgeAsOf = draft.KnowledgeAsOf?.ToUniversalTime()
        };
    }

    private static string Text(string value, int maximum, string[] secrets, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
            throw new ArgumentException("建议归档文本为空、过长或包含无效字符。");
        if (Regex.IsMatch(value, @"(?im)^\s*(?:HTTP/\d|(?:[DIEW]\s+)?\d{2}:\d{2}:\d{2}.*(?:DebugPrintPower|DebugPrintGame|DebugPrintChoices))", RegexOptions.CultureInvariant) ||
            IsHttpEnvelope(value)) throw new ArgumentException("建议归档只接受结构化建议摘要，不能保存原始HTTP正文或源日志。");
        string Replace(string pattern, string replacement) => Regex.Replace(value, pattern, replacement,
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        value = Replace(@"(?i)\bBearer\s+[^\s,;""'<>]+", "Bearer [已隐藏]");
        value = Replace(@"\bsk-[A-Za-z0-9_-]{8,}\b", "[已隐藏密钥]");
        value = Replace(@"(?i)([""']?(?:api[_-]?key|authorization|access[_-]?token|secret)[""']?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;}]+)", "$1[已隐藏]");
        value = Replace(@"(?i)https?://[^\s<>""']+", "[已隐藏地址]");
        // Do exact-value redaction last: even a short credential must not break URL/label detection.
        // Choose a marker without any provided credential, so revalidation cannot recursively redact its own output.
        var marker = "[已隐藏敏感信息]";
        if (secrets.Any(secret => marker.Contains(secret, StringComparison.Ordinal)))
            marker = Enumerable.Range(0x25A0, 96).Select(code => ((char)code).ToString())
                .First(candidate => !secrets.Any(secret => candidate.Contains(secret, StringComparison.Ordinal)));
        foreach (var secret in secrets) value = value.Replace(secret, marker, StringComparison.Ordinal);
        // Redaction may expand very short tokens. Bound the returned value, not only its original input,
        // so a prepared record remains valid when SaveAsync prepares it again after queueing.
        var length = Math.Min(value.Length, maximum);
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
    private static bool IsHttpEnvelope(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{')) return false;
        try
        {
            return JsonNode.Parse(trimmed) is JsonObject body && body.Any(p => p.Key is "choices" or "usage" or "error" or "tool_calls" or "output" or "response");
        }
        catch (JsonException) { return false; }
    }
    private static AdviceRecordDraft Read(string json) => JsonSerializer.Deserialize<AdviceRecordDraft>(json)
        ?? throw new InvalidDataException("归档建议记录无效。");
    private static AdviceRecordDraft Freeze(AdviceRecordDraft value) => value with
    {
        Reasons = Array.AsReadOnly(value.Reasons.ToArray()), Risks = Array.AsReadOnly(value.Risks.ToArray()),
        Actions = Array.AsReadOnly(value.Actions.ToArray()), Evidence = Array.AsReadOnly(value.Evidence.ToArray()),
        RequestIds = value.RequestIds is null ? null : Array.AsReadOnly(value.RequestIds.ToArray()),
        ConversationArchiveIds = value.ConversationArchiveIds is null ? null : Array.AsReadOnly(value.ConversationArchiveIds.ToArray())
    };
    private static IReadOnlyList<string>? ProvenanceIds(IReadOnlyList<string>? values, int maximum)
    {
        if (values is null || values.Count == 0) return null;
        if (values.Count > maximum || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("归档来源标识超出上限或重复。");
        foreach (var id in values) CoachLocalDatabase.Identifier(id);
        return Array.AsReadOnly(values.ToArray());
    }
}
