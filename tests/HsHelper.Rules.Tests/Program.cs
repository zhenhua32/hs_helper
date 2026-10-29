using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

var passed = 0;
var failed = 0;
var evaluator = new LineEvaluator();
var cards = new Dictionary<string, CardRecord>(StringComparer.Ordinal)
{
    ["VANILLA"] = Card("VANILLA"),
    ["BEAST"] = Card("BEAST", race: "BEAST"),
    ["TAUNT"] = Card("TAUNT", text: "嘲讽", mechanics: ["TAUNT"]),
    ["SHIELD"] = Card("SHIELD", text: "圣盾", mechanics: ["DIVINE_SHIELD"]),
    ["POISON"] = Card("POISON", text: "剧毒", mechanics: ["POISONOUS"]),
    ["LIFE"] = Card("LIFE", text: "吸血", mechanics: ["LIFESTEAL"]),
    ["DEATH"] = Card("DEATH", text: "亡语：未知效果", mechanics: ["DEATHRATTLE"]),
    ["AURA"] = Card("AURA", text: "其他友方角色受伤减少", mechanics: ["AURA"]),
    ["SPELLPOWER"] = Card("SPELLPOWER", text: "法术伤害+1", spellDamage: 1),
    ["EDR_480"] = Card("EDR_480", text: "突袭。友方野兽造成的伤害翻倍。", mechanics: ["RUSH", "AURA"], race: "BEAST", attack: 9, health: 9),
    ["EDR_227"] = Card("EDR_227", text: "突袭。亡语：灌注你的英雄技能。", mechanics: ["DEATHRATTLE", "RUSH"], race: "BEAST"),
    ["FIR_953"] = Card("FIR_953", text: "攻击随从并存活后，随机分配伤害", mechanics: ["RUSH", "TRIGGER_VISUAL"], race: "BEAST"),
    ["EDR_852"] = Card("EDR_852", text: "战吼：灌注你的英雄技能。", mechanics: ["BATTLECRY"]),
    ["CORE_DS1_185"] = Card("CORE_DS1_185", "SPELL", cost: 1, text: "造成2点伤害。"),
    ["CORE_EX1_539"] = Card("CORE_EX1_539", "SPELL", cost: 3, text: "造成3点伤害，有野兽时改为5点。"),
    ["CS2_084"] = Card("CS2_084", "SPELL", cost: 1, text: "使一个随从的生命值变为1。"),
    ["GAME_005"] = Card("GAME_005", "SPELL", cost: 0, text: "获得一点本回合的法力。"),
    ["HERO_05bp"] = Card("HERO_05bp", "HERO_POWER", cost: 2, text: "对敌方英雄造成2点伤害。"),
    ["WEAPON"] = Card("WEAPON", "WEAPON", attack: 3, text: ""),
    ["CHARGE"] = Card("CHARGE", attack: 2, health: 2, text: "冲锋", mechanics: ["CHARGE"])
};

