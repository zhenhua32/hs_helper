using System.Security.Cryptography;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>
/// Complete, pinned source definitions used by existing native rules. Registration establishes
/// metadata identity, not that every effect of a registered card has been simulated.
/// cards.json SHA256 7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299;
/// CardDefs.base.xml SHA256 EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8.
/// </summary>
public static class NativeCardDefinitions
{
    public const string Build = "255790";
    private sealed record Definition(int DbfId, string Type, bool ImplementedBody, string Fingerprint);
    private static readonly IReadOnlyDictionary<string, Definition> Definitions = new Dictionary<string, Definition>(StringComparer.Ordinal)
    {
        ["BE_044"] = new(128892, "MINION", true, "A4FDAD092108D4BB0A637EB8E087DA5CB207A908FEA1BDC38A2854490B710D98"),
        ["CORE_CS2_106"] = new(69534, "WEAPON", false, "E96210FF10F2B09E774400720BEECD507C19061A162055060300DE8C62A0D16A"),
        ["CORE_CS2_189"] = new(69565, "MINION", true, "8B88F3B0530DAA425A623581BF7F9079CC7A282CAD280B766BA656823E9A365E"),
        ["CORE_DS1_184"] = new(69545, "SPELL", false, "0C8BAC4FC90669AF70809E97BF37B1590B782CA74B29B7C47CB73B91DE23254A"),
        ["CORE_DS1_185"] = new(69546, "SPELL", false, "D46272E70CC6FA3010A3295A5F27837F6700A4091F36AB56CA78C9CCF99DCA88"),
        ["CORE_EDR_004"] = new(119798, "MINION", true, "0F8FF8A1D86AC6C41DB4DB6E2D895AECFF49A9E24528936D5A671D29D708A5E8"),
        ["CORE_EDR_004_2026"] = new(131356, "MINION", true, "6C120E2ADDE795669B18F10A8C0D2F65DCA751CF864B6F055DF9088019D6B66F"),
        ["CORE_EX1_539"] = new(111361, "SPELL", false, "698099A5287415E03AA2B40CD83E988DA617D933915E0A0B8F49A9325345AC77"),
        ["CORE_EX1_567"] = new(69630, "WEAPON", false, "B4683E5732070CDABC67140F2788D0AF40BDA8B558824160155A6F1220BAE61A"),
        ["CORE_EX1_610"] = new(69603, "SPELL", false, "A10CD29AA95827DE96641E2608A0F33387DF5551FD80A4B8D1C3F8F2EC2B52C1"),
        ["CORE_LOOT_222"] = new(86543, "WEAPON", false, "67DBB1B6628FF887F65D1D808FB723432248B57332D37011155E142191FE9F0F"),
        ["CORE_TTN_866"] = new(129169, "MINION", true, "E7DC2F40977573AB05F1D17971515EEB3D1BD1E4DB7FBD026FC865CC3BF63E35"),
        ["CORE_UNG_912"] = new(97376, "MINION", true, "6F9E73E03FC966673390D527CD369C9A018BB0593050FDAEEF543EFE8A3E5C4D"),
        ["CS2_084"] = new(141, "SPELL", false, "93A7E91A7DC99DEC6C8B525A1140838EC84114F742699315B5092134903DAA17"),
        ["CS2_106"] = new(401, "WEAPON", false, "6102E4BDAC51E757353BC9505DE597E749CD284A50F76EB7929EBDC87EFFCEFF"),
        ["CS2_189"] = new(389, "MINION", true, "E1F5397D021DC46912200F9B862B11C7DBA9163325347D824383169596C6B0B7"),
        ["DINO_421"] = new(118617, "MINION", true, "05D24C37EFCFC6FA7D62AAC50868BE4F2C8C08541250A31E72CB83763579CB0C"),
        ["DINO_421e"] = new(118618, "ENCHANTMENT", false, "AA0860D52DD794458EEF672A8BD21C1072BF3A928D6E07E028FD010215EDCDF2"),
        ["DS1_185"] = new(877, "SPELL", false, "E906E7433A956CD53B1D00538F1D8D24D9FC3DBF193D3A61997CD1E13216192E"),
        ["DS1_188"] = new(311, "WEAPON", false, "9B6AEE55516AFC7D691F68448D1E5E480048F1256C64BB41F58AC947D52D0737"),
        ["EDR_031"] = new(113434, "MINION", false, "F4669C7982271571C94BC2F9FC7CD9C2484263DA80E855DD574CD6CC50B9B652"),
        ["EDR_100t"] = new(113796, "SPELL", false, "78A7445B487FA5656BFA28AA47C22058D6945AD28289EA51A9C00DBA59930743"),
        ["EDR_100t1"] = new(113797, "SPELL", false, "0E9AB5ACA59E78334B33D99489C54B278A8E5466636A5524859938EB2C5A5784"),
        ["EDR_100t10"] = new(115658, "SPELL", false, "0DFDCC964119C64078661AFBF22FCF5EE24D7A2A0D844370112A41B7962A437F"),
        ["EDR_100t13"] = new(120225, "SPELL", false, "3C1733B907F9B22D0AEB860F3664F3182B14DD1B30F2A25BB64859531B27EF61"),
        ["EDR_100t13e"] = new(120226, "ENCHANTMENT", false, "48B799C3E02748DCD2EC1286B858F91D5FCDC1490C0CC4DB3287D79D959EE11B"),
        ["EDR_100t1e"] = new(113804, "ENCHANTMENT", false, "541B460522C566401EC4CF8E06D4E3B3183A36CBB04D29A1D8D40E4127A01299"),
        ["EDR_100t2"] = new(113798, "SPELL", false, "253C2F5678A3DC64F5905F370586E6FF9983009B3ED8A16D69B3471378B68CA7"),
        ["EDR_100t2e"] = new(113805, "ENCHANTMENT", false, "9854B0D5BCE012A5F51B7CDBC19F6EC16D97F78C0698857107C140274FE38A5C"),
        ["EDR_100t3"] = new(113799, "SPELL", false, "4CF8B2A0E18F5913CACA7CD243EC8E102CF3FC0D61AEF4F76EC3B357571473BB"),
        ["EDR_100t3e"] = new(113806, "ENCHANTMENT", false, "F2EA6920853BFEAE09FE827B5DE77E3360A4240564F089089AD752659B301774"),
        ["EDR_100t4"] = new(113800, "SPELL", false, "96858FFC762999D29FDBA7C4B201B1388A09FB447A459652E5181364B7F5AE57"),
        ["EDR_100t4e"] = new(113807, "ENCHANTMENT", false, "4BCE26F813DE4AAC67280F5A90EE6E59D053BF03369BC6906C3E3F849D768DC2"),
        ["EDR_100t5"] = new(113801, "SPELL", false, "071EB7F40D9C04C2A6615784954FCBBCF5C1FD8FB0D8D36E0829F8F4A55EA54F"),
        ["EDR_100t5e2"] = new(117173, "ENCHANTMENT", false, "F1864403E83D71E62C2546C856EBEBF36CFA8428ED702252B14138F6A2A855D9"),
        ["EDR_100t5e5"] = new(120020, "ENCHANTMENT", false, "BFE7503482A298714C5060524D514E2B5A6501C2903FA0B8EB50E8A1947B0C6A"),
        ["EDR_100t6"] = new(113802, "SPELL", false, "33AEF571F3DE422B61D604789B20A047007C1BE8EE255FFEBD627C1C9C7D505E"),
        ["EDR_100t6e"] = new(113809, "ENCHANTMENT", false, "D2B2DB02AC482B8A1DB7A3EC3705D4011360B343553657F0269197D095978328"),
        ["EDR_100t7"] = new(113882, "SPELL", false, "15198097078FFCE28E20B35393E42E837F231D8F5C69139AFE40FA4EBF2CA1BC"),
        ["EDR_100t8"] = new(113884, "SPELL", false, "C591E65339C3721E0978461DAC427C83122BE079719CFDC508D329AE67C0F75B"),
        ["EDR_100t8e"] = new(113885, "ENCHANTMENT", false, "54085604894150AB5CFAF12323F3B31469CC9F73FDD01848FDFCDC85E3EC309A"),
        ["EDR_100t8e1"] = new(115355, "ENCHANTMENT", false, "F8D7BD5689F97DF1A8866CF4AADF2CDBACC78A602BAFCB4DF05665E67B8AEF9E"),
        ["EDR_100t9"] = new(113886, "SPELL", false, "38714EBD7C22F3C7A46E7E897FBC9FA195166DF9CD5038D64BE5FA3812083DE2"),
        ["EDR_100te"] = new(113803, "ENCHANTMENT", false, "A02523FE54B70350112A57C8158544704F023134B63DBBD71D325958E981B9C7"),
        ["EDR_209"] = new(113588, "MINION", false, "3ABBC2B1695C420C9E6549BBEC1774BD014BDA2CE85E3FDF1C626A8F446DEC5D"),
        ["EDR_226"] = new(114018, "MINION", true, "BE4CC19E8E010C5E02FDFB3F595000103BCE186DC3FE62BEB1F3EFF611224458"),
        ["EDR_227"] = new(114020, "MINION", true, "AF62F68FF8753F225D1E2370134BE205A997628B876CE8622ECEC26283905243"),
        ["EDR_259"] = new(114868, "MINION", false, "23AEB098771DBAFDED0E64DCA6FFB59877DF8D501D9BF2AD095239B8FA40F951"),
        ["EDR_421"] = new(113606, "MINION", false, "1384674B3722A7CD66C066C540B28B96CBF71EA34C4378303678DDC754AD22C6"),
        ["EDR_430"] = new(113700, "MINION", false, "083192780B8A5EE74CD30B9411DFACE98CEA0179A3DB3EC0DA3E9BDD7F43800B"),
        ["EDR_471"] = new(114353, "MINION", false, "C04321296BF55D7528DD47F18536EF71DCCD95E09C458F666FDD61AE1EE458BE"),
        ["EDR_480"] = new(114254, "MINION", true, "317E05FBEFDB1ADC4D227C96DD20FF0A3B9BC3BEC3E3EFB748568BDA80D409B3"),
        ["EDR_489"] = new(115203, "MINION", true, "2508997755BE0F7C992A2F3FB037B7F576D43F31FF1B62CA1B02EDBBAC839E1C"),
        ["EDR_489e1"] = new(116678, "ENCHANTMENT", false, "F2D21C2DA981E24398BA80FA787DFC125B5018C6AF069F22EB33504DDC926013"),
        ["EDR_489e2"] = new(120560, "ENCHANTMENT", false, "AEF72A4AE556908038F9E181C17929C3502346DF436BE85293A14A9CDD65E9EA"),
        ["EDR_527"] = new(114527, "MINION", false, "6BF78C6153014B8A89B771CD6FE999B444B68F589EC4B1DD62F406DE395C2643"),
        ["EDR_800"] = new(114006, "MINION", true, "EFD951C8FC68AD2A1CFDA6672DA3C33BDCFF2BBF6B5376F6FE45E124BEECADDF"),
        ["EDR_819"] = new(116018, "MINION", false, "BB752F52194A81E8014819B7FDE23E5D36B957B0250B2025703E82D3B644ED25"),
        ["EDR_849"] = new(114933, "MINION", true, "6A4B51F2B5E9D180ED742E19AAA769BBC3C68DB2686D18E3414D9A4C76DF48B3"),
        ["EDR_850p"] = new(114052, "HERO_POWER", false, "7A2B1AB895A11D5E1B210109817B401DCFB9ADD0968BBFF3709392F99F073DB4"),
        ["EDR_850pe1"] = new(114619, "ENCHANTMENT", false, "F5058FCEE30A82CC9D5209116863D7EDC2C40598F7BC5C7ED1160BFD5232B2CE"),
        ["EDR_850pe5"] = new(114959, "ENCHANTMENT", false, "2BEA6BAA760F4DC5C68C30C931A7D030F962F3DB9928CE611F5C1EC2B57D5EAD"),
        ["EDR_852"] = new(114935, "MINION", true, "C33ECC8684CCA8C3DFE62C6792CDEC54CB01B2EB5D70FA2773D0C509079A7943"),
        ["EDR_888"] = new(115186, "MINION", true, "E823E5C7C0EB6E75E45F223A5F8FA4BB74229AEFA2B411A6C903840AE7E4AE30"),
        ["EDR_895"] = new(115264, "MINION", false, "57464490833736BB986733F57ED19ED2414EAC54FE856DD56978F699A8F25ED3"),
        ["EX1_539"] = new(296, "SPELL", false, "34CEE8320137FD3F047F622EB5086BAAD31C27391E880212CC9586A0A2C47A35"),
        ["EX1_567"] = new(352, "WEAPON", false, "CC3A5FAECCDCBAC111F366AAAD2608C48748034D58586024FD47F4E99C60C3B6"),
        ["EX1_610"] = new(585, "SPELL", false, "4CAD0ADCF77CE98B91FB9B3C03A64EF75B48E461F7D8BFC95132DB767C0C1CFB"),
        ["FIR_921"] = new(115648, "MINION", true, "670EC9479EFEEBF2A3B24C14715E6576523AF8933775593511BFE9828D5C1487"),
        ["FIR_953"] = new(115751, "MINION", true, "309266F75C03168FAE80A003A44454229D457D245345BD27096F1D0C4B1776B7"),
        ["GAME_005"] = new(1746, "SPELL", false, "3C94C0B7CDDBB16054111CC1492A9738129E050E237FCBB8087B21E9928ACADE"),
        ["HERO_01bp"] = new(725, "HERO_POWER", false, "00D85E3C125F05BF4CDB49A6FD59F006586783D03E1BD0B9F783CD45CEC893A8"),
        ["HERO_02bp"] = new(687, "HERO_POWER", false, "545FA014627BF58146333B0504EE73C6E21FDEB27E920C256DABA4DB9D6E4E22"),
        ["HERO_03bp"] = new(730, "HERO_POWER", false, "0417FB38EC2D5DE1FC8F3231C923E41F71268DFDC511F72196ECFE7C039AF024"),
        ["HERO_04bp"] = new(472, "HERO_POWER", false, "317EEE51B02378597B0217743032DFAE754CFD37772C42A335CC0A052DB53353"),
        ["HERO_05bp"] = new(229, "HERO_POWER", false, "1DC22172D811BEAA283823CEEE0F9E7B73E330915B66D162242F98B0AF7796BE"),
        ["HERO_06bp"] = new(1123, "HERO_POWER", false, "74FD8E9CBAEB222723A25FB8128616731D96C755594FFCEB9E9B1451ECA8F9D0"),
        ["HERO_07bp"] = new(300, "HERO_POWER", false, "E645A8635018C79F3647F8338827716EA334B28FC9517D56E4FC3AF3A174CE8E"),
        ["HERO_08bp"] = new(807, "HERO_POWER", false, "F09F9171981181415166D9734A16B6DAED3A61EDDDD247DCB85AE1F0F85E633D"),
        ["HERO_09bp"] = new(479, "HERO_POWER", false, "0F42835E7527B6A59077BB3B3FAD560085136412D04552DB06303306C015DC58"),
        ["HERO_10bp"] = new(60224, "HERO_POWER", false, "6A547391266049501DABCB41953FDB5570F536798F7CF81E092A73B8B8A25C2B"),
        ["HERO_11bp"] = new(78162, "HERO_POWER", false, "071D5F7BEAD32C184549CB79BCB978448FDA614008CA9352EF66BE65F4CFD0F4"),
        ["JAIL_831"] = new(125998, "MINION", true, "8C5D426A4AC272DF3EF01CC67BBD08B70B87C533324DFC9453C0158306AE905A"),
        ["LOOT_222"] = new(45537, "WEAPON", false, "E81954D8DD701FBDEA9FC3EF1603CF35E4D37FE1CD9E4B97EEECE42F2DBBF90B"),
        ["TIME_000ta"] = new(119589, "SPELL", false, "38C27F8AE70507FB169668EAEB39DA7F847C8E7294A327F2B6F8108EC8904672"),
        ["TIME_000tb"] = new(119636, "SPELL", false, "A1C0519685351802D3E125475F0A95D07C7C997DD1F1D88436547A0A1F363DD2"),
        ["TLC_824"] = new(117596, "SPELL", false, "32BC3D91D08CDCC3C792016787E494F3598ADFDB80C74BE8755B2C9DB04EA50D"),
        ["TLC_EVENT_400"] = new(120648, "SPELL", false, "E9418797E02A5E9C17E76AF016A9C5FCB53E05A3122378E99580C48D23D8B53C"),
    };

