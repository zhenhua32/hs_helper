using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HsHelper.Cards;

public sealed partial class CardCatalog
{
    private static readonly JsonSerializerOptions RuleJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<CardPoolRules> ImportPoolRulesAsync(Stream json, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadLimitedAsync(json, 2 * 1024 * 1024, cancellationToken);
        CardPoolRules rules;
        try { rules = JsonSerializer.Deserialize<CardPoolRules>(bytes, RuleJson) ?? throw new InvalidDataException("卡池规则不能为空。"); }
        catch (JsonException error) { throw new InvalidDataException("卡池规则 JSON 无效。", error); }
        rules = NormalizeRules(rules);
        var canonical = JsonSerializer.Serialize(rules);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var existing = connection.CreateCommand();
        existing.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        existing.CommandText = "SELECT sha256 FROM card_pool_rules WHERE rule_id=$id";
        existing.Parameters.AddWithValue("$id", rules.RuleId);
        var oldHash = await existing.ExecuteScalarAsync(cancellationToken) as string;
        if (oldHash is not null && oldHash != hash) throw new InvalidDataException("该规则版本已有不同内容，请创建新 RuleId 保留历史。");
        if (oldHash is null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO card_pool_rules VALUES($id,$build,$format,$from,$until,$imported,$source,$hash,$json)";
            command.Parameters.AddWithValue("$id", rules.RuleId);
            command.Parameters.AddWithValue("$build", rules.Build);
            command.Parameters.AddWithValue("$format", rules.Format);
            command.Parameters.AddWithValue("$from", rules.EffectiveFrom.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$until", rules.ValidUntil.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$imported", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$source", rules.Source);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$json", canonical);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return rules;
    }

    public async Task<CardPoolRules?> GetPoolRulesAsync(string build, int format = 2,
        DateTimeOffset? at = null, CancellationToken cancellationToken = default)
    {
        ValidateBuild(build, false);
        var instant = at ?? DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM card_pool_rules WHERE build=$build AND format=$format AND effective_from <= $at ORDER BY effective_from DESC,imported_at DESC,rule_id DESC LIMIT 1";
        command.Parameters.AddWithValue("$build", build);
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$at", instant.ToUnixTimeMilliseconds());
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null) return null;
        var rules = JsonSerializer.Deserialize<CardPoolRules>(json, RuleJson)!;
        // A newer expired rule does not revive an older, superseded rule.
        return instant >= rules.ValidUntil ? null : rules;
    }

