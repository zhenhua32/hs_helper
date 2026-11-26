namespace HsHelper.Core;

/// <summary>One privacy boundary for current offered identities in UI, local tools and model contexts.</summary>
public static class ChoiceVisibility
{
    private static readonly HashSet<string> ChoiceObservedTags = ["ZONE", "CONTROLLER", "ZONE_POSITION", "ENTITY_ID", "CARDTYPE", "COST", "ATK", "HEALTH", "DAMAGE", "ARMOR", "DURABILITY",
        "TAUNT", "DIVINE_SHIELD", "CHARGE", "RUSH", "LIFESTEAL", "POISONOUS", "STEALTH", "WINDFURY", "MEGA_WINDFURY", "ELUSIVE", "REBORN", "SILENCED", "FROZEN", "EXHAUSTED", "NUM_TURNS_IN_PLAY", "CARDRACE", "TAG_SCRIPT_DATA_NUM_1", "TAG_SCRIPT_DATA_NUM_2",
        "CRAFTABLE_FIRST_HALF_DBID", "CRAFTABLE_SECOND_HALF_DBID"];

    internal static bool IsObservedOptionTag(string tag) => ChoiceObservedTags.Contains(tag);

    /// <summary>Recognizes only an actual current own option entity carrying the shared anonymous projection.</summary>
    public static bool IsProjectedCurrentOptionEntity(GameSnapshot snapshot, EntitySnapshot entity, bool allowSpellInput = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (entity is null || entity.Kind != EntityKind.Card || string.IsNullOrWhiteSpace(entity.CardId) ||
            !snapshot.Entities.TryGetValue(entity.Id, out var actual) || !ReferenceEquals(actual, entity) ||
            Confirmed(snapshot, allowSpellInput && entity.Zone == "SETASIDE") is not { } choice || choice.PlayerId is not (1 or 2) ||
            entity.ControllerId != choice.PlayerId || choice.SourceEntityId is not > 0 ||
            !snapshot.Entities.TryGetValue(choice.SourceEntityId.Value, out var source) || source.Id != choice.SourceEntityId.Value ||
            entity.Tags.Keys.Any(tag => !IsObservedOptionTag(tag))) return false;
        var option = choice.Options.SingleOrDefault(candidate => candidate.EntityId == entity.Id);
        return option is not null && option.ControllerId == choice.PlayerId &&
            !string.IsNullOrWhiteSpace(option.CardId) && option.CardId == entity.CardId;
    }

    public static int? ConfirmedLocalPlayer(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !snapshot.Entities.Values.Any(entity => entity is null || entity.Tags is null) &&
            snapshot.Quality.HasLocalPerspective && !snapshot.IsSpectating && snapshot.LocalPlayerId is > 0 and var local &&
            snapshot.Entities.Values.Count(entity => entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == local) == 1
                ? local : null;
    }

    public static CurrentChoiceSnapshot? ConfirmedOwn(GameSnapshot snapshot) => Confirmed(snapshot, false);

    public static CurrentChoiceSnapshot? ConfirmedDecision(GameSnapshot snapshot) => Confirmed(snapshot, true);

