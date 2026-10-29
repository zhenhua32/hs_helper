using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HsHelper.Cards;

/// <summary>Combines an explicit official Standard set list with a fixed-build CardDefs mapping.</summary>
public static class OfficialStandardPool
{
    public static CardPoolRules CreatePartial(string officialHtml, Stream cardDefinitions,
        string build, DateTimeOffset observedAt)
    {
        var sets = ReadOfficialSets(officialHtml);
        var ids = new List<string>();
        var counts = new Dictionary<int, int>();
        using var reader = XmlReader.Create(cardDefinitions, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        reader.MoveToContent();
        if (reader.Name != "CardDefs" || reader.GetAttribute("build") != build)
            throw new InvalidDataException("卡牌定义的 build 与规则请求不一致。");
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "Entity") continue;
            using var subtree = reader.ReadSubtree();
            var entity = XElement.Load(subtree);
            var cardId = entity.Attribute("CardID")?.Value;
            var tags = entity.Elements("Tag").Where(t => t.Attribute("name") is not null)
                .GroupBy(t => t.Attribute("name")!.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Attribute("value")?.Value, StringComparer.Ordinal);
            if (tags.GetValueOrDefault("COLLECTIBLE") != "1" ||
                !int.TryParse(tags.GetValueOrDefault("CARD_SET"), out var set) || !sets.ContainsKey(set)) continue;
            if (string.IsNullOrWhiteSpace(cardId)) throw new InvalidDataException("标准卡牌定义缺少CardID。");
            ids.Add(cardId);
            counts[set] = counts.GetValueOrDefault(set) + 1;
        }
        if (ids.Count == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new InvalidDataException("官方卡池与固定构建定义无法形成有效唯一成员列表。");
        foreach (var set in sets)
            if (counts.GetValueOrDefault(set.Key) != set.Value)
                throw new InvalidDataException($"官方系列 {set.Key} 收藏卡数量与固定build不一致，不能自动绑定规则。");
        var utc = observedAt.ToUniversalTime();
        return new($"official-standard-membership-{build}-{utc:yyyyMMddHHmmssfff}", build, 2,
            utc, utc.AddDays(7),
            $"https://hearthstone.blizzard.com/en-gb/cards/ ; https://api.hearthstonejson.com/v1/{build}/CardDefs.base.xml",
            [], ids.OrderBy(x => x, StringComparer.Ordinal).ToArray(), [], [], false,
            "官方页面明确列出的标准系列与固定build收藏卡计数一致。只确认系列成员；来源未包含完整禁用、单卡上线或国服特殊规则，合法性保持Unknown。图片和池成员不作为完整构筑合法性证明。");
    }

    private static Dictionary<int, int> ReadOfficialSets(string html)
    {
        if (html.Length > 8 * 1024 * 1024) throw new InvalidDataException("官方页面过大。");
        foreach (Match match in Regex.Matches(html, "\\bconfig=\"([^\"]*)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var value = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (!value.Contains("\"setGroups\"", StringComparison.Ordinal)) continue;
            using var json = JsonDocument.Parse(value);
            if (!json.RootElement.TryGetProperty("sets", out var allSets)) continue;
            var all = allSets.EnumerateArray().ToArray();
            var standard = all.SingleOrDefault(x => x.TryGetProperty("slug", out var slug) && slug.GetString() == "standard");
            if (standard.ValueKind != JsonValueKind.Object || !standard.TryGetProperty("cardSets", out var members))
                throw new InvalidDataException("官方页面未明确提供标准系列列表。");
            var result = new Dictionary<int, int>();
            foreach (var slug in members.EnumerateArray().Select(x => x.GetString()))
            {
                var set = all.SingleOrDefault(x => x.TryGetProperty("slug", out var candidate) && candidate.GetString() == slug);
                if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) ||
                    !set.TryGetProperty("collectibleCount", out var count) || !count.TryGetInt32(out var cardCount) || cardCount <= 0)
                    throw new InvalidDataException("标准系列缺少有效编号或收藏卡计数。");
                if (!result.TryAdd(number, cardCount)) throw new InvalidDataException("标准系列重复。");
            }
            if (result.Count == 0) throw new InvalidDataException("标准系列列表为空。");
            return result;
        }
        throw new InvalidDataException("官方页面格式改变，无法定位标准卡池元数据。");
    }
}
