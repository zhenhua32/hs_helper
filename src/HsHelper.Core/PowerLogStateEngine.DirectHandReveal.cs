namespace HsHelper.Core;

public sealed partial class PowerLogStateEngine
{
    // SHOW alone is private. Retain candidates only inside a completely observed history scope;
    // no candidate or partially closed child is ever published in a snapshot or event.
    private sealed class HistoryBlock(long started, string type, int source, string card, int owner, string category, string zone,
        int? observer, string perspective, bool carrier, bool valid)
    {
        public long Started { get; } = started;
        public string Type { get; } = type;
        public int Source { get; } = source;
        public string Card { get; set; } = card;
        public int Owner { get; } = owner;
        public string Category { get; set; } = category;
        public string Zone { get; set; } = zone;
        public int? Observer { get; } = observer;
        public string Perspective { get; } = perspective;
        public bool Carrier { get; } = carrier;
        public bool PublicSource { get; set; } = carrier;
        public long PublicSourceAt { get; set; }
        public bool Valid { get; set; } = valid;
        public Dictionary<int, HistoryShow> Shows { get; } = [];
        public Dictionary<int, HistoryTarget> Targets { get; } = [];
        public HashSet<int> ConflictedTargets { get; } = [];
        public List<(HistoryBlock Block, HistoryShow Show, long Target)> Closed { get; } = [];
    }

    private sealed record HistoryShow(int Id, string Card, int Owner, string Category, long Sequence);
    private sealed record HistoryTarget(long Sequence, string? Card, int? Owner);
    private sealed class HistoryMeta(HistoryBlock block, string type, int count)
    {
        public HistoryBlock Block { get; } = block;
        public string Type { get; } = type;
        public int Count { get; } = count;
        public Dictionary<int, int> Entities { get; } = [];
        public Dictionary<int, (string? Card, int? Owner)> Descriptors { get; } = [];
        public bool Invalid { get; set; }
    }

    private readonly Stack<HistoryBlock> _historyBlocks = [];
    private HistoryMeta? _historyMeta;

    private static bool HistoryReference(string descriptor) =>
        descriptor == "GameEntity" || int.TryParse(descriptor, out var numeric) && numeric > 0 ||
        descriptor.StartsWith('[') && descriptor.EndsWith(']') && ReferencePattern.Matches(descriptor).Count == 1 &&
        DescriptorCardPattern.Matches(descriptor).Count <= 1 && DescriptorPlayerPattern.Matches(descriptor).Count <= 1 &&
        Pattern(@"\bzone=(\w+)").Matches(descriptor).Count <= 1;

    private int? HistoryEntityId(string descriptor)
    {
        if (!HistoryReference(descriptor)) return null;
        if (descriptor == "GameEntity") return _gameEntity;
        if (int.TryParse(descriptor, out var numeric) && numeric > 0) return numeric;
        var match = ReferencePattern.Match(descriptor);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }

    private void InvalidateHistoryScopes()
    {
        foreach (var block in _historyBlocks) { block.Valid = false; block.Shows.Clear(); block.Targets.Clear(); block.Closed.Clear(); }
        _historyMeta = null;
    }

    private void InvalidateHistoryEntity(int id, bool keepTargets = false, bool keepSource = false)
    {
        foreach (var block in _historyBlocks)
        {
            if (!keepSource && block.Source == id) block.Valid = false;
            block.Shows.Remove(id);
            if (!keepTargets) block.Targets.Remove(id);
            block.Closed.RemoveAll(candidate => candidate.Show.Id == id || !keepSource && candidate.Block.Source == id);
        }
    }

