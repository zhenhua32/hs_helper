using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Core;

internal static class WeaponDurabilityChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("modern weapon durability is observed HEALTH minus DAMAGE", () =>
        {
            var result = Modern(Weapon(("HEALTH", "8"), ("DAMAGE", "3")));
            Check(result is { IsKnown: true, IsDepleted: false, Remaining: 5, MaximumHealth: 8, Damage: 3,
                LegacyDurability: null, Mode: WeaponDurabilityMode.ModernHealthDamage } && result.Unknowns.Count == 0);
        });
        yield return ("modern weapon numeric aliases work without named attributes", () =>
        {
            var result = Modern(Weapon(("45", "3"), ("44", "1"), ("202", "7")));
            Check(result is { Remaining: 2, MaximumHealth: 3, Damage: 1 });
        });
        yield return ("modern coherent named and numeric aliases describe one resource", () =>
        {
            var result = Modern(Weapon(("HEALTH", "3"), ("45", "3"), ("DAMAGE", "1"), ("44", "1"), ("202", "7")));
            Check(result is { Remaining: 2 } && result.Unknowns.Count == 0);
        });
        yield return ("modern missing observations never fall back to legacy or an assumed zero", () =>
        {
            foreach (var entity in new[]
            {
                Weapon(("HEALTH", "2"), ("DURABILITY", "2")),
                Weapon(("DAMAGE", "0"), ("DURABILITY", "2")),
                Weapon(("DURABILITY", "2")), Weapon()
            }) Check(Modern(entity) is { IsKnown: false, IsDepleted: null, Remaining: null });
        });
        yield return ("modern stale legacy value does not create an additional remaining attack", () =>
        {
            var result = Modern(Weapon(("HEALTH", "2"), ("DAMAGE", "1"), ("DURABILITY", "2")));
            Check(result is { Remaining: 1, LegacyDurability: 2, IsKnown: true });
            // The runtime must consume this one remaining use, rather than decrementing the stale two.
        });
        yield return ("modern deprecated malformed legacy values cannot override known HEALTH and DAMAGE", () =>
        {
            foreach (var legacy in new[]
            {
                new[] { ("DURABILITY", "bad") }, new[] { ("187", "-1") },
                new[] { ("DURABILITY", "2"), ("187", "3") }
            })
            {
                var result = Modern(Weapon([("HEALTH", "2"), ("DAMAGE", "1"), .. legacy]));
                Check(result is { IsKnown: true, Remaining: 1, LegacyDurability: null } && result.Unknowns.Count > 0);
            }
        });
        yield return ("modern explicit exhausted and overdrawn weapons retain zero", () =>
        {
            foreach (var (health, damage) in new[] { ("2", "2"), ("2", "3"), ("0", "0"), ("0", "1") })
                Check(Modern(Weapon(("HEALTH", health), ("DAMAGE", damage))) is { IsKnown: true, IsDepleted: true, Remaining: 0 });
        });
        yield return ("modern malformed and negative active observations are unknown", () =>
        {
            foreach (var key in new[] { "HEALTH", "45", "DAMAGE", "44" })
            foreach (var invalid in new[] { "", "bad", "1.5", "1,000", "2147483648", "-1" })
            {
                var values = new Dictionary<string, string> { ["HEALTH"] = "2", ["DAMAGE"] = "0", [key] = invalid };
                Check(Modern(Weapon(values.Select(value => (value.Key, value.Value)).ToArray())) is { IsKnown: false, Remaining: null });
            }
        });
        yield return ("modern contradictory HEALTH or DAMAGE aliases cannot be repaired", () =>
        {
            foreach (var entity in new[]
            {
                Weapon(("HEALTH", "2"), ("45", "3"), ("DAMAGE", "0")),
                Weapon(("HEALTH", "2"), ("DAMAGE", "0"), ("44", "1")),
                Weapon(("HEALTH", "bad"), ("45", "2"), ("DAMAGE", "0")),
                Weapon(("HEALTH", "2"), ("DAMAGE", "bad"), ("44", "0"))
            }) Check(Modern(entity) is { IsKnown: false, Remaining: null });
        });
        yield return ("modern weapon arithmetic is bounded at observed integer extremes", () =>
        {
            Check(Modern(Weapon(("HEALTH", int.MaxValue.ToString()), ("DAMAGE", "0"))) is { Remaining: int.MaxValue });
            Check(Modern(Weapon(("HEALTH", "0"), ("DAMAGE", int.MaxValue.ToString()))) is { Remaining: 0 });
        });
        yield return ("weapon type must be an observed consistent weapon rather than printed identity", () =>
        {
            foreach (var type in new[] { "", "MINION", "5", "bad" })
            {
                var entity = Weapon(("HEALTH", "2"), ("DAMAGE", "0"));
                var tags = entity.Tags.ToDictionary();
                if (type.Length == 0) tags.Remove("CARDTYPE"); else tags["CARDTYPE"] = type;
                Check(Modern(entity with { Tags = tags.ToFrozenDictionary() }) is { IsKnown: false });
            }
            Check(Modern(Weapon(("HEALTH", "2"), ("DAMAGE", "0"), ("202", "5"))) is { IsKnown: false });
            Check(Modern(Weapon(("HEALTH", "2"), ("DAMAGE", "0")) with { Kind = EntityKind.Player }) is { IsKnown: false });
            Check(Modern(Weapon(("HEALTH", "2"), ("DAMAGE", "0")) with { Id = 0 }) is { IsKnown: false });
        });
        yield return ("explicit legacy mode uses DURABILITY minus observed DAMAGE", () =>
        {
            var result = WeaponDurabilityEvidence.ResolveLegacy(Weapon(("DURABILITY", "5"), ("187", "5"), ("DAMAGE", "2"), ("HEALTH", "99")));
            Check(result is { Mode: WeaponDurabilityMode.LegacyDurability, Remaining: 3, LegacyDurability: 5, Damage: 2, IsKnown: true });
            Check(WeaponDurabilityEvidence.ResolveLegacy(Weapon(("187", "2"), ("44", "2"))) is { Remaining: 0, IsDepleted: true });
        });
        yield return ("explicit legacy mode also preserves unknown missing or conflicting active fields", () =>
        {
            foreach (var entity in new[]
            {
                Weapon(("DURABILITY", "2")), Weapon(("DAMAGE", "0")),
                Weapon(("DURABILITY", "bad"), ("DAMAGE", "0")),
                Weapon(("DURABILITY", "2"), ("187", "3"), ("DAMAGE", "0")),
                Weapon(("DURABILITY", "2"), ("DAMAGE", "0"), ("44", "1")),
                Weapon(("DURABILITY", "-1"), ("DAMAGE", "0"))
            }) Check(WeaponDurabilityEvidence.ResolveLegacy(entity) is { Remaining: null, IsKnown: false });
        });
        yield return ("a legacy entry point does not mix an unrelated HEALTH resource into legacy durability", () =>
        {
            Check(WeaponDurabilityEvidence.ResolveLegacy(Weapon(("DURABILITY", "3"), ("DAMAGE", "1"), ("HEALTH", "bad"))) is { Remaining: 2 });
        });
        yield return ("unknown builds cannot silently select legacy or modern semantics", () =>
        {
            var entity = Weapon(("HEALTH", "2"), ("DAMAGE", "0"), ("DURABILITY", "8"));
            foreach (var build in new string?[] { null, "", "legacy", "250000", "255791", " 255790" })
                Check(WeaponDurabilityEvidence.Resolve(build, entity) is { Mode: WeaponDurabilityMode.Unknown, IsKnown: false, Remaining: null });
        });
        yield return ("snapshot lookup binds the requested entity without first-weapon selection", () =>
        {
            var first = Weapon(("HEALTH", "2"), ("DAMAGE", "1"));
            var second = Weapon(("HEALTH", "4"), ("DAMAGE", "1")) with { Id = 32 };
            var snapshot = Frame(first, second);
            Check(WeaponDurabilityEvidence.Resolve(snapshot, 31) is { EntityId: 31, Remaining: 1 });
            Check(WeaponDurabilityEvidence.Resolve(snapshot, 32) is { EntityId: 32, Remaining: 3 });
            Check(WeaponDurabilityEvidence.Resolve(snapshot, 99) is { EntityId: 99, Mode: WeaponDurabilityMode.Unknown, Remaining: null });
            // Equipment uniqueness and action eligibility are separate from this per-entity numeric evidence.
        });
        yield return ("opponent weapon resources need no private card identity or printed definition", () =>
        {
            var entity = Weapon(("HEALTH", "2"), ("DAMAGE", "1"), ("CONTROLLER", "2")) with { CardId = null };
            var result = Modern(entity);
            Check(result is { EntityId: 31, Remaining: 1, IsKnown: true });
            var serialized = JsonSerializer.Serialize(result);
            Check(!serialized.Contains("CardId", StringComparison.Ordinal) && !serialized.Contains("CONTROLLER", StringComparison.Ordinal));
        });
        yield return ("weapon resource readers preserve raw observations and freeze unknown explanations", () =>
        {
            var entity = Weapon(("HEALTH", "2"), ("DURABILITY", "2"));
            var snapshot = Frame(entity);
            var before = JsonSerializer.Serialize(snapshot);
            var result = WeaponDurabilityEvidence.Resolve(snapshot, entity.Id);
            Check(!result.IsKnown && result.Unknowns.Count > 0 && JsonSerializer.Serialize(snapshot) == before);
            Check(!entity.Tags.ContainsKey("DAMAGE"));
            try { ((IList<string>)result.Unknowns).Add("mutation"); throw new Exception("Unknowns allowed mutation."); }
            catch (NotSupportedException) { }
        });
    }

    private static WeaponDurabilityResolution Modern(EntitySnapshot entity) => WeaponDurabilityEvidence.Resolve("255790", entity);
    private static EntitySnapshot Weapon(params (string Key, string Value)[] values)
    {
        var tags = new Dictionary<string, string> { ["CARDTYPE"] = "WEAPON", ["CONTROLLER"] = "1", ["ZONE"] = "PLAY" };
        foreach (var (key, value) in values) tags[key] = value;
        return new(31, "CS2_106", EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Frame(params EntitySnapshot[] entities) => new("weapon-resource-check", 10, 100, 5, 1, 1,
        GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Weapon resource evidence assertion failed.");
    }
}
