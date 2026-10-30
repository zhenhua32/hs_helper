using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using Microsoft.Data.Sqlite;

namespace HsHelper.Storage;

/// <summary>Local immutable deck versions and perspective-filtered match history. No raw Power.log text is stored.</summary>
public sealed class SessionStore
{
    private static readonly HashSet<string> IdentityTags = new(StringComparer.OrdinalIgnoreCase)
    { "NAME", "PLAYER_NAME", "ENTITY_NAME", "ACCOUNT_ID", "GAME_ACCOUNT_ID", "PlayerName", "EntityName", "AccountId", "GameAccountId" };
    private readonly string _connectionString;
    public SessionStore(string databasePath)
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS hs_storage_schema(version INTEGER NOT NULL);
            INSERT INTO hs_storage_schema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM hs_storage_schema);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "SELECT version FROM hs_storage_schema";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version is < 1 or > 2)
            throw new InvalidDataException("当前应用不支持此历史数据库版本，请使用兼容版本。");
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS saved_deck_versions(
              id TEXT PRIMARY KEY, version_key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, code TEXT NOT NULL,
              card_build TEXT NOT NULL, created_at TEXT NOT NULL, previous_version_id TEXT,
              definition_json TEXT NOT NULL,
              FOREIGN KEY(previous_version_id) REFERENCES saved_deck_versions(id));
            CREATE TABLE IF NOT EXISTS game_sessions(
              id TEXT PRIMARY KEY, binding_hash TEXT NOT NULL, deck_version_id TEXT, card_build TEXT NOT NULL,
              local_player_id INTEGER, format TEXT NOT NULL, region TEXT NOT NULL,
              started_at TEXT NOT NULL, updated_at TEXT NOT NULL, ended_at TEXT,
              turn_count INTEGER, outcome TEXT, went_first INTEGER, player_class TEXT, opponent_class TEXT,
              is_complete INTEGER NOT NULL DEFAULT 0, last_event_seq INTEGER NOT NULL DEFAULT 0, game_type TEXT,
              FOREIGN KEY(deck_version_id) REFERENCES saved_deck_versions(id));
            CREATE TABLE IF NOT EXISTS session_events(
              session_id TEXT NOT NULL, event_seq INTEGER NOT NULL, event_index INTEGER NOT NULL,
              source_line INTEGER NOT NULL, kind TEXT NOT NULL, entity_id INTEGER, card_id TEXT,
              from_zone TEXT, to_zone TEXT, summary TEXT NOT NULL, data_json TEXT NOT NULL,
              PRIMARY KEY(session_id,event_seq,event_index),
              FOREIGN KEY(session_id) REFERENCES game_sessions(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS session_snapshots(
              id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, event_seq INTEGER NOT NULL,
              state_version INTEGER NOT NULL, turn INTEGER, phase TEXT NOT NULL, active_player_id INTEGER,
              is_stable INTEGER NOT NULL, is_complete INTEGER NOT NULL, is_decision INTEGER NOT NULL,
              recorded_at TEXT NOT NULL, snapshot_json BLOB NOT NULL,
              UNIQUE(session_id,event_seq,state_version),
              FOREIGN KEY(session_id) REFERENCES game_sessions(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS sessions_started ON game_sessions(started_at DESC);
            CREATE INDEX IF NOT EXISTS sessions_deck ON game_sessions(deck_version_id,card_build);
            CREATE INDEX IF NOT EXISTS snapshots_sequence ON session_snapshots(session_id,event_seq,state_version);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "PRAGMA table_info(game_sessions)";
        var hasGameType = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) hasGameType |= reader.GetString(1) == "game_type";
        if (!hasGameType)
        {
            command.CommandText = "ALTER TABLE game_sessions ADD COLUMN game_type TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        command.CommandText = "UPDATE hs_storage_schema SET version=2";
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<DeckVersion> SaveDeckVersionAsync(string name, DeckDefinition definition, string cardBuild,
        string? previousVersionId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new ArgumentException("套牌名称长度应为1至200个字符。", nameof(name));
        if (string.IsNullOrWhiteSpace(cardBuild)) throw new ArgumentException("必须保存卡库版本或明确的 unknown 标识。", nameof(cardBuild));
        var code = DeckCode.Encode(definition);
        var canonical = DeckCode.Decode(code);
        var key = Hash(JsonSerializer.Serialize(new { Name = name.Trim(), Code = code, CardBuild = cardBuild, Previous = previousVersionId }));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,code,card_build,created_at,previous_version_id,definition_json FROM saved_deck_versions WHERE version_key=$key";
        command.Parameters.AddWithValue("$key", key);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) return ReadDeck(reader);
        var version = new DeckVersion(Guid.NewGuid().ToString("N"), name.Trim(), code, cardBuild,
            DateTimeOffset.UtcNow, previousVersionId, canonical);
        command.CommandText = "INSERT INTO saved_deck_versions VALUES($id,$key,$name,$code,$build,$created,$previous,$json)";
        Add(command, "$id", version.Id); Add(command, "$name", version.Name); Add(command, "$code", code);
        Add(command, "$build", cardBuild); Add(command, "$created", Timestamp(version.CreatedAt));
        Add(command, "$previous", previousVersionId); Add(command, "$json", JsonSerializer.Serialize(canonical));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return version;
    }

    public async Task<IReadOnlyList<DeckVersion>> ListDeckVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,code,card_build,created_at,previous_version_id,definition_json FROM saved_deck_versions ORDER BY created_at DESC,id";
        var result = new List<DeckVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDeck(reader));
        return result.AsReadOnly();
    }

    public async Task<DeckVersion?> GetDeckVersionAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,code,card_build,created_at,previous_version_id,definition_json FROM saved_deck_versions WHERE id=$id";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDeck(reader) : null;
    }

    public async Task<GameSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + " WHERE g.id=$id";
        Add(command, "$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<GameSession>> ListSessionsAsync(SessionFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new();
        if (filter.Limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(filter));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + FilterSql(command, filter) + " ORDER BY g.started_at DESC,g.id LIMIT $limit";
        Add(command, "$limit", filter.Limit);
        var result = new List<GameSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSession(reader));
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<DecisionPoint>> ListDecisionPointsAsync(string sessionId, bool includeUnstable = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,event_seq,state_version,turn,phase,active_player_id,is_stable,is_complete,is_decision,recorded_at FROM session_snapshots WHERE session_id=$id" +
            (includeUnstable ? "" : " AND is_decision=1") + " ORDER BY event_seq,state_version";
        Add(command, "$id", sessionId);
        var result = new List<DecisionPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            NullableInt(reader, 4), Enum.Parse<GamePhase>(reader.GetString(5)), NullableInt(reader, 6), reader.GetBoolean(7), reader.GetBoolean(8),
            reader.GetBoolean(9), DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        return result.AsReadOnly();
    }

    public async Task<GameSnapshot?> GetSnapshotAtAsync(string sessionId, long eventSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eventSequence);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM session_snapshots WHERE session_id=$id AND event_seq<=$seq ORDER BY event_seq DESC,state_version DESC LIMIT 1";
        Add(command, "$id", sessionId); Add(command, "$seq", eventSequence);
        var data = await command.ExecuteScalarAsync(cancellationToken);
        return data is null or DBNull ? null : SnapshotCodec.Deserialize(Unpack(data));
    }

    public async Task<GameSnapshot?> GetDecisionSnapshotAsync(long decisionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM session_snapshots WHERE id=$id";
        Add(command, "$id", decisionId);
        var data = await command.ExecuteScalarAsync(cancellationToken);
        return data is null or DBNull ? null : SnapshotCodec.Deserialize(Unpack(data));
    }

    /// <summary>Only fixed binding data, an as-of snapshot and past events enter this context; final results are excluded.</summary>
    public async Task<HistoricalContext?> GetHistoricalContextAtAsync(string sessionId, long eventSequence,
        int recentEventLimit = 100, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        if (session is null) return null;
        var snapshot = await GetSnapshotAtAsync(sessionId, eventSequence, cancellationToken);
        if (snapshot is null) return null;
        var deck = session.DeckVersionId is { } version ? await GetDeckVersionAsync(version, cancellationToken) : null;
        return new(sessionId, session.CardBuild, session.DeckVersionId, deck, snapshot,
            await GetEventsAtAsync(sessionId, snapshot.EventSequence, recentEventLimit, cancellationToken));
    }

    public async Task<IReadOnlyList<SessionEvent>> GetEventsAtAsync(string sessionId, long eventSequence, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (eventSequence < 0 || limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(eventSequence));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_seq,event_index,source_line,kind,entity_id,card_id,from_zone,to_zone,summary,data_json FROM session_events WHERE session_id=$id AND event_seq<=$seq ORDER BY event_seq DESC,event_index DESC LIMIT $limit";
        Add(command, "$id", sessionId); Add(command, "$seq", eventSequence); Add(command, "$limit", limit);
        var result = new List<SessionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3),
            NullableInt(reader, 4), NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), reader.GetString(8), reader.GetString(9)));
        result.Reverse();
        return result.AsReadOnly();
    }

    public async Task<PersonalStats> GetStatsAsync(SessionFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(g.opponent_class,'Unknown'),g.outcome,COUNT(*) FROM game_sessions g" + FilterSql(command, filter) +
            " GROUP BY COALESCE(g.opponent_class,'Unknown'),g.outcome";
        var groups = new Dictionary<string, int[]>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            if (!groups.TryGetValue(key, out var counts)) groups[key] = counts = new int[4];
            var outcome = NullableString(reader, 1);
            var index = outcome switch { "Won" => 0, "Lost" or "Conceded" => 1, "Tied" => 2, _ => 3 };
            counts[index] += reader.GetInt32(2);
        }
        return BuildStats(groups);
    }

    /// <summary>
    /// Aggregates only the latest state actually recorded by the cutoff, including only then-complete matches.
    /// Dynamic filters use the historical frame, never final session metadata. Limit does not truncate statistics;
    /// more than 10,000 candidate matches produces an explicit error so callers can narrow the date/build range.
    /// </summary>
    public async Task<PersonalStats> GetStatsAsOfAsync(DateTimeOffset asOf, SessionFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new();
        const int maximumCandidates = 10000;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var clauses = new List<string>();
        void Fixed(string column, string parameter, object? value, string comparison = "=")
        {
            if (value is null) return;
            clauses.Add(column + comparison + parameter + (column == "g.region" ? " COLLATE NOCASE" : ""));
            Add(command, parameter, value);
        }
        Fixed("g.card_build", "$build", filter.CardBuild); Fixed("g.deck_version_id", "$deck", filter.DeckVersionId);
        Fixed("g.region", "$region", filter.Region); Fixed("g.id", "$excluded", filter.ExcludeSessionId, "<>");
        Fixed("g.started_at", "$since", filter.Since.HasValue ? Timestamp(filter.Since.Value) : null, ">=");
        Fixed("g.started_at", "$until", filter.Until.HasValue ? Timestamp(filter.Until.Value) : null, "<=");
        if (filter.EndedBefore is { } ended)
        {
            clauses.Add("EXISTS(SELECT 1 FROM session_snapshots e WHERE e.session_id=g.id AND e.phase='Complete' AND e.recorded_at<=$ended AND e.recorded_at<=$asOf)");
            Add(command, "$ended", Timestamp(ended));
        }
        command.CommandText = """
            SELECT s.snapshot_json FROM game_sessions g JOIN session_snapshots s ON s.id=(
              SELECT x.id FROM session_snapshots x WHERE x.session_id=g.id AND x.recorded_at<=$asOf
              ORDER BY x.event_seq DESC,x.state_version DESC,x.id DESC LIMIT 1)
            """ + (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses)) +
            " ORDER BY g.id LIMIT $maximum";
        Add(command, "$asOf", Timestamp(asOf)); Add(command, "$maximum", maximumCandidates + 1);
        var candidates = 0;
        var groups = new Dictionary<string, int[]>(StringComparer.Ordinal);
        static bool Matches(string? value, string? expected) => expected is null || string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (++candidates > maximumCandidates)
                throw new InvalidDataException("历史统计超过10000局候选范围，未返回截断结果；请缩小时间、套牌或卡库版本范围。");
            var snapshot = SnapshotCodec.Deserialize(Unpack(reader.GetValue(0)));
            if (snapshot.Phase != GamePhase.Complete || !Matches(snapshot.Format, filter.Format) ||
                !Matches(snapshot.GameType, filter.GameType) || !Matches(snapshot.LocalClass, filter.PlayerClass) ||
                !Matches(snapshot.OpponentClass ?? "Unknown", filter.OpponentClass)) continue;
            bool? first = snapshot.FirstPlayerId is { } firstPlayer && snapshot.LocalPlayerId is { } local ? firstPlayer == local : null;
            if (filter.WentFirst.HasValue && filter.WentFirst != first) continue;
            var key = snapshot.OpponentClass ?? "Unknown";
            if (!groups.TryGetValue(key, out var counts)) groups[key] = counts = new int[4];
            var outcome = snapshot.LocalOutcome switch { "Won" => 0, "Lost" or "Conceded" => 1, "Tied" => 2, _ => 3 };
            counts[outcome]++;
        }
        return BuildStats(groups);
    }

    private static PersonalStats BuildStats(Dictionary<string, int[]> groups)
    {
        var matchups = groups.OrderBy(g => g.Key).Select(g => new ClassMatchupStats(g.Key, g.Value.Sum(),
            g.Value[0], g.Value[1], g.Value[2], g.Value[3], Rate(g.Value[0], g.Value[1]))).ToArray();
        var wins = matchups.Sum(g => g.Wins); var losses = matchups.Sum(g => g.Losses);
        return new(matchups.Sum(g => g.Games), wins, losses, matchups.Sum(g => g.Ties), matchups.Sum(g => g.Unknown),
            Rate(wins, losses), Array.AsReadOnly(matchups));
    }

    public async Task<CleanupResult> DeleteBeforeAsync(DateTimeOffset cutoff, bool includeUnfinished = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        var clause = "started_at<$cutoff" + (includeUnfinished ? "" : " AND ended_at IS NOT NULL");
        Add(command, "$cutoff", Timestamp(cutoff));
        async Task<int> Count(string table)
        {
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE session_id IN(SELECT id FROM game_sessions WHERE {clause})";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        var events = await Count("session_events"); var snapshots = await Count("session_snapshots");
        command.CommandText = "DELETE FROM game_sessions WHERE " + clause;
        var sessions = await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return new(sessions, events, snapshots);
    }

    internal async Task SaveBatchAsync(SessionBinding binding, IReadOnlyList<PendingObservation> observations, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        var bindingHash = Hash(JsonSerializer.Serialize(new { binding.DeckVersionId, binding.CardBuild,
            binding.LocalPlayerId, binding.Format, binding.Region }));
        var initialized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = observation.Snapshot;
            ValidatePerspective(snapshot, binding, inspectEntities: false);
            if (initialized.Add(snapshot.MatchId))
                await EnsureSessionAsync(connection, transaction, binding, bindingHash, snapshot, observation.ObservedAt, token);
            foreach (var entry in observation.Events)
                await InsertEventAsync(connection, transaction, snapshot.MatchId, entry, token);
            if (observation.SaveSnapshot)
                await InsertSnapshotAsync(connection, transaction, observation, token);
        }
        foreach (var last in observations.GroupBy(o => o.Snapshot.MatchId, StringComparer.Ordinal).Select(group => group.Last()))
            await UpdateMetadataAsync(connection, transaction, last.Snapshot, binding, last.ObservedAt, token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    internal static void ValidateBinding(SessionBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.CardBuild) || string.IsNullOrWhiteSpace(binding.Format) || string.IsNullOrWhiteSpace(binding.Region))
            throw new ArgumentException("记录需要明确的卡库版本、模式和地区；未知版本请使用 unknown。");
        if (binding.LocalPlayerId is not (null or 1 or 2)) throw new ArgumentException("我方玩家ID应为1、2或未知。");
    }

    internal static void ValidatePerspective(GameSnapshot snapshot, SessionBinding binding, bool inspectEntities = true)
    {
        if (string.IsNullOrWhiteSpace(snapshot.MatchId) || snapshot.EventSequence < 0 || snapshot.StateVersion < 0)
            throw new ArgumentException("快照缺少有效对局标识或序号。");
        if (snapshot.LocalPlayerId is not null && snapshot.LocalPlayerId != binding.LocalPlayerId)
            throw new InvalidOperationException("快照视角与固定会话视角不一致，请重新建立记录器。");
        if (snapshot.ChoiceHistory is { } history)
            foreach (var entry in history.Entries)
            {
                if (entry is null || entry.OptionEntities.Any(entity => entity is null || entity.Tags is null))
                    throw new InvalidDataException("历史选择的实体证据不完整。");
                foreach (var entity in entry.OptionEntities) ValidateEntity(entity);
            }
        if (inspectEntities) foreach (var entity in snapshot.Entities.Values) ValidateEntity(entity);
    }

    internal static void ValidateEntity(EntitySnapshot entity)
    {
        if (entity.Tags.Keys.Any(IdentityTags.Contains))
            throw new InvalidDataException("历史快照不能包含玩家名称或账号字段。");
    }

    private static async Task EnsureSessionAsync(SqliteConnection connection, SqliteTransaction transaction,
        SessionBinding binding, string bindingHash, GameSnapshot snapshot, DateTimeOffset observedAt, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT binding_hash FROM game_sessions WHERE id=$id";
        Add(command, "$id", snapshot.MatchId);
        if (await command.ExecuteScalarAsync(token) is string existing)
        {
            if (existing != bindingHash) throw new InvalidOperationException("同一历史对局不能改变套牌、卡库版本或玩家视角。");
            return;
        }
        command.CommandText = """
            INSERT INTO game_sessions(id,binding_hash,deck_version_id,card_build,local_player_id,format,region,started_at,updated_at,player_class)
            VALUES($id,$hash,$deck,$build,$player,$format,$region,$time,$time,$class)
            """;
        Add(command, "$hash", bindingHash); Add(command, "$deck", binding.DeckVersionId); Add(command, "$build", binding.CardBuild);
        Add(command, "$player", binding.LocalPlayerId); Add(command, "$format", binding.Format); Add(command, "$region", binding.Region);
        Add(command, "$time", Timestamp(observedAt)); Add(command, "$class", binding.PlayerClass);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        SessionEvent entry, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO session_events VALUES($id,$seq,$index,$line,$kind,$entity,$card,$from,$to,$summary,$data) ON CONFLICT(session_id,event_seq,event_index) DO NOTHING";
        Add(command, "$id", sessionId); Add(command, "$seq", entry.EventSequence); Add(command, "$index", entry.EventIndex);
        Add(command, "$line", entry.SourceLineNumber); Add(command, "$kind", entry.Kind); Add(command, "$entity", entry.EntityId);
        Add(command, "$card", entry.CardId); Add(command, "$from", entry.FromZone); Add(command, "$to", entry.ToZone);
        Add(command, "$summary", entry.Summary); Add(command, "$data", entry.DataJson);
        if (await command.ExecuteNonQueryAsync(token) != 0) return;
        command.CommandText = "SELECT kind,data_json FROM session_events WHERE session_id=$id AND event_seq=$seq AND event_index=$index";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(0) != entry.Kind || reader.GetString(1) != entry.DataJson)
            throw new InvalidDataException("相同事件序号的历史数据不一致，已拒绝覆盖。");
    }

    private static async Task InsertSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction,
        PendingObservation observation, CancellationToken token)
    {
        var snapshot = observation.Snapshot;
        var json = SnapshotCodec.Serialize(snapshot);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_snapshots(session_id,event_seq,state_version,turn,phase,active_player_id,is_stable,is_complete,is_decision,recorded_at,snapshot_json)
            VALUES($id,$seq,$version,$turn,$phase,$active,$stable,$complete,$decision,$time,$json)
            ON CONFLICT(session_id,event_seq,state_version) DO NOTHING
            """;
        Add(command, "$id", snapshot.MatchId); Add(command, "$seq", snapshot.EventSequence); Add(command, "$version", snapshot.StateVersion);
        Add(command, "$turn", snapshot.Turn); Add(command, "$phase", snapshot.Phase.ToString()); Add(command, "$active", snapshot.ActivePlayerId);
        Add(command, "$stable", snapshot.Quality.IsStable); Add(command, "$complete", snapshot.Quality.IsComplete);
        Add(command, "$decision", snapshot.Quality.IsStable || snapshot.Phase == GamePhase.Complete);
        Add(command, "$time", Timestamp(observation.ObservedAt)); Add(command, "$json", Pack(json));
        if (await command.ExecuteNonQueryAsync(token) != 0) return;
        command.CommandText = "SELECT snapshot_json FROM session_snapshots WHERE session_id=$id AND event_seq=$seq AND state_version=$version";
        var existing = await command.ExecuteScalarAsync(token);
        if (existing is null or DBNull || Unpack(existing) != json)
            throw new InvalidDataException("相同时间点的快照不一致，已拒绝覆盖。");
    }

    private static async Task UpdateMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        GameSnapshot snapshot, SessionBinding binding, DateTimeOffset observedAt, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE game_sessions SET updated_at=$time,ended_at=COALESCE(ended_at,$ended),
              turn_count=COALESCE($turn,turn_count),outcome=COALESCE($outcome,outcome),
              went_first=COALESCE($first,went_first),player_class=COALESCE($class,player_class),
              opponent_class=COALESCE($opponent,opponent_class),game_type=COALESCE($gameType,game_type),
              is_complete=$complete,last_event_seq=$seq
            WHERE id=$id AND last_event_seq<=$seq
            """;
        Add(command, "$id", snapshot.MatchId); Add(command, "$time", Timestamp(observedAt));
        Add(command, "$ended", snapshot.Phase == GamePhase.Complete ? Timestamp(observedAt) : null);
        Add(command, "$turn", snapshot.Turn); Add(command, "$outcome", snapshot.LocalOutcome);
        Add(command, "$first", snapshot.FirstPlayerId is { } first && binding.LocalPlayerId is { } local ? first == local : null);
        Add(command, "$class", snapshot.LocalClass ?? binding.PlayerClass); Add(command, "$opponent", snapshot.OpponentClass);
        Add(command, "$gameType", snapshot.GameType);
        Add(command, "$complete", snapshot.Quality.IsComplete); Add(command, "$seq", snapshot.EventSequence);
        await command.ExecuteNonQueryAsync(token);
    }

    private const string SessionSelect = """
        SELECT g.id,g.deck_version_id,g.card_build,g.local_player_id,g.format,g.region,g.started_at,g.updated_at,g.ended_at,
        g.turn_count,g.outcome,g.went_first,g.player_class,g.opponent_class,g.is_complete,g.last_event_seq,
        (SELECT COUNT(*) FROM session_snapshots s WHERE s.session_id=g.id),g.game_type FROM game_sessions g
        """;

    private static string FilterSql(SqliteCommand command, SessionFilter filter)
    {
        var clauses = new List<string>();
        void Filter(string column, string name, object? value)
        {
            if (value is null) return;
            var comparison = column is "g.format" or "g.opponent_class" or "g.player_class" or "g.region" or "g.game_type"
                ? " COLLATE NOCASE" : "";
            clauses.Add(column + "=" + name + comparison); Add(command, name, value);
        }
        Filter("g.card_build", "$build", filter.CardBuild); Filter("g.deck_version_id", "$deck", filter.DeckVersionId);
        Filter("g.opponent_class", "$class", filter.OpponentClass); Filter("g.went_first", "$first", filter.WentFirst);
        Filter("g.format", "$format", filter.Format);
        Filter("g.player_class", "$playerClass", filter.PlayerClass); Filter("g.region", "$region", filter.Region);
        Filter("g.game_type", "$gameType", filter.GameType);
        if (filter.Since is { } since) { clauses.Add("g.started_at>=$since"); Add(command, "$since", Timestamp(since)); }
        if (filter.Until is { } until) { clauses.Add("g.started_at<=$until"); Add(command, "$until", Timestamp(until)); }
        if (filter.EndedBefore is { } endedBefore)
        { clauses.Add("g.ended_at IS NOT NULL AND g.ended_at<=$endedBefore"); Add(command, "$endedBefore", Timestamp(endedBefore)); }
        if (filter.ExcludeSessionId is { } excluded)
        { clauses.Add("g.id<>$excludedSession"); Add(command, "$excludedSession", excluded); }
        return clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
    }

    private static DeckVersion ReadDeck(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), NullableString(reader, 5),
        JsonSerializer.Deserialize<DeckDefinition>(reader.GetString(6)) ?? throw new InvalidDataException("套牌记录无效。"));
    private static GameSession ReadSession(SqliteDataReader reader) => new(reader.GetString(0), NullableString(reader, 1), reader.GetString(2),
        NullableInt(reader, 3), reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        NullableInt(reader, 9), NullableString(reader, 10), reader.IsDBNull(11) ? null : reader.GetBoolean(11), NullableString(reader, 12),
        NullableString(reader, 13), reader.GetBoolean(14), reader.GetInt64(15), reader.GetInt32(16), NullableString(reader, 17));
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        return connection;
    }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static double? Rate(int wins, int losses) => wins + losses == 0 ? null : (double)wins / (wins + losses);
    private static byte[] Pack(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("单个局面快照超过32 MiB。");
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private static string Unpack(object data)
    {
        if (data is string json) return json; // Reads early uncompressed development databases as well.
        if (data is not byte[] bytes) throw new InvalidDataException("快照存储格式无效。");
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > 32 * 1024 * 1024) throw new InvalidDataException("快照解压超过允许大小。");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
