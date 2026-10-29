using System.Collections.Frozen;
using System.Globalization;

namespace HsHelper.Core;

public enum GamePhase { Unknown, Starting, Mulligan, Main, Choice, Complete }
public enum EntityKind { Unknown, Game, Player, Card }

public sealed record ChoiceOptionSnapshot(int EntityId, string? CardId, int? ControllerId, int? Position);
public sealed record CurrentChoiceSnapshot(int Id, int? PlayerId, string Type, int? SourceEntityId,
    int CountMin, int CountMax, IReadOnlyList<ChoiceOptionSnapshot> Options, bool IsReady);
public sealed record ObservedGameEvent(long Sequence, string Kind, int? EntityId, int? ControllerId,
    string? CardId, string? FromZone, string? ToZone, string Summary);

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
    public IReadOnlyList<ObservedGameEvent> RecentEvents { get; init; } = [];
    public long EventHistoryTruncatedThrough { get; init; }
    public string? Format { get; init; }
    public string? ClientBuild { get; init; }
    public string? GameType { get; init; }
    public DeckTrackingSnapshot? DeckTracking { get; init; }
    public PublicPlayTrackingSnapshot? PublicPlays { get; init; }
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
