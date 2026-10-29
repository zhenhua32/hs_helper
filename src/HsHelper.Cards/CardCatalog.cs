using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HsHelper.Cards;

/// <summary>Version-pinned catalog. Imports and the active pointer commit in one transaction.</summary>
public sealed partial class CardCatalog
{
    private const int MaximumBytes = 100 * 1024 * 1024;
    private readonly string _connectionString;

    public CardCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false, ForeignKeys = true }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 2)
                throw new InvalidDataException("卡库由较新版本创建，当前程序不能迁移该数据库。");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS card_manifests (
              build TEXT NOT NULL, locale TEXT NOT NULL, source TEXT NOT NULL,
              imported_at TEXT NOT NULL, sha256 TEXT NOT NULL, card_count INTEGER NOT NULL,
              PRIMARY KEY(build,locale));
            CREATE TABLE IF NOT EXISTS catalog_state (
              locale TEXT PRIMARY KEY, active_build TEXT NOT NULL,
              FOREIGN KEY(active_build,locale) REFERENCES card_manifests(build,locale));
            CREATE TABLE IF NOT EXISTS cards (
              build TEXT NOT NULL, locale TEXT NOT NULL, card_id TEXT NOT NULL,
              dbf_id INTEGER, name TEXT NOT NULL, text TEXT NOT NULL, card_class TEXT NOT NULL,
              card_set TEXT NOT NULL, type TEXT NOT NULL, cost INTEGER, collectible INTEGER NOT NULL,
              raw_json TEXT NOT NULL,
              PRIMARY KEY(build,locale,card_id),
              FOREIGN KEY(build,locale) REFERENCES card_manifests(build,locale));
            CREATE INDEX IF NOT EXISTS cards_dbf ON cards(build,locale,dbf_id);
            CREATE INDEX IF NOT EXISTS cards_class ON cards(build,locale,card_class);
            CREATE TABLE IF NOT EXISTS card_pool_rules (
              rule_id TEXT PRIMARY KEY, build TEXT NOT NULL, format INTEGER NOT NULL,
              effective_from INTEGER NOT NULL, valid_until INTEGER NOT NULL,
              imported_at INTEGER NOT NULL, source TEXT NOT NULL, sha256 TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS pool_rules_version ON card_pool_rules(build,format,effective_from,imported_at);
            PRAGMA user_version=2;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CardManifest> ImportAsync(Stream json, string build, string locale = "zhCN",
        string source = "local", CancellationToken cancellationToken = default)
    {
        ValidateBuild(build, allowLatest: false);
        ValidateLocale(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await json.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("卡牌数据超过 100 MiB 限制。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("卡牌数据根节点必须为数组。");
        var cards = new List<CardRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var card = ParseCard(item);
            if (!ids.Add(card.Id)) throw new InvalidDataException($"卡牌标识重复：{card.Id}");
            cards.Add(card);
        }
        if (cards.Count == 0) throw new InvalidDataException("空卡库不能替换活动版本。");

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new CardManifest(build, locale, source, DateTimeOffset.UtcNow, hash, cards.Count);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // A build/locale is immutable: a changed payload must use a different build label.
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT sha256 FROM card_manifests WHERE build=$build AND locale=$locale";
            existing.Parameters.AddWithValue("$build", build);
            existing.Parameters.AddWithValue("$locale", locale);
            var existingHash = await existing.ExecuteScalarAsync(cancellationToken) as string;
            if (existingHash is not null && existingHash != hash)
                throw new InvalidDataException("该构建版本已存在不同数据，请使用新的版本标识以保留历史。");
            if (existingHash is not null)
            {
                await SetActiveAsync(connection, transaction, build, locale, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (await GetManifestAsync(build, locale, cancellationToken))!;
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO card_manifests VALUES ($build,$locale,$source,$time,$hash,$count)";
            insert.Parameters.AddWithValue("$build", build);
            insert.Parameters.AddWithValue("$locale", locale);
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$time", manifest.ImportedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$count", cards.Count);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cards VALUES ($build,$locale,$id,$dbf,$name,$text,$class,$set,$type,$cost,$collectible,$raw)
                """;
            foreach (var name in new[] { "$build", "$locale", "$id", "$dbf", "$name", "$text", "$class", "$set", "$type", "$cost", "$collectible", "$raw" })
                insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
            foreach (var card in cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                insert.Parameters["$build"].Value = build;
                insert.Parameters["$locale"].Value = locale;
                insert.Parameters["$id"].Value = card.Id;
                insert.Parameters["$dbf"].Value = (object?)card.DbfId ?? DBNull.Value;
                insert.Parameters["$name"].Value = card.Name;
                insert.Parameters["$text"].Value = card.Text;
                insert.Parameters["$class"].Value = card.CardClass;
                insert.Parameters["$set"].Value = card.Set;
                insert.Parameters["$type"].Value = card.Type;
                insert.Parameters["$cost"].Value = (object?)card.Cost ?? DBNull.Value;
                insert.Parameters["$collectible"].Value = card.Collectible;
                insert.Parameters["$raw"].Value = card.RawJson;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await SetActiveAsync(connection, transaction, build, locale, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return manifest;
    }

    public async Task<CardManifest> SyncAsync(HttpClient client, string build = "latest", string locale = "zhCN",
        CancellationToken cancellationToken = default)
    {
        ValidateBuild(build, allowLatest: true);
        ValidateLocale(locale);
        if (build == "latest") build = await ResolveLatestBuildAsync(client, cancellationToken);
        var request = new Uri($"https://api.hearthstonejson.com/v1/{build}/{locale}/cards.json");
        using var response = await client.GetAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("远程卡库超过大小限制。");
        var resolved = response.RequestMessage?.RequestUri ?? request;
        if (resolved.Scheme != "https" || resolved.Host != "api.hearthstonejson.com")
            throw new InvalidDataException("卡库重定向目标不是预期的 HTTPS 数据源。");
        var segments = resolved.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 || segments[0] != "v1" || segments[1] != build || segments[2] != locale || segments[3] != "cards.json")
            throw new InvalidDataException("无法识别卡库响应的版本路径。");
        // latest must resolve to a concrete build so replays never silently change version.
        var concreteBuild = segments[1];
        ValidateBuild(concreteBuild, allowLatest: false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ImportAsync(stream, concreteBuild, locale, resolved.ToString(), cancellationToken);
    }

    public async Task<CardManifest?> GetActiveManifestAsync(string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        ValidateLocale(locale);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT active_build FROM catalog_state WHERE locale=$locale";
        command.Parameters.AddWithValue("$locale", locale);
        var build = await command.ExecuteScalarAsync(cancellationToken) as string;
        return build is null ? null : await GetManifestAsync(build, locale, cancellationToken);
    }

    public async Task<CardManifest?> GetManifestAsync(string build, string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,imported_at,sha256,card_count FROM card_manifests WHERE build=$build AND locale=$locale";
        command.Parameters.AddWithValue("$build", build);
        command.Parameters.AddWithValue("$locale", locale);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CardManifest(build, locale, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2), reader.GetInt32(3))
            : null;
    }

    public async Task<IReadOnlyList<CardRecord>> SearchAsync(CardSearchQuery query, string? build = null,
        string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 1000 || query.Offset < 0) throw new ArgumentOutOfRangeException(nameof(query));
        build ??= (await GetActiveManifestAsync(locale, cancellationToken))?.Build;
        if (build is null) return Array.Empty<CardRecord>();
        var poolRules = await GetPoolRulesAsync(build, 2, query.At, cancellationToken);
        if ((query.StandardOnly || query.StandardPoolOnly) && poolRules is null) return Array.Empty<CardRecord>();
        if (query.StandardOnly && poolRules?.IsComplete != true) return Array.Empty<CardRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string> { "build=$build", "locale=$locale" };
        command.Parameters.AddWithValue("$build", build);
        command.Parameters.AddWithValue("$locale", locale);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            conditions.Add("(instr(name,$text)>0 OR instr(text,$text)>0 OR instr(card_id,$text)>0)");
            command.Parameters.AddWithValue("$text", query.Text.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.CardClass))
        {
            // Shared-class cards may have NEUTRAL as cardClass; include their classes array.
            conditions.Add("(card_class=$class OR EXISTS(SELECT 1 FROM json_each(cards.raw_json,'$.classes') WHERE value=$class))");
            command.Parameters.AddWithValue("$class", query.CardClass.ToUpperInvariant());
        }
        if (query.Collectible is not null)
        {
            conditions.Add("collectible=$collectible");
            command.Parameters.AddWithValue("$collectible", query.Collectible.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            conditions.Add("(EXISTS(SELECT 1 FROM json_each(cards.raw_json,'$.mechanics') WHERE upper(value)=$keyword) OR EXISTS(SELECT 1 FROM json_each(cards.raw_json,'$.referencedTags') WHERE upper(value)=$keyword))");
            command.Parameters.AddWithValue("$keyword", query.Keyword.ToUpperInvariant());
        }
        if (query.StandardOnly || query.StandardPoolOnly)
        {
            conditions.Add("collectible=1 AND (card_set IN (SELECT value FROM json_each($sets)) OR card_id IN (SELECT value FROM json_each($allowed))) AND card_id NOT IN (SELECT value FROM json_each($banned)) AND card_id NOT IN (SELECT value FROM json_each($unreleased))");
            command.Parameters.AddWithValue("$sets", JsonSerializer.Serialize(poolRules!.AllowedSets));
            command.Parameters.AddWithValue("$allowed", JsonSerializer.Serialize(poolRules.AllowedCardIds));
            command.Parameters.AddWithValue("$banned", JsonSerializer.Serialize(poolRules.BannedCardIds));
            command.Parameters.AddWithValue("$unreleased", JsonSerializer.Serialize(poolRules.UnreleasedCardIds));
        }
        command.CommandText = $"SELECT raw_json FROM cards WHERE {string.Join(" AND ", conditions)} ORDER BY cost,card_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var result = new List<CardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            var card = ParseCard(document.RootElement);
            result.Add(card with { StandardLegality = EvaluateLegality(card, poolRules) });
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<CardRecord>> GetByIdsAsync(IEnumerable<string> cardIds, string? build = null,
        string locale = "zhCN", CancellationToken cancellationToken = default)
        => await GetByKeysAsync("card_id", cardIds.Distinct(StringComparer.Ordinal).Cast<object>().ToArray(), build, locale, cancellationToken);

    public async Task<IReadOnlyList<CardRecord>> GetByDbfIdsAsync(IEnumerable<int> dbfIds, string? build = null,
        string locale = "zhCN", CancellationToken cancellationToken = default)
        => await GetByKeysAsync("dbf_id", dbfIds.Distinct().Cast<object>().ToArray(), build, locale, cancellationToken);

    private async Task<IReadOnlyList<CardRecord>> GetByKeysAsync(string column, object[] ids, string? build,
        string locale, CancellationToken cancellationToken)
    {
        if (ids.Length > 500) throw new ArgumentOutOfRangeException(nameof(ids));
        if (ids.Length == 0) return Array.Empty<CardRecord>();
        build ??= (await GetActiveManifestAsync(locale, cancellationToken))?.Build;
        if (build is null) return Array.Empty<CardRecord>();
        var poolRules = await GetPoolRulesAsync(build, 2, cancellationToken: cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var names = ids.Select((_, i) => $"$id{i}").ToArray();
        command.CommandText = $"SELECT raw_json FROM cards WHERE build=$build AND locale=$locale AND {column} IN ({string.Join(',', names)}) ORDER BY card_id";
        command.Parameters.AddWithValue("$build", build);
        command.Parameters.AddWithValue("$locale", locale);
        for (var i = 0; i < ids.Length; i++) command.Parameters.AddWithValue(names[i], ids[i]);
        var result = new List<CardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            var card = ParseCard(document.RootElement);
            result.Add(card with { StandardLegality = EvaluateLegality(card, poolRules) });
        }
        return result.AsReadOnly();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task SetActiveAsync(SqliteConnection connection, SqliteTransaction transaction,
        string build, string locale, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO catalog_state VALUES ($locale,$build) ON CONFLICT(locale) DO UPDATE SET active_build=excluded.active_build";
        command.Parameters.AddWithValue("$locale", locale);
        command.Parameters.AddWithValue("$build", build);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateBuild(string build, bool allowLatest)
    {
        if (allowLatest && build == "latest") return;
        if (string.IsNullOrEmpty(build) || !Regex.IsMatch(build, "^[0-9]{1,12}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("构建版本必须为数字；latest 只能用于远程同步。", nameof(build));
    }

    private static async Task<string> ResolveLatestBuildAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("https://api.hearthstonejson.com/v1/latest/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var uri = response.RequestMessage?.RequestUri;
        if (uri is null || uri.Scheme != "https" || uri.Host != "api.hearthstonejson.com")
            throw new InvalidDataException("无法验证 latest 目录的数据来源。");
        var redirect = Regex.Match(uri.AbsolutePath, "^/v1/([0-9]{1,12})/?$", RegexOptions.CultureInvariant);
        if (redirect.Success) return redirect.Groups[1].Value;
        // The current static-hosted index uses build-pinned links rather than an HTTP redirect.
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > 100 * 1024) throw new InvalidDataException("latest 目录索引超出大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        var html = Encoding.UTF8.GetString(output.ToArray());
        var builds = Regex.Matches(html, "href=[\"']/v1/([0-9]{1,12})(?:/|[\"'])", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (builds.Length != 1) throw new InvalidDataException("latest 索引未包含唯一的固定构建版本，请指定数字 build。");
        return builds[0];
    }

    private static void ValidateLocale(string locale)
    {
        if (string.IsNullOrEmpty(locale) || !Regex.IsMatch(locale, "^[a-z]{2}[A-Z]{2}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("语言标识应类似 zhCN。", nameof(locale));
    }

    private static CardRecord ParseCard(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("卡牌条目必须为对象。");
        string Required(string name) => item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()! : throw new InvalidDataException($"卡牌缺少有效字段 {name}。");
        string Optional(string name) => item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
        int? Number(string name)
        {
            if (!item.TryGetProperty(name, out var field)) return null;
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var value)) throw new InvalidDataException($"字段 {name} 必须为整数。");
            return value;
        }
        IReadOnlyList<string> Strings(string name)
        {
            if (!item.TryGetProperty(name, out var field)) return Array.Empty<string>();
            if (field.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"字段 {name} 必须为数组。");
            return Array.AsReadOnly(field.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new InvalidDataException($"字段 {name} 只能包含字符串。")).ToArray());
        }
        var collectible = false;
        if (item.TryGetProperty("collectible", out var collectibleField))
        {
            if (collectibleField.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("collectible 必须为布尔值。");
            collectible = collectibleField.GetBoolean();
        }
        var dbf = Number("dbfId");
        if (dbf is null or <= 0) throw new InvalidDataException("dbfId 必须存在且为正数。");
        var id = Required("id");
        // Hidden/internal non-collectible entries can omit localized display text.
        var name = collectible ? Required("name") : Optional("name");
        return new CardRecord(id, dbf, string.IsNullOrEmpty(name) ? id : name, Optional("text"), Optional("cardClass"),
            Optional("set"), collectible ? Required("type") : Optional("type"), Number("cost"), collectible, Strings("classes"), Strings("entourage"), item.GetRawText());
    }
}
