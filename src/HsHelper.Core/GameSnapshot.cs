using System.Collections.Frozen;
using System.Globalization;

namespace HsHelper.Core;

public enum GamePhase { Unknown, Starting, Mulligan, Main, Choice, Complete }
public enum EntityKind { Unknown, Game, Player, Card }

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
    public IEnumerable<EntitySnapshot> Hand => LocalPlayerId.HasValue
        ? Entities.Values.Where(e => e.Kind == EntityKind.Card && e.ControllerId == LocalPlayerId && e.Zone == "HAND")
            .OrderBy(e => e.ZonePosition ?? int.MaxValue).ThenBy(e => e.Id)
        : [];

    public IEnumerable<EntitySnapshot> Board => Entities.Values
        .Where(e => e.Kind == EntityKind.Card && e.Zone == "PLAY")
        .OrderBy(e => e.ControllerId).ThenBy(e => e.ZonePosition ?? int.MaxValue).ThenBy(e => e.Id);
}
