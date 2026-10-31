using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class EndTurnRegression
{
    private static readonly LineEvaluator Evaluator = new();
    private static readonly IReadOnlyDictionary<string, CardRecord> Cards = LoadCards();

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("mythical terror summons using observed cost and stats before its end-turn trigger", Summon);
        yield return ("end turn requires the local player and closes the own-action boundary", EndBoundary);
        yield return ("single forced combat unfolds damage and lifesteal while retaining unresolved readiness", SingleCombat);
        yield return ("forced simultaneous combat handles divine shield poisonous and lifesteal", ShieldPoison);
        yield return ("both owners receive their own forced-combat lifesteal and attacking immunity is respected", CombatModifiers);
        yield return ("pinned terror secondary beast race respects its observed primary and later race changes", DualRaceGoldrinn);
        yield return ("multiple enemies retain every pending candidate without inventing an attack order", MultipleEnemies);
        yield return ("native terror definition changes reject before payment or entry", SourceDefinition);
        yield return ("multiple terrors do not invent trigger ordering", MultipleTerrors);
        yield return ("silenced dormant dead or enemy terrors do not trigger on our end turn", InactiveTerrors);
        yield return ("unmapped end-turn text and repetition modifiers never claim fully resolved triggers", UnmappedEffects);
        yield return ("missing forced-combat data and unaudited builds leave inspectable pending effects", MissingEvidence);
        yield return ("forced combat special readiness is conditional rather than treated as a manual illegal attack", SpecialReadiness);
        yield return ("forced-combat results are immutable and preserve the source snapshot", Immutable);
    }

    private static void Summon()
    {
        var snapshot = Fixture(Hand(30, 4, 9, 14), Minion(20, "VANILLA", 2, 3, 15));
        var played = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30));
        Check(played.Verification == LineVerification.Verified && played.Result.AvailableMana == 6 &&
            Find(played, 30) is { Zone: "PLAY", Attack: 9, MaximumHealth: 14, RemainingHealth: 14 } && !played.Result.TurnEnded,
            "Summoning lost observed discounts/stats or executed the end-turn trigger early.");
        var ended = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.EndTurn, 2));
        Check(ended.Result.TurnEnded && ended.Result.ForcedAttacks.Count == 1 && Find(ended, 30).RemainingHealth == 11 &&
            Find(ended, 20).RemainingHealth == 6 && Find(ended, 10).RemainingHealth == 29,
            "A freshly summoned terror did not retaliate with its current stats and native Lifesteal.");
        Check(Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30, 20)).Verification == LineVerification.Invalid, "Terror accepted an artificial battlecry target.");
        var full = Fixture([Hand(30, 4, 9, 14), .. Enumerable.Range(40, 7).Select(id => Minion(id, "VANILLA", 1, 1, 1))]);
        Check(Eval(full, new PlannedAction(ActionKind.PlayCard, 30)).Verification == LineVerification.Invalid, "A full board accepted another terror.");
    }

    private static void SourceDefinition()
    {
        var source = Cards["CORE_TTN_866"];
        foreach (var definition in new[] { source with { Text = "未覆盖的替代文字" }, source with { DbfId = 1 },
            source with { RawJson = source.RawJson.Replace("LIFESTEAL", "POISONOUS", StringComparison.Ordinal) } })
        {
            var cards = Cards.ToDictionary(); cards[source.Id] = definition;
            var line = Evaluator.EvaluateLine(Fixture(Hand(30, 1, 4, 5)), [new PlannedAction(ActionKind.PlayCard, 30)], cards);
            Check(line.Verification == LineVerification.Unsupported && line.AppliedActions == 0 && line.Result.AvailableMana == 10 &&
                Find(line, 30).Zone == "HAND", "An unaudited native terror definition consumed mana or entered play.");
        }
    }

    private static void EndBoundary()
    {
        var snapshot = Fixture();
        var ended = Eval(snapshot, new PlannedAction(ActionKind.EndTurn, 2));
        Check(ended.Verification == LineVerification.Verified && ended.IsLegal == true && ended.AppliedActions == 1 &&
            ended.Result.TurnEnded && ended.Result.EndTurnTriggersResolved && ended.Result.ForcedAttacks.Count == 0,
            "Plain end turn did not establish a resolved own-turn boundary.");
        foreach (var action in new[] { new PlannedAction(ActionKind.EndTurn, 3), new PlannedAction(ActionKind.EndTurn, 10), new PlannedAction(ActionKind.EndTurn, 2, 11) })
            Check(Eval(snapshot, action).Verification == LineVerification.Invalid, "An invalid end-turn source or target was accepted.");
        Check(Eval(snapshot, new PlannedAction(ActionKind.EndTurn, 2), new PlannedAction(ActionKind.HeroPower, 12)).Verification == LineVerification.Invalid,
            "A later own action crossed the end-turn boundary.");
        Check(Eval(snapshot with { Phase = GamePhase.Choice }, new PlannedAction(ActionKind.EndTurn, 2)).Verification == LineVerification.Invalid,
            "End turn bypassed an unresolved choice phase.");
        Check(ended.RuleCoverage.Contains("own-end-turn-boundary-before-opponent-start"), "The result did not name its opponent-start boundary.");
    }

    private static void SingleCombat()
    {
        var snapshot = Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6, ("EXHAUSTED", 1), ("NUM_ATTACKS_THIS_TURN", 2)));
        var result = Eval(snapshot, new PlannedAction(ActionKind.EndTurn, 2));
        Check(result.Verification == LineVerification.Conditional && !result.IsLethal && result.Result.TurnEnded && !result.Result.EndTurnTriggersResolved,
            "Unobserved forced-attack counters became a complete verified state.");
        Check(Find(result, 30).RemainingHealth == 7 && Find(result, 20).RemainingHealth == 2 && Find(result, 10).RemainingHealth == 24,
            "The unique forced exchange or defending terror's Lifesteal was not applied.");
        var attack = result.Result.ForcedAttacks.Single();
        Check(attack is { TriggerEntityId: 30, SourceEntityId: 20, TargetEntityId: 30, DamageToTarget: 3, DamageToSource: 4,
            SourceSurvived: true, TargetSurvived: true }, "Forced-combat evidence referred to the wrong participants or damage.");
        Check(Find(result, 20).AttacksUsed == 2 && Find(result, 20).Flags["EXHAUSTED"] == 1 &&
            Find(result, 20).Flags["RULES_FORCED_ATTACK_READINESS_NOT_PROJECTED"] == 1 &&
            result.Result.UnresolvedEndTurnFields.Contains("entity:20:NUM_ATTACKS_THIS_TURN") &&
            result.UnsupportedMechanics.Contains("FORCED_ATTACK_READINESS_UNCONFIRMED"),
            "Carried observations were not clearly identified as unresolved post-trigger fields.");
    }

    private static void ShieldPoison()
    {
        var snapshot = Fixture(Terror(("DIVINE_SHIELD", 1)), Minion(20, "POISON", 2, 3, 4));
        var shield = Eval(snapshot, new PlannedAction(ActionKind.EndTurn, 2));
        Check(Find(shield, 30).RemainingHealth == 10 && Find(shield, 30).Flags["DIVINE_SHIELD"] == 0 &&
            !Find(shield, 20).IsAlive && Find(shield, 10).RemainingHealth == 24,
            "A shielded poisonous hit killed the terror or prevented its simultaneous retaliation.");
        var poison = Eval(Fixture(Terror(), Minion(20, "POISON", 2, 3, 4)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(!Find(poison, 30).IsAlive && !Find(poison, 20).IsAlive && Find(poison, 10).RemainingHealth == 24,
            "Simultaneous poison death suppressed a covered Lifesteal retaliation.");
    }

    private static void CombatModifiers()
    {
        var immune = Eval(Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6, ("IMMUNE_WHILE_ATTACKING", 1))), new PlannedAction(ActionKind.EndTurn, 2));
        Check(Find(immune, 20).RemainingHealth == 6 && Find(immune, 30).RemainingHealth == 7 &&
            Find(immune, 10).RemainingHealth == 20 && immune.Result.ForcedAttacks.Single().DamageToSource == 0,
            "Attacking immunity or actual Lifesteal damage was ignored.");
        var both = Eval(Fixture(Terror(), Minion(20, "LIFE", 2, 3, 6)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(Find(both, 10).RemainingHealth == 24 && Find(both, 11).RemainingHealth == 23,
            "Forced combat healed the wrong owner or omitted one participant's Lifesteal.");
        var death = Eval(Fixture(Terror(), Minion(20, "DEATH", 2, 3, 4)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(death.Verification == LineVerification.Conditional && death.UnsupportedMechanics.Contains("DEATHRATTLE_OR_REBORN") && !death.Result.EndTurnTriggersResolved,
            "Unimplemented death effects were reported as a complete end turn.");
    }

    private static void DualRaceGoldrinn()
    {
        var god = Minion(40, "EDR_480", 1, 9, 9);
        var native = Eval(Fixture(Terror(("CARDRACE", 15)), god, Minion(20, "VANILLA", 2, 3, 12)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(native.Result.ForcedAttacks.Single().DamageToSource == 8 && Find(native, 20).RemainingHealth == 4 && Find(native, 10).RemainingHealth == 28,
            "The audited DEMON primary tag erased terror's secondary BEAST race and Goldrinn damage.");
        var changed = Eval(Fixture(Terror(("CARDRACE", 24)), god, Minion(20, "VANILLA", 2, 3, 12)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(changed.Result.ForcedAttacks.Single().DamageToSource == 4 && Find(changed, 20).RemainingHealth == 8 && Find(changed, 10).RemainingHealth == 24,
            "An observed change to DRAGON incorrectly retained the printed BEAST secondary race.");
    }

    private static void MultipleEnemies()
    {
        var result = Eval(Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6), Minion(21, "VANILLA", 2, 5, 6)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(result.Verification == LineVerification.Conditional && result.Result.ForcedAttacks.Count == 0 && !result.Result.EndTurnTriggersResolved,
            "Multiple attackers were silently ordered.");
        var pending = result.Result.PendingForcedAttacks.Single();
        Check(pending.TriggerEntityId == 30 && pending.TargetEntityId == 30 && pending.CandidateSourceEntityIds.SequenceEqual([20, 21]) &&
            pending.Reason.Contains("顺序", StringComparison.Ordinal) && Find(result, 30).RemainingHealth == 10 &&
            Find(result, 20).RemainingHealth == 6 && Find(result, 21).RemainingHealth == 6 && Find(result, 10).RemainingHealth == 20,
            "The pending range lost candidates or mutated an arbitrarily chosen exchange.");
    }

    private static void MultipleTerrors()
    {
        var result = Eval(Fixture(Terror(), Terror(31), Minion(20, "VANILLA", 2, 3, 6)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(result.Verification == LineVerification.Conditional && result.Result.PendingForcedAttacks.Count == 2 &&
            result.Result.ForcedAttacks.Count == 0 && result.Result.PendingForcedAttacks.All(p => p.CandidateSourceEntityIds.SequenceEqual([20])) &&
            Find(result, 30).RemainingHealth == 10 && Find(result, 31).RemainingHealth == 10,
            "Multiple terror triggers fabricated an unsupported sequence.");
    }

    private static void InactiveTerrors()
    {
        foreach (var terror in new[] { Terror(("SILENCED", 1)), Terror(("DORMANT", 1)),
            Minion(30, "CORE_TTN_866", 1, 4, 0), Minion(30, "CORE_TTN_866", 2, 4, 10) })
        {
            var result = Eval(Fixture(terror, Minion(20, "VANILLA", 2, 3, 6)), new PlannedAction(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.Result.EndTurnTriggersResolved &&
                result.Result.ForcedAttacks.Count == 0 && result.Result.PendingForcedAttacks.Count == 0 && Find(result, 20).RemainingHealth == 6,
                "An inactive or enemy-owned terror triggered at our end turn.");
        }
        var empty = Eval(Fixture(Terror()), new PlannedAction(ActionKind.EndTurn, 2));
        Check(empty.Verification == LineVerification.Verified && empty.Result.EndTurnTriggersResolved,
            "A trigger with no enemy minions invented combat or unnecessary ordering uncertainty.");
    }

    private static void UnmappedEffects()
    {
        var unknown = Eval(Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6), Minion(40, "END_EFFECT", 1, 1, 2)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(unknown.Verification == LineVerification.Conditional && unknown.UnsupportedMechanics.Contains("UNMAPPED_END_TURN_EFFECT") &&
            unknown.Result.UnresolvedEndTurnFields.Contains("entity:40:END_TURN_EFFECT") && !unknown.Result.EndTurnTriggersResolved,
            "An uncovered end-turn trigger disappeared from the resolution boundary.");
        var multiplied = Fixture();
        var entities = multiplied.Entities.ToDictionary();
        entities[2] = entities[2] with { Tags = entities[2].Tags.Append(new KeyValuePair<string, string>("EXTRA_END_TURN_EFFECTS", "1")).ToFrozenDictionary() };
        var result = Eval(multiplied with { Entities = entities.ToFrozenDictionary() }, new PlannedAction(ActionKind.EndTurn, 2));
        Check(result.Verification == LineVerification.Conditional && result.UnsupportedMechanics.Contains("END_TURN_TRIGGER_MULTIPLIER") &&
            !result.Result.EndTurnTriggersResolved, "A repeated-end-effect modifier was treated as a normal resolved boundary.");
    }

    private static void MissingEvidence()
    {
        var snapshot = Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6));
        foreach (var frame in new[] { snapshot with { ClientBuild = "999999" }, Fixture(Terror(), Entity(20, "VANILLA", 2, "PLAY", "MINION", [])) })
        {
            var result = Eval(frame, new PlannedAction(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Conditional && result.Result.ForcedAttacks.Count == 0 && result.Result.PendingForcedAttacks.Count == 1,
                "Missing or wrong-build evidence became a fabricated precise exchange.");
        }
        var absent = Evaluator.EvaluateLine(snapshot, [new PlannedAction(ActionKind.EndTurn, 2)], Cards.Where(pair => pair.Key != "CORE_TTN_866").ToDictionary());
        Check(absent.Verification == LineVerification.Conditional && absent.Result.PendingForcedAttacks.Count == 1,
            "A missing terror definition still produced an audited trigger.");
        var empty = Fixture(Terror());
        var wrongEmpty = Eval(empty with { ClientBuild = "999999" }, new PlannedAction(ActionKind.EndTurn, 2));
        var missingEmpty = Evaluator.EvaluateLine(empty, [new PlannedAction(ActionKind.EndTurn, 2)], Cards.Where(pair => pair.Key != "CORE_TTN_866").ToDictionary());
        foreach (var result in new[] { wrongEmpty, missingEmpty })
            Check(result.Verification == LineVerification.Conditional && result.Result.PendingForcedAttacks.Count == 1 && !result.Result.EndTurnTriggersResolved,
                "An empty opposing board bypassed pinned-version or card-definition validation.");
        var unknownOwner = Minion(20, "VANILLA", 2, 3, 6);
        unknownOwner = unknownOwner with { Tags = unknownOwner.Tags.Where(pair => pair.Key != "CONTROLLER").ToFrozenDictionary() };
        foreach (var frame in new[] { Fixture(Terror(), unknownOwner), Fixture(Terror(), Minion(20, "VANILLA", 3, 3, 6)) })
        {
            var result = Eval(frame, new PlannedAction(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Conditional && result.Result.ForcedAttacks.Count == 0 &&
                result.UnsupportedMechanics.Contains("UNKNOWN_FORCED_COMBAT_OWNERSHIP"),
                "An unknown or third-party controller was treated as the confirmed opponent.");
        }
    }

    private static void SpecialReadiness()
    {
        var result = Eval(Fixture(Terror(), Minion(20, "VANILLA", 2, 0, 6, ("FROZEN", 1), ("EXHAUSTED", 1), ("CANT_ATTACK", 1))), new PlannedAction(ActionKind.EndTurn, 2));
        Check(result.Verification == LineVerification.Conditional && result.Result.ForcedAttacks.Count == 1 &&
            result.UnsupportedMechanics.Contains("FORCED_ATTACK_SPECIAL_READINESS") && Find(result, 30).RemainingHealth == 10 && Find(result, 20).RemainingHealth == 2,
            "Special forced readiness was either a false manual-action rejection or a verified claim.");
        var immuneTarget = Eval(Fixture(Terror(("IMMUNE", 1)), Minion(20, "VANILLA", 2, 3, 6)), new PlannedAction(ActionKind.EndTurn, 2));
        Check(immuneTarget.Verification == LineVerification.Conditional && immuneTarget.Result.ForcedAttacks.Count == 0 &&
            immuneTarget.Result.PendingForcedAttacks.Count == 1, "Unconfirmed forced targeting of an immune terror was guessed.");
    }

    private static void Immutable()
    {
        var frame = Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6));
        var before = JsonSerializer.Serialize(frame);
        var result = Eval(frame, new PlannedAction(ActionKind.EndTurn, 2));
        Check(before == JsonSerializer.Serialize(frame), "Forced combat mutated the captured game snapshot.");
        RejectMutation(() => ((IList<ForcedAttackResult>)result.Result.ForcedAttacks).Clear());
        RejectMutation(() => ((IList<string>)result.Result.UnresolvedEndTurnFields).Clear());
        var pending = Eval(Fixture(Terror(), Minion(20, "VANILLA", 2, 3, 6), Minion(21, "VANILLA", 2, 3, 6)), new PlannedAction(ActionKind.EndTurn, 2));
        RejectMutation(() => ((IList<PendingForcedAttack>)pending.Result.PendingForcedAttacks).Clear());
        RejectMutation(() => ((IList<int>)pending.Result.PendingForcedAttacks.Single().CandidateSourceEntityIds).Clear());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Check(json.RootElement.GetProperty("Result").GetProperty("TurnEnded").GetBoolean() &&
            json.RootElement.GetProperty("Result").GetProperty("ForcedAttacks")[0].GetProperty("SourceEntityId").GetInt32() == 20,
            "End-turn evidence did not survive JSON serialization.");
    }

    private static IReadOnlyDictionary<string, CardRecord> LoadCards()
    {
        var cards = new Dictionary<string, CardRecord>(StringComparer.Ordinal)
        {
            ["VANILLA"] = Card("VANILLA"), ["POISON"] = Card("POISON", ["POISONOUS"]),
            ["LIFE"] = Card("LIFE", ["LIFESTEAL"]), ["DEATH"] = Card("DEATH", ["DEATHRATTLE"], "亡语：未知效果。"),
            ["END_EFFECT"] = Card("END_EFFECT", ["TRIGGER_VISUAL"], "在你的回合结束时，造成1点伤害。")
        };
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mythical-terror-255790.json")));
        foreach (var raw in file.RootElement.GetProperty("cards").EnumerateArray())
        {
            var id = raw.GetProperty("id").GetString()!;
            cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                raw.GetProperty("cost").GetInt32(), true, [], [], raw.GetRawText());
        }
        using var hunter = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "beast-hunter-255790.json")));
        var god = hunter.RootElement.GetProperty("cards").EnumerateArray().Single(raw => raw.GetProperty("id").GetString() == "EDR_480");
        cards["EDR_480"] = new("EDR_480", god.GetProperty("dbfId").GetInt32(), god.GetProperty("name").GetString()!, god.GetProperty("text").GetString()!,
            god.GetProperty("cardClass").GetString()!, god.GetProperty("set").GetString()!, god.GetProperty("type").GetString()!,
            god.GetProperty("cost").GetInt32(), true, [], [], god.GetRawText());
        return cards;
    }
    private static CardRecord Card(string id, string[]? mechanics = null, string text = "")
        => new(id, 1, id, text, "HUNTER", "TEST", "MINION", 1, true, [], [],
            JsonSerializer.Serialize(new { id, attack = 3, health = 6, mechanics = mechanics ?? [], text, type = "MINION" }));
    private static LineEvaluation Eval(GameSnapshot snapshot, params PlannedAction[] actions) => Evaluator.EvaluateLine(snapshot, actions, Cards);
    private static SimulatedEntity Find(LineEvaluation result, int id) => result.Result.Entities.Single(e => e.EntityId == id);
    private static EntitySnapshot Terror(params (string Key, int Value)[] extra) => Terror(30, extra);
    private static EntitySnapshot Terror(int id, params (string Key, int Value)[] extra) => Minion(id, "CORE_TTN_866", 1, 4, 10, extra);
    private static EntitySnapshot Hand(int id, int cost, int attack, int health) => Entity(id, "CORE_TTN_866", 1, "HAND", "MINION", [("COST", cost), ("ATK", attack), ("HEALTH", health)]);
    private static EntitySnapshot Minion(int id, string card, int owner, int attack, int health, params (string Key, int Value)[] extra)
        => Entity(id, card, owner, "PLAY", "MINION", [("ATK", attack), ("HEALTH", health), ("DAMAGE", 0), ("EXHAUSTED", 0), ("NUM_TURNS_IN_PLAY", 1), .. extra]);
    private static EntitySnapshot Entity(int id, string? card, int owner, string zone, string type, (string Key, int Value)[] values)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = type };
        foreach (var value in values) tags[value.Key] = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Fixture(params EntitySnapshot[] additional)
    {
        EntitySnapshot Player(int id, int owner, int hero) => new(id, null, EntityKind.Player,
            new Dictionary<string, string> { ["CARDTYPE"] = "2", ["PLAYER_ID"] = owner.ToString(), ["HERO_ENTITY"] = hero.ToString(), ["RESOURCES"] = "10", ["RESOURCES_USED"] = "0" }.ToFrozenDictionary());
        EntitySnapshot Hero(int id, int owner) => Entity(id, "HERO_" + owner, owner, "PLAY", "HERO", [("HEALTH", 30), ("DAMAGE", 10), ("ATK", 0), ("ARMOR", 0)]);
        EntitySnapshot[] entities = [Player(2, 1, 10), Player(3, 2, 11), Hero(10, 1), Hero(11, 2),
            Entity(12, "HERO_05bp", 1, "PLAY", "HERO_POWER", [("COST", 2), ("EXHAUSTED", 0)]), .. additional];
        return new("end-turn-fixture", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(e => e.Id), new(true, true, true, true, 0, []))
        { ClientBuild = "255790", Format = "Standard" };
    }
    private static void RejectMutation(Action action)
    {
        try { action(); throw new Exception("End-turn result allowed a consumer mutation."); }
        catch (NotSupportedException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