    private void FinishHistoryMeta()
    {
        if (_historyMeta is not { } meta) return;
        _historyMeta = null;
        if (meta.Invalid || meta.Entities.Count != meta.Count || meta.Entities.Values.Distinct().Count() != meta.Count ||
            !meta.Entities.Keys.Order().SequenceEqual(Enumerable.Range(0, meta.Count)))
        { meta.Block.Valid = false; return; }
        if (meta.Type == "HISTORY_TARGET")
            foreach (var (index, id) in meta.Entities)
            {
                var descriptor = meta.Descriptors.GetValueOrDefault(index);
                var target = new HistoryTarget(_eventSequence, descriptor.Card, descriptor.Owner);
                if (meta.Block.Targets.TryGetValue(id, out var old) &&
                    (old.Card is not null && target.Card is not null && old.Card != target.Card ||
                     old.Owner.HasValue && target.Owner.HasValue && old.Owner != target.Owner)) meta.Block.ConflictedTargets.Add(id);
                else meta.Block.Targets.TryAdd(id, target);
            }
    }

    private void HistoryBoundary(string content)
    {
        if (!content.StartsWith("Info[", StringComparison.Ordinal)) FinishHistoryMeta();
    }

    private void StartHistoryBlock(string content)
    {
        var match = Pattern(@"^BLOCK_START\s+BlockType=(\w+)\s+Entity=(.+?)\s+EffectCardId=\S*\s+EffectIndex=-?\d+\s+Target=.+?\s+SubOption=-?\d+(?:\s+TriggerKeyword=(\w+))?$").Match(content);
        var descriptor = match.Success ? match.Groups[2].Value : "";
        var id = HistoryEntityId(descriptor) ?? 0;
        var type = match.Success ? match.Groups[1].Value : "UNKNOWN";
        _entities.TryGetValue(id, out var source);
        var declaredCard = DescriptorCardPattern.Match(descriptor);
        var card = source?.CardId ?? (declaredCard.Success ? declaredCard.Groups[1].Value : "");
        var owner = source is null ? null : IntTag(source, source.Kind == EntityKind.Player ? "PLAYER_ID" : "CONTROLLER");
        var category = source?.Tags.GetValueOrDefault("CARDTYPE") ?? "";
        var zone = source?.Tags.GetValueOrDefault("ZONE") ?? "";
        var declaredOwner = DescriptorPlayerPattern.Match(descriptor);
        var declaredZone = Pattern(@"\bzone=(\w+)").Match(descriptor);
        var carrier = source is { Hidden: false } &&
            (source.Kind == EntityKind.Game && source.Id == _gameEntity || source.Kind == EntityKind.Player && owner is 1 or 2);
        var valid = match.Success && HistoryReference(descriptor) && type is "PLAY" or "POWER" or "TRIGGER" &&
            match.Groups[3].Value is "" or "0" or "BATTLECRY" or "DEATHRATTLE" or "217" or "218" &&
            _clientBuild == "255790" && GetPhase() == GamePhase.Main && MemoryObserver is 1 or 2 &&
            (carrier || source is { Kind: EntityKind.Card } && owner is 1 or 2 &&
                zone is "PLAY" or "GRAVEYARD" or "HAND" && (card.Length == 0 || PublicHandMemoryVisibility.CardToken(card))) &&
            (!declaredCard.Success || (carrier ? NullIfEmpty(declaredCard.Groups[1].Value) == source!.CardId : declaredCard.Groups[1].Value == card)) &&
            (!declaredOwner.Success || int.TryParse(declaredOwner.Groups[1].Value, out var declaredPlayer) && declaredPlayer == owner) &&
            (!declaredZone.Success || declaredZone.Groups[1].Value == zone);
        _historyBlocks.Push(new(_eventSequence, type, id, card, owner ?? 0, category, zone, MemoryObserver, MemoryPerspective, carrier, valid));
        ObserveHistorySources();
    }

    private void ObserveHistorySources()
    {
        foreach (var block in _historyBlocks)
        {
            if (MemoryObserver is not (1 or 2) || MemoryObserver != block.Observer || MemoryPerspective != block.Perspective)
                block.Valid = false;
            if (!block.Valid || block.PublicSource || !_entities.TryGetValue(block.Source, out var source)) continue;
            if (block.Card.Length > 0 && source.CardId is not null && source.CardId != block.Card || IntTag(source, "CONTROLLER") != block.Owner) { block.Valid = false; continue; }
            var zone = source.Tags.GetValueOrDefault("ZONE");
            var category = source.Tags.GetValueOrDefault("CARDTYPE");
            if (zone is "PLAY" or "GRAVEYARD" && PublicHandMemoryVisibility.OrdinaryType(category) &&
                !IsAttachmentEntity(source) && ObservedIdentityProof(source) == EventIdentityVisibility.Public &&
                PublicHandMemoryVisibility.CardToken(source.CardId) && _publicHandMemory.HasContinuousIdentity(source.Id))
            { block.PublicSource = true; block.Card = source.CardId!; block.Zone = zone; block.Category = category!; block.PublicSourceAt = _eventSequence; }
        }
    }

