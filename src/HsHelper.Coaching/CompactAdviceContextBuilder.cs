using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Coaching;

/// <summary>
/// A deliberately projected model view, shared by initial prompts and lazy get_snapshot responses.
/// Full snapshots stay on the host for rule validation; model data contains neither deck entity slots,
/// player names, raw card JSON, event DataJson, nor duplicated computed entity properties.
/// </summary>
public static class CompactAdviceContextBuilder
{
    public const string SchemaVersion = "compact-v1";
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly HashSet<string> IdentityTags = new(StringComparer.Ordinal)
    { "NAME", "ENTITY_NAME", "PLAYER_NAME", "BATTLETAG", "ACCOUNT_ID", "ACCOUNTID", "GAME_ACCOUNT_ID", "USER_ID", "USERID" };
    private static readonly HashSet<string> GameTags = new(StringComparer.Ordinal)
    { "TURN", "STEP", "STATE", "NEXT_STEP", "FIRST_PLAYER", "CURRENT_PLAYER", "NUM_TURNS_LEFT", "FATIGUE" };
    private static readonly HashSet<string> EnumTags = new(StringComparer.Ordinal)
    { "CARDTYPE", "CLASS", "CARDRACE", "RACE", "MULLIGAN_STATE", "PLAYSTATE", "STATE", "STEP", "NEXT_STEP", "FACTION", "RARITY", "SPELL_SCHOOL" };

    public static AdviceContext Create(GameSnapshot snapshot, string? build, AdviceTaskKind task,
        DeckDefinition? deck = null, IEnumerable<CardRecord>? cards = null,
        IEnumerable<ObservedGameEvent>? recentEvents = null)
    {
        if (task == AdviceTaskKind.Mulligan && snapshot.Phase != GamePhase.Mulligan)
            throw new InvalidOperationException("留牌建议需要当前处于留牌阶段的局面。");
        if (task == AdviceTaskKind.Choice && OwnChoice(snapshot) is null)
            throw new InvalidOperationException("选择建议需要已确认属于我方的完整候选列表。");
        var template = AdviceTaskTemplates.Get(task);
        return new(snapshot.MatchId, snapshot.StateVersion, task.ToString(), Build(snapshot, build, deck, cards, recentEvents))
        { TaskKind = task, TemplateVersion = template.Version };
    }

