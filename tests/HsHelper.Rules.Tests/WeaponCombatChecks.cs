using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class WeaponCombatChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("modern native weapon attack spends one observed health resource without a legacy tag", () =>
        {
            foreach (var id in new[] { "CS2_106", "CORE_CS2_106" })
            {
                var line = Eval(Frame(Weapon(31, id, 1, 3, 2, 0)), cards, Attack());
                Check(line.Verification == LineVerification.Verified && line.AppliedActions == 1 && Find(line, 11).RemainingHealth == 27 &&
                    Find(line, 31).WeaponDurability is { Remaining: 1, Mode: WeaponDurabilityMode.ModernHealthDamage } &&
                    Find(line, 31).Flags["HEALTH"] == 2 && Find(line, 31).Flags["DAMAGE"] == 1 &&
                    !Find(line, 31).Flags.ContainsKey("DURABILITY"), "The modern use was rejected, spent twice or projected a legacy resource.");
            }
        });
        yield return ("modern last weapon use destroys it and preserves independent observed hero attack", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 0, 5), Weapon(31, "CS2_106", 1, 3, 2, 1)), cards, Attack());
            Check(line.Verification == LineVerification.Verified && Find(line, 31) is { IsAlive: false, Zone: "GRAVEYARD", WeaponDurability.Remaining: 0 } &&
                Find(line, 31).Flags["DAMAGE"] == 2 && Find(line, 10).Attack == 2 && Find(line, 11).RemainingHealth == 25,
                "Weapon destruction retained its attack or erased independent hero attack.");
        });
        yield return ("an equipped native Doomhammer supports two current attacks and then breaks", () =>
        {
            foreach (var id in new[] { "EX1_567", "CORE_EX1_567" })
            {
                var line = Eval(Frame(Hero(10, 1, 30, 0, 2), Weapon(31, id, 1, 2, 8, 6)), cards, Attack(), Attack());
                Check(line.Verification == LineVerification.Verified && line.AppliedActions == 2 && Find(line, 11).RemainingHealth == 26 &&
                    Find(line, 31) is { IsAlive: false, WeaponDurability.Remaining: 0 } && Find(line, 10).Attack == 0,
                    "Past Overload was re-applied or weapon Windfury/durability was not honored.");
            }
        });
        yield return ("weapon Windfury disappears on its final use instead of authorizing another base attack", () =>
        {
            var frame = Frame(Hero(10, 1, 30, 0, 4), Weapon(31, "EX1_567", 1, 2, 8, 7));
            var line = Eval(frame, cards, Attack(), Attack());
            Check(line.AppliedActions == 1 && !line.IsLethal && Find(line, 11).RemainingHealth == 26 && Find(line, 10).Attack == 2,
                "Destroyed weapon Windfury authorized a second attack from independent hero attack.");
        });
        yield return ("independent hero Windfury survives weapon destruction and uses only remaining base attack", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 0, 5, ("WINDFURY", "1")), Weapon(31, "CS2_106", 1, 3, 2, 1)), cards, Attack(), Attack());
            Check(line.Verification == LineVerification.Verified && line.AppliedActions == 2 && Find(line, 11).RemainingHealth == 23 &&
                Find(line, 10).Attack == 2 && Find(line, 10).AttacksUsed == 2, "Weapon destruction removed an independent current Windfury source.");
        });
        yield return ("stale deprecated durability cannot produce a second attack or a verified lethal", () =>
        {
            var frame = Frame(Hero(10, 1, 30, 0, 3, ("WINDFURY", "1")), Hero(11, 2, 6, 0, 0),
                Tag(Weapon(31, "CS2_106", 1, 3, 2, 1), "DURABILITY", "2"));
            var line = Eval(frame, cards, Attack(), Attack());
            Check(!line.IsLethal && line.AppliedActions == 1 && Find(line, 11).RemainingHealth == 3 &&
                Find(line, 31) is { IsAlive: false, WeaponDurability.Remaining: 0 }, "Stale DURABILITY created the known false-lethal regression.");
        });
        yield return ("missing modern weapon DAMAGE remains unknown even with deprecated durability", () =>
        {
            var source = Remove(Tag(Weapon(31, "CS2_106", 1, 3, 2, 0), "DURABILITY", "2"), "DAMAGE");
            var line = Eval(Frame(source), cards, Attack());
            NoAttack(line);
            Check(Find(line, 31).WeaponDurability is { IsKnown: false, Remaining: null } && !Find(line, 31).Flags.ContainsKey("DAMAGE"),
                "A missing observed DAMAGE was repaired to zero in the output.");
        });
        yield return ("weapon modern alias conflicts and malformed numbers reject before combat", () =>
        {
            foreach (var (key, value) in new[] { ("45", "3"), ("44", "1"), ("HEALTH", "bad"), ("DAMAGE", "-1"), ("202", "4") })
                NoAttack(Eval(Frame(Tag(Weapon(31, "CS2_106", 1, 3, 2, 0), key, value)), cards, Attack()));
        });
        yield return ("weapon attacks do not replace missing current hero or weapon observations with defaults", () =>
        {
            NoAttack(Eval(Frame(Remove(Hero(10, 1, 30, 0, 3), "DAMAGE"), Weapon(31, "CS2_106", 1, 3, 2, 0)), cards, Attack()));
            NoAttack(Eval(Frame(Remove(Weapon(31, "CS2_106", 1, 3, 2, 0), "ATK")), cards, Attack()));
        });
        yield return ("multiple equipped weapons and an already depleted PLAY weapon are not selected by enumeration order", () =>
        {
            NoAttack(Eval(Frame(Weapon(31, "CS2_106", 1, 3, 2, 0), Weapon(32, "CORE_CS2_106", 1, 3, 2, 0)), cards, Attack()));
            NoAttack(Eval(Frame(Weapon(31, "CS2_106", 1, 3, 2, 2)), cards, Attack()));
        });
        yield return ("weapon current resources are not inferred from another or unknown build", () =>
        {
            foreach (var build in new string?[] { null, "255789", "255791" })
                NoAttack(Eval(Frame(Weapon(31, "CS2_106", 1, 3, 2, 0)) with { ClientBuild = build }, cards, Attack()));
        });
        yield return ("current weapon Lifesteal heals before the last-use destruction", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 10, 2), Weapon(31, "CORE_BT_921", 1, 2, 2, 1), Body(40, 2, 3, 4)), cards, Attack(40));
            Check(line.Verification == LineVerification.Verified && Find(line, 10).RemainingHealth == 19 &&
                Find(line, 40).RemainingHealth == 2 && !Find(line, 31).IsAlive,
                "Last-use Lifesteal vanished before healing or retaliation was skipped.");
        });
        yield return ("current weapon Poisonous kills a minion but a shield blocks poison while still spending one use", () =>
        {
            var weapon = Tag(Weapon(31, "CS2_106", 1, 3, 2, 0), "POISONOUS", "1");
            var line = Eval(Frame(weapon, Body(40, 2, 1, 20)), cards, Attack(40));
            Check(line.Verification == LineVerification.Verified && !Find(line, 40).IsAlive && Find(line, 31).WeaponDurability?.Remaining == 1,
                "Observed weapon Poisonous did not apply to positive minion combat damage.");
            line = Eval(Frame(weapon, Tag(Body(40, 2, 1, 20), "DIVINE_SHIELD", "1")), cards, Attack(40));
            Check(line.Verification == LineVerification.Verified && Find(line, 40).RemainingHealth == 20 &&
                Find(line, 40).Flags["DIVINE_SHIELD"] == 0 && Find(line, 31).WeaponDurability?.Remaining == 1,
                "Shield failed to block Poisonous or prevented weapon-use consumption.");
        });
        yield return ("observed equipped Longbow attacking immunity prevents retaliation without preventing durability loss", () =>
        {
            var weapon = Tag(Weapon(31, "DS1_188", 1, 5, 2, 0), "IMMUNE_WHILE_ATTACKING", "1");
            var line = Eval(Frame(Hero(10, 1, 30, 5, 5), weapon, Body(40, 2, 8, 10)), cards, Attack(40));
            Check(line.Verification == LineVerification.Verified && Find(line, 10).RemainingHealth == 25 && Find(line, 40).RemainingHealth == 5 &&
                Find(line, 31).WeaponDurability?.Remaining == 1, "While-attacking immunity retained durability or allowed retaliation.");
        });
        yield return ("an enemy equipped weapon is not consumed by an ordinary friendly minion attack", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 0, 0), Weapon(32, "CS2_106", 2, 3, 2, 1), Body(40, 1, 3, 4)), cards, new PlannedAction(ActionKind.Attack, 40, 11));
            Check(line.Verification == LineVerification.Verified && Find(line, 11).RemainingHealth == 27 && Find(line, 40).RemainingHealth == 4 &&
                Find(line, 32) is { IsAlive: true, WeaponDurability.Remaining: 1 } && Find(line, 32).Flags["DAMAGE"] == 1,
                "A defender's weapon was spent or hero weapon attack caused retaliation outside its turn.");
        });
        yield return ("simple weapon equipment uses current cost attack and health and enters PLAY with fresh DAMAGE zero", () =>
        {
            var frame = Frame(Hero(10, 1, 30, 0, 2), HandWeapon("CS2_106", 4, 3, 1));
            var line = Eval(frame, cards, new PlannedAction(ActionKind.PlayCard, 30));
            Check(line.Verification == LineVerification.Verified && line.AppliedActions == 1 && line.Result.AvailableMana == 9 &&
                Find(line, 10).Attack == 6 && Find(line, 30) is { Zone: "PLAY", Attack: 4, WeaponDurability.Remaining: 3 } &&
                Find(line, 30).Flags["DAMAGE"] == 0, "Printed stats/cost replaced observed hand values or fresh equipment lacked explicit zero damage.");
        });
        yield return ("replacing a simple equipped weapon changes only its attack contribution", () =>
        {
            var frame = Frame(Hero(10, 1, 30, 0, 5), Weapon(31, "CS2_106", 1, 3, 2, 1), HandWeapon("CORE_CS2_106", 4, 3, 1));
            var line = Eval(frame, cards, new PlannedAction(ActionKind.PlayCard, 30), Attack());
            Check(line.Verification == LineVerification.Verified && line.AppliedActions == 2 && !Find(line, 31).IsAlive && Find(line, 31).Zone == "GRAVEYARD" &&
                Find(line, 10).Attack == 6 && Find(line, 11).RemainingHealth == 24 && Find(line, 30).WeaponDurability?.Remaining == 2,
                "Replacement stacked the old attack contribution or destroyed the new weapon.");
        });
        yield return ("weapon equipment with a supplied target rejects before mana or alternate health payment", () =>
        {
            var frame = Armed(Frame(Weapon(31, "CS2_106", 1, 3, 2, 0), HandWeapon("CS2_106", 4, 3, 1)));
            var line = Eval(frame, cards, new PlannedAction(ActionKind.PlayCard, 30, 11));
            NoEquipmentPayment(line, expectedEnemyHealth: 30);
            Check(line.Result.NextCardCostsOpponentHealth == true && Find(line, 31).IsAlive, "Invalid weapon target spent the alternate-payment effect or old weapon.");
        });
        yield return ("weapon equipment never fills missing current attack health or cost from printed data", () =>
        {
            foreach (var field in new[] { "ATK", "HEALTH", "COST" })
            {
                var frame = Frame(Remove(HandWeapon("CS2_106", 4, 3, 1), field));
                var line = Eval(frame, cards, new PlannedAction(ActionKind.PlayCard, 30));
                NoEquipmentPayment(line, expectedEnemyHealth: 30);
            }
        });
        yield return ("uncovered weapon Battlecry and Overload reject before payment even when alternate payment is armed", () =>
        {
            foreach (var id in new[] { "EX1_567", "CORE_EX1_567", "CORE_ICC_064" })
            {
                var line = Eval(Armed(Frame(HandWeapon(id, 2, 8, 5))), cards, new PlannedAction(ActionKind.PlayCard, 30));
                NoEquipmentPayment(line, expectedEnemyHealth: 30);
                Check(line.Result.NextCardCostsOpponentHealth == true, "Unsupported weapon use consumed the next-card payment effect.");
            }
        });
        yield return ("unknown old-weapon deathrattle prevents following actions from using an invented replacement result", () =>
        {
            var frame = Frame(Hero(10, 1, 30, 0, 1), Weapon(31, "BAR_330", 1, 1, 2, 1), HandWeapon("CS2_106", 3, 2, 1));
            var line = Eval(frame, cards, new PlannedAction(ActionKind.PlayCard, 30), Attack());
            Check(line.Verification != LineVerification.Verified && !line.IsLethal && line.AppliedActions <= 1 && Find(line, 11).RemainingHealth == 30,
                "An unresolved weapon deathrattle was crossed to perform the following attack.");
        });
        yield return ("a passive native weapon deathrattle does not fire during a surviving ordinary use", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 0, 1), Weapon(31, "BAR_330", 1, 1, 2, 0)), cards, Attack());
            Check(line.Verification == LineVerification.Verified && line.AppliedActions == 1 && Find(line, 11).RemainingHealth == 29 &&
                Find(line, 31) is { IsAlive: true, WeaponDurability.Remaining: 1 }, "An inactive deathrattle blocked ordinary observed weapon use.");
        });
        yield return ("last-use native weapon deathrattle stops before a requested following end-turn action", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 0, 1), Weapon(31, "BAR_330", 1, 1, 2, 1)), cards, Attack(), new PlannedAction(ActionKind.EndTurn, 2));
            Check(line.Verification != LineVerification.Verified && line.AppliedActions == 1 && !line.Result.TurnEnded &&
                !Find(line, 31).IsAlive && Find(line, 11).RemainingHealth == 29,
                "Unobserved weapon-deathrattle draw or downstream triggers were crossed into another action.");
        });
        yield return ("alternate health payment equips a supported weapon without spending mana", () =>
        {
            var line = Eval(Armed(Frame(Hero(10, 1, 30, 0, 0), HandWeapon("CS2_106", 3, 2, 4))), cards, new PlannedAction(ActionKind.PlayCard, 30));
            Check(line.Verification == LineVerification.Verified && line.Result.AvailableMana == 10 && Find(line, 11).RemainingHealth == 26 &&
                line.Result.NextCardCostsOpponentHealth == false && line.Result.OpponentHealthPayments.Single().Cost == 4 &&
                Find(line, 30) is { Zone: "PLAY", WeaponDurability.Remaining: 2 }, "Weapon equipment ignored the observed next-card opponent-health payment.");
        });
        yield return ("terminal alternate payment does not equip or destroy either weapon before game-ending payment", () =>
        {
            var frame = Armed(Frame(Hero(11, 2, 30, 28, 0), Weapon(31, "CS2_106", 1, 3, 2, 1), HandWeapon("CORE_CS2_106", 4, 3, 3)));
            var line = Eval(frame, cards, new PlannedAction(ActionKind.PlayCard, 30));
            Check(line.Verification == LineVerification.Verified && line.Result.OpponentDefeatedByPayment && line.IsLethal &&
                Find(line, 30).Zone == "HAND" && Find(line, 31) is { Zone: "PLAY", IsAlive: true, WeaponDurability.Remaining: 1 } &&
                line.Result.AvailableMana == 10, "Terminal payment prematurely replaced the equipped weapon or created a playable new one.");
        });
        yield return ("insufficient mana and alternate cost above ten cannot destroy the old weapon", () =>
        {
            var lowMana = Replace(Frame(Weapon(31, "CS2_106", 1, 3, 2, 1), HandWeapon("CS2_106", 3, 2, 5)), Player(2, 1, 10, 2));
            var line = Eval(lowMana, cards, new PlannedAction(ActionKind.PlayCard, 30));
            Check(line.AppliedActions == 0 && line.Result.AvailableMana == 2 && Find(line, 30).Zone == "HAND" && Find(line, 31).IsAlive,
                "Failed mana payment changed equipment.");
            line = Eval(Armed(Frame(Weapon(31, "CS2_106", 1, 3, 2, 1), HandWeapon("CS2_106", 3, 2, 11))), cards, new PlannedAction(ActionKind.PlayCard, 30));
            NoEquipmentPayment(line, expectedEnemyHealth: 30);
            Check(line.Result.NextCardCostsOpponentHealth == true && Find(line, 31).IsAlive, "Alternate payment above ten changed equipment.");
        });
        yield return ("modern weapon combat leaves original snapshots and fixed card definitions untouched", () =>
        {
            var frame = Frame(Weapon(31, "CS2_106", 1, 3, 2, 0));
            var original = JsonSerializer.Serialize(frame); var definitions = JsonSerializer.Serialize(cards);
            var line = Eval(frame, cards, Attack());
            Check(line.AppliedActions == 1 && JsonSerializer.Serialize(frame) == original && JsonSerializer.Serialize(cards) == definitions &&
                frame.Entities[31].GetIntTag("DAMAGE") == 0, "Simulation mutated authoritative weapon or card data.");
        });
        yield return ("weapon combat rejects contradictory raw character keywords before an attack", () =>
        {
            foreach (var (tag, alias) in new[] { ("WINDFURY", "189"), ("IMMUNE", "240"), ("FROZEN", "260"), ("CANT_ATTACK", "227") })
                NoAttack(Eval(Frame(Hero(10, 1, 30, 0, 3, (tag, "0"), (alias, "1")), Weapon(31, "CS2_106", 1, 3, 2, 0)), cards, Attack()));
            NoAttack(Eval(Frame(Tag(Tag(Hero(11, 2, 30, 0, 0), "IMMUNE", "0"), "240", "1"), Weapon(31, "CS2_106", 1, 3, 2, 0)), cards, Attack()));
        });
        yield return ("weapon-derived raw hero Windfury stops at both last-use and replacement boundaries", () =>
        {
            var hero = Hero(10, 1, 30, 0, 2, ("WINDFURY", "1"));
            var last = Eval(Frame(hero, Weapon(31, "EX1_567", 1, 2, 8, 7)), cards, Attack(), Attack());
            Check(last.AppliedActions == 1 && last.Result.WeaponResolutionBoundaryReached && !last.IsLethal,
                "Last use treated the raw hero Windfury as an independent source.");
            var replace = Eval(Frame(hero, Hero(11, 2, 5, 0, 0), Weapon(31, "EX1_567", 1, 2, 8, 0), HandWeapon("CS2_106", 3, 2, 1)), cards,
                Attack(), new PlannedAction(ActionKind.PlayCard, 30), Attack());
            Check(replace.AppliedActions == 2 && replace.Result.WeaponResolutionBoundaryReached && !replace.IsLethal &&
                Find(replace, 11).RemainingHealth == 3 && Find(replace, 30).Zone == "HAND",
                "Replacement reused weapon-derived raw hero Windfury to prove a second attack or equipped beyond the boundary.");
        });
        yield return ("unknown weapon destruction listeners stop replacement before projecting new equipment", () =>
        {
            var unknown = Body(40, 1, 1, 3) with { CardId = "UNAUDITED_WEAPON_LISTENER" };
            var line = Eval(Frame(unknown, Weapon(31, "CS2_106", 1, 3, 2, 0), HandWeapon("CS2_106", 4, 2, 1)), cards,
                new PlannedAction(ActionKind.PlayCard, 30), Attack());
            Check(line.AppliedActions == 1 && line.Result.WeaponResolutionBoundaryReached && !line.IsLethal &&
                !Find(line, 31).IsAlive && Find(line, 30).Zone == "HAND" && Find(line, 11).RemainingHealth == 30,
                "Unknown destruction listeners allowed a projected replacement and subsequent attack.");
        });
        yield return ("signed independent hero attack is preserved across last use and re-equipment", () =>
        {
            var line = Eval(Frame(Hero(10, 1, 30, 0, 1), Weapon(31, "CS2_106", 1, 3, 2, 1), HandWeapon("CS2_106", 4, 2, 1)), cards,
                Attack(), new PlannedAction(ActionKind.PlayCard, 30));
            Check(line.Verification == LineVerification.Verified && Find(line, 10).Attack == 2 && Find(line, 30).WeaponDurability?.Remaining == 2,
                "A negative independent modifier was clamped away before re-equipment.");
        });
        yield return ("bounded lethal search includes native simple equip then hero attack", () =>
        {
            var found = new LineEvaluator().FindLethal(Frame(Hero(10, 1, 30, 0, 0), Hero(11, 2, 3, 0, 0), HandWeapon("CS2_106", 3, 2, 1)), cards, 2, 200);
            Check(found.VerifiedLethalFound && found.Actions.Count == 2 && found.Actions[0] == new PlannedAction(ActionKind.PlayCard, 30) &&
                found.Actions[1] == Attack(), "Simple weapon equipment was omitted from lethal search.");
        });
        yield return ("uncertain weapon ownership and reverse zone aliases cannot imply an unarmed hero", () =>
        {
            var hero = Hero(10, 1, 30, 0, 3, ("WINDFURY", "1"));
            var weapon = Weapon(31, "CS2_106", 1, 3, 2, 1);
            foreach (var candidate in new[] { Remove(weapon, "CONTROLLER"), Tag(Tag(weapon, "CONTROLLER", "2"), "50", "1"),
                Tag(Tag(weapon, "ZONE", "GRAVEYARD"), "49", "1") })
            {
                var line = Eval(Frame(hero, Hero(11, 2, 6, 0, 0), candidate), cards, Attack(), Attack());
                Check(line.Verification != LineVerification.Verified && line.AppliedActions == 0 && !line.IsLethal && Find(line, 11).RemainingHealth == 6,
                    "An uncertain weapon disappeared before combat and allowed a two-attack false lethal.");
            }
        });
        yield return ("bounded lethal search includes terminal opponent health payment for supported equipment", () =>
        {
            var found = new LineEvaluator().FindLethal(Armed(Frame(Hero(10, 1, 30, 0, 0), Hero(11, 2, 1, 0, 0), HandWeapon("CS2_106", 3, 2, 1))), cards, 1, 200);
            Check(found.VerifiedLethalFound && found.Actions.Count == 1 && found.Evaluation?.Result.OpponentDefeatedByPayment == true,
                "Supported weapon health payment was omitted from bounded search.");
        });
        yield return ("malformed weapon modifiers and conflicting current equip cost reject before combat or payment", () =>
        {
            foreach (var key in new[] { "IMMUNE", "CANT_BE_DESTROYED", "CANT_DAMAGE", "CANT_LOSE_DURABILITY", "DURABILITY_LOSS_DISABLED" })
                NoAttack(Eval(Frame(Tag(Weapon(31, "CS2_106", 1, 3, 2, 1), key, "bad")), cards, Attack()));
            NoEquipmentPayment(Eval(Frame(Tag(HandWeapon("CS2_106", 3, 2, 1), "48", "2")), cards,
                new PlannedAction(ActionKind.PlayCard, 30)), 30);
            NoEquipmentPayment(Eval(Frame(Tag(Tag(HandWeapon("CS2_106", 3, 2, 1), "LIFESTEAL", "1"), "685", "0")), cards,
                new PlannedAction(ActionKind.PlayCard, 30)), 30);
            NoEquipmentPayment(Eval(Frame(Tag(HandWeapon("CS2_106", 3, 2, 1), "IMMUNE", "bad")), cards,
                new PlannedAction(ActionKind.PlayCard, 30)), 30);
        });
    }

    private static PlannedAction Attack(int target = 11) => new PlannedAction(ActionKind.Attack, 10, target);
    private static LineEvaluation Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, params PlannedAction[] actions)
        => new LineEvaluator().EvaluateLine(frame, actions, cards);
    private static SimulatedEntity Find(LineEvaluation result, int id) => result.Result.Entities.Single(entity => entity.EntityId == id);
    private static void NoAttack(LineEvaluation line) => Check(line.Verification != LineVerification.Verified && line.AppliedActions == 0 &&
        !line.IsLethal && Find(line, 11).RemainingHealth == 30, "Unconfirmed equipment performed combat or proved lethal.");
    private static void NoEquipmentPayment(LineEvaluation line, int expectedEnemyHealth) => Check(line.Verification != LineVerification.Verified && line.AppliedActions == 0 &&
        line.Result.AvailableMana == 10 && Find(line, 11).RemainingHealth == expectedEnemyHealth && Find(line, 30).Zone == "HAND",
        "Rejected equipment was paid for or moved out of hand.");
    private static GameSnapshot Frame(params EntitySnapshot[] changes)
    {
        var entities = new[] { Player(2, 1, 10), Player(3, 2, 11), Hero(10, 1, 30, 0, 3), Hero(11, 2, 30, 0, 0) }.ToDictionary(entity => entity.Id);
        foreach (var entity in changes) entities[entity.Id] = entity;
        return new("modern-weapon-check", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static EntitySnapshot Player(int id, int owner, int hero, int resources = 10) => new(id, null, EntityKind.Player,
        new Dictionary<string, string> { ["PLAYER_ID"] = owner.ToString(), ["CONTROLLER"] = owner.ToString(), ["HERO_ENTITY"] = hero.ToString(),
            ["RESOURCES"] = resources.ToString(), ["RESOURCES_USED"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Hero(int id, int owner, int maximum, int damage, int attack, params (string Key, string Value)[] flags)
        => Entity(id, "HERO_" + owner, owner, "PLAY", "HERO", [("HEALTH", maximum.ToString()), ("DAMAGE", damage.ToString()), ("ARMOR", "0"),
            ("ATK", attack.ToString()), ("EXHAUSTED", "0"), ("NUM_ATTACKS_THIS_TURN", "0"), .. flags]);
    private static EntitySnapshot Body(int id, int owner, int attack, int health) => Entity(id, "CS2_172", owner, "PLAY", "MINION",
        [("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ("EXHAUSTED", "0"), ("NUM_TURNS_IN_PLAY", "1")]);
    private static EntitySnapshot Weapon(int id, string card, int owner, int attack, int maximum, int damage) => Entity(id, card, owner, "PLAY", "WEAPON",
        [("ATK", attack.ToString()), ("HEALTH", maximum.ToString()), ("DAMAGE", damage.ToString())]);
    private static EntitySnapshot HandWeapon(string card, int attack, int maximum, int cost) => Entity(30, card, 1, "HAND", "WEAPON",
        [("ATK", attack.ToString()), ("HEALTH", maximum.ToString()), ("COST", cost.ToString())]);
    private static EntitySnapshot Entity(int id, string card, int owner, string zone, string type, (string Key, string Value)[] values)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = type };
        foreach (var (key, value) in values) tags[key] = value;
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Armed(GameSnapshot frame) => Replace(frame, Entity(50, "EDR_489e1", 1, "SETASIDE", "ENCHANTMENT", [("ATTACHED", "2")]));
    private static GameSnapshot Replace(GameSnapshot frame, params EntitySnapshot[] changes)
    { var entities = frame.Entities.ToDictionary(); foreach (var entity in changes) entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static EntitySnapshot Remove(EntitySnapshot entity, string key)
    { var tags = entity.Tags.ToDictionary(); tags.Remove(key); return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "weapon-combat-255790.json")));
        Check(doc.RootElement.GetProperty("build").GetString() == "255790", "Weapon fixture is not pinned.");
        return doc.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(),
            raw.GetProperty("name").GetString()!, raw.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "", raw.GetProperty("cardClass").GetString()!,
            raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!, raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
            raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
