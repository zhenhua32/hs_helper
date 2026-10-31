using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

public static class WeaponContextChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("compact weapon separates modern current durability from printed and deprecated values", () => Run(ObservedModern));
        yield return ("compact weapon missing or conflicting observations never acquire printed durability", () => Run(MissingAndConflicting));
        yield return ("compact weapon protocols stay explicit and private weapon identities stay hidden", () => Run(ProtocolsAndPrivacy));
    }

    private static void ObservedModern()
    {
        var frame = Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("DURABILITY", "99"));
        var before = JsonSerializer.Serialize(frame); var context = Build(frame); var current = Weapon(context)["current_weapon"]!;
        Check(current["mode"]?.ToString() == "ModernHealthDamage" && current["build"]?.ToString() == "255790" &&
            current["remaining"]?.GetValue<int>() == 2 && current["is_known"]?.GetValue<bool>() == true &&
            current["is_depleted"]?.GetValue<bool>() == false && Weapon(context)["tags"]?["DURABILITY"]?.ToString() == "99" &&
            context["cards"]![0]!["durability"]?.GetValue<int>() == 88 && JsonSerializer.Serialize(frame) == before,
            "The compact context rewrote original tags, used printed durability as current or mutated its frame.");
        var damaged = Frame(("HEALTH", "3"), ("DAMAGE", "3"));
        Check(Weapon(Build(damaged))["current_weapon"] is { } depleted && depleted["remaining"]?.GetValue<int>() == 0 &&
            depleted["is_depleted"]?.GetValue<bool>() == true, "Observed exhausted modern durability was not explicit.");
    }

    private static void MissingAndConflicting()
    {
        foreach (var frame in new[] { Frame(("HEALTH", "3"), ("DURABILITY", "99")),
            Frame(("DURABILITY", "3"), ("DAMAGE", "1")), Frame(("HEALTH", "3"), ("45", "4"), ("DAMAGE", "1")),
            Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("44", "0")), Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("202", "MINION")) })
        {
            var context = Build(frame); var evidence = Weapon(context)["current_weapon"]!;
            Check(evidence["remaining"] is null && evidence["is_known"]?.GetValue<bool>() == false && evidence["unknowns"]!.AsArray().Count > 0,
                "Missing or contradictory observations became an exact current durability.");
        }
        var missing = Weapon(Build(Frame(("HEALTH", "3"), ("DURABILITY", "99"))));
        Check(missing["tags"]?["DAMAGE"] is null, "The model's raw current tags silently acquired DAMAGE=0.");
    }

    private static void ProtocolsAndPrivacy()
    {
        var frame = Frame(("HEALTH", "3"), ("DAMAGE", "1"));
        foreach (var other in new[] { frame with { ClientBuild = null }, frame with { ClientBuild = "255789" } })
            Check(Weapon(Build(other))["current_weapon"] is { } evidence && evidence["mode"]?.ToString() == "Unknown" &&
                evidence["remaining"] is null && evidence["is_known"]?.GetValue<bool>() == false,
                "An unaudited build implicitly selected modern or legacy weapon semantics.");
        var malformedLegacy = Weapon(Build(Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("DURABILITY", "bad"))))["current_weapon"]!;
        Check(malformedLegacy["remaining"]?.GetValue<int>() == 2 && malformedLegacy["is_known"]?.GetValue<bool>() == true &&
            malformedLegacy["unknowns"]!.AsArray().Count > 0, "An ignored deprecated warning invalidated independent current evidence.");
        var privateFrame = frame with { Entities = frame.Entities.Values.Append(new EntitySnapshot(90, "PRIVATE_WEAPON_PROBE", EntityKind.Card,
            new Dictionary<string, string> { ["CONTROLLER"] = "2", ["ZONE"] = "HAND", ["CARDTYPE"] = "WEAPON", ["HEALTH"] = "3", ["DAMAGE"] = "0" }.ToFrozenDictionary())).ToFrozenDictionary(entity => entity.Id) };
        Check(!Build(privateFrame).ToJsonString().Contains("PRIVATE_WEAPON_PROBE", StringComparison.Ordinal) &&
            !Build(privateFrame)["entities"]!.AsArray().Any(entity => entity!["id"]?.GetValue<int>() == 90),
            "Current weapon evidence exported a private opposing weapon identity or slot.");
    }

    private static GameSnapshot Frame(params (string Key, string Value)[] numbers) => new("weapon-context", 5, 20, 2, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            new EntitySnapshot(11, "WEAPON_CONTEXT", EntityKind.Card, numbers.Concat(new[] { ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "WEAPON") })
                .ToFrozenDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal)) }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        { ClientBuild = "255790" };
    private static JsonObject Build(GameSnapshot frame) => CompactAdviceContextBuilder.Build(frame, frame.ClientBuild, cards:
        [new CardRecord("WEAPON_CONTEXT", 123, "武器样本", "", "HUNTER", "SYNTHETIC", "WEAPON", 1, true, [], [],
            """{"durability":88,"attack":5,"mechanics":[]}""")]);
    private static JsonNode Weapon(JsonObject context) => context["entities"]!.AsArray().Single(entity => entity!["id"]?.GetValue<int>() == 11)!;
    private static Task Run(Action run) { run(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
