using System.Collections.Frozen;
using System.Text.Json.Nodes;
using HsHelper.App;
using HsHelper.Core;

public static class WeaponViewChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("battle weapon durability follows observed modern health minus damage", ObservedModern);
        yield return ("battle weapon missing damage stays unknown despite printed and deprecated durability", MissingModern);
        yield return ("battle weapon distinguishes modern alias conflict and explicit legacy evidence", ProtocolBoundaries);
        yield return ("line weapon summary uses bound current evidence for wear destruction and unknowns", LineSummary);
        yield return ("weapon resolution boundary downgrades forged line and lethal search JSON", PendingResolution);
    }

    private static void ObservedModern()
    {
        var frame = Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("DURABILITY", "99"));
        Check(View(frame)?.Durability == 2, "Deprecated or printed durability replaced the modern observed pair.");
        var tags = frame.Entities[11].Tags.ToDictionary(); tags["DAMAGE"] = "2";
        var worn = frame.Entities.ToDictionary(); worn[11] = frame.Entities[11] with { Tags = tags.ToFrozenDictionary() };
        Check(View(frame with { StateVersion = frame.StateVersion + 1, Entities = worn.ToFrozenDictionary() })?.Durability == 1 &&
            View(frame)?.Durability == 2, "Current durability did not follow newly observed damage or changed its earlier immutable frame.");
        var malformedOld = Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("DURABILITY", "obsolete"));
        var evidence = WeaponDurabilityEvidence.Resolve(malformedOld, 11);
        Check(evidence.IsKnown && evidence.Unknowns.Count > 0 && View(malformedOld)?.Durability == 2,
            "An ignored deprecated tag warning discarded a reliable modern pair.");
    }

    private static void MissingModern()
    {
        foreach (var frame in new[] { Frame(("HEALTH", "3"), ("DURABILITY", "99")),
            Frame(("DURABILITY", "3"), ("DAMAGE", "1")), Frame(("HEALTH", "3"), ("DAMAGE", "bad")),
            Frame(("HEALTH", "3"), ("DAMAGE", "-1")) })
            Check(View(frame)?.Durability is null, "Missing or invalid current damage was filled from zero, printed values or legacy tags.");
    }

    private static void ProtocolBoundaries()
    {
        Check(View(Frame(("HEALTH", "3"), ("45", "2"), ("DAMAGE", "1")))?.Durability is null &&
            View(Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("44", "0")))?.Durability is null &&
            View(Frame(("HEALTH", "3"), ("DAMAGE", "1"), ("202", "MINION")))?.Durability is null,
            "A current health, damage or type alias conflict became a reliable weapon durability.");
        var legacy = Frame(("DURABILITY", "3"), ("DAMAGE", "1"));
        Check(WeaponDurabilityEvidence.ResolveLegacy(legacy.Entities[11]) is { IsKnown: true, Remaining: 2 } &&
            View(legacy)?.Durability is null && View(Frame(("HEALTH", "3"), ("DAMAGE", "1")) with { ClientBuild = null })?.Durability is null,
            "An explicit legacy result or unknown build was automatically promoted into modern current evidence.");
    }

    private static void LineSummary()
    {
        var result = JsonNode.Parse("""
            {"Verification":"Verified","Result":{"Entities":[
             {"EntityId":11,"Type":"WEAPON","Zone":"PLAY","Flags":{"DURABILITY":99},
              "WeaponDurability":{"Build":"255790","Mode":"ModernHealthDamage","IsKnown":true,"IsDepleted":false,"Remaining":1,"Unknowns":[]}},
             {"EntityId":12,"Type":"WEAPON","Zone":"GRAVEYARD",
              "WeaponDurability":{"Build":"255790","Mode":"ModernHealthDamage","IsKnown":true,"IsDepleted":true,"Remaining":0,"Unknowns":[]}},
             {"EntityId":13,"Type":"WEAPON","Zone":"PLAY","MaximumHealth":3,"RemainingHealth":3,"Flags":{"DURABILITY":99}},
             {"EntityId":14,"Type":"WEAPON","Zone":"PLAY",
              "WeaponDurability":{"Build":null,"Mode":"LegacyDurability","IsKnown":true,"Remaining":2,"Unknowns":[]}},
             {"EntityId":90,"Type":"WEAPON","Zone":"HAND","CardId":"PRIVATE_WEAPON_PROBE"},
             {"EntityId":91,"Type":"WEAPON","Zone":"DECK","CardId":"PRIVATE_WEAPON_PROBE"}]}}
            """)!;
        var text = LocalToolResultFormatter.Format("evaluate_line", result, id => id >= 90 ? "PRIVATE_WEAPON_PROBE" : "武器-" + id);
        Check(text.Contains("武器-11：当前耐久 1", StringComparison.Ordinal) && text.Contains("协议 现代耐久；版本 255790", StringComparison.Ordinal) &&
            text.Contains("武器-12：当前耐久 0；耐久耗尽，已损毁", StringComparison.Ordinal) &&
            text.Contains("武器-13：当前耐久 未知", StringComparison.Ordinal) && text.Contains("不能用印刷值、旧标签或原局面补齐", StringComparison.Ordinal) &&
            text.Contains("武器-14：当前耐久 未知", StringComparison.Ordinal) && text.Contains("旧版兼容，不能替代当前协议", StringComparison.Ordinal) &&
            !text.Contains("当前耐久 99", StringComparison.Ordinal) && !text.Contains("PRIVATE_WEAPON_PROBE", StringComparison.Ordinal),
            "Weapon result display inferred current evidence, hid destruction, mixed legacy durability or exported a private weapon.");
    }

    private static void PendingResolution()
    {
        var line = JsonNode.Parse("""
            {"Verification":"Verified","IsLegal":true,"IsLethal":true,"AppliedActions":1,"Result":{"WeaponResolutionBoundaryReached":true,
             "Entities":[{"EntityId":11,"Type":"WEAPON","Zone":"GRAVEYARD",
             "WeaponDurability":{"Build":"255790","Mode":"ModernHealthDamage","IsKnown":true,"Remaining":0,"Unknowns":[]}}]}}
            """)!;
        var text = LocalToolResultFormatter.Format("evaluate_line", line);
        Check(text.Contains("验证级别：条件成立", StringComparison.Ordinal) && text.Contains("等待武器后续结算", StringComparison.Ordinal) &&
            text.Contains("损毁或替换后的触发尚未观察", StringComparison.Ordinal) && text.Contains("后续动作须等待新局面", StringComparison.Ordinal) &&
            text.Contains("未能证明", StringComparison.Ordinal) && !text.Contains("验证级别：已验证", StringComparison.Ordinal) &&
            !text.Contains("已验证斩杀：是", StringComparison.Ordinal), "A forged Verified flag bypassed the unresolved weapon boundary.");
        var search = new JsonObject { ["VerifiedLethalFound"] = true, ["ExploredNodes"] = 3, ["IsExhaustive"] = false,
            ["Actions"] = new JsonArray(), ["Evaluation"] = line.DeepClone() };
        text = LocalToolResultFormatter.Format("find_lethal", search);
        Check(!text.Contains("找到已验证的斩杀路线", StringComparison.Ordinal) && !text.Contains("已验证斩杀：是", StringComparison.Ordinal) &&
            text.Contains("验证级别：条件成立", StringComparison.Ordinal) && text.Contains("等待武器后续结算", StringComparison.Ordinal),
            "A forged lethal-search banner promoted pending weapon triggers into a complete verified route.");
    }

    private static GameSnapshot Frame(params (string Key, string Value)[] numbers)
    {
        var frame = BattleViewRegression.Frame(); var entities = frame.Entities.ToDictionary();
        entities[11] = new(11, "UI_WEAPON", EntityKind.Card, numbers.Concat(new[] { ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "WEAPON"), ("ATK", "4") })
            .ToFrozenDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal));
        return frame with { Entities = entities.ToFrozenDictionary() };
    }
    private static BattleCardView? View(GameSnapshot snapshot) => BattleViewBuilder.Build(snapshot, BattleViewRegression.Cards(), BattleViewRegression.Build).Local.Weapon;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
