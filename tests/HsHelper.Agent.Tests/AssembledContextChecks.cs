using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class AssembledContextChecks
{
    private const string Base = "CORE_CFM_753";
    private const string Donor = "BE_349";
    private const string First = "CRAFTABLE_FIRST_HALF_DBID";
    private const string Second = "CRAFTABLE_SECOND_HALF_DBID";
    private const int DonorDbf = 130871;

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        var cards = LoadCards();
        yield return ("assembled context keeps custom and native text separate for two instances of one CardId", () => Sync(() => SeparateInstances(cards)));
        yield return ("assembled context never fills missing donors with the printed base effect", () => Sync(() => MissingDonor(cards)));
        yield return ("assembled own four-button choices describe trigger and effect halves rather than full minions", () => Sync(() => Offers(cards)));
        yield return ("assembled hidden and opposing hand data never supplies donor IDs or definitions", () => Sync(() => PrivateCards(cards)));
        yield return ("assembled spectator and unconfirmed-perspective contexts exclude private halves and offers", () => Sync(() => Spectators(cards)));
        yield return ("assembled wrong or missing observed builds keep effects unknown and do not load donor definitions", () => Sync(() => BuildBindings(cards)));
        yield return ("assembled context projection never modifies source card records or snapshots", () => Sync(() => SourceImmutability(cards)));
    }

    private static void SeparateInstances(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(Custom(40, Base, "HAND", 1, "0", DonorDbf.ToString()), Entity(41, Base, "HAND", 1));
        var context = Build(frame, cards);
        var custom = EntityNode(context, 40)["effect"]!;
        var native = EntityNode(context, 41)["effect"]!;
        Check(custom["is_resolved"]!.GetValue<bool>() && custom["is_observed"]!.GetValue<bool>() &&
            custom["text"]!.GetValue<string>() == "<b>战吼：</b> 召唤两个2/2并具有<b>突袭</b>的元素。" &&
            custom["effect_card_id"]!.GetValue<string>() == Donor, "The custom instance used the base text or lost its donor.");
        Check(native["is_resolved"]!.GetValue<bool>() && !native["is_observed"]!.GetValue<bool>() &&
            native["text"]!.GetValue<string>() == cards[Base].Text && native["effect_card_id"]!.GetValue<string>() == Base,
            "A native instance inherited the other entity's custom text.");
        var definition = Definition(context, Base);
        Check(definition["text"] is null && definition["printed_text"]!.GetValue<string>() == cards[Base].Text &&
            definition["text_scope"] is not null && context["quality"]!["context_complete"]!.GetValue<bool>(),
            "The global CardId definition advertised one instance's text as every instance's current effect.");
        Check(SnapshotCardEffects.VisibleDonorDbfIds(frame, "255790").SequenceEqual([DonorDbf]),
            "Entity-scoped donor loading did not deduplicate the visible reference.");
    }

    private static void MissingDonor(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(Custom(40, Base, "HAND", 1, "0", DonorDbf.ToString()), Entity(41, Base, "HAND", 1));
        var definitions = cards.Where(pair => pair.Key != Donor).ToDictionary();
        var context = Build(frame, definitions);
        var effect = EntityNode(context, 40)["effect"]!;
        Check(!effect["is_resolved"]!.GetValue<bool>() && effect["text"] is null && effect["effect_card_id"] is null &&
            effect["unknowns"]!.AsArray().Count > 0 && !context["quality"]!["context_complete"]!.GetValue<bool>(),
            "A missing donor was repaired with the base effect or still claimed complete context.");
        Check(Definition(context, Base)["text"] is null && Definition(context, Base)["printed_text"]!.GetValue<string>() == cards[Base].Text &&
            EntityNode(context, 41)["effect"]!["text"]!.GetValue<string>() == cards[Base].Text,
            "Unresolved custom text hid the printed/native distinction or damaged the separate native entity.");
    }

    private static void Offers(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = OfferFrame(); var context = Build(frame, cards);
        var expected = new (int Id, string Card, string Half, int Part)[] { (121, Donor, "Trigger", 0), (122, Donor, "Effect", 1),
            (123, "CORE_BAR_878", "Trigger", 0), (124, "CORE_BAR_878", "Effect", 1) };
        var options = context["choice"]!["options"]!.AsArray();
        Check(options.Count == 4, "Four actual half buttons were grouped into two full-card choices.");
        foreach (var (id, card, half, part) in expected)
        {
            var option = options.Single(node => node!["entity_id"]!.GetValue<int>() == id)!;
            var effect = option["effect"]!;
            Check(effect["is_offer"]!.GetValue<bool>() && !effect["is_resolved"]!.GetValue<bool>() && effect["text"] is null &&
                effect["offered_half"]!.GetValue<string>() == half &&
                effect["offered_half_text"]!.GetValue<string>() == cards[card].Text.Split('&')[part].Trim(),
                "A half button became a completed minion, lost its half direction, or exposed a full-card effect.");
            Check(Definition(context, card)["text"] is null && Definition(context, card)["printed_text"]!.GetValue<string>() == cards[card].Text,
                "A donor's global printed definition was advertised as the entire offered effect.");
        }
        Check(context["quality"]!["context_complete"]!.GetValue<bool>(), "Explicit offered fragments were called missing assembled text.");
        Check(SnapshotCardEffects.VisibleCards(frame).Where(entity => entity.Zone == "SETASIDE").Select(entity => entity.Id).Order().SequenceEqual([121, 122, 123, 124]),
            "The public visibility helper dropped one of the confirmed own half buttons.");
        Check(SnapshotCardEffects.VisibleDonorDbfIds(frame, "255790").Count == 0, "Marker 1 was fetched as a donor database reference.");
    }

    private static void PrivateCards(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(Custom(50, "TIME_428", "HAND", 2, "0", DonorDbf.ToString()),
            Custom(51, "CORE_BAR_878", "SECRET", 2, "117027", "0"), Custom(52, Base, "DECK", 1, "0", "118076"));
        var context = Build(frame, cards);
        NoPrivateDonors(frame, context);
        foreach (var id in new[] { "TIME_428", "CORE_BAR_878", Base, Donor, "CORE_RLK_083", "CORE_REV_308" })
            Check(!context.ToJsonString().Contains(id, StringComparison.Ordinal), "A hidden/opponent card or donor definition leaked: " + id);
        // A stored/host-supplied hidden card can still contain private numeric tags. The model projection must not trust them.
        foreach (var aliases in new[] { false, true })
        {
            var unknownIdentity = Frame(Custom(53, null, "HAND", 1, "0", DonorDbf.ToString(), aliases));
            var projected = Build(unknownIdentity, cards);
            Check(SnapshotCardEffects.VisibleCards(unknownIdentity).All(entity => entity.Id != 53), "An unknown card identity reached effect resolution.");
            NoPrivateDonors(unknownIdentity, projected);
        }
    }

    private static void Spectators(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var original = OfferFrame();
        original = Add(original, Custom(40, Base, "HAND", 1, "0", DonorDbf.ToString()));
        original = Add(original, Custom(50, "TIME_428", "HAND", 2, "117027", "118076"));
        foreach (var frame in new[] { original with { IsSpectating = true }, original with { LocalPlayerId = null },
            original with { Quality = original.Quality with { HasLocalPerspective = false } } })
        {
            var context = Build(frame, cards);
            NoPrivateDonors(frame, context);
            Check(context["choice"] is null && SnapshotCardEffects.VisibleCards(frame).All(entity => entity.Id == 30),
                "Spectator/unconfirmed perspective kept private candidates or hand instances.");
            foreach (var id in new[] { Base, "TIME_428", Donor, "CORE_BAR_878", "CORE_RLK_083", "CORE_REV_308" })
                Check(!context.ToJsonString().Contains(id, StringComparison.Ordinal), "A spectator private donor identity or definition leaked: " + id);
        }
    }

    private static void BuildBindings(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var original = Frame(Custom(40, Base, "HAND", 1, "0", DonorDbf.ToString()));
        foreach (var (frame, catalogBuild) in new (GameSnapshot, string?)[]
        {
            (original with { ClientBuild = null }, "255790"), (original with { ClientBuild = "255791" }, "255790"),
            (original, null), (original, ""), (original, "255791")
        })
        {
            var context = Build(frame, cards, catalogBuild); var effect = EntityNode(context, 40)["effect"]!;
            Check(!effect["is_resolved"]!.GetValue<bool>() && effect["text"] is null &&
                !context["quality"]!["context_complete"]!.GetValue<bool>() && Definition(context, Base)["text"] is null &&
                SnapshotCardEffects.VisibleDonorDbfIds(frame, catalogBuild).Count == 0,
                "Missing/wrong observed build resolved custom text, fetched donors, or reused the printed base effect.");
        }
    }

    private static void SourceImmutability(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Add(OfferFrame(), Custom(40, Base, "HAND", 1, "0", DonorDbf.ToString()));
        frame = Add(frame, Entity(41, Base, "HAND", 1));
        var cardBefore = JsonSerializer.Serialize(cards); var stateBefore = JsonSerializer.Serialize(frame);
        var projected = Build(frame, cards); var effectBefore = EntityNode(projected, 40)["effect"]!["text"]!.GetValue<string>();
        Check(cardBefore == JsonSerializer.Serialize(cards) && stateBefore == JsonSerializer.Serialize(frame),
            "Entity-scoped text reconstruction rewrote a shared CardRecord or the authoritative snapshot.");
        Check(cards[Base].Text.Contains('&') && cards[Base].Text != effectBefore && cards[Donor].Type == "WEAPON",
            "Source definitions were flattened or a weapon donor was converted into a minion.");
    }

    private static void NoPrivateDonors(GameSnapshot frame, JsonObject context)
    {
        Check(SnapshotCardEffects.VisibleDonorDbfIds(frame, "255790").Count == 0, "Private tags supplied donor IDs to catalog loading.");
        foreach (var forbidden in new[] { "130871", "117027", "118076", First, Second, "4572", "4573" })
            Check(!context.ToJsonString().Contains(forbidden, StringComparison.Ordinal), "Private half metadata escaped into the model context: " + forbidden);
    }
    private static JsonObject Build(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, string? build = "255790") =>
        CompactAdviceContextBuilder.Build(frame, build, cards: cards.Values);
    private static JsonNode EntityNode(JsonObject context, int id) => context["entities"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == id)!;
    private static JsonNode Definition(JsonObject context, string id) => context["cards"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == id)!;
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static GameSnapshot Frame(params EntitySnapshot[] cards) => new("assembled-context-fixture", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { Player(1, 1), Player(2, 2), Entity(30, "BE_044", "PLAY", 1) }.Concat(cards).ToFrozenDictionary(entity => entity.Id),
        new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static GameSnapshot OfferFrame()
    {
        var frame = Frame(Custom(121, Donor, "SETASIDE", 1, null, "1"), Custom(122, Donor, "SETASIDE", 1, "1", null, true),
            Custom(123, "CORE_BAR_878", "SETASIDE", 1, null, "1", true), Custom(124, "CORE_BAR_878", "SETASIDE", 1, "1", null));
        return frame with { Phase = GamePhase.Choice, Choice = new(7, 1, "GENERAL", 30, 1, 1,
            [new(121, Donor, 1, 0), new(122, Donor, 1, 1), new(123, "CORE_BAR_878", 1, 2), new(124, "CORE_BAR_878", 1, 3)], true)
            { SourceCardId = "BE_044", OfferedAtEventSequence = 90 } };
    }
    private static EntitySnapshot Player(int id, int player) => new(id, null, EntityKind.Player,
        new Dictionary<string, string> { ["PLAYER_ID"] = player.ToString() }.ToFrozenDictionary());
    private static EntitySnapshot Entity(int id, string? card, string zone, int player) => new(id, card, EntityKind.Card,
        new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = player.ToString() }.ToFrozenDictionary());
    private static EntitySnapshot Custom(int id, string? card, string zone, int player, string? first, string? second, bool numeric = false)
    {
        var entity = Entity(id, card, zone, player); var tags = entity.Tags.ToDictionary();
        if (first is not null) tags[numeric ? "4572" : First] = first;
        if (second is not null) tags[numeric ? "4573" : Second] = second;
        return entity with { Tags = tags.ToFrozenDictionary() };
    }
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }

    private static IReadOnlyDictionary<string, CardRecord> LoadCards()
    {
        var fixturePath = FindFixture();
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Assembled context fixture build changed.");
        return fixture.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!,
            raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
            raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
            raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static string FindFixture()
    {
        const string name = "assembled-card-text-255790.json";
        var copied = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (File.Exists(copied)) return copied;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "tests", "HsHelper.Cards.Tests", "Fixtures", name);
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("Cannot locate the fixed build 255790 assembled card text fixture.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