Run("combat exchange is simultaneous and does not mutate snapshot", () =>
{
    var snapshot = Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "VANILLA", 2, 3, 5));
    var before = JsonSerializer.Serialize(snapshot);
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification);
    Equal(3, Find(result, 21).RemainingHealth);
    Equal(1, Find(result, 20).RemainingHealth);
    Equal(before, JsonSerializer.Serialize(snapshot));
});
Run("taunt blocks hero targeting but immune or stealthed taunts do not", () =>
{
    var ordinary = Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "TAUNT", 2, 1, 5));
    Equal(LineVerification.Invalid, Eval(ordinary, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    var stealthed = Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "TAUNT", 2, 1, 5, ("STEALTH", 1)));
    Equal(LineVerification.Verified, Eval(stealthed, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    var immune = Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "TAUNT", 2, 1, 5, ("IMMUNE", 1)));
    Equal(LineVerification.Verified, Eval(immune, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    Equal(LineVerification.Invalid, Eval(immune, new PlannedAction(ActionKind.Attack, 21, 20)).Verification);
});
Run("divine shield consumes one hit and prevents poisonous", () =>
{
    var result = Eval(Fixture(Minion(21, "POISON", 1, 1, 6), Minion(20, "SHIELD", 2, 3, 20)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification);
    Equal(20, Find(result, 20).RemainingHealth);
    Equal(0, Find(result, 20).Flags["DIVINE_SHIELD"]);
    Equal(3, Find(result, 21).RemainingHealth);
});
Run("poisonous kills after unprevented minion damage", () =>
{
    var result = Eval(Fixture(Minion(21, "POISON", 1, 1, 6), Minion(20, "VANILLA", 2, 3, 20)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification);
    Equal(false, Find(result, 20).IsAlive);
});
Run("lifesteal uses dealt damage including overkill and respects maximum health", () =>
{
    var result = Eval(Fixture(Minion(21, "LIFE", 1, 4, 6), Minion(20, "VANILLA", 2, 1, 2)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(29, Find(result, 10).RemainingHealth);
    var big = Eval(Fixture(Minion(21, "LIFE", 1, 20, 6), Minion(20, "VANILLA", 2, 1, 2)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(30, Find(big, 10).RemainingHealth);
});
Run("armor is consumed before hero health", () =>
{
    var snapshot = Replace(Fixture(Minion(21, "VANILLA", 1, 4, 6)), Hero(11, 2, 5, 0, 3));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(4, Find(result, 11).RemainingHealth); Equal(0, Find(result, 11).Armor);
});
Run("attack count windfury frozen and ownership are checked", () =>
{
    var snapshot = Fixture(Minion(21, "VANILLA", 1, 4, 8, ("WINDFURY", 1)));
    var twice = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11), new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Verified, twice.Verification); Equal(12, Find(twice, 11).RemainingHealth);
    Equal(2, Find(twice, 21).AttacksUsed);
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11), new PlannedAction(ActionKind.Attack, 21, 11), new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    Equal(LineVerification.Invalid, Eval(Fixture(Minion(21, "VANILLA", 1, 4, 8, ("FROZEN", 1))), new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    Equal(LineVerification.Invalid, Eval(Fixture(Minion(21, "VANILLA", 2, 4, 8)), new PlannedAction(ActionKind.Attack, 21, 10)).Verification);
    Equal(LineVerification.Invalid, Eval(snapshot with { ActivePlayerId = 2 }, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
});
Run("rush can attack minions but cannot attack hero on entry turn", () =>
{
    var snapshot = Fixture(Minion(21, "VANILLA", 1, 4, 8, ("NUM_TURNS_IN_PLAY", 0), ("RUSH", 1)), Minion(20, "VANILLA", 2, 1, 3));
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    Equal(LineVerification.Verified, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 20)).Verification);
    var noRush = Fixture(Minion(21, "VANILLA", 1, 4, 8, ("NUM_TURNS_IN_PLAY", 0)));
    Equal(LineVerification.Invalid, Eval(noRush, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
});
Run("silence removes static taunt and lifesteal while preserving observed stats", () =>
{
    var snapshot = Fixture(Minion(21, "LIFE", 1, 4, 8, ("SILENCED", 1)), Minion(20, "TAUNT", 2, 1, 5, ("SILENCED", 1)));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Verified, result.Verification); Equal(25, Find(result, 10).RemainingHealth);
});
Run("steady shot spends mana once and does not use spell damage", () =>
{
    var snapshot = Fixture(Minion(21, "SPELLPOWER", 1, 1, 3));
    var result = Eval(snapshot, new PlannedAction(ActionKind.HeroPower, 12));
    Equal(LineVerification.Verified, result.Verification); Equal(18, Find(result, 11).RemainingHealth); Equal(8, result.Result.AvailableMana);
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.HeroPower, 12), new PlannedAction(ActionKind.HeroPower, 12)).Verification);
    var scarce = Replace(snapshot, Player(2, 1, 1, 10));
    Equal(LineVerification.Invalid, Eval(scarce, new PlannedAction(ActionKind.HeroPower, 12)).Verification);
    Equal(LineVerification.Unsupported, Eval(snapshot with { ClientBuild = "999999" }, new PlannedAction(ActionKind.HeroPower, 12)).Verification);
});
Run("coin enables a supported power and current spell cost is mandatory", () =>
{
    var snapshot = Replace(Fixture(Hand(30, "GAME_005", 0)), Player(2, 1, 1, 10));
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.HeroPower, 12));
    Equal(LineVerification.Verified, result.Verification); Equal(0, result.Result.AvailableMana);
    var missing = Entity(30, "CORE_DS1_185", 1, "HAND", "SPELL", []);
    Equal(LineVerification.Unsupported, Eval(Fixture(missing), new PlannedAction(ActionKind.PlayCard, 30, 11)).Verification);
});
Run("supported spells apply spell damage targets and hunter mark", () =>
{
    var snapshot = Fixture(Hand(30, "CORE_DS1_185", 1), Minion(21, "SPELLPOWER", 1, 1, 3));
    Equal(17, Find(Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30, 11)), 11).RemainingHealth);
    var elusive = Fixture(Hand(30, "CORE_DS1_185", 1), Minion(20, "VANILLA", 2, 1, 3, ("ELUSIVE", 1)));
    Equal(LineVerification.Invalid, Eval(elusive, new PlannedAction(ActionKind.PlayCard, 30, 20)).Verification);
    var mark = Fixture(Hand(30, "CS2_084", 1), Minion(21, "VANILLA", 1, 1, 5), Minion(20, "VANILLA", 2, 2, 10));
    var result = Eval(mark, new PlannedAction(ActionKind.PlayCard, 30, 20), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(false, Find(result, 20).IsAlive);
});
Run("goldrinn doubles friendly beast damage and silence disables aura", () =>
{
    var gold = Minion(22, "EDR_480", 1, 9, 9);
    var snapshot = Replace(Fixture(Minion(21, "BEAST", 1, 4, 6), gold), Hero(11, 2, 8));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Verified, result.Verification); Equal(true, result.IsLethal);
    var silenced = Replace(snapshot, Minion(22, "EDR_480", 1, 9, 9, ("SILENCED", 1)));
    Equal(4, Find(Eval(silenced, new PlannedAction(ActionKind.Attack, 21, 11)), 11).RemainingHealth);
});
Run("deathrattle and random after-attack effects cannot become verified lethal", () =>
{
    var death = Eval(Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "DEATH", 2, 1, 3)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Conditional, death.Verification); Equal(false, death.IsLethal);
    var random = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 8), Minion(20, "VANILLA", 2, 1, 3)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Conditional, random.Verification);
    Equal(true, random.UnsupportedMechanics.Contains("FIR_953_RANDOM_AFTER_ATTACK"));
});
Run("secrets and unknown auras explicitly downgrade exact outcomes", () =>
{
    var secret = Entity(40, null, 2, "SECRET", "SPELL", []);
    var snapshot = Replace(Fixture(Minion(21, "VANILLA", 1, 4, 6), secret), Hero(11, 2, 4));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Conditional, result.Verification); Equal(false, result.IsLethal); Equal(true, result.PotentialLethal);
    Equal(LineVerification.Conditional, Eval(Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "AURA", 2, 1, 3)), new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
});
Run("unimplemented battlecry does not pretend to consume and resolve the card", () =>
{
    var result = Eval(Fixture(Hand(30, "EDR_852", 2)), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Unsupported, result.Verification); Equal(0, result.AppliedActions);
    Equal(10, result.Result.AvailableMana); Equal("HAND", Find(result, 30).Zone);
});
Run("simple charge minion play is simulated with summon and mana rules", () =>
{
    var result = Eval(Fixture(Hand(30, "CHARGE", 2)), new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 11));
    Equal(LineVerification.Verified, result.Verification); Equal(18, Find(result, 11).RemainingHealth); Equal(8, result.Result.AvailableMana);
});
Run("hero weapon combat spends durability and immunity while attacking prevents retaliation", () =>
{
    var weapon = Entity(31, "WEAPON", 1, "PLAY", "WEAPON", [("ATK", 3), ("DURABILITY", 2)]);
    var hero = Hero(10, 1, 30, 5, 0, 3, ("IMMUNE_WHILE_ATTACKING", 1));
    var result = Eval(Replace(Fixture(weapon, Minion(20, "VANILLA", 2, 4, 5)), hero), new PlannedAction(ActionKind.Attack, 10, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(25, Find(result, 10).RemainingHealth);
    Equal(2, Find(result, 20).RemainingHealth); Equal(1, Find(result, 31).Flags["DURABILITY"]);
});
Run("bounded lethal search returns only verified paths and never exhaustive claims", () =>
{
    var snapshot = Replace(Fixture(Minion(21, "VANILLA", 1, 4, 6)), Hero(11, 2, 6));
    var result = evaluator.FindLethal(snapshot, cards, maximumDepth: 2, maximumNodes: 200);
    Equal(true, result.VerifiedLethalFound); Equal(false, result.IsExhaustive);
    Equal(LineVerification.Verified, result.Evaluation!.Verification);
    var secret = Entity(40, null, 2, "SECRET", "SPELL", []);
    Equal(false, evaluator.FindLethal(Replace(snapshot, secret), cards, 2, 200).VerifiedLethalFound);
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { evaluator.FindLethal(snapshot, cards, cancellationToken: canceled.Token); throw new Exception("Cancellation ignored."); }
    catch (OperationCanceledException) { }
});
Run("canonical attack restriction tags and ignore-taunt are respected", () =>
{
    var snapshot = Fixture(Minion(21, "VANILLA", 1, 4, 6, ("CANNOT_ATTACK_HEROES", 1)));
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    var bypass = Fixture(Minion(21, "VANILLA", 1, 4, 6, ("IGNORE_TAUNT", 1)), Minion(20, "TAUNT", 2, 1, 5));
    Equal(LineVerification.Verified, Eval(bypass, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    var inaccessible = Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "VANILLA", 2, 1, 5, ("CANT_BE_ATTACKED", 1)));
    Equal(LineVerification.Invalid, Eval(inaccessible, new PlannedAction(ActionKind.Attack, 21, 20)).Verification);
});
Run("rush without entry-turn evidence cannot verify a face attack", () =>
{
    var rush = Entity(21, "VANILLA", 1, "PLAY", "MINION", [("ATK", 4), ("HEALTH", 6), ("EXHAUSTED", 0), ("RUSH", 1)]);
    Equal(LineVerification.Unsupported, Eval(Fixture(rush), new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
});
Run("damage modifiers and unknown numeric tags never verify lethal", () =>
{
    var protectedHero = Hero(11, 2, 4, 0, 0, 0, ("HEALTH_MINIMUM", 1));
    var snapshot = Replace(Fixture(Minion(21, "VANILLA", 1, 4, 6)), protectedHero);
    Equal(LineVerification.Conditional, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    Equal(false, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11)).IsLethal);
    var unknown = Replace(Fixture(Minion(21, "VANILLA", 1, 4, 6, ("999999", 1))), Hero(11, 2, 4));
    Equal(LineVerification.Conditional, Eval(unknown, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
});
Run("explicit spellpower and post-silence granted flags use current state", () =>
{
    var afterSilence = Fixture(Hand(30, "CORE_DS1_185", 1), Minion(21, "SPELLPOWER", 1, 1, 3, ("SILENCED", 1), ("SPELLPOWER", 2)));
    Equal(16, Find(Eval(afterSilence, new PlannedAction(ActionKind.PlayCard, 30, 11)), 11).RemainingHealth);
    var death = Eval(Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(20, "VANILLA", 2, 1, 3, ("SILENCED", 1), ("DEATHRATTLE", 1))), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Conditional, death.Verification);
});
Run("unresolved intermediate deathrattle cannot prove a later action illegal", () =>
{
    var result = Eval(Fixture(Minion(21, "VANILLA", 1, 4, 6, ("WINDFURY", 1)), Minion(20, "DEATH", 2, 1, 3)),
        new PlannedAction(ActionKind.Attack, 21, 20), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Unsupported, result.Verification); Equal<bool?>(null, result.IsLegal);
    Equal(true, result.UnsupportedMechanics.Contains("CONDITIONAL_STATE_VALIDITY"));
});
Run("friendly secrets and unaudited builds retain explicit uncertainty", () =>
{
    var secret = Entity(40, "UNKNOWN_SECRET", 1, "SECRET", "SPELL", []);
    var snapshot = Replace(Fixture(Minion(21, "VANILLA", 1, 4, 6), secret), Hero(11, 2, 4));
    Equal(LineVerification.Conditional, Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
    var unaudited = Fixture(Minion(21, "VANILLA", 1, 4, 6), Minion(22, "EDR_480", 1, 9, 9)) with { ClientBuild = "999999" };
    Equal(LineVerification.Conditional, Eval(unaudited, new PlannedAction(ActionKind.Attack, 21, 11)).Verification);
});
Run("minions immune while attacking also avoid retaliation", () =>
{
    var result = Eval(Fixture(Minion(21, "VANILLA", 1, 4, 2, ("IMMUNE_WHILE_ATTACKING", 1)), Minion(20, "POISON", 2, 5, 8)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(2, Find(result, 21).RemainingHealth);
});
Run("attached enchantments outside play are still relevant to verification", () =>
{
    var enchantment = Entity(42, "UNKNOWN_ENCHANTMENT", 1, "SETASIDE", "ENCHANTMENT", [("ATTACHED", 21)]);
    var result = Eval(Fixture(Minion(21, "VANILLA", 1, 4, 6), enchantment), new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Conditional, result.Verification);
    Equal(true, result.UnsupportedMechanics.Contains("UNMAPPED_ATTACHED_ENCHANTMENT"));
});

if (args.Length == 1)
{
    Run("optional real build-255790 goldrinn and beast card data", () =>
    {
        using var file = JsonDocument.Parse(File.ReadAllText(args[0]));
        var loaded = 0;
        foreach (var raw in file.RootElement.EnumerateArray().Where(c => c.GetProperty("id").GetString() is "EDR_480" or "EDR_227"))
        {
            loaded++;
            var id = raw.GetProperty("id").GetString()!;
            cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!, raw.GetProperty("cost").GetInt32(), true, [], [], raw.GetRawText());
        }
        Equal(2, loaded);
        var result = Eval(Replace(Fixture(Minion(21, "EDR_227", 1, 5, 2), Minion(22, "EDR_480", 1, 9, 9)), Hero(11, 2, 10)), new PlannedAction(ActionKind.Attack, 21, 11));
        Equal(LineVerification.Verified, result.Verification); Equal(true, result.IsLethal);
    });
}
Console.WriteLine($"Rules regression: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
LineEvaluation Eval(GameSnapshot snapshot, params PlannedAction[] actions) => evaluator.EvaluateLine(snapshot, actions, cards);
static SimulatedEntity Find(LineEvaluation evaluation, int id) => evaluation.Result.Entities.Single(e => e.EntityId == id);
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
static CardRecord Card(string id, string type = "MINION", int cost = 2, int attack = 4, int health = 6,
    string text = "", string[]? mechanics = null, string? race = null, int spellDamage = 0)
{
    var raw = JsonSerializer.Serialize(new { id, type, cost, attack, health, text, mechanics = mechanics ?? [], race, spellDamage });
    return new(id, 1, id, text, "HUNTER", "TEST", type, cost, true, [], [], raw);
}
static EntitySnapshot Entity(int id, string? card, int controller, string zone, string type, (string Key, int Value)[] values)
{
    var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["CONTROLLER"] = controller.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = type };
    foreach (var value in values) tags[value.Key] = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return new(id, card, EntityKind.Card, tags.ToFrozenDictionary(StringComparer.Ordinal));
}
static EntitySnapshot Minion(int id, string card, int owner, int attack, int health, params (string Key, int Value)[] extra)
    => Entity(id, card, owner, "PLAY", "MINION", [("ATK", attack), ("HEALTH", health), ("DAMAGE", 0), ("NUM_TURNS_IN_PLAY", 1), ("EXHAUSTED", 0), .. extra]);
static EntitySnapshot Hand(int id, string card, int cost) => Entity(id, card, 1, "HAND", "", [("COST", cost)]);
static EntitySnapshot Hero(int id, int owner, int maxHealth, int damage = 0, int armor = 0, int attack = 0, params (string Key, int Value)[] extra)
    => Entity(id, "HERO_" + owner, owner, "PLAY", "HERO", [("HEALTH", maxHealth), ("DAMAGE", damage), ("ARMOR", armor), ("ATK", attack), ("EXHAUSTED", 0), .. extra]);
static EntitySnapshot Player(int id, int player, int resources, int hero) => new(id, null, EntityKind.Player,
    new Dictionary<string, string> { ["CARDTYPE"] = "2", ["PLAYER_ID"] = player.ToString(), ["RESOURCES"] = resources.ToString(),
        ["RESOURCES_USED"] = "0", ["MAXRESOURCES"] = "10", ["HERO_ENTITY"] = hero.ToString() }.ToFrozenDictionary());
static GameSnapshot Fixture(params EntitySnapshot[] additional)
{
    EntitySnapshot[] entities = [Player(2, 1, 10, 10), Player(3, 2, 10, 11), Hero(10, 1, 30, 5), Hero(11, 2, 20),
        Entity(12, "HERO_05bp", 1, "PLAY", "HERO_POWER", [("COST", 2), ("EXHAUSTED", 0)]), .. additional];
    return new("rules-fixture", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(e => e.Id), new(true, true, true, true, 0, []))
    { ClientBuild = "255790", Format = "Standard" };
}
static GameSnapshot Replace(GameSnapshot snapshot, params EntitySnapshot[] replacements)
{
    var entities = snapshot.Entities.ToDictionary();
    foreach (var entity in replacements) entities[entity.Id] = entity;
    return snapshot with { Entities = entities.ToFrozenDictionary() };
}
