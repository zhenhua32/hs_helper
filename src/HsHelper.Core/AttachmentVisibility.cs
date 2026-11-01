using System.Collections.Frozen;
using System.Globalization;

namespace HsHelper.Core;

/// <summary>One observer boundary for modifiers whose own PLAY zone does not make their target public.</summary>
public static class AttachmentVisibility
{
    internal enum Audience { Concealed, Unresolved, Public, PlayerOne, PlayerTwo }
    internal readonly record struct Node(int Id, EntityKind Kind, string? Type, string? Zone, int? Controller,
        string? Attached, string? AttachedAlias, bool Hidden = false, bool PublicHero = false, bool OwnOption = false);
    internal readonly record struct Observation(Audience Audience, bool ChoiceBound = false);

    internal static bool IsAttachment(Node node) => node.Type is "ENCHANTMENT" or "6" ||
        node.Zone is "PLAY" or "GRAVEYARD" && (HasBinding(node.Attached) || HasBinding(node.AttachedAlias));

    private static bool HasBinding(string? value) => value is not null &&
        (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number != 0);

    internal static Observation Resolve(int id, Func<int, Node?> find)
    {
        HashSet<int>? visited = null;
        var choiceBound = false;
        Audience? sourcePrivate = null;
        for (var depth = 0; depth < 256; depth++)
        {
            if (find(id) is not { } node || node.Id != id || node.Kind == EntityKind.Unknown)
                return new(Audience.Unresolved);
            if (node.Kind is EntityKind.Game or EntityKind.Player) return Combine(Audience.Public);
            if (node.Hidden) return new(Audience.Concealed);
            var attachment = IsAttachment(node);
            var self = node.OwnOption ? node.Controller switch
                { 1 => Audience.PlayerOne, 2 => Audience.PlayerTwo, _ => Audience.Unresolved } : node.Zone switch
            {
                "PLAY" or "GRAVEYARD" => attachment || node.PublicHero || IsPublicType(node.Type)
                    ? Audience.Public : Audience.Unresolved,
                "HAND" or "SECRET" => node.Controller switch { 1 => Audience.PlayerOne, 2 => Audience.PlayerTwo, _ => Audience.Unresolved },
                _ => Audience.Concealed
            };
            choiceBound |= node.OwnOption;
            if (!attachment) return Combine(self);
            if (self is Audience.Concealed or Audience.Unresolved) return new(self);
            if (self is Audience.PlayerOne or Audience.PlayerTwo)
            {
                if (sourcePrivate.HasValue && sourcePrivate != self) return new(Audience.Concealed);
                sourcePrivate = self;
            }
            visited ??= [];
            if (!visited.Add(id) || !TryAttached(node, out id)) return new(Audience.Unresolved);
        }
        return new(Audience.Unresolved);

        Observation Combine(Audience target) => new(target is Audience.Concealed or Audience.Unresolved ? target :
            sourcePrivate.HasValue && target != Audience.Public && target != sourcePrivate ? Audience.Concealed : sourcePrivate ?? target, choiceBound);
    }

    private static bool IsPublicType(string? type) => type is "HERO" or "MINION" or "SPELL" or "WEAPON" or "HERO_POWER" or
        "LOCATION" or "ITEM" or "TOKEN" or "3" or "4" or "5" or "7" or "8" or "9" or "10" or "39";