    public static bool IsRegistered(string? cardId) => cardId is not null && Definitions.ContainsKey(cardId);
    public static string? ExpectedType(string? cardId) => cardId is not null && Definitions.TryGetValue(cardId, out var definition) ? definition.Type : null;
    internal static bool IsImplementedBody(string? cardId) => cardId is not null && Definitions.TryGetValue(cardId, out var definition) && definition.ImplementedBody;

    public static bool IsAudited(string? build, string? cardId, CardRecord? card)
    {
        if (build != Build || cardId is null || !Definitions.TryGetValue(cardId, out var definition) ||
            card is null || card.Id != cardId || card.DbfId != definition.DbfId || card.Type != definition.Type ||
            card.RawJson is not { Length: > 0 and <= 65536 }) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson);
            return RecordMatchesRaw(card, document.RootElement) && CanonicalHash(document.RootElement) == definition.Fingerprint;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { return false; }
    }

    internal static bool IsAuditedEntity(string? build, EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards,
        string? currentCardId = null)
    {
        var id = currentCardId ?? entity.CardId;
        if (entity.Kind != EntityKind.Card || entity.Id <= 0 || entity.Tags is null || id is null ||
            entity.ControllerId is not (1 or 2) || !OptionalIdentity(entity, "ENTITY_ID", entity.Id) || !OptionalIdentity(entity, "53", entity.Id) ||
            !OptionalIdentity(entity, "50", entity.ControllerId.Value) ||
            !cards.TryGetValue(id, out var card) || !IsAudited(build, id, card) || !ObservedType(entity, card.Type)) return false;
        // A trusted runtime overlay may describe a covered same-entity hero-power transition;
        // it cannot repair a corrupt registered definition of its original observed source.
        return entity.CardId == id || !IsRegistered(entity.CardId) ||
            cards.TryGetValue(entity.CardId!, out var original) && IsAudited(build, entity.CardId, original);
    }
    private static bool OptionalIdentity(EntitySnapshot entity, string tag, int expected) =>
        !entity.Tags.TryGetValue(tag, out var raw) || int.TryParse(raw, out var value) && value == expected;

    internal static bool ObservedType(EntitySnapshot entity, string expected)
    {
        var number = expected switch { "MINION" => "4", "SPELL" => "5", "ENCHANTMENT" => "6", "WEAPON" => "7", "HERO_POWER" => "10", "HERO" => "3", _ => "" };
        // A pinned CardId establishes its inherent base type when that observation is absent.
        // Present current evidence always wins: contradictory or malformed aliases cannot fall back.
        if (number.Length == 0 || entity.Tags is null) return false;
        return (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named == expected || named == number) &&
            (!entity.Tags.TryGetValue("202", out var alias) || alias == expected || alias == number);
    }

    // Only sources that participate in this helper's present calculation are inspected.
    // History, ordinary hidden/deck cards and unselected hand cards are deliberately not a global gate.
    internal static bool HasInvalidContext(GameSnapshot snapshot, IReadOnlyDictionary<string, CardRecord> cards,
        int? sourceId = null, IEnumerable<EntitySnapshot>? currentEntities = null, bool includePowers = false)
    {
        foreach (var entity in currentEntities ?? snapshot.Entities.Values)
        {
            if (entity is null || !IsRegistered(entity.CardId)) continue;
            if (entity.Tags is null) return true;
            var selected = entity.Id == sourceId;
            var active = entity.Zone == "PLAY" && (includePowers || ExpectedType(entity.CardId) != "HERO_POWER" || !ObservedType(entity, "HERO_POWER"));
            var secret = entity.Zone == "SECRET" && entity.ControllerId == snapshot.LocalPlayerId;
            var attached = ExpectedType(entity.CardId) == "ENCHANTMENT" && entity.Zone != "GRAVEYARD" && entity.GetIntTag("ATTACHED") is > 0;
            if ((selected || active || secret || attached) && !IsAuditedEntity(snapshot.ClientBuild, entity, cards)) return true;
        }
        return false;
    }

    internal static bool RecordMatchesRaw(CardRecord card, JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || card.DbfId is null or <= 0 ||
            raw.GetProperty("id").GetString() != card.Id || raw.GetProperty("dbfId").GetInt32() != card.DbfId || raw.GetProperty("type").GetString() != card.Type) return false;
        string Text(string key) => raw.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        var name = Text("name");
        var cost = raw.TryGetProperty("cost", out var costValue) ? costValue.GetInt32() : (int?)null;
        var collectible = raw.TryGetProperty("collectible", out var collect) && collect.GetBoolean();
        return (name.Length == 0 ? card.Id : name) == card.Name && Text("text") == card.Text && Text("cardClass") == card.CardClass &&
            Text("set") == card.Set && cost == card.Cost && collectible == card.Collectible && SameList("classes", card.Classes) && SameList("entourage", card.Entourage);
        bool SameList(string key, IReadOnlyList<string>? values) => values is not null &&
            (raw.TryGetProperty(key, out var array) ? array.EnumerateArray().Select(value => value.GetString()!).SequenceEqual(values, StringComparer.Ordinal) : values.Count == 0);
    }

    internal static string CanonicalHash(JsonElement raw)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteCanonical(raw, writer);
        return Convert.ToHexString(SHA256.HashData(output.ToArray()));
    }
    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
