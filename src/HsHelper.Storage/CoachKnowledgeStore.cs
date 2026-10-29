using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HsHelper.Storage;

public enum CoachKnowledgeKind { DeckPlan, MatchNote }
public enum CoachKnowledgeSource { UserPlan, UserNote }

public sealed record CoachKnowledgeDraft(string DeckVersionId, CoachKnowledgeKind Kind, string Title, string Content,
    string? OpponentClass = null, bool? WentFirst = null, string? MatchId = null)
{
    public long? EventSequence { get; init; }
}

/// <summary>User-authored data, never model instructions or independently verified game facts.</summary>
public sealed record CoachKnowledgeVersion(string EntryId, int Version, string DeckVersionId, CoachKnowledgeKind Kind,
    CoachKnowledgeSource Source, string Title, string Content, string? OpponentClass, bool? WentFirst,
    string? MatchId, DateTimeOffset RecordedAt, bool IsDeleted)
{
    public long? EventSequence { get; init; }
}

public sealed record CoachKnowledgeFilter(string? DeckVersionId = null, CoachKnowledgeKind? Kind = null,
    string? OpponentClass = null, bool? WentFirst = null, string? MatchId = null, int Limit = 100,
    bool IncludeDeleted = false, bool IncludeGeneral = true);

/// <summary>Append-only user notes/plans. Updates and deletion retain every earlier version for historical queries.</summary>
public sealed class CoachKnowledgeStore
{
    private readonly CoachLocalDatabase _database;
    private readonly TimeProvider _clock;
    public CoachKnowledgeStore(string databasePath, TimeProvider? clock = null)
        => (_database, _clock) = (new(databasePath), clock ?? TimeProvider.System);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        await CoachLocalDatabase.CheckSchemaAsync(command, "coach_knowledge_schema", cancellationToken);
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS coach_knowledge_versions(
              entry_id TEXT NOT NULL,version INTEGER NOT NULL,deck_version_id TEXT NOT NULL,kind INTEGER NOT NULL,
              opponent_class TEXT,went_first INTEGER,match_id TEXT,recorded_at TEXT NOT NULL,is_deleted INTEGER NOT NULL,
              payload_json TEXT NOT NULL,PRIMARY KEY(entry_id,version));
            CREATE INDEX IF NOT EXISTS coach_knowledge_scope ON coach_knowledge_versions(deck_version_id,recorded_at,entry_id,version);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken); transaction.Commit();
    }

    public async Task<CoachKnowledgeVersion> SaveAsync(CoachKnowledgeDraft draft, string? entryId = null,
        int? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft = Normalize(draft);
        if (entryId is not null) CoachLocalDatabase.Identifier(entryId);
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        await using var connection = await _database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var previous = entryId is null ? null : await LatestAsync(connection, transaction, entryId, cancellationToken);
        if (entryId is not null && previous is null) throw new KeyNotFoundException("笔记条目不存在，不能建立未定义的修订。");
        CheckExpected(previous, expectedVersion);
        if (previous is not null && (previous.DeckVersionId != draft.DeckVersionId || previous.Kind != draft.Kind ||
            previous.MatchId != draft.MatchId || previous.EventSequence != draft.EventSequence))
            throw new InvalidOperationException("已有条目的套牌、类型和对局绑定不可变，请新建条目。");
        if (previous is { IsDeleted: false } && DraftOf(previous) == draft) return previous;
        var version = new CoachKnowledgeVersion(entryId ?? Guid.NewGuid().ToString("N"), NextVersion(previous), draft.DeckVersionId,
            draft.Kind, draft.Kind == CoachKnowledgeKind.DeckPlan ? CoachKnowledgeSource.UserPlan : CoachKnowledgeSource.UserNote,
            draft.Title, draft.Content, draft.OpponentClass, draft.WentFirst, draft.MatchId,
            await RecordedAtAsync(connection, transaction, cancellationToken), false) { EventSequence = draft.EventSequence };
        await InsertAsync(connection, transaction, version, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return version;
    }

    public async Task<CoachKnowledgeVersion> DeleteAsync(string entryId, int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        CoachLocalDatabase.Identifier(entryId);
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        await using var connection = await _database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var previous = await LatestAsync(connection, transaction, entryId, cancellationToken)
            ?? throw new KeyNotFoundException("笔记条目不存在。");
        CheckExpected(previous, expectedVersion);
        if (previous.IsDeleted) return previous;
        var tombstone = previous with { Version = NextVersion(previous),
            RecordedAt = await RecordedAtAsync(connection, transaction, cancellationToken), IsDeleted = true };
        await InsertAsync(connection, transaction, tombstone, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return tombstone;
    }

    public async Task<CoachKnowledgeVersion?> GetAsOfAsync(string entryId, DateTimeOffset asOf, bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        CoachLocalDatabase.Identifier(entryId);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM coach_knowledge_versions WHERE entry_id=$id AND recorded_at<=$at ORDER BY version DESC LIMIT 1";
        CoachLocalDatabase.Add(command, "$id", entryId); CoachLocalDatabase.Add(command, "$at", CoachLocalDatabase.Timestamp(asOf));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        var version = json is null ? null : Read(json);
        return version is { IsDeleted: true } && !includeDeleted ? null : version;
    }

    public Task<IReadOnlyList<CoachKnowledgeVersion>> ListAsync(CoachKnowledgeFilter? filter = null,
        CancellationToken cancellationToken = default) => ListAsOfAsync(DateTimeOffset.MaxValue, filter, cancellationToken);

    public async Task<IReadOnlyList<CoachKnowledgeVersion>> ListAsOfAsync(DateTimeOffset asOf,
        CoachKnowledgeFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new();
        if (filter.Limit is < 1 or > 1000 || (filter.Kind.HasValue && !Enum.IsDefined(filter.Kind.Value)))
            throw new ArgumentException("笔记筛选参数无效。");
        if (filter.DeckVersionId is not null) CoachLocalDatabase.Identifier(filter.DeckVersionId);
        if (filter.MatchId is not null) CoachLocalDatabase.Identifier(filter.MatchId);
        var opponent = NormalizeClass(filter.OpponentClass);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var clauses = new List<string>
        { "v.version=(SELECT MAX(x.version) FROM coach_knowledge_versions x WHERE x.entry_id=v.entry_id AND x.recorded_at<=$at)" };
        CoachLocalDatabase.Add(command, "$at", CoachLocalDatabase.Timestamp(asOf));
        void Scope(string column, string parameter, object? value, bool general = false)
        {
            if (value is null) return;
            clauses.Add(general ? $"({column}={parameter} OR {column} IS NULL)" : $"{column}={parameter}");
            CoachLocalDatabase.Add(command, parameter, value);
        }
        Scope("v.deck_version_id", "$deck", filter.DeckVersionId);
        Scope("v.kind", "$kind", filter.Kind.HasValue ? (int)filter.Kind.Value : null);
        Scope("v.opponent_class", "$opponent", opponent, filter.IncludeGeneral);
        Scope("v.went_first", "$first", filter.WentFirst, filter.IncludeGeneral);
        Scope("v.match_id", "$match", filter.MatchId);
        if (!filter.IncludeDeleted) clauses.Add("v.is_deleted=0");
        command.CommandText = "SELECT v.payload_json FROM coach_knowledge_versions v WHERE " + string.Join(" AND ", clauses) + " ORDER BY v.recorded_at DESC,v.entry_id LIMIT $limit";
        CoachLocalDatabase.Add(command, "$limit", filter.Limit);
        var result = new List<CoachKnowledgeVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader.GetString(0)));
        return result.AsReadOnly();
    }

    private static CoachKnowledgeDraft Normalize(CoachKnowledgeDraft draft)
    {
        CoachLocalDatabase.Identifier(draft.DeckVersionId);
        if (draft.MatchId is not null) CoachLocalDatabase.Identifier(draft.MatchId);
        if (draft.EventSequence < 0 || (draft.EventSequence.HasValue && draft.MatchId is null))
            throw new ArgumentException("关键回合标记需要关联对局和非负事件序号。");
        if (!Enum.IsDefined(draft.Kind) || string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200 ||
            string.IsNullOrWhiteSpace(draft.Content) || draft.Content.Length > 16000 || draft.Title.Any(char.IsControl) || draft.Content.Contains('\0'))
            throw new ArgumentException("计划或笔记需要1至200字标题和不超过16000字正文。");
        return draft with { Title = draft.Title.Trim(), Content = draft.Content.Trim(), OpponentClass = NormalizeClass(draft.OpponentClass) };
    }
    private static string? NormalizeClass(string? value)
    {
        if (value is null) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized is not ("DEATHKNIGHT" or "DEMONHUNTER" or "DRUID" or "HUNTER" or "MAGE" or "PALADIN" or "PRIEST" or "ROGUE" or "SHAMAN" or "WARLOCK" or "WARRIOR"))
            throw new ArgumentException("对手职业需要标准职业标识，通用条件请使用空值。");
        return normalized;
    }
    private static void CheckExpected(CoachKnowledgeVersion? previous, int? expected)
    {
        if (expected.HasValue && expected.Value != (previous?.Version ?? 0))
            throw new InvalidOperationException("笔记已被其他操作修订，请重新加载后再保存。");
    }
    private async Task<DateTimeOffset> RecordedAtAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        return await CoachLocalDatabase.NextRecordedAtAsync(command, "coach_knowledge_versions", _clock, token);
    }
    private static int NextVersion(CoachKnowledgeVersion? previous)
        => previous?.Version == int.MaxValue ? throw new InvalidOperationException("笔记版本已达到上限。") : (previous?.Version ?? 0) + 1;
    private static CoachKnowledgeDraft DraftOf(CoachKnowledgeVersion version)
        => new(version.DeckVersionId, version.Kind, version.Title, version.Content, version.OpponentClass, version.WentFirst, version.MatchId)
        { EventSequence = version.EventSequence };
    private static async Task<CoachKnowledgeVersion?> LatestAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload_json FROM coach_knowledge_versions WHERE entry_id=$id ORDER BY version DESC LIMIT 1";
        CoachLocalDatabase.Add(command, "$id", id);
        return await command.ExecuteScalarAsync(token) is string json ? Read(json) : null;
    }
    private static CoachKnowledgeVersion Read(string json) => JsonSerializer.Deserialize<CoachKnowledgeVersion>(json)
        ?? throw new InvalidDataException("笔记版本记录无效。");
    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, CoachKnowledgeVersion value, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO coach_knowledge_versions VALUES($id,$version,$deck,$kind,$opponent,$first,$match,$at,$deleted,$json)";
        CoachLocalDatabase.Add(command, "$id", value.EntryId); CoachLocalDatabase.Add(command, "$version", value.Version);
        CoachLocalDatabase.Add(command, "$deck", value.DeckVersionId); CoachLocalDatabase.Add(command, "$kind", (int)value.Kind);
        CoachLocalDatabase.Add(command, "$opponent", value.OpponentClass); CoachLocalDatabase.Add(command, "$first", value.WentFirst);
        CoachLocalDatabase.Add(command, "$match", value.MatchId); CoachLocalDatabase.Add(command, "$at", CoachLocalDatabase.Timestamp(value.RecordedAt));
        CoachLocalDatabase.Add(command, "$deleted", value.IsDeleted); CoachLocalDatabase.Add(command, "$json", JsonSerializer.Serialize(value));
        await command.ExecuteNonQueryAsync(token);
    }
}

internal sealed class CoachLocalDatabase
{
    private readonly string _connectionString;
    public CoachLocalDatabase(string databasePath)
    {
        var path = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString();
    }
    public async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public static async Task CheckSchemaAsync(SqliteCommand command, string table, CancellationToken token)
    {
        command.CommandText = $"CREATE TABLE IF NOT EXISTS {table}(version INTEGER NOT NULL); INSERT INTO {table} SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM {table})";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = $"SELECT version FROM {table}";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("当前应用不支持此教练资料数据库版本。");
    }
    public static void Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$", RegexOptions.CultureInvariant) || value.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("教练资料需要匿名版本或对局标识。");
    }
    public static async Task<DateTimeOffset> NextRecordedAtAsync(SqliteCommand command, string table, TimeProvider clock, CancellationToken token)
    {
        command.CommandText = "SELECT MAX(recorded_at) FROM " + table;
        var now = clock.GetUtcNow();
        if (await command.ExecuteScalarAsync(token) is not string stored) return now;
        var last = DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture);
        return now <= last ? last.AddTicks(1) : now;
    }
    public static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    public static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