    public async Task<CardPoolRules> SyncStandardPoolAsync(HttpClient client, string build,
        CancellationToken cancellationToken = default)
    {
        ValidateBuild(build, false);
        if (await GetManifestAsync(build, cancellationToken: cancellationToken) is null)
            throw new InvalidOperationException("请先加载对应 build 的中文卡库。");
        var official = new Uri("https://hearthstone.blizzard.com/en-gb/cards/");
        using var response = await client.GetAsync(official, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https", Host: "hearthstone.blizzard.com" })
            throw new InvalidDataException("官方卡池页面重定向到未认可的来源。");
        await using var htmlStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var html = Encoding.UTF8.GetString(await ReadLimitedAsync(htmlStream, 8 * 1024 * 1024, cancellationToken));
        var definitions = new Uri($"https://api.hearthstonejson.com/v1/{build}/CardDefs.base.xml");
        using var dataResponse = await client.GetAsync(definitions, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        dataResponse.EnsureSuccessStatusCode();
        if (dataResponse.RequestMessage?.RequestUri != definitions)
            throw new InvalidDataException("固定 build 卡牌定义的响应路径不匹配。");
        await using var xml = await dataResponse.Content.ReadAsStreamAsync(cancellationToken);
        var boundedXml = await ReadLimitedAsync(xml, 64 * 1024 * 1024, cancellationToken);
        using var source = new MemoryStream(boundedXml, writable: false);
        var rules = OfficialStandardPool.CreatePartial(html, source, build, DateTimeOffset.UtcNow);
        using var ruleInput = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(rules));
        return await ImportPoolRulesAsync(ruleInput, cancellationToken);
    }

    public static CardLegality EvaluateLegality(CardRecord card, CardPoolRules? rules)
    {
        if (rules is null) return CardLegality.Unknown;
        if (!card.Collectible || rules.BannedCardIds.Contains(card.Id, StringComparer.Ordinal) ||
            rules.UnreleasedCardIds.Contains(card.Id, StringComparer.Ordinal)) return CardLegality.Illegal;
        var inPool = rules.AllowedCardIds.Contains(card.Id, StringComparer.Ordinal) ||
            rules.AllowedSets.Contains(card.Set, StringComparer.Ordinal);
        if (!inPool) return CardLegality.Illegal;
        return rules.IsComplete ? CardLegality.Legal : CardLegality.Unknown;
    }

    public async Task<DeckDescription> DescribeDeckAsync(DeckDefinition deck, string build,
        DateTimeOffset? at = null, string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ValidateBuild(build, false);
        if (deck.Cards.Count > 500) throw new ArgumentException("套牌条目过多。", nameof(deck));
        var rules = await GetPoolRulesAsync(build, deck.Format, at, cancellationToken);
        var requestedIds = deck.Cards.Select(c => c.DbfId).Concat(deck.Heroes)
            .Concat(deck.Sideboards.Select(s => s.OwnerDbfId)).Concat(deck.Sideboards.SelectMany(s => s.Cards).Select(c => c.DbfId)).Distinct().ToArray();
        if (requestedIds.Length > 500) throw new ArgumentException("套牌与边栏的卡牌种类过多。", nameof(deck));
        var records = (await GetByDbfIdsAsync(requestedIds, build, locale, cancellationToken))
            .Where(c => c.DbfId.HasValue).ToDictionary(c => c.DbfId!.Value);
        var heroClass = deck.Heroes.Count == 1 && records.TryGetValue(deck.Heroes[0], out var hero) ? hero.CardClass : null;
        var issues = new List<string>();
        var illegal = false;
        var unknown = rules is null || !rules.IsComplete;
        if (deck.Format != 2) { unknown = true; issues.Add("当前完整构筑检查面向标准模式；其他模式需要相应规则。"); }
        if (heroClass is null) { unknown = true; issues.Add("英雄身份尚未从当前卡库解析。"); }
        if (rules is null) issues.Add("没有当前 build 和时点的有效卡池规则。");
        else if (!rules.IsComplete) issues.Add("卡池来源仅确认系列成员，临时禁用和单卡上线情况尚未完整核对。");
        var described = new List<DeckCardDescription>();
        var seen = new HashSet<int>();
        var hasConstructionExceptions = false;
        foreach (var entry in deck.Cards)
        {
            if (entry.Count <= 0 || entry.DbfId <= 0 || !seen.Add(entry.DbfId))
            { illegal = true; issues.Add("主牌库包含无效数量、ID或重复条目。"); }
            if (!records.TryGetValue(entry.DbfId, out var card))
            {
                unknown = true; issues.Add($"当前卡库缺少 dbfId {entry.DbfId}。");
                described.Add(new(entry.DbfId, entry.Count, null, $"未知牌 {entry.DbfId}", null, "", "", CardLegality.Unknown));
                continue;
            }
            using var raw = JsonDocument.Parse(card.RawJson);
            var rarity = Attribute(raw.RootElement, "rarity");
            var canOverride = IsConstructionException(raw.RootElement, card.Text);
            hasConstructionExceptions |= canOverride;
            var legality = EvaluateLegality(card, rules);
            described.Add(new(entry.DbfId, entry.Count, card.Id, card.Name, card.Cost, card.CardClass, rarity, legality));
            if (legality == CardLegality.Illegal) { illegal = true; issues.Add($"{card.Name} 不在有效卡池或被规则排除。"); }
            if (legality == CardLegality.Unknown) unknown = true;
            if (string.IsNullOrWhiteSpace(rarity)) { unknown = true; issues.Add($"{card.Name} 缺少稀有度信息，无法确认同名牌数量限制。"); }
            if (heroClass is not null && (card.Classes.Count > 0
                    ? !card.Classes.Contains(heroClass, StringComparer.Ordinal)
                    : card.CardClass != "NEUTRAL" && card.CardClass != heroClass))
            { illegal = true; issues.Add($"{card.Name} 的职业与英雄不符。"); }
        }
        var total = deck.Cards.Sum(c => (long)c.Count);
        if (hasConstructionExceptions)
        { unknown = true; issues.Add("套牌包含可能改变构筑规则的卡，需专用规则验证数量和限制。"); }
        else
        {
            if (total != 30) { unknown = true; issues.Add($"普通构筑应为30张，当前{total}张；未识别的构筑例外需要核对。"); }
            foreach (var card in described.Where(c => c.Count > (c.Rarity == "LEGENDARY" ? 1 : 2)))
            { illegal = true; issues.Add($"{card.Name} 超出普通构筑的同名牌数量限制。"); }
        }
        var sideboards = new List<DeckSideboardDescription>();
        foreach (var sideboard in deck.Sideboards)
        {
            if (!deck.Cards.Any(c => c.DbfId == sideboard.OwnerDbfId))
            { illegal = true; issues.Add("边栏关联牌不在主牌库中。"); }
            var sideCards = new List<DeckCardDescription>();
            var sideIds = new HashSet<int>();
            foreach (var entry in sideboard.Cards)
            {
                if (entry.Count <= 0 || !sideIds.Add(entry.DbfId)) { illegal = true; issues.Add("边栏包含无效数量或重复条目。"); }
                if (!records.TryGetValue(entry.DbfId, out var card))
                {
                    unknown = true; issues.Add($"当前卡库缺少边栏 dbfId {entry.DbfId}。");
                    sideCards.Add(new(entry.DbfId, entry.Count, null, $"未知牌 {entry.DbfId}", null, "", "", CardLegality.Unknown));
                    continue;
                }
                using var raw = JsonDocument.Parse(card.RawJson);
                var legality = EvaluateLegality(card, rules);
                if (legality == CardLegality.Illegal) { illegal = true; issues.Add($"边栏中的 {card.Name} 不在有效卡池或被规则排除。"); }
                if (legality == CardLegality.Unknown) unknown = true;
                sideCards.Add(new(entry.DbfId, entry.Count, card.Id, card.Name, card.Cost, card.CardClass,
                    Attribute(raw.RootElement, "rarity"), legality));
            }
            records.TryGetValue(sideboard.OwnerDbfId, out var owner);
            sideboards.Add(new(sideboard.OwnerDbfId, owner?.Id, owner?.Name ?? $"未知牌 {sideboard.OwnerDbfId}",
                sideCards.AsReadOnly(), Curve(sideCards)));
        }
        if (deck.Sideboards.Count > 0)
        { unknown = true; issues.Add("边栏名称与费用已解析，边栏容量与特殊构筑合法性需专用规则核对。"); }
        var curve = described.Where(c => c.Cost.HasValue && c.Count > 0).GroupBy(c => Math.Min(c.Cost!.Value, 10))
            .OrderBy(g => g.Key).Select(g => new ManaCurveBucket(g.Key, g.Sum(c => c.Count))).ToArray();
        return new DeckDescription(deck.Format, heroClass, described.AsReadOnly(), Array.AsReadOnly(curve),
            illegal ? CardLegality.Illegal : unknown ? CardLegality.Unknown : CardLegality.Legal,
            issues.Distinct(StringComparer.Ordinal).ToArray(), build, rules?.RuleId) { Sideboards = sideboards.AsReadOnly() };
    }

    private static IReadOnlyList<ManaCurveBucket> Curve(IEnumerable<DeckCardDescription> cards) => Array.AsReadOnly(cards
        .Where(c => c.Cost.HasValue && c.Count > 0).GroupBy(c => Math.Min(c.Cost!.Value, 10)).OrderBy(g => g.Key)
        .Select(g => new ManaCurveBucket(g.Key, g.Sum(c => c.Count))).ToArray());

    private static bool IsConstructionException(JsonElement raw, string text)
    {
        if (raw.EnumerateObject().Any(p => p.Name.Contains("deckLimit", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("deckSize", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("deckBuilding", StringComparison.OrdinalIgnoreCase))) return true;
        return new[] { "your deck size", "your deck can", "you may include", "per deck", "套牌上限", "套牌大小", "牌组大小", "牌組大小", "每副套牌", "构筑", "構築" }
            .Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
    private static string Attribute(JsonElement raw, string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static CardPoolRules NormalizeRules(CardPoolRules rules)
    {
        if (string.IsNullOrWhiteSpace(rules.RuleId) || rules.RuleId.Length > 200 || rules.RuleId.Any(char.IsControl))
            throw new InvalidDataException("规则 RuleId 必须是可显示的短标识。");
        ValidateBuild(rules.Build, false);
        if (rules.Format is < 1 or > 4) throw new InvalidDataException("规则模式无效。");
        if (rules.ValidUntil <= rules.EffectiveFrom) throw new InvalidDataException("规则必须有晚于生效时间的失效时间。");
        if (string.IsNullOrWhiteSpace(rules.Source) || rules.Source.Length > 2000) throw new InvalidDataException("规则必须保留来源。");
        static IReadOnlyList<string> Normalize(IReadOnlyList<string>? values)
        {
            if (values is null || values.Count > 50000 || values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200 || x.Any(char.IsControl)))
                throw new InvalidDataException("规则卡牌或系列列表无效。");
            return Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        var result = rules with
        {
            EffectiveFrom = rules.EffectiveFrom.ToUniversalTime(), ValidUntil = rules.ValidUntil.ToUniversalTime(),
            AllowedSets = Normalize(rules.AllowedSets), AllowedCardIds = Normalize(rules.AllowedCardIds),
            BannedCardIds = Normalize(rules.BannedCardIds), UnreleasedCardIds = Normalize(rules.UnreleasedCardIds)
        };
        if (result.AllowedSets.Count == 0 && result.AllowedCardIds.Count == 0) throw new InvalidDataException("规则不能使用空卡池替换已有规则。");
        return result;
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("卡池来源文件超过允许大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
}
