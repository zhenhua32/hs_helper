namespace HsHelper.Cards;

public enum CardLegality { Unknown, Legal, Illegal }

public sealed record CardRecord(
    string Id, int? DbfId, string Name, string Text, string CardClass,
    string Set, string Type, int? Cost, bool Collectible,
    IReadOnlyList<string> Classes, IReadOnlyList<string> Entourage, string RawJson,
    CardLegality StandardLegality = CardLegality.Unknown);

public sealed record CardManifest(
    string Build, string Locale, string Source, DateTimeOffset ImportedAt,
    string Sha256, int CardCount);

public sealed record CardSearchQuery(
    string? Text = null, string? CardClass = null, bool? Collectible = null,
    int Limit = 100, int Offset = 0);

public sealed record DeckCard(int DbfId, int Count);
public sealed record DeckSideboard(int OwnerDbfId, IReadOnlyList<DeckCard> Cards);
public sealed record DeckDefinition(
    int Format, IReadOnlyList<int> Heroes, IReadOnlyList<DeckCard> Cards,
    IReadOnlyList<DeckSideboard> Sideboards);