    private void ObserveHistoryShow(Entity entity)
    {
        if (_historyBlocks.Count == 0 || _historyBlocks.Peek().ConflictedTargets.Contains(entity.Id) || MemoryObserver is not (1 or 2) || GetPhase() != GamePhase.Main ||
            entity.Kind != EntityKind.Card || entity.Tags.GetValueOrDefault("ZONE") != "HAND" ||
            IntTag(entity, "CONTROLLER") is not (1 or 2) || IntTag(entity, "CONTROLLER") == MemoryObserver ||
            !PublicHandMemoryVisibility.CardToken(entity.CardId) || entity.CardId == "TIME_875t" ||
            entity.Tags.TryGetValue("CARDTYPE", out var category) && !PublicHandMemoryVisibility.OrdinaryType(category) ||
            entity.Tags.TryGetValue("ATTACHED", out var attached) && attached != "0") return;
        _historyBlocks.Peek().Shows[entity.Id] = new(entity.Id, entity.CardId!, IntTag(entity, "CONTROLLER")!.Value,
            entity.Tags.GetValueOrDefault("CARDTYPE") ?? "", _eventSequence);
    }

    private void RefreshHistoryCategories()
    {
        foreach (var block in _historyBlocks)
            foreach (var (id, show) in block.Shows.ToArray())
                if (show.Category.Length == 0 && _entities.TryGetValue(id, out var entity) && entity.CardId == show.Card &&
                    IntTag(entity, "CONTROLLER") == show.Owner && entity.Tags.GetValueOrDefault("ZONE") == "HAND" &&
                    PublicHandMemoryVisibility.OrdinaryType(entity.Tags.GetValueOrDefault("CARDTYPE")))
                    block.Shows[id] = show with { Category = entity.Tags["CARDTYPE"] };
    }

    private void PrepareHistoryIdentity(Entity entity, string? card, bool show)
    {
        // Numeric Info can have an observed identity even though its descriptor omitted CardID.
        // Retain that private value only as an internal epoch constraint, never as authorization.
        var targetChanged = entity.CardId is not null && entity.CardId != card ||
            _historyBlocks.Any(block => block.Targets.TryGetValue(entity.Id, out var target) && target.Card is not null && target.Card != card);
        if (show)
            foreach (var block in _historyBlocks)
                if (block.Shows.TryGetValue(entity.Id, out var previous) && previous.Card != card)
                    block.ConflictedTargets.Add(entity.Id);
        // An opponent's played source often has no raw identity until its first actual SHOW.
        // A descriptor only proposes the match; it cannot make PublicSource true.
        var sameSource = show && (entity.CardId == card || entity.CardId is null &&
            _historyBlocks.Where(block => block.Source == entity.Id).All(block => block.Card.Length == 0 || block.Card == card));
        InvalidateHistoryEntity(entity.Id, keepTargets: show && !targetChanged, keepSource: sameSource);
    }