    private static CurrentChoiceSnapshot? Confirmed(GameSnapshot snapshot, bool allowSpellInput)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ConfirmedLocalPlayer(snapshot) is not { } local || !snapshot.Quality.IsComplete ||
            snapshot.Phase is not (GamePhase.Choice or GamePhase.Mulligan) ||
            snapshot.Choice is not { IsReady: true, Options.Count: > 0 } choice || choice.Id <= 0 || choice.PlayerId != local)
            return null;
        if (!snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0)
        {
            if (!allowSpellInput || !snapshot.LocalSpellChoiceInput || snapshot.Phase != GamePhase.Choice ||
                snapshot.Quality.OpenBlockCount != 2 || choice is not { Type: "GENERAL", CountMin: 1, CountMax: 1, SourceEntityId: int source } ||
                !snapshot.Entities.TryGetValue(source, out var spell) || spell.ControllerId != local || spell.Zone != "PLAY" ||
                spell.CardId is null || spell.CardId != choice.SourceCardId ||
                !(spell.Tags.GetValueOrDefault("CARDTYPE") is "SPELL" or "5" || IsMapFollowupInput(snapshot)) ||
                choice.Options.Any(option => option is null || option.ControllerId != local || string.IsNullOrEmpty(option.CardId))) return null;
        }
        var options = choice.Options.ToArray();
        if (choice.CountMin < 0 || choice.CountMax < choice.CountMin || choice.CountMax > options.Length ||
            options.Any(option => option is null || option.EntityId <= 0 || option.ControllerId is { } owner && owner != local) ||
            options.Select(option => option.EntityId).Distinct().Count() != options.Length)
            return null;
        foreach (var option in options)
        {
            if (!snapshot.Entities.TryGetValue(option.EntityId, out var entity)) continue;
            if (entity.Kind is EntityKind.Game or EntityKind.Player ||
                entity.ControllerId is { } owner && owner != local ||
                option.CardId is { } offeredCard && entity.CardId is { } observedCard && offeredCard != observedCard)
                return null;
        }
        return choice with { Options = Array.AsReadOnly(options) };
    }

    internal static bool IsMapFollowupInput(GameSnapshot snapshot, int? playedEntityId = null)
    {
        if (snapshot.Choice is not { Type: "GENERAL", CountMin: 1, CountMax: 1, Options.Count: 2,
                SourceCardId: "TLC_824e", SourceEntityId: int source, PlayerId: int local } choice ||
            local != ConfirmedLocalPlayer(snapshot) || !snapshot.Entities.TryGetValue(source, out var enchantment) ||
            enchantment.CardId != choice.SourceCardId || enchantment.Zone != "PLAY" || enchantment.ControllerId != local ||
            enchantment.Tags.GetValueOrDefault("CARDTYPE") is not ("ENCHANTMENT" or "6") ||
            enchantment.GetIntTag("ATTACHED") is not int attachment || !snapshot.Entities.TryGetValue(attachment, out var player) ||
            player.Kind != EntityKind.Player || player.GetIntTag("PLAYER_ID") != local ||
            enchantment.GetIntTag("CREATOR") is not int map || snapshot.ChoiceHistory is not { } history ||
            history.MatchId != snapshot.MatchId || history.StateVersion != snapshot.StateVersion || history.EventSequence != snapshot.EventSequence)
            return false;

        var first = history.Entries.LastOrDefault(entry => entry?.SourceEntityId == map);
        if (first is not { IsComplete: true, Type: "GENERAL", SourceCardId: "TLC_824", CountMin: 1, CountMax: 1,
                Options.Count: 3, ChosenEntityIds.Count: 1 } || first.PlayerId != local ||
            first.ChosenAtEventSequence >= choice.OfferedAtEventSequence || choice.OfferedAtEventSequence > snapshot.EventSequence)
            return false;
        var chosen = first.ChosenEntityIds[0];
        if (playedEntityId.HasValue && playedEntityId != chosen || !snapshot.Entities.TryGetValue(chosen, out var played) ||
            played.ControllerId != local || played.Zone != "PLAY" ||
            !first.Options.Any(option => option is { CardId.Length: > 0 } && option.EntityId == chosen &&
                option.ControllerId == local && option.CardId == played.CardId))
            return false;
        return choice.Options.All(candidate => candidate is { CardId.Length: > 0 } && candidate.ControllerId == local &&
            candidate.EntityId != chosen && first.Options.Any(option => option is not null && option.EntityId == candidate.EntityId &&
                option.CardId == candidate.CardId && option.ControllerId == local)) && choice.Options[0].EntityId != choice.Options[1].EntityId;
    }
}
