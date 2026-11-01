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
}
