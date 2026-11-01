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
                NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, source.CardId, card))
            { Coverage.Add("GAME_005-fixed-native-definition"); return true; }
            Unsupported("UNAUDITED_COIN_DEFINITION", "硬币的固定身份、DBF、完整文字或原始定义不一致，未支付或使用。");
            return false;
        }
    }
}
