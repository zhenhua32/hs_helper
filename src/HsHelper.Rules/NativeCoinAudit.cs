using System.Text.Json;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        // Exact public build-255790 GAME_005. Source SHA256 and CardDefs provenance are
        // recorded in lethal-search-255790.json; formatting/property order are immaterial.
        public bool AuditedCoinSource(Entity source)
        {
            if (source.CardId == "GAME_005" && _cards.TryGetValue(source.CardId, out var card) &&
                card.Id == source.CardId && card.DbfId == 1746 && card.Type == "SPELL" &&
                card.Text == "在本回合中，获得一个\n法力水晶。" && card.RawJson is { Length: <= 65536 })
            {
                try
                {
                    using var doc = JsonDocument.Parse(card.RawJson);
                    if (GiftRecordMatchesRaw(card, doc.RootElement) &&
                        GiftMetadataHash(doc.RootElement) == "3C94C0B7CDDBB16054111CC1492A9738129E050E237FCBB8087B21E9928ACADE")
                    { Coverage.Add("GAME_005-fixed-native-definition"); return true; }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
                { }
            }
            Unsupported("UNAUDITED_COIN_DEFINITION", "硬币的固定身份、DBF、完整文字或原始定义不一致，未支付或使用。");
            return false;
        }
    }
}
