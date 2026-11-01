using System.Text.Json.Serialization;

namespace HsHelper.Core;

/// <summary>A historical public identity on the same entity, never its hidden current attributes or position.</summary>
public sealed record PublicHandMemoryEntry(int EntityId, string CardId, int ControllerId, string FromZone,
    long PublicAtSequence, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? EnteredHandAtSequence)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicHandDirectRevealEvidence? DirectReveal { get; init; }
}

/// <summary>Closed history-display evidence, distinct from an observed public-zone return.</summary>
public sealed record PublicHandDirectRevealEvidence(string ClientBuild, long ShowAtSequence,
    long HistoryTargetAtSequence, long BlockStartedAtSequence, long ConfirmedAtSequence,
    int SourceEntityId, string SourceCardId, int SourceControllerId, string SourceCardType,
    string SourceZone, string BlockType, long SourcePublicAtSequence = 0);

public sealed record PublicHandMemorySnapshot
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string MatchId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long StateVersion { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long EventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int LocalPlayerId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string PerspectiveSource { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<PublicHandMemoryEntry> Entries { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsComplete { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<string> Warnings { get; }
    // Unlike global snapshot quality, a new observation can settle after an earlier input gap.
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsSettled { get; }
    // Missing legacy evidence stays missing; constructors/deserialization must not promote old records.
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContinuityPolicy { get; }

    public PublicHandMemorySnapshot(string matchId, long stateVersion, long eventSequence, int localPlayerId,
        string perspectiveSource, IReadOnlyList<PublicHandMemoryEntry> entries, bool isComplete,
        IReadOnlyList<string> warnings, bool isSettled = false, string? continuityPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(entries); ArgumentNullException.ThrowIfNull(warnings);
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence; LocalPlayerId = localPlayerId;
        PerspectiveSource = perspectiveSource; Entries = Array.AsReadOnly(entries.ToArray()); IsComplete = isComplete;
        Warnings = Array.AsReadOnly(warnings.ToArray()); IsSettled = isSettled;
        ContinuityPolicy = continuityPolicy;
    }
}

/// <summary>One consumer boundary for a bound ledger. It never restores identities into private entities.</summary>
public static class PublicHandMemoryVisibility
{
    public const string CurrentContinuityPolicy = "input-gaps-v2";
    private static readonly HashSet<string> SafePrivateTags = ["ZONE", "CONTROLLER", "ENTITY_ID", "ZONE_POSITION"];

    public static IReadOnlyList<string> KnownCardIds(GameSnapshot snapshot) => Array.AsReadOnly(
        (Confirmed(snapshot)?.Entries.SelectMany(entry => entry.DirectReveal is { } direct
            ? new[] { entry.CardId, direct.SourceCardId } : new[] { entry.CardId }) ?? [])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    public static PublicHandMemorySnapshot? Confirmed(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not (1 or 2) || snapshot.PublicHandMemory is not { } memory ||
            snapshot.Quality.OpenBlockCount != 0 ||
            !(snapshot.Quality.IsStable || !snapshot.Quality.IsComplete && memory.IsSettled) ||
            memory.MatchId != snapshot.MatchId || memory.StateVersion != snapshot.StateVersion ||
            memory.EventSequence != snapshot.EventSequence || memory.LocalPlayerId != snapshot.LocalPlayerId ||
            memory.PerspectiveSource != snapshot.PerspectiveSource ||
            memory.PerspectiveSource is not ("manual" or "initial_private_hand") || memory.Entries is null || memory.Warnings is null ||
            snapshot.Quality.Warnings.Contains("结算块结束缺少对应开始") && memory.ContinuityPolicy != CurrentContinuityPolicy ||
            memory.StateVersion < 0 || memory.EventSequence < 0 ||
            !snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => entity.GetIntTag("PLAYER_ID"))
                .Order().SequenceEqual(new int?[] { 1, 2 }) ||
            memory.Entries.Count > 1000 ||
            memory.Entries.Any(entry => entry is null) || memory.Entries.Select(entry => entry.EntityId).Distinct().Count() != memory.Entries.Count)
            return null;
        foreach (var entry in memory.Entries)
        {
            if (entry.EntityId <= 0 || !CardToken(entry.CardId) || entry.ControllerId is not (1 or 2) ||
                entry.ControllerId == memory.LocalPlayerId || !ValidEvidence(snapshot, entry) ||
                !snapshot.Entities.TryGetValue(entry.EntityId, out var entity) || entity.Id != entry.EntityId ||
                entity.Kind != EntityKind.Card || entity.Zone != "HAND" || entity.ControllerId != entry.ControllerId ||
                entity.CardId is not null || entity.Tags.Keys.Any(tag => !SafePrivateTags.Contains(tag)) ||
                entity.Tags.TryGetValue("ENTITY_ID", out var rawId) && rawId != entry.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return null;
        }
        return !snapshot.Quality.IsComplete
            ? new(memory.MatchId, memory.StateVersion, memory.EventSequence, memory.LocalPlayerId, memory.PerspectiveSource,
                memory.Entries, false, Array.AsReadOnly(memory.Warnings.Append("局面其它信息不完整；这里只保留具有独立连续公开证据的手牌记忆，不代表其余手牌。").Distinct(StringComparer.Ordinal).ToArray()), memory.IsSettled, memory.ContinuityPolicy)
            : memory;
    }

    internal static bool CardToken(string? value) => value is { Length: > 0 and <= 200 } &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool ValidEvidence(GameSnapshot snapshot, PublicHandMemoryEntry entry)
    {
        if (entry.DirectReveal is not { } direct)
            return entry.FromZone is "PLAY" or "GRAVEYARD" && entry.EnteredHandAtSequence is { } entered &&
                entry.PublicAtSequence > 0 && entry.PublicAtSequence <= entered && entered <= snapshot.EventSequence;
        return entry.FromZone == "HAND" && entry.EnteredHandAtSequence is null && entry.CardId != "TIME_875t" &&
            snapshot.ClientBuild == "255790" && direct.ClientBuild == snapshot.ClientBuild &&
            direct.BlockStartedAtSequence > 0 && direct.ShowAtSequence > direct.BlockStartedAtSequence &&
            direct.HistoryTargetAtSequence >= direct.BlockStartedAtSequence &&
            direct.ConfirmedAtSequence > Math.Max(direct.ShowAtSequence, direct.HistoryTargetAtSequence) &&
            direct.ConfirmedAtSequence == entry.PublicAtSequence && direct.ConfirmedAtSequence <= snapshot.EventSequence &&
            direct.SourcePublicAtSequence >= direct.BlockStartedAtSequence && direct.SourcePublicAtSequence < direct.ConfirmedAtSequence &&
            direct.SourceEntityId > 0 && direct.SourceEntityId != entry.EntityId && CardToken(direct.SourceCardId) &&
            direct.SourceControllerId is 1 or 2 && OrdinaryType(direct.SourceCardType) &&
            direct.SourceZone is "PLAY" or "GRAVEYARD" && direct.BlockType is "PLAY" or "POWER" or "TRIGGER";
    }

    internal static bool OrdinaryType(string? type) => type is "HERO" or "MINION" or "SPELL" or "WEAPON" or "HERO_POWER" or
        "LOCATION" or "ITEM" or "TOKEN" or "3" or "4" or "5" or "7" or "8" or "9" or "10" or "39";
}

/// <summary>Raw-engine observer. Only public identities enter this state, never private CardID values.</summary>
internal sealed class PublicHandMemoryTracker
{
    internal sealed record Observation(int EntityId, EntityKind Kind, int? ControllerId, string? Zone,
        bool IsAttachment, string? PublicCardId, long Revision);
    private sealed record Source(string CardId, int ControllerId, string Zone, long Sequence, long Revision);
    private readonly Dictionary<int, Source> _public = [];
    private readonly Dictionary<int, PublicHandMemoryEntry> _hand = [];
    private readonly HashSet<int> _freshIdentity = [];
    private int? _local;
    private string? _perspective;
    private bool _needsFreshIdentity, _historyLost;

    public void Reset()
    { _public.Clear(); _hand.Clear(); _freshIdentity.Clear(); _local = null; _perspective = null; _needsFreshIdentity = _historyLost = false; }
    public void Gap()
    { _public.Clear(); _hand.Clear(); _freshIdentity.Clear(); _needsFreshIdentity = _historyLost = true; }
    public void Invalidate(int entityId)
    { _public.Remove(entityId); _hand.Remove(entityId); }
    public void IdentityObserved(int entityId)
    { Invalidate(entityId); _freshIdentity.Add(entityId); }
    public bool HasContinuousIdentity(int entityId) => !_needsFreshIdentity || _freshIdentity.Contains(entityId);
    public void ConfirmDirect(PublicHandMemoryEntry entry) => _hand.TryAdd(entry.EntityId, entry);
    public void PerspectiveChanged()
    { _public.Clear(); _hand.Clear(); _freshIdentity.Clear(); _local = null; _perspective = null; _needsFreshIdentity = _historyLost; }

    public void Observe(int? local, string perspective, long sequence, IReadOnlyList<Observation> entities)
    {
        if (local is not (1 or 2)) { if (_local.HasValue) PerspectiveChanged(); return; }
        if (_local.HasValue && (_local != local || _perspective != perspective)) PerspectiveChanged();
        _local = local; _perspective = perspective;
        var present = entities.Select(entity => entity.EntityId).ToHashSet();
        foreach (var id in _public.Keys.Concat(_hand.Keys).Where(id => !present.Contains(id)).Distinct().ToArray()) Invalidate(id);
        foreach (var entity in entities)
        {
            if (entity.Kind != EntityKind.Card || entity.IsAttachment || entity.ControllerId is not (1 or 2) ||
                entity.Zone is not ("PLAY" or "GRAVEYARD" or "SECRET" or "HAND"))
            { Invalidate(entity.EntityId); continue; }
            if (_public.TryGetValue(entity.EntityId, out var source) && source.ControllerId != entity.ControllerId)
            { Invalidate(entity.EntityId); source = null; }
            if (entity.Zone != "HAND") _hand.Remove(entity.EntityId);
            if (entity.PublicCardId is { } publicCard && PublicHandMemoryVisibility.CardToken(publicCard) &&
                entity.Zone is "PLAY" or "GRAVEYARD" && sequence > 0 && (!_needsFreshIdentity || _freshIdentity.Contains(entity.EntityId)))
            {
                if (source is null || source.Revision != entity.Revision)
                    _public[entity.EntityId] = source = new(publicCard, entity.ControllerId.Value, entity.Zone, sequence, entity.Revision);
            }
            if (entity.Zone == "HAND" && entity.ControllerId != local && source is not null && !_hand.ContainsKey(entity.EntityId))
                _hand[entity.EntityId] = new(entity.EntityId, source.CardId, source.ControllerId, source.Zone, source.Sequence, sequence);
        }
    }

    public PublicHandMemorySnapshot? Snapshot(string matchId, long version, long sequence, int? local,
        string perspective, bool complete, bool settled)
    {
        if (local is not (1 or 2) || local != _local || perspective != _perspective) return null;
        return new(matchId, version, sequence, local.Value, perspective,
            _hand.Values.OrderBy(entry => entry.EnteredHandAtSequence).ThenBy(entry => entry.EntityId).ToArray(),
            complete && !_historyLost, _historyLost ? ["公开手牌记忆此前有记录缺口；仅列缺口后重新公开并连续追踪的身份，不代表其余手牌。"] : [], settled,
            PublicHandMemoryVisibility.CurrentContinuityPolicy);
    }
}