    private static bool TryAttached(Node node, out int id)
    {
        id = 0; int? value = null;
        foreach (var raw in new[] { node.Attached, node.AttachedAlias })
            if (raw is not null)
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number <= 0 || value.HasValue && number != value)
                    return false;
                value = number;
            }
        id = value ?? 0;
        return value.HasValue;
    }

    internal static bool CanObserve(Audience audience, int? local) => audience == Audience.Public ||
        local == 1 && audience == Audience.PlayerOne || local == 2 && audience == Audience.PlayerTwo;

    internal static EventIdentityVisibility Proof(Audience audience, int? local) => !CanObserve(audience, local)
        ? EventIdentityVisibility.Unobserved : audience switch
        {
            Audience.Public => EventIdentityVisibility.Public,
            Audience.PlayerOne => EventIdentityVisibility.PlayerOne,
            Audience.PlayerTwo => EventIdentityVisibility.PlayerTwo,
            _ => EventIdentityVisibility.Unobserved
        };

    public static bool IsAttachment(EntitySnapshot entity) => IsAttachment(new Node(entity.Id, entity.Kind,
        entity.Tags.GetValueOrDefault("CARDTYPE"), entity.Zone, entity.ControllerId,
        entity.Tags.GetValueOrDefault("ATTACHED"), entity.Tags.GetValueOrDefault("40")));

    /// <summary>Captures the audience for a currently visible identity; never use it to backfill an older event.</summary>
    public static EventIdentityVisibility IdentityAt(GameSnapshot snapshot, int entityId) =>
        Proof(Resolve(entityId, CreateFinder(snapshot)).Audience, ChoiceVisibility.ConfirmedLocalPlayer(snapshot));

    /// <summary>Legacy identities with no observed category cannot be assumed to be ordinary public cards.</summary>
    public static bool HasKnownOrdinaryCategory(GameSnapshot snapshot, int entityId) =>
        snapshot.Entities.TryGetValue(entityId, out var entity) && !IsAttachment(entity) &&
        (entity.Kind is EntityKind.Game or EntityKind.Player || IsPublicType(entity.Tags.GetValueOrDefault("CARDTYPE")) ||
            !entity.Tags.ContainsKey("CARDTYPE") && snapshot.Entities.Values.Any(player => player.Kind == EntityKind.Player && player.GetIntTag("HERO_ENTITY") == entityId));

    private static Func<int, Node?> CreateFinder(GameSnapshot snapshot)
    {
        var ownChoice = ChoiceVisibility.ConfirmedOwn(snapshot);
        var optionIds = ownChoice?.Options.Where(option => snapshot.Entities.TryGetValue(option.EntityId, out var entity) &&
            ChoiceVisibility.IsProjectedCurrentOptionEntity(snapshot, entity)).Select(option => option.EntityId).ToHashSet() ?? [];
        var heroes = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player)
            .Select(entity => entity.GetIntTag("HERO_ENTITY")).OfType<int>().ToHashSet();
        return id => snapshot.Entities.TryGetValue(id, out var entity) && entity.Tags is not null
            ? new(entity.Id, entity.Kind, entity.Tags.GetValueOrDefault("CARDTYPE"), entity.Zone, entity.ControllerId,
                entity.Tags.GetValueOrDefault("ATTACHED"), entity.Tags.GetValueOrDefault("40"),
                PublicHero: !entity.Tags.ContainsKey("CARDTYPE") && heroes.Contains(id), OwnOption: optionIds.Contains(id)) : null;
    }

    /// <summary>Returns a derived observer view; original snapshot bytes and historical bindings remain unchanged.</summary>
    public static GameSnapshot Project(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        var find = CreateFinder(snapshot);
        var attachments = snapshot.Entities.Where(pair => find(pair.Key) is { } node && IsAttachment(node)).ToArray();
        HashSet<int>? hidden = null;
        Dictionary<int, EntitySnapshot>? limited = null;
        var unresolvedActive = false;
        foreach (var pair in attachments)
        {
            var observation = Resolve(pair.Key, find);
            if (!CanObserve(observation.Audience, local))
            {
                (hidden ??= []).Add(pair.Key);
                unresolvedActive |= observation.Audience == Audience.Unresolved && pair.Value.Zone == "PLAY";
            }
            else if (observation.ChoiceBound)
            {
                var tags = pair.Value.Tags.Where(tag => ChoiceVisibility.IsObservedOptionTag(tag.Key) ||
                    tag.Key is "ATTACHED" or "40" or "TAG_SCRIPT_DATA_NUM_6" or "2921" or "IS_NIGHTMARE_BONUS" or "3567" or
                        "ENCHANTMENT_BIRTH_VISUAL" or "330" or "ENCHANTMENT_IDLE_VISUAL" or "331").ToFrozenDictionary(StringComparer.Ordinal);
                if (tags.Count != pair.Value.Tags.Count) (limited ??= [])[pair.Key] = pair.Value with { Tags = tags };
            }
        }
        var attachmentIds = attachments.Select(pair => pair.Key).ToHashSet();
        var events = snapshot.RecentEvents.Select(observed =>
        {
            var permitted = observed.IdentityVisibility switch
            {
                EventIdentityVisibility.Public => true,
                EventIdentityVisibility.PlayerOne => local == 1,
                EventIdentityVisibility.PlayerTwo => local == 2,
                EventIdentityVisibility.Unobserved => false,
                _ => observed.EntityId is { } id && !attachmentIds.Contains(id) && HasKnownOrdinaryCategory(snapshot, id)
            };
            return !permitted && observed.CardId is not null ? observed with { CardId = null } : observed;
        }).ToArray();
        var eventsChanged = events.Where((entry, index) => !ReferenceEquals(entry, snapshot.RecentEvents[index])).Any();
        if (hidden is null && limited is null && !eventsChanged && !unresolvedActive) return snapshot;
        var quality = unresolvedActive ? snapshot.Quality with
        {
            IsComplete = false, IsStable = false,
            Warnings = Array.AsReadOnly(snapshot.Quality.Warnings.Append("存在关联目标未确认的附魔，未公开其身份，相关精确分析等待完整局面。").Distinct(StringComparer.Ordinal).ToArray())
        } : snapshot.Quality;
        return snapshot with
        {
            Entities = hidden is null && limited is null ? snapshot.Entities : snapshot.Entities.Where(pair => hidden?.Contains(pair.Key) != true)
                .ToFrozenDictionary(pair => pair.Key, pair => limited?.GetValueOrDefault(pair.Key) ?? pair.Value),
            RecentEvents = eventsChanged ? Array.AsReadOnly(events) : snapshot.RecentEvents,
            Quality = quality
        };
    }
}
