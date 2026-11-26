namespace HsHelper.Core;

public sealed partial class PowerLogStateEngine
{
    private int? IdentityObserver => !_isSpectating && _localPlayerId is 1 or 2 ? _localPlayerId : null;

    private AttachmentVisibility.Node? VisibilityNode(int id)
    {
        if (!_entities.TryGetValue(id, out var entity)) return null;
        var zone = entity.Tags.GetValueOrDefault("ZONE");
        var type = entity.Tags.GetValueOrDefault("CARDTYPE");
        var hero = type is null && (zone is "PLAY" or "GRAVEYARD") &&
            _entities.Values.Any(player => player.Kind == EntityKind.Player && IntTag(player, "HERO_ENTITY") == id);
        var option = GetOwnChoice() is { Ready: true } choice &&
            (choice.TaskList is null || _taskComplete) && IdentityObserver == choice.PrivateOwner &&
            choice.Options.Any(row => row.EntityId == id && row.ControllerId == IdentityObserver && row.CardId == entity.CardId) &&
            IntTag(entity, "CONTROLLER") == IdentityObserver;
        return new(id, entity.Kind, type, zone, IntTag(entity, "CONTROLLER"),
            entity.Tags.GetValueOrDefault("ATTACHED"), entity.Tags.GetValueOrDefault("40"), entity.Hidden, hero, option);
    }

    private AttachmentVisibility.Observation IdentityObservation(Entity entity) => AttachmentVisibility.Resolve(entity.Id, VisibilityNode);
    private EventIdentityVisibility ObservedIdentityProof(Entity entity) => AttachmentVisibility.Proof(IdentityObservation(entity).Audience, IdentityObserver);
    private bool IsAttachmentEntity(Entity entity) => VisibilityNode(entity.Id) is { } node && AttachmentVisibility.IsAttachment(node);

    // Prepare can move anonymous effects to PLAY while the source card remains in the owner's hand.
    // An untyped entity that entered the board through another path still blocks analysis.
    private int? PreparationPlaceholderOwner(Entity entity)
    {
        if (_blockCount != 2 || _turnBlocks.Count != 2 || _turnBlocks.Peek() is not ("POWER", int sourceId) ||
            _turnBlocks.Last() != ("DECK_ACTION", sourceId) || !_entities.TryGetValue(sourceId, out var source) ||
            source.CardId is not null || source.Tags.GetValueOrDefault("ZONE") != "HAND" ||
            (source.Tags.GetValueOrDefault("PREPARE_REVEALED") ?? source.Tags.GetValueOrDefault("4459")) != "1")
            return null;
        var owner = IntTag(source, "CONTROLLER");
        return owner is 1 or 2 && IntTag(entity, "CONTROLLER") == owner && IsAnonymousPreparationPlaceholder(entity) ? owner : null;
    }

    private static bool IsAnonymousPreparationPlaceholder(Entity entity) => entity.CardId is null && !entity.Hidden &&
        entity.Kind == EntityKind.Card && IntTag(entity, "ZONE_POSITION") == 0 && IntTag(entity, "ENTITY_ID") == entity.Id &&
        entity.Tags.Keys.All(HiddenSafeTags.Contains);
}
