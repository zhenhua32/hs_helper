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
    string? Notes = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DeckConstructionEvidence? Construction { get; init; }
}

/// <summary>Reviewed constraints inherit no legality claims from pool membership; their own scope must match the parent rules.</summary>
public sealed record DeckConstructionEvidence(string RuleId, string Build, int Format,
    DateTimeOffset EffectiveFrom, DateTimeOffset ValidUntil, string Source,
    IReadOnlyList<DeckCardGroupLimit> CardGroups, IReadOnlyList<DeckSideboardRule> Sideboards)
{
    // Null is kept absent when old evidence is reserialized, preserving its immutable hash.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeckMainDeckSizeRule>? MainDeckSizes { get; init; }
}
public sealed record DeckMainDeckSizeRule(string OwnerCardId, int RequiredCards, bool KeepsOrdinaryCopyLimits = false);
public sealed record DeckMainDeckSizeSource(string OwnerCardId, string OwnerName, int RequiredCards, string EvidenceId, string EvidenceSource);
public sealed record DeckMainDeckSizeOutcome(long ActualCards, int? RequiredCards, bool IsKnown, bool? IsSatisfied,
    IReadOnlyList<DeckMainDeckSizeSource> Sources, IReadOnlyList<string> Unknowns);
public sealed record DeckCardGroupLimit(string GroupId, IReadOnlyList<string> CardIds, int MaximumCopies);
public sealed record DeckSideboardRule(string OwnerCardId, int MaximumCards,
    IReadOnlyList<string> AllowedCardIds, bool AllowedCardsComplete = false,
    int? MinimumCards = null, int? MaximumCopiesPerCard = null,
    bool KeepsOrdinaryMainDeckRules = false, bool IgnoresHeroClass = false);

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
    public DeckMainDeckSizeOutcome? MainDeckSize { get; init; }
}

public sealed record DeckCard(int DbfId, int Count);
public sealed record DeckSideboard(int OwnerDbfId, IReadOnlyList<DeckCard> Cards);
public sealed record DeckDefinition(
    int Format, IReadOnlyList<int> Heroes, IReadOnlyList<DeckCard> Cards,
    IReadOnlyList<DeckSideboard> Sideboards);
