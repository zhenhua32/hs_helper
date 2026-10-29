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
    int Limit = 100, int Offset = 0, string? Keyword = null,
    bool StandardOnly = false, bool StandardPoolOnly = false, DateTimeOffset? At = null);

/// <summary>Explicit, version-pinned evidence. Partial evidence never yields Legal.</summary>
public sealed record CardPoolRules(
    string RuleId, string Build, int Format, DateTimeOffset EffectiveFrom,
    DateTimeOffset ValidUntil, string Source, IReadOnlyList<string> AllowedSets,
    IReadOnlyList<string> AllowedCardIds, IReadOnlyList<string> BannedCardIds,
    IReadOnlyList<string> UnreleasedCardIds, bool IsComplete = false,
    string? Notes = null);

public sealed record DeckCardDescription(int DbfId, int Count, string? CardId, string Name,
    int? Cost, string CardClass, string Rarity, CardLegality Legality);
public sealed record ManaCurveBucket(int Cost, int Count);
public sealed record DeckSideboardDescription(int OwnerDbfId, string? OwnerCardId, string OwnerName,
    IReadOnlyList<DeckCardDescription> Cards, IReadOnlyList<ManaCurveBucket> ManaCurve);
public sealed record DeckDescription(int Format, string? HeroClass,
    IReadOnlyList<DeckCardDescription> Cards, IReadOnlyList<ManaCurveBucket> ManaCurve,
    CardLegality Legality, IReadOnlyList<string> Issues, string Build, string? RuleId)
{
    public IReadOnlyList<DeckSideboardDescription> Sideboards { get; init; } = [];
}

public sealed record DeckCard(int DbfId, int Count);
public sealed record DeckSideboard(int OwnerDbfId, IReadOnlyList<DeckCard> Cards);
public sealed record DeckDefinition(
    int Format, IReadOnlyList<int> Heroes, IReadOnlyList<DeckCard> Cards,
    IReadOnlyList<DeckSideboard> Sideboards);
