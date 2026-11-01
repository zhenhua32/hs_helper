using System.Text.Json;
using HsHelper.Core;

internal static class DeathrattleTagChecks
{
    // Pinned HearthDb enum source SHA256 4219B7932E78F63AD6DF719AFC329562040AB315A594442F748FEFC58569EE61.
    private static readonly (string Number, string Name)[] Aliases =
    [ ("217", "DEATHRATTLE"), ("371", "EXTRA_MINION_DEATHRATTLES_BASE"), ("882", "EXTRA_DEATHRATTLES_BASE"),
      ("1131", "EXTRA_DEATHRATTLES_ADDITIONAL"), ("1831", "CANT_TRIGGER_DEATHRATTLE") ];

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("numeric deathrattle and each repetition or suppression modifier stay distinct observed tags", Distinct);
        yield return ("named and numeric deathrattle updates share current zeros and duplicate observation semantics", Updates);
        yield return ("deathrattle aliases never reveal opposing or spectator private hand attributes", Privacy);
        yield return ("deathrattle modifier malformed evidence and earlier serialized snapshots remain unchanged", Evidence);
    }

    private static PowerLogStateEngine Frame()
    {
        var engine = new PowerLogStateEngine(1, "deathrattle-tag-checks");
        Feed(engine, "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0",
            "FULL_ENTITY - Creating ID=30 CardID=PUBLIC_DEATHRATTLE_BODY", "tag=CARDTYPE value=MINION", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY",
            "tag=HEALTH value=1", "tag=DAMAGE value=0", "tag=ATK value=1", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }
    private static void Feed(PowerLogStateEngine engine, params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void Distinct()
    {
        foreach (var (number, name) in Aliases)
        {
            var engine = Frame(); var id = number == "217" ? 30 : 2;
            Feed(engine, $"TAG_CHANGE Entity={id} tag={number} value=1");
            var entity = engine.Snapshot().Entities[id];
            Check(entity.GetIntTag(name) == 1 && !entity.Tags.ContainsKey(number) &&
                Aliases.Where(alias => alias.Name != name).All(alias => !entity.Tags.ContainsKey(alias.Name)),
                "A numeric deathrattle modifier disappeared or changed the meaning of another modifier.");
        }
    }

    private static void Updates()
    {
        foreach (var (number, name) in Aliases)
        {
            var engine = Frame(); Feed(engine, $"TAG_CHANGE Entity=30 tag={number} value=1"); var old = engine.Snapshot();
            Check(!engine.ProcessLine($"TAG_CHANGE Entity=30 tag={name} value=1") && engine.Snapshot().StateVersion == old.StateVersion &&
                engine.Snapshot().EventSequence == old.EventSequence + 1, "Equivalent named/numeric observations duplicated a state change.");
            Feed(engine, $"TAG_CHANGE Entity=30 tag={number} value=0");
            Check(engine.Snapshot().Entities[30].GetIntTag(name) == 0 && old.Entities[30].GetIntTag(name) == 1,
                "An explicit current zero was discarded or rewrote an earlier observation.");
        }
    }

    private static void Privacy()
    {
        foreach (var spectating in new[] { false, true })
        {
            var engine = Frame(); if (spectating) Feed(engine, "Begin Spectating");
            Feed(engine, "FULL_ENTITY - Creating ID=40 CardID=PRIVATE_DEATHRATTLE_BODY", "tag=CARDTYPE value=MINION", "tag=CONTROLLER value=2", "tag=ZONE value=HAND");
            foreach (var (number, _) in Aliases) Feed(engine, $"tag={number} value=1");
            var snapshot = engine.Snapshot(); var hidden = snapshot.Entities[40];
            Check(snapshot.IsSpectating == spectating && hidden.CardId is null && Aliases.All(alias =>
                !hidden.Tags.ContainsKey(alias.Name) && !hidden.Tags.ContainsKey(alias.Number)) &&
                !JsonSerializer.Serialize(snapshot).Contains("PRIVATE_DEATHRATTLE_BODY", StringComparison.Ordinal),
                "A canonical deathrattle tag exposed a private hand attribute or identity.");
        }
    }

    private static void Evidence()
    {
        var engine = Frame(); foreach (var (number, _) in Aliases) Feed(engine, $"TAG_CHANGE Entity=30 tag={number} value=1");
        var old = engine.Snapshot(); var json = JsonSerializer.Serialize(old);
        foreach (var (number, name) in Aliases)
        {
            Feed(engine, $"TAG_CHANGE Entity=30 tag={number} value=bad"); var entity = engine.Snapshot().Entities[30];
            Check(entity.Tags.GetValueOrDefault(name) == "bad" && entity.GetIntTag(name) is null, "Malformed modifier evidence was repaired to zero or removed.");
        }
        Check(json == JsonSerializer.Serialize(old) && Aliases.All(alias => old.Entities[30].GetIntTag(alias.Name) == 1),
            "Later modifier updates mutated an earlier public snapshot.");
    }
}
