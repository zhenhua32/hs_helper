using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json.Serialization;

namespace HsHelper.Core;

public enum GamePhase { Unknown, Starting, Mulligan, Main, Choice, Complete }
public enum EntityKind { Unknown, Game, Player, Card }

public sealed record ChoiceOptionSnapshot(int EntityId, string? CardId, int? ControllerId, int? Position);
public sealed record CurrentChoiceSnapshot(int Id, int? PlayerId, string Type, int? SourceEntityId,
    int CountMin, int CountMax, IReadOnlyList<ChoiceOptionSnapshot> Options, bool IsReady)
{
    public long OfferedAtEventSequence { get; init; }
    public long PreviousOfferedAtEventSequence { get; init; }
    public string? SourceCardId { get; init; }
}

/// <summary>An observed offer and its actual chosen rows, frozen before later entity changes.</summary>
public sealed record ChoiceHistoryEntry
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int ChoiceId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? PlayerId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string Type { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? SourceEntityId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? SourceCardId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int CountMin { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int CountMax { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long OfferedAtEventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long ChosenAtEventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<ChoiceOptionSnapshot> Options { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<EntitySnapshot> OptionEntities { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<int> ChosenEntityIds { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsComplete { get; }

    public ChoiceHistoryEntry(int choiceId, int? playerId, string type, int? sourceEntityId, string? sourceCardId,
        int countMin, int countMax, long offeredAtEventSequence, long chosenAtEventSequence,
        IReadOnlyList<ChoiceOptionSnapshot> options, IReadOnlyList<EntitySnapshot> optionEntities,
        IReadOnlyList<int> chosenEntityIds, bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(optionEntities); ArgumentNullException.ThrowIfNull(chosenEntityIds);
        ChoiceId = choiceId; PlayerId = playerId; Type = type; SourceEntityId = sourceEntityId; SourceCardId = sourceCardId;
        CountMin = countMin; CountMax = countMax; OfferedAtEventSequence = offeredAtEventSequence; ChosenAtEventSequence = chosenAtEventSequence;
        Options = Array.AsReadOnly(options.ToArray());
        OptionEntities = Array.AsReadOnly(optionEntities.Select(entity => entity is null ? null! :
            entity with { Tags = entity.Tags?.ToFrozenDictionary(StringComparer.Ordinal)! }).ToArray());
        ChosenEntityIds = Array.AsReadOnly(chosenEntityIds.ToArray()); IsComplete = isComplete;
    }
}

/// <summary>Perspective-safe choices observed by this version; missing rows and truncation remain explicit.</summary>
public sealed record ChoiceHistorySnapshot
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string MatchId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long StateVersion { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long EventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<ChoiceHistoryEntry> Entries { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsComplete { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long TruncatedThroughSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<string> Warnings { get; }

    public ChoiceHistorySnapshot(string matchId, long stateVersion, long eventSequence,
        IReadOnlyList<ChoiceHistoryEntry> entries, bool isComplete, long truncatedThroughSequence, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(entries); ArgumentNullException.ThrowIfNull(warnings);
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence;
        Entries = Array.AsReadOnly(entries.ToArray()); IsComplete = isComplete;
        TruncatedThroughSequence = truncatedThroughSequence; Warnings = Array.AsReadOnly(warnings.ToArray());
    }
}
public enum EventIdentityVisibility { Unobserved, Public, PlayerOne, PlayerTwo }

public sealed record ObservedGameEvent(long Sequence, string Kind, int? EntityId, int? ControllerId,
    string? CardId, string? FromZone, string? ToZone, string Summary)
{
    // Host evidence captured at the event, not inferred from a later reveal or perspective.
    // Omitting this for legacy records preserves their original serialized representation.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventIdentityVisibility? IdentityVisibility { get; init; }
}

/// <summary>An immutable, perspective-filtered view. Missing attributes remain null.</summary>
public sealed record EntitySnapshot(
    int Id,
    string? CardId,
    EntityKind Kind,
    FrozenDictionary<string, string> Tags)
{
    public string? Zone => Tags.GetValueOrDefault("ZONE");
    public int? ControllerId => GetIntTag("CONTROLLER");
    public int? ZonePosition => GetIntTag("ZONE_POSITION");
    public int? Cost => GetIntTag("COST");
    public int? Attack => GetIntTag("ATK");
    public int? RemainingHealth => GetIntTag("HEALTH") is { } health && GetIntTag("DAMAGE") is { } damage
        ? health - damage : null;

    public int? GetIntTag(string tag) => Tags.TryGetValue(tag, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

public sealed record SnapshotQuality(
    bool HasGameStart,
    bool IsComplete,
    bool IsStable,
    bool HasLocalPerspective,
    int OpenBlockCount,
    IReadOnlyList<string> Warnings);

public sealed record GameSnapshot(
    string MatchId,
    long StateVersion,
    long EventSequence,
    int? Turn,
    int? ActivePlayerId,
    int? LocalPlayerId,
    GamePhase Phase,
    FrozenDictionary<int, EntitySnapshot> Entities,
    SnapshotQuality Quality)
{
    public CurrentChoiceSnapshot? Choice { get; init; }
    public ChoiceHistorySnapshot? ChoiceHistory { get; init; }
    public TurnHistorySnapshot? TurnHistory { get; init; }
    public IReadOnlyList<ObservedGameEvent> RecentEvents { get; init; } = [];
    public long EventHistoryTruncatedThrough { get; init; }
    public string? Format { get; init; }
    public string? ClientBuild { get; init; }
    public string? GameType { get; init; }
    public DeckTrackingSnapshot? DeckTracking { get; init; }
    public PublicPlayTrackingSnapshot? PublicPlays { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicHandMemorySnapshot? PublicHandMemory { get; init; }
    public string PerspectiveSource { get; init; } = "unconfirmed";
    public bool IsSpectating { get; init; }
    public int? FirstPlayerId
    {
        get
        {
            var candidates = Entities.Values.Where(e => e.Kind == EntityKind.Player && e.GetIntTag("FIRST_PLAYER") == 1)
                .Select(e => e.GetIntTag("PLAYER_ID")).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
    public string? LocalOutcome => Player(LocalPlayerId)?.Tags.GetValueOrDefault("PLAYSTATE") switch
    {
        "WON" or "4" => "Won", "LOST" or "5" => "Lost", "TIED" or "6" => "Tied",
        "CONCEDED" or "8" => "Conceded", "DISCONNECTED" or "7" => "Disconnected", _ => null
    };
    public string? LocalClass => HeroClass(LocalPlayerId);
    public string? OpponentClass => LocalPlayerId is null ? null : HeroClass(Entities.Values.Where(e => e.Kind == EntityKind.Player)
        .Select(e => e.GetIntTag("PLAYER_ID")).FirstOrDefault(id => id != LocalPlayerId));

    public IEnumerable<EntitySnapshot> Hand => LocalPlayerId.HasValue
        ? Entities.Values.Where(e => e.Kind == EntityKind.Card && e.ControllerId == LocalPlayerId && e.Zone == "HAND")
            .OrderBy(e => e.ZonePosition ?? int.MaxValue).ThenBy(e => e.Id)
        : [];

    public IEnumerable<EntitySnapshot> Board => Entities.Values
        .Where(e => e.Kind == EntityKind.Card && e.Zone == "PLAY")
        .OrderBy(e => e.ControllerId).ThenBy(e => e.ZonePosition ?? int.MaxValue).ThenBy(e => e.Id);

    private EntitySnapshot? Player(int? id) => id is null ? null : Entities.Values
        .FirstOrDefault(e => e.Kind == EntityKind.Player && e.GetIntTag("PLAYER_ID") == id);
    private string? HeroClass(int? playerId)
    {
        var heroId = Player(playerId)?.GetIntTag("HERO_ENTITY");
        if (heroId is null || !Entities.TryGetValue(heroId.Value, out var hero)) return null;
        return hero.Tags.GetValueOrDefault("CLASS") switch
        {
            "1" => "DEATHKNIGHT", "2" => "DRUID", "3" => "HUNTER", "4" => "MAGE",
            "5" => "PALADIN", "6" => "PRIEST", "7" => "ROGUE", "8" => "SHAMAN",
            "9" => "WARLOCK", "10" => "WARRIOR", "14" => "DEMONHUNTER", var text => text
        };
    }
}
