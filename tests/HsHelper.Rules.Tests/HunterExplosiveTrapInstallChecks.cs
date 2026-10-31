using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterExplosiveTrapInstallChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> baseCards)
    {
        var cards = baseCards.ToDictionary();
        using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "explosive-trap-255790.json"))))
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.TryGetProperty("text", out var text) ? text.GetString()! : "",
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
                    raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
            }
        var evaluator = new LineEvaluator();
        yield return ("native Explosive Trap aliases install using current cost without executing a hypothetical opposing attack", () =>
        {
            foreach (var id in new[] { "CORE_EX1_610", "EX1_610" })
            foreach (var cost in new[] { 0, 1, 2 })
            {
                var frame = Frame(id, cost); var before = JsonSerializer.Serialize(frame);
                var line = evaluator.EvaluateLine(frame, [new(ActionKind.PlayCard, 30)], cards);
                Check(line.Verification == LineVerification.Verified && line.AppliedActions == 1 && line.Result.AvailableMana == 10 - cost &&
                    line.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "SECRET" &&
                    line.Result.Entities.Single(entity => entity.EntityId == 11).RemainingHealth == 30 && before == JsonSerializer.Serialize(frame),
                    "The fixed native trap failed installation, used printed cost or attacked the opponent during installation.");
            }
        });
        yield return ("Explosive Trap definition text DBF school mechanics and raw identity must be audited before installation payment", () =>
        {
            var original = cards["CORE_EX1_610"];
            var changes = new[] { original with { Text = "changed native effect" }, original with { DbfId = 1 },
                ChangeRaw(original, "id", JsonValue.Create("WRONG_TRAP")), ChangeRaw(original, "text", JsonValue.Create("changed native effect")),
                ChangeRaw(original, "spellSchool", JsonValue.Create("FROST")), ChangeRaw(original, "mechanics", new JsonArray("BATTLECRY")) };
            foreach (var altered in changes)
            {
                var map = cards.ToDictionary(); map["CORE_EX1_610"] = altered;
                var line = evaluator.EvaluateLine(Frame("CORE_EX1_610", 2), [new(ActionKind.PlayCard, 30)], map);
                Check(line.Verification == LineVerification.Unsupported && line.AppliedActions == 0 && line.Result.AvailableMana == 10 &&
                    line.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "HAND" && line.Result.OpponentHealthPayments.Count == 0,
                    "A substituted same-ID native definition paid or installed before its effect was checked.");
            }
        });
        yield return ("Explosive Trap core and legacy aliases share installed uniqueness before payment", () =>
        {
            var frame = Frame("EX1_610", 2); var installed = Card(31, "CORE_EX1_610", "SECRET", 0);
            var entities = frame.Entities.ToDictionary(); entities[31] = installed; frame = frame with { Entities = entities.ToFrozenDictionary() };
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.PlayCard, 30)], cards);
            Check(line.Verification == LineVerification.Invalid && line.AppliedActions == 0 && line.Result.AvailableMana == 10 &&
                line.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "HAND", "A second alias bypassed the same-secret installation restriction.");
        });
        yield return ("Explosive Trap current secret school and race contradictions cannot be repaired by its printed definition", () =>
        {
            foreach (var (key, value) in new[] { ("SECRET", "0"), ("219", "0"), ("219", "bad"), ("SPELL_SCHOOL", "FROST"),
                ("1635", "4"), ("CARDRACE", "20"), ("200", "26") })
            {
                var frame = Frame("CORE_EX1_610", 2); var entities = frame.Entities.ToDictionary(); var tags = entities[30].Tags.ToDictionary(); tags[key] = value;
                entities[30] = entities[30] with { Tags = tags.ToFrozenDictionary() }; frame = frame with { Entities = entities.ToFrozenDictionary() };
                var line = evaluator.EvaluateLine(frame, [new(ActionKind.PlayCard, 30)], cards);
                Check(line.AppliedActions == 0 && line.Verification == LineVerification.Unsupported && line.Result.AvailableMana == 10 &&
                    line.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "HAND", "An explicit current counterexample was silently restored from native metadata.");
            }
        });
        yield return ("installed same-ID unknown trap text is not silently exempt from interaction inspection", () =>
        {
            var frame = Frame("CORE_EX1_610", 0); var entities = frame.Entities.ToDictionary(); entities[30] = Card(30, "CORE_EX1_610", "SECRET", 0);
            frame = frame with { Entities = entities.ToFrozenDictionary() };
            var altered = cards.ToDictionary(); altered["CORE_EX1_610"] = cards["CORE_EX1_610"] with { Text = "unreviewed persistent secret" };
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.EndTurn, 2)], altered);
            Check(line.Verification == LineVerification.Conditional && line.UnsupportedMechanics.Contains("FRIENDLY_SECRET_OR_QUEST"),
                "An altered installed trap bypassed all interaction checks by its ID alone.");
        });
    }

    private static CardRecord ChangeRaw(CardRecord card, string key, JsonNode? value)
    { var raw = JsonNode.Parse(card.RawJson)!; raw[key] = value; return card with { RawJson = raw.ToJsonString() }; }
    private static GameSnapshot Frame(string id, int cost)
    {
        var entities = new[] { Player(2, 1, 10), Player(3, 2, 11), Hero(10, 1), Hero(11, 2), Card(30, id, "HAND", cost) };
        return new("trap-install", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id),
            new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static EntitySnapshot Player(int id, int owner, int hero) => new(id, null, EntityKind.Player,
        new Dictionary<string, string> { ["PLAYER_ID"] = owner.ToString(), ["HERO_ENTITY"] = hero.ToString(), ["RESOURCES"] = "10", ["RESOURCES_USED"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Hero(int id, int owner) => new(id, "HERO_" + owner, EntityKind.Card,
        new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["CARDTYPE"] = "HERO", ["HEALTH"] = "30", ["DAMAGE"] = "0", ["ARMOR"] = "0", ["ATK"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Card(int id, string card, string zone, int cost) => new(id, card, EntityKind.Card,
        new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = zone, ["CARDTYPE"] = "SPELL", ["COST"] = cost.ToString() }.ToFrozenDictionary());
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
