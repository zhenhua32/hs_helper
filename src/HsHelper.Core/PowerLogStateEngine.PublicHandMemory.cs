namespace HsHelper.Core;

public sealed partial class PowerLogStateEngine
{
    private readonly PublicHandMemoryTracker _publicHandMemory = new();
    private string MemoryPerspective => !_isSpectating && _localPlayerId.HasValue
        ? _manualPerspective ? "manual" : "initial_private_hand" : "unconfirmed";

    private int? MemoryObserver => !_isSpectating && _localPlayerId is 1 or 2 &&
        _entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => IntTag(entity, "PLAYER_ID"))
            .Order().SequenceEqual(new int?[] { 1, 2 }) ? _localPlayerId : null;

    private void ObservePublicHandMemory()
    {
        var local = MemoryObserver;
        var observations = _entities.Values.Select(entity => new PublicHandMemoryTracker.Observation(entity.Id,
            entity.Kind, IntTag(entity, "CONTROLLER"), entity.Tags.GetValueOrDefault("ZONE"), IsAttachmentEntity(entity),
            ObservedIdentityProof(entity) == EventIdentityVisibility.Public ? entity.CardId : null, entity.Revision)).ToArray();
        _publicHandMemory.Observe(local, MemoryPerspective, _eventSequence, observations);
    }
}
