using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Coaching;

/// <summary>Entity-scoped effect metadata; it never changes a catalog record shared by different card instances.</summary>
public static class SnapshotCardEffects
{
    public static IReadOnlyList<EntitySnapshot> VisibleCards(GameSnapshot snapshot, bool includePublicGraveyard = false)
    {
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        var ownChoice = ChoiceVisibility.ConfirmedOwn(snapshot);
        return Array.AsReadOnly(snapshot.Entities.Values.Where(entity => entity is { Kind: EntityKind.Card, CardId: not null, Tags: not null } &&
            !string.IsNullOrWhiteSpace(entity.CardId) &&
            (entity.Zone == "PLAY" || includePublicGraveyard && entity.Zone == "GRAVEYARD" ||
             local.HasValue && entity.ControllerId == local && entity.Zone is "HAND" or "SECRET" ||
             ownChoice?.Options.Any(option => option.EntityId == entity.Id && option.CardId == entity.CardId &&
                 (entity.ControllerId is null || entity.ControllerId == local)) == true)).ToArray());
    }

    public static IReadOnlyList<int> VisibleDonorDbfIds(GameSnapshot snapshot, string? build)
    {
        if (build != "255790" || snapshot.ClientBuild != build) return [];
        return Array.AsReadOnly(VisibleCards(snapshot).SelectMany(entity => AssembledCardText.ReferencedDbfIds(entity.Tags)).Distinct().Order().ToArray());
    }

    public static async Task<IReadOnlyDictionary<string, CardRecord>> LoadDonorsAsync(GameSnapshot snapshot, string? build,
        CardCatalog catalog, IEnumerable<CardRecord> definitions, CancellationToken cancellationToken = default)
    {
        var result = definitions.DistinctBy(card => card.Id).ToDictionary(card => card.Id, StringComparer.Ordinal);
        var ids = VisibleDonorDbfIds(snapshot, build);
        foreach (var card in await catalog.GetByDbfIdsAsync(ids.Take(500), build, cancellationToken: cancellationToken)) result.TryAdd(card.Id, card);
        return result;
    }

    public static AssembledTextResolution Resolve(GameSnapshot snapshot, string? build, EntitySnapshot entity,
        IReadOnlyDictionary<string, CardRecord> cards) => AssembledCardText.Resolve(
            build == snapshot.ClientBuild ? build : null, entity.CardId, entity.Tags, cards);
}
