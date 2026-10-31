using HsHelper.Core;
using System.Text.Json;

internal static class ElusiveTagChecks
{
    // HearthDbEnums.cs SHA256 4219B7932E78F63AD6DF719AFC329562040AB315A594442F748FEFC58569EE61:
    // ELUSIVE=1211, spells/legacy abilities=311, hero powers=332. The three are not synonyms.
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("numeric Elusive spell and hero-power restrictions remain distinct observed tags", DistinctTags);
        yield return ("numeric and named Elusive updates share state and duplicate-event semantics", Updates);
        yield return ("new restriction aliases do not expose private opponent or spectator hand attributes", Privacy);
        yield return ("Elusive observations retain malformed evidence and immutable serialized prior states", Evidence);
    }

    private static void DistinctTags()
    {
        foreach (var (number, name) in new[] { ("1211", "ELUSIVE"), ("311", "CANT_BE_TARGETED_BY_SPELLS"), ("332", "CANT_BE_TARGETED_BY_HERO_POWERS") })
        {
            var engine = Frame(); Feed(engine, $"TAG_CHANGE Entity=30 tag={number} value=1");
            var entity = engine.Snapshot().Entities[30];
            Check(entity.GetIntTag(name) == 1 && !entity.Tags.ContainsKey(number), "Numeric restriction was not normalized.");
            foreach (var other in new[] { "ELUSIVE", "CANT_BE_TARGETED_BY_SPELLS", "CANT_BE_TARGETED_BY_HERO_POWERS" }.Where(tag => tag != name))
                Check(!entity.Tags.ContainsKey(other), "A narrower restriction was promoted into another keyword.");
        }
    }

    private static void Updates()
    {
        var engine = Frame(); Feed(engine, "TAG_CHANGE Entity=30 tag=1211 value=1");
        var observed = engine.Snapshot();
        var changed = engine.ProcessLine("TAG_CHANGE Entity=30 tag=ELUSIVE value=1");
        Check(!changed && engine.Snapshot().StateVersion == observed.StateVersion && engine.Snapshot().EventSequence == observed.EventSequence + 1,
            "Equivalent numeric/named observations changed state or lost their source event count.");
        Feed(engine, "TAG_CHANGE Entity=30 tag=1211 value=0");
        var cleared = engine.Snapshot();
        Check(cleared.StateVersion > observed.StateVersion && cleared.Entities[30].GetIntTag("ELUSIVE") == 0 &&
            observed.Entities[30].GetIntTag("ELUSIVE") == 1, "An explicit current zero was discarded or changed the old frame.");
    }

    private static void Privacy()
    {
        foreach (var spectating in new[] { false, true })
        {
            var engine = Frame();
            if (spectating) Feed(engine, "Begin Spectating");
            Feed(engine, "FULL_ENTITY - Creating ID=40 CardID=PRIVATE_ELUSIVE_CARD", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION",
                "tag=1211 value=1", "tag=311 value=1", "tag=332 value=1");
            var snapshot = engine.Snapshot(); var hidden = snapshot.Entities[40];
            Check(snapshot.IsSpectating == spectating, "The privacy control did not use the intended perspective.");
            Check(hidden.CardId is null && !hidden.Tags.Keys.Any(tag => tag is "1211" or "311" or "332" or "ELUSIVE" or
                "CANT_BE_TARGETED_BY_SPELLS" or "CANT_BE_TARGETED_BY_HERO_POWERS"), "Private attributes escaped through a new alias.");
            Check(!JsonSerializer.Serialize(snapshot).Contains("PRIVATE_ELUSIVE_CARD", StringComparison.Ordinal), "Private identity escaped serialization.");
        }
    }

    private static void Evidence()
    {
        var engine = Frame(); Feed(engine, "TAG_CHANGE Entity=30 tag=1211 value=1", "TAG_CHANGE Entity=30 tag=311 value=0", "TAG_CHANGE Entity=30 tag=332 value=0");
        var original = engine.Snapshot(); var encoded = JsonSerializer.Serialize(original);
        Feed(engine, "TAG_CHANGE Entity=30 tag=1211 value=bad");
        var malformed = engine.Snapshot().Entities[30];
        Check(malformed.Tags.GetValueOrDefault("ELUSIVE") == "bad" && malformed.GetIntTag("ELUSIVE") is null,
            "Malformed evidence was turned into a positive or silently removed.");
        using var restored = JsonDocument.Parse(encoded);
        var tags = restored.RootElement.GetProperty("Entities").GetProperty("30").GetProperty("Tags");
        Check(JsonSerializer.Serialize(original) == encoded && tags.GetProperty("ELUSIVE").GetString() == "1" &&
            tags.GetProperty("CANT_BE_TARGETED_BY_SPELLS").GetString() == "0" && tags.GetProperty("CANT_BE_TARGETED_BY_HERO_POWERS").GetString() == "0",
            "Snapshot isolation or restriction evidence was lost during serialization.");
    }

    private static PowerLogStateEngine Frame()
    {
        var engine = new PowerLogStateEngine(1, "elusive-alias-fixture");
        Feed(engine, "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0",
            "FULL_ENTITY - Creating ID=30 CardID=PUBLIC_ELUSIVE_MINION", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION",
            "tag=ATK value=2", "tag=HEALTH value=3", "tag=DAMAGE value=0", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION",
            "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }

    private static void Feed(PowerLogStateEngine engine, params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