    public static JsonObject Build(GameSnapshot snapshot, string? build, DeckDefinition? deck = null,
        IEnumerable<CardRecord>? cards = null, IEnumerable<ObservedGameEvent>? recentEvents = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var local = snapshot.Quality.HasLocalPerspective ? snapshot.LocalPlayerId : null;
        var visible = snapshot.Entities.Values.Where(entity => entity.Kind is EntityKind.Game or EntityKind.Player ||
            entity.Zone == "PLAY" || local.HasValue && entity.ControllerId == local && entity.Zone is "HAND" or "SECRET")
            .OrderBy(entity => entity.Id).ToArray();
        var choice = OwnChoice(snapshot);
        var contextLimits = new List<string>();
        var playTracking = snapshot.PublicPlays is { } publicPlays && publicPlays.MatchId == snapshot.MatchId &&
            publicPlays.StateVersion == snapshot.StateVersion ? publicPlays : null;
        if (snapshot.PublicPlays is not null && playTracking is null)
            contextLimits.Add("公开出牌附加信息不属于当前对局和局面版本，已省略其身份与计数。");
        var plays = playTracking?.Plays.Where(play => play.Sequence <= snapshot.EventSequence).ToArray() ?? [];
        var tracking = local.HasValue && snapshot.DeckTracking is { } deckTracking && deckTracking.MatchId == snapshot.MatchId &&
            deckTracking.StateVersion == snapshot.StateVersion && build is not (null or "" or "unknown") && deckTracking.CardBuild == build
            ? deckTracking : null;
        if (snapshot.DeckTracking is not null && tracking is null)
            contextLimits.Add("牌库追踪附加信息与当前视角、对局、局面版本或卡库版本不匹配，已省略其组成与数量。");
        var cardIds = visible.Select(entity => entity.CardId).OfType<string>()
            .Concat(choice?.Options.Select(option => option.CardId).OfType<string>() ?? [])
            .Concat(plays.Select(play => play.CardId).OfType<string>())
            .Concat(tracking?.KnownRemaining.Keys ?? []).ToHashSet(StringComparer.Ordinal);
        var dbfIds = (deck?.Cards.Select(card => card.DbfId) ?? [])
            .Concat(deck?.Sideboards.SelectMany(board => board.Cards.Select(card => card.DbfId)) ?? []).ToHashSet();
        var definitions = (cards ?? []).Where(card => cardIds.Contains(card.Id) || card.DbfId is { } id && dbfIds.Contains(id))
            .DistinctBy(card => card.Id).OrderBy(card => card.Id, StringComparer.Ordinal).Select(CardSummary).ToArray();
        var events = (recentEvents ?? snapshot.RecentEvents).Where(observed => observed.Sequence > 0 && observed.Sequence <= snapshot.EventSequence)
            .Where(observed => observed.Kind is "ZoneChanged" or "CardRevealed" or "CardChanged" or "AttributeChanged" or "PhaseChanged")
            .OrderBy(observed => observed.Sequence).TakeLast(12).Select(observed => new
            {
                sequence = observed.Sequence, kind = observed.Kind, entity_id = observed.EntityId, controller = observed.ControllerId,
                card_id = EventIdentity(snapshot, observed, plays), from = Zone(observed.FromZone), to = Zone(observed.ToZone)
            }).ToArray();
        return JsonSerializer.SerializeToNode(new
        {
            schema = SchemaVersion, match_id = snapshot.MatchId, state_version = snapshot.StateVersion,
            event_sequence = snapshot.EventSequence, turn = snapshot.Turn, phase = snapshot.Phase,
            active_player_id = snapshot.ActivePlayerId, local_player_id = local,
            format = snapshot.Format, card_build = build is null or "" or "unknown" ? null : build,
            observed_build = snapshot.ClientBuild,
            build_matches = build is null or "" or "unknown" || snapshot.ClientBuild is null ? (bool?)null : build == snapshot.ClientBuild,
            quality = new { has_start = snapshot.Quality.HasGameStart, complete = snapshot.Quality.IsComplete,
                stable = snapshot.Quality.IsStable, perspective = snapshot.Quality.HasLocalPerspective, spectating = snapshot.IsSpectating,
                open_blocks = snapshot.Quality.OpenBlockCount, warnings = snapshot.Quality.Warnings.Concat(contextLimits).ToArray(),
                context_complete = contextLimits.Count == 0, context_limits = contextLimits,
                history_truncated_through = snapshot.EventHistoryTruncatedThrough },
            entities = visible.Select(entity => new
            {
                id = entity.Id, kind = entity.Kind, card_id = entity.CardId, controller = entity.ControllerId,
                zone = Zone(entity.Zone), position = entity.ZonePosition, tags = Tags(entity)
            }).ToArray(),
            zone_counts = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.Zone is "DECK" or "HAND" or "SECRET")
                .GroupBy(entity => (entity.ControllerId, entity.Zone)).OrderBy(group => group.Key.ControllerId).ThenBy(group => group.Key.Zone)
                .Select(group => new { controller = group.Key.ControllerId, zone = group.Key.Zone, count = group.Count() }).ToArray(),
            choice = choice is null ? null : new
            {
                confirmed = true, id = choice.Id, player_id = choice.PlayerId, type = choice.Type, source_entity_id = choice.SourceEntityId,
                count_min = choice.CountMin, count_max = choice.CountMax,
                options = choice.Options.OrderBy(option => option.Position).Select(option => new { entity_id = option.EntityId, card_id = option.CardId, position = option.Position }).ToArray()
            },
            imported_deck = deck is null ? null : new
            {
                format = deck.Format, heroes = deck.Heroes,
                cards = deck.Cards.Select(card => new { dbf_id = card.DbfId, count = card.Count }).ToArray(),
                sideboards = deck.Sideboards.Select(board => new { owner_dbf_id = board.OwnerDbfId,
                    cards = board.Cards.Select(card => new { dbf_id = card.DbfId, count = card.Count }).ToArray() }).ToArray()
            },
            deck_tracking = tracking is null ? null : new
            {
                deck_version = tracking.DeckVersion, card_build = tracking.CardBuild,
                current = tracking.MatchId == snapshot.MatchId && tracking.StateVersion == snapshot.StateVersion,
                total = tracking.TotalCards, known_remaining = tracking.KnownRemaining, unknown = tracking.UnknownCount,
                exact = tracking.IsExact && tracking.MatchId == snapshot.MatchId && tracking.StateVersion == snapshot.StateVersion &&
                    tracking.CardBuild == build && snapshot.Quality.IsComplete, warnings = tracking.Warnings
            },
            public_plays = playTracking is null ? null : new
            {
                complete = playTracking.IsComplete && playTracking.StateVersion == snapshot.StateVersion &&
                    playTracking.Plays.All(play => play.Sequence <= snapshot.EventSequence), warnings = playTracking.Warnings,
                counts = plays.GroupBy(play => (play.ControllerId, play.CardId)).OrderBy(group => group.Key.ControllerId).ThenBy(group => group.Key.CardId)
                    .Select(group => new { controller = group.Key.ControllerId, card_id = group.Key.CardId, count = group.Count() }).ToArray(),
                recent = plays.OrderBy(play => play.Sequence).TakeLast(8).Select(play => new { sequence = play.Sequence,
                    entity_id = play.EntityId, card_id = play.CardId, controller = play.ControllerId }).ToArray()
            },
            recent_events = events, cards = definitions,
            limitations = "未显示字段保持未知；卡库为基础定义，当前动态属性以实体tags为准；完整本地规则验证结果另由工具提供。"
        }, Json)!.AsObject();
    }

    private static CurrentChoiceSnapshot? OwnChoice(GameSnapshot snapshot) => snapshot.Phase is GamePhase.Choice or GamePhase.Mulligan &&
        snapshot.Quality.HasLocalPerspective && snapshot.LocalPlayerId.HasValue &&
        snapshot.Choice is { IsReady: true, Options.Count: > 0 } choice && choice.PlayerId == snapshot.LocalPlayerId ? choice : null;

    private static SortedDictionary<string, string> Tags(EntitySnapshot entity)
    {
        var tags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entity.Tags)
        {
            if (key is "ZONE" or "CONTROLLER" or "ZONE_POSITION" || IdentityTags.Contains(key) ||
                key.Contains("NAME", StringComparison.OrdinalIgnoreCase) || key.Contains("ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("BATTLETAG", StringComparison.OrdinalIgnoreCase) || key.Contains("USER_ID", StringComparison.OrdinalIgnoreCase)) continue;
            if (entity.Kind == EntityKind.Game && !GameTags.Contains(key)) continue;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || EnumTags.Contains(key) && Token(value)) tags[key] = value;
        }
        return tags;
    }

    private static bool Token(string value) => value.Length is > 0 and <= 80 &&
        value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static string? Zone(string? value) => value is "PLAY" or "DECK" or "HAND" or "GRAVEYARD" or
        "REMOVEDFROMGAME" or "SETASIDE" or "SECRET" or "INVALID" ? value : null;

    private static string? EventIdentity(GameSnapshot snapshot, ObservedGameEvent observed, IReadOnlyList<PlayedCardRecord> plays)
    {
        if (observed.CardId is null) return null;
        if (observed.FromZone is "PLAY" or "GRAVEYARD" || observed.ToZone is "PLAY" or "GRAVEYARD" ||
            snapshot.Quality.HasLocalPerspective && snapshot.LocalPlayerId.HasValue && observed.ControllerId == snapshot.LocalPlayerId ||
            plays.Any(play => play.EntityId == observed.EntityId && play.CardId == observed.CardId)) return observed.CardId;
        return observed.EntityId is { } id && snapshot.Entities.TryGetValue(id, out var entity) &&
            entity.Zone is "PLAY" or "GRAVEYARD" && entity.CardId == observed.CardId ? observed.CardId : null;
    }

    private static object CardSummary(CardRecord card)
    {
        int? attack = null, health = null, durability = null;
        string[] mechanics = [];
        try
        {
            if (card.RawJson is { Length: > 0 and <= 1024 * 1024 })
            {
                using var document = JsonDocument.Parse(card.RawJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                int? Number(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
                attack = Number("attack"); health = Number("health"); durability = Number("durability");
                if (root.TryGetProperty("mechanics", out var keywords) && keywords.ValueKind == JsonValueKind.Array)
                    mechanics = keywords.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()!).Where(Token).Distinct(StringComparer.Ordinal).ToArray();
            }
        }
        catch (JsonException) { /* Text and current dynamic tags remain available when optional metadata is invalid. */ }
        return new { id = card.Id, dbf_id = card.DbfId, name = card.Name, text = card.Text, cost = card.Cost,
            type = card.Type, card_class = card.CardClass, attack, health, durability, mechanics, entourage = card.Entourage };
    }
}
