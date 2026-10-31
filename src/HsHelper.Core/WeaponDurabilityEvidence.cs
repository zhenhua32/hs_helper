using System.Globalization;

namespace HsHelper.Core;

public enum WeaponDurabilityMode { Unknown, ModernHealthDamage, LegacyDurability }

/// <summary>Observed weapon resources only; this does not establish equipment, attack legality or native effects.</summary>
public sealed record WeaponDurabilityResolution(string? Build, int EntityId, WeaponDurabilityMode Mode,
    int? MaximumHealth, int? Damage, int? LegacyDurability, int? Remaining, IReadOnlyList<string> Unknowns)
{
    public bool IsKnown => Remaining.HasValue;
    public bool? IsDepleted => Remaining is { } value ? value == 0 : null;
}

/// <summary>
/// Build 255790 uses current HEALTH minus DAMAGE for weapon durability. The deprecated DURABILITY
/// tag is never a substitute for either observation. No printed card values or missing-zero defaults are used.
/// Reference: Hearthstone-Deck-Tracker Utility/BoardDamage/BoardCard.cs, CalculateHealth (since patch 32.0).
/// </summary>
public static class WeaponDurabilityEvidence
{
    public const string AuditedModernBuild = "255790";

    public static WeaponDurabilityResolution Resolve(GameSnapshot snapshot, int entityId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Entities.TryGetValue(entityId, out var entity)
            ? Resolve(snapshot.ClientBuild, entity)
            : new(snapshot.ClientBuild, entityId, WeaponDurabilityMode.Unknown, null, null, null, null,
                Array.AsReadOnly(new[] { "当前局面没有该武器实体。" }));
    }

    public static WeaponDurabilityResolution Resolve(string? build, EntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Read(build, entity, build == AuditedModernBuild
            ? WeaponDurabilityMode.ModernHealthDamage : WeaponDurabilityMode.Unknown);
    }

    /// <summary>
    /// Explicit compatibility entry point for a caller that has already established legacy log semantics.
    /// Resolve(build, entity) never guesses this mode from an absent/unaudited build or missing modern tags.
    /// </summary>
    public static WeaponDurabilityResolution ResolveLegacy(EntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Read(null, entity, WeaponDurabilityMode.LegacyDurability);
    }

    private static WeaponDurabilityResolution Read(string? build, EntitySnapshot entity, WeaponDurabilityMode mode)
    {
        var unknowns = new List<string>();
        if (entity.Kind != EntityKind.Card || entity.Id <= 0 || !WeaponType(entity.Tags))
            unknowns.Add("当前实体类别未明确且一致地观察为武器。");
        var healthProblems = new List<string>();
        var legacyProblems = new List<string>();
        var maximum = Number(entity.Tags, "HEALTH", "45", healthProblems);
        var damage = Number(entity.Tags, "DAMAGE", "44", unknowns);
        var legacy = Number(entity.Tags, "DURABILITY", "187", legacyProblems);
        int? remaining = null;
        if (mode == WeaponDurabilityMode.ModernHealthDamage)
        {
            unknowns.AddRange(healthProblems);
            if (maximum is null) unknowns.Add("现代武器的当前HEALTH尚未可靠观察。");
            if (damage is null) unknowns.Add("现代武器的当前DAMAGE尚未可靠观察，不能补为零。");
            if (unknowns.Count == 0) remaining = Math.Max(0, maximum!.Value - damage!.Value);
            // A deprecated field cannot invalidate or repair the independently observed modern pair.
            foreach (var problem in legacyProblems) unknowns.Add("已忽略deprecated DURABILITY：" + problem);
        }
        else if (mode == WeaponDurabilityMode.LegacyDurability)
        {
            unknowns.AddRange(legacyProblems);
            if (legacy is null) unknowns.Add("旧版武器的当前DURABILITY尚未可靠观察。");
            if (damage is null) unknowns.Add("旧版武器的当前DAMAGE尚未可靠观察，不能补为零。");
            if (unknowns.Count == 0) remaining = Math.Max(0, legacy!.Value - damage!.Value);
        }
        else
        {
            unknowns.AddRange(healthProblems);
            unknowns.AddRange(legacyProblems);
            unknowns.Add("武器数值协议尚未由已审计build或显式旧版调用确认。");
        }
        return new(build, entity.Id, mode, maximum, damage, legacy, remaining,
            Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static bool WeaponType(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null) return false;
        var values = new[] { "CARDTYPE", "202" }.Where(tags.ContainsKey).Select(key => tags[key]).ToArray();
        return values.Length > 0 && values.All(value => value is "WEAPON" or "7");
    }

    private static int? Number(IReadOnlyDictionary<string, string>? tags, string name, string numeric,
        ICollection<string> unknowns)
    {
        if (tags is null) return null;
        var present = new[] { name, numeric }.Where(tags.ContainsKey).Select(key => tags[key]).ToArray();
        if (present.Length == 0) return null;
        int? result = null;
        foreach (var value in present)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0 ||
                result is { } previous && previous != number)
            {
                unknowns.Add($"武器{name}的当前值非法、为负或命名/数字别名互相矛盾。");
                return null;
            }
            result = number;
        }
        return result;
    }
}