    private bool ProcessHistoryMeta(string content)
    {
        if (content.StartsWith("META_DATA", StringComparison.Ordinal))
        {
            _currentEntity = null;
            var match = Pattern(@"^META_DATA\s*-\s*Meta=(\w+)\s+Data=-?\d+\s+InfoCount=(\d+)$").Match(content);
            if (_historyBlocks.TryPeek(out var block))
            {
                if (!match.Success || match.Groups[1].Value == "OVERRIDE_HISTORY") block.Valid = false;
                else if (int.TryParse(match.Groups[2].Value, out var count) && count <= 1000)
                    _historyMeta = new(block, match.Groups[1].Value, count);
                else block.Valid = false;
            }
            return false;
        }
        if (!content.StartsWith("Info[", StringComparison.Ordinal)) return false;
        _currentEntity = null;
        if (_historyMeta is { } meta)
        {
            var match = Pattern(@"^Info\[(\d+)\]\s*=\s*(.+)$").Match(content);
            var id = match.Success ? HistoryEntityId(match.Groups[2].Value.Trim()) : null;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index >= meta.Count ||
                id is null || !HistoryReference(match.Groups[2].Value.Trim()) || !meta.Entities.TryAdd(index, id.Value)) meta.Invalid = true;
            else if (meta.Type == "HISTORY_TARGET")
            {
                // A descriptor may add counterevidence; it cannot replace a real same-entity SHOW.
                var descriptor = match.Groups[2].Value;
                var declaredCard = DescriptorCardPattern.Match(descriptor);
                var declaredOwner = DescriptorPlayerPattern.Match(descriptor);
                var declaredZones = Pattern(@"\bzone=([^\s\]]*)").Matches(descriptor);
                string? card = declaredCard.Success ? NullIfEmpty(declaredCard.Groups[1].Value) : null;
                int? owner = declaredOwner.Success && int.TryParse(declaredOwner.Groups[1].Value, out var parsedOwner) ? parsedOwner : null;
                if (!_entities.TryGetValue(id.Value, out var entity) ||
                    declaredOwner.Success && !owner.HasValue ||
                    card is not null && entity.CardId is not null && card != entity.CardId ||
                    owner.HasValue && IntTag(entity, "CONTROLLER") is { } actual && owner != actual ||
                    declaredZones.Count > 1 || declaredZones.Count == 1 &&
                    NormalizeValue("ZONE", declaredZones[0].Groups[1].Value) != entity.Tags.GetValueOrDefault("ZONE")) meta.Invalid = true;
                else meta.Descriptors[index] = (card ?? entity.CardId, owner ?? IntTag(entity, "CONTROLLER"));
            }
            if (!meta.Invalid && meta.Entities.Count == meta.Count) FinishHistoryMeta();
        }
        else if (_historyBlocks.TryPeek(out var block)) block.Valid = false;
        return false;
    }

    private void EndHistoryBlock()
    {
        if (!_historyBlocks.TryPop(out var block)) return;
        if (!block.Valid || !block.PublicSource) return;
        foreach (var show in block.Carrier ? [] : block.Shows.Values.AsEnumerable())
            if (PublicHandMemoryVisibility.OrdinaryType(show.Category) && !block.ConflictedTargets.Contains(show.Id) && block.Targets.TryGetValue(show.Id, out var target) &&
                (target.Card is null || target.Card == show.Card) && (target.Owner is null || target.Owner == show.Owner))
                block.Closed.Add((block, show, target.Sequence));
        if (_historyBlocks.TryPeek(out var parent)) { parent.Closed.AddRange(block.Closed); return; }
        if (_clientBuild != "255790" || GetPhase() != GamePhase.Main || MemoryObserver is not (1 or 2)) return;
        foreach (var (scope, show, target) in block.Closed)
        {
            if (!_entities.TryGetValue(show.Id, out var entity) || entity.Kind != EntityKind.Card ||
                entity.Tags.GetValueOrDefault("ZONE") != "HAND" || IntTag(entity, "CONTROLLER") != show.Owner ||
                show.Owner == MemoryObserver || entity.CardId is not null && entity.CardId != show.Card ||
                entity.Tags.TryGetValue("CARDTYPE", out var category) && !PublicHandMemoryVisibility.OrdinaryType(category) ||
                entity.Tags.TryGetValue("ATTACHED", out var attached) && attached != "0") continue;
            var evidence = new PublicHandDirectRevealEvidence("255790", show.Sequence, target, scope.Started, _eventSequence,
                scope.Source, scope.Card, scope.Owner, scope.Category, scope.Zone, scope.Type, scope.PublicSourceAt);
            _publicHandMemory.ConfirmDirect(new(show.Id, show.Card, show.Owner, "HAND", _eventSequence, null) { DirectReveal = evidence });
        }
    }
}
