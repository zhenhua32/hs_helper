using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace HsHelper.Cards;

// This recognizes original deck construction only. It does not create an opponent's copied
// cards, replace a Whizbang deck, change starting health, or simulate a Start of Game effect.
internal static class ReviewedMainDeckSizes
{
    private sealed record Definition(int DbfId, int RequiredCards, string CardHash, string DefinitionHash);
    // Fixed public cards.json SHA256 7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299;
    // CardDefs.base.xml SHA256 EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8.
    private static readonly IReadOnlyDictionary<string, Definition> Definitions = new Dictionary<string, Definition>(StringComparer.Ordinal)
    {
        ["JAIL_430"] = new(126055, 20, "156F5DB7FC2D219DA0C84CC14188591BB5F6DA2A33A2DD6F9FD2B90713B21766", "E410506751C79C0C1406DDC170E860BF95BC76514D486D3216B82841C031C1C2"),
        ["REV_018"] = new(79767, 40, "547C3729E7C029FE119A3743F5E4A461D22DDD746A7937E8A55ED69C0AAA33B1", "E838179422CAE343CCB93068774D7A87CA8551800B9D9E041E58494D26A267C6")
    };
    // Known modifiers with additional, unreviewed replacement/composition semantics are
    // recognized as needing evidence. Their numeric tag alone is never a complete rule.
    internal static bool IsKnownModifier(string id) => Definitions.ContainsKey(id) || id is "BOT_914" or "DAL_800" or "TOY_700" or "TIME_005";
    internal static bool IsKnownModifierDbf(int? id) => id is 126055 or 79767 or 50477 or 56047 or 104819 or 119432;
    internal static bool IsKnownModifier(CardRecord card) => IsKnownModifier(card.Id) || IsKnownModifierDbf(card.DbfId);
    internal static int? ExpectedCards(string id) => Definitions.TryGetValue(id, out var definition) ? definition.RequiredCards : null;

    internal static DeckMainDeckSizeRule? FromDefinition(string build, XElement entity)
    {
        var id = entity.Attribute("CardID")?.Value;
        if (build != "255790" || id is null || !Definitions.TryGetValue(id, out var definition) ||
            entity.Attribute("ID")?.Value != definition.DbfId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            DefinitionHash(entity) != definition.DefinitionHash) return null;
        return new(id, definition.RequiredCards, KeepsOrdinaryCopyLimits: true);
    }

    internal static bool MatchesCard(string build, CardRecord card, int requiredCards)
    {
        if (build != "255790" || !Definitions.TryGetValue(card.Id, out var definition) || definition.RequiredCards != requiredCards ||
            card.DbfId != definition.DbfId || card.Type != "MINION" || card.RawJson is not { Length: > 0 and <= 65536 }) return false;
        try
        {
            using var raw = JsonDocument.Parse(card.RawJson);
            var value = raw.RootElement;
            string Text(string name) => value.TryGetProperty(name, out var text) ? text.GetString() ?? "" : "";
            bool List(string name, IReadOnlyList<string>? observed) => observed is not null &&
                (value.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(item => item.GetString()!).SequenceEqual(observed, StringComparer.Ordinal) : observed.Count == 0);
            return Text("id") == card.Id && value.GetProperty("dbfId").GetInt32() == card.DbfId && Text("type") == card.Type &&
                Text("name") == card.Name && Text("text") == card.Text && Text("cardClass") == card.CardClass && Text("set") == card.Set &&
                (value.TryGetProperty("cost", out var cost) ? cost.GetInt32() : (int?)null) == card.Cost &&
                (value.TryGetProperty("collectible", out var collect) && collect.GetBoolean()) == card.Collectible &&
                List("classes", card.Classes) && List("entourage", card.Entourage) && CardHash(value) == definition.CardHash;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { return false; }
    }

    internal static string CardHash(JsonElement card) => Hash(writer => WriteJson(writer, card));
    internal static string DefinitionHash(XElement element) => Hash(writer => WriteXml(writer, element));
    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) write(writer);
        return Convert.ToHexString(SHA256.HashData(output.ToArray()));
    }
    private static void WriteJson(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteJson(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var child in value.EnumerateArray()) WriteJson(writer, child); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    private static void WriteXml(Utf8JsonWriter writer, XElement element)
    {
        writer.WriteStartObject(); writer.WriteString("name", element.Name.ToString()); writer.WritePropertyName("attributes"); writer.WriteStartObject();
        foreach (var attribute in element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)) writer.WriteString(attribute.Name.ToString(), attribute.Value);
        writer.WriteEndObject();
        if (element.HasElements)
        {
            writer.WritePropertyName("children"); writer.WriteStartArray();
            foreach (var child in element.Elements().OrderBy(child => child.Name.ToString(), StringComparer.Ordinal)
                .ThenBy(child => child.Attribute("enumID")?.Value, StringComparer.Ordinal).ThenBy(child => child.Attribute("name")?.Value, StringComparer.Ordinal)) WriteXml(writer, child);
            writer.WriteEndArray();
        }
        else writer.WriteString("value", element.Value);
        writer.WriteEndObject();
    }
}
