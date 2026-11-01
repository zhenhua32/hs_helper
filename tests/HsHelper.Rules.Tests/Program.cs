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
    ["HERO_05bp"] = Card("HERO_05bp", "HERO_POWER", cost: 2, text: "对敌方英雄造成2点伤害。"),
    ["WEAPON"] = Card("WEAPON", "WEAPON", attack: 3, text: ""),
    ["CHARGE"] = Card("CHARGE", attack: 2, health: 2, text: "冲锋", mechanics: ["CHARGE"])
};
foreach (var fixtureName in new[] { "beast-hunter-255790.json", "lethal-search-255790.json", "native-definitions-255790.json" })
{
    using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName)));
    Equal("255790", fixture.RootElement.GetProperty("build").GetString());
    foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
    {
        var id = raw.GetProperty("id").GetString()!;
        cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.TryGetProperty("text", out var printedText) ? printedText.GetString()! : "",
            raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
            raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
    }
}

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
    var enormous = Eval(Fixture(Minion(21, "LIFE", 1, int.MaxValue, 6), Minion(20, "VANILLA", 2, 1, 2)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(30, Find(enormous, 10).RemainingHealth);
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
    var random = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 8), Minion(20, "VANILLA", 2, 1, 8)), new PlannedAction(ActionKind.Attack, 21, 20));
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
    var result = Eval(Fixture(Hand(30, "BE_044", 3)), new PlannedAction(ActionKind.PlayCard, 30));
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
    var weapon = Entity(31, "WEAPON", 1, "PLAY", "WEAPON", [("ATK", 3), ("HEALTH", 2), ("DAMAGE", 0)]);
    var hero = Hero(10, 1, 30, 5, 0, 3, ("IMMUNE_WHILE_ATTACKING", 1));
    var result = Eval(Replace(Fixture(weapon, Minion(20, "VANILLA", 2, 4, 5)), hero), new PlannedAction(ActionKind.Attack, 10, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(25, Find(result, 10).RemainingHealth);
    Equal(2, Find(result, 20).RemainingHealth); Equal(1, Find(result, 31).Flags["DAMAGE"]);
    Equal(1, Find(result, 31).WeaponDurability!.Remaining);
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
    var declined = Eval(unaudited, new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Unsupported, declined.Verification); Equal(0, declined.AppliedActions);
    Equal(true, declined.UnsupportedMechanics.Contains("UNAUDITED_CARD_BUILD"));
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
Run("real bitterbloom and flutterwing battlecries strengthen an observed wolf power", () =>
{
    var knight = WithWolf(Fixture(Hand(30, "EDR_852", 2)), 2);
    var before = JsonSerializer.Serialize(knight);
    var result = Eval(knight, new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Verified, result.Verification); Equal(8, result.Result.AvailableMana);
    Equal(2, Find(result, 30).Attack); Equal(3, Find(result, 30).RemainingHealth);
    Equal(3, Find(result, 12).Flags["TAG_SCRIPT_DATA_NUM_1"]);
    Equal(1, result.Result.HeroPowerChanges.Count); Equal(2, result.Result.HeroPowerChanges[0].PreviousLevel);
    Equal(3, result.Result.HeroPowerChanges[0].CurrentLevel); Equal(true, result.Result.HeroPowerChanges[0].AvailabilityKnown);
    Equal(before, JsonSerializer.Serialize(knight));
    using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
    Equal(3, serialized.RootElement.GetProperty("Result").GetProperty("HeroPowerChanges")[0].GetProperty("CurrentLevel").GetInt32());
    var shield = Eval(WithWolf(Fixture(Hand(30, "EDR_800", 4), Hand(31, "CORE_CS2_189", 1)), 3),
        new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31, 30));
    Equal(LineVerification.Verified, shield.Verification); Equal(4, Find(shield, 30).RemainingHealth);
    Equal(0, Find(shield, 30).Flags["DIVINE_SHIELD"]); Equal(4, Find(shield, 12).Flags["TAG_SCRIPT_DATA_NUM_2"]);
});
Run("replacement from steady shot does not invent first imbue level or refresh availability", () =>
{
    var snapshot = Fixture(Hand(30, "EDR_852", 2));
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Conditional, result.Verification); Equal("EDR_850p", Find(result, 12).CardId);
    Equal<int?>(null, result.Result.HeroPowerChanges[0].CurrentLevel); Equal(false, result.Result.HeroPowerChanges[0].AvailabilityKnown);
    Equal(true, result.UnsupportedMechanics.Contains("UNKNOWN_IMBUE_LEVEL"));
    Equal(LineVerification.Unsupported, Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.HeroPower, 12)).Verification);
    var missing = cards.Where(pair => pair.Key != "EDR_850p").ToDictionary();
    var withoutDependency = evaluator.EvaluateLine(snapshot, [new(ActionKind.PlayCard, 30)], missing);
    Equal(LineVerification.Conditional, withoutDependency.Verification);
    Equal(true, withoutDependency.UnsupportedMechanics.Contains("MISSING_IMBUE_CARD_DATA"));
    Equal(LineVerification.Unsupported, Eval(snapshot with { ClientBuild = "999999" }, new PlannedAction(ActionKind.PlayCard, 30)).Verification);
});
Run("real umbraclaw is playable with rush and resolves only its audited native deathrattle", () =>
{
    var snapshot = WithWolf(Fixture(Hand(30, "EDR_227", 4), Minion(20, "VANILLA", 2, 3, 5)), 2);
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(false, Find(result, 30).IsAlive); Equal(false, Find(result, 20).IsAlive);
    Equal(3, Find(result, 12).Flags["TAG_SCRIPT_DATA_NUM_1"]);
    Equal("deathrattle:EDR_227", result.Result.HeroPowerChanges.Single().Reason);
    var silenced = WithWolf(Fixture(Minion(21, "EDR_227", 1, 5, 2, ("SILENCED", 1)), Minion(20, "VANILLA", 2, 3, 5)), 2);
    var noTrigger = Eval(silenced, new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, noTrigger.Verification); Equal(0, noTrigger.Result.HeroPowerChanges.Count);
    Equal(2, Find(noTrigger, 12).Flags["TAG_SCRIPT_DATA_NUM_1"]);
    Equal(LineVerification.Invalid, Eval(WithWolf(Fixture(Hand(30, "EDR_227", 4)), 2), new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 11)).Verification);
});
Run("real wolf blessing deterministically buffs exactly one observed beast and clamps cost at zero", () =>
{
    var snapshot = WithWolf(Fixture(ObservedHand(30, "EDR_227", 1, 5, 2)), 3);
    var result = Eval(snapshot, new PlannedAction(ActionKind.HeroPower, 12), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Verified, result.Verification); Equal(8, result.Result.AvailableMana);
    Equal(8, Find(result, 30).Attack); Equal(0, Find(result, 30).Cost); Equal(2, Find(result, 30).RemainingHealth);
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.HeroPower, 12, 30)).Verification);
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.HeroPower, 12), new PlannedAction(ActionKind.HeroPower, 12)).Verification);
});
Run("wolf randomness unknown stats and unknown parameters never produce exact outcomes", () =>
{
    var snapshot = WithWolf(Fixture(ObservedHand(30, "EDR_227", 3, 5, 2), ObservedHand(31, "EDR_480", 9, 9, 9)), 2);
    var random = Eval(snapshot, new PlannedAction(ActionKind.HeroPower, 12));
    Equal(LineVerification.Conditional, random.Verification); Equal(8, random.Result.AvailableMana);
    Equal(5, Find(random, 30).Attack); Equal(9, Find(random, 31).Attack);
    Equal(true, random.UnsupportedMechanics.Contains("RANDOM_WOLF_TARGET"));
    Equal(LineVerification.Unsupported, Eval(WithWolf(Fixture(Hand(30, "EDR_227", 4)), 2), new PlannedAction(ActionKind.HeroPower, 12)).Verification);
    Equal(LineVerification.Unsupported, Eval(Replace(snapshot, Wolf(2, 3)), new PlannedAction(ActionKind.HeroPower, 12)).Verification);
    var unknownHand = Entity(33, null, 1, "HAND", "", []);
    Equal(LineVerification.Unsupported, Eval(Replace(snapshot, unknownHand), new PlannedAction(ActionKind.HeroPower, 12)).Verification);
    var numeric = Replace(WithWolf(Fixture(ObservedHand(30, "EDR_227", 4, 5, 2)), 2),
        Entity(12, "EDR_850p", 1, "PLAY", "HERO_POWER", [("COST", 2), ("EXHAUSTED", 0), ("2", 2), ("3", 2)]));
    Equal(LineVerification.Verified, Eval(numeric, new PlannedAction(ActionKind.HeroPower, 12)).Verification);
});
Run("houndmaster previews its beast draw before the still pending imbue", () =>
{
    var deck = Entity(40, null, 1, "DECK", "", []);
    var result = Eval(WithWolf(Fixture(Entity(30, "EDR_226", 1, "HAND", "MINION", [("COST", 2)]), deck), 2), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Conditional, result.Verification); Equal(1, result.AppliedActions);
    Equal(2, Find(result, 12).Flags["TAG_SCRIPT_DATA_NUM_1"]); Equal("DECK", Find(result, 40).Zone);
    Equal<string?>(null, Find(result, 40).CardId); Equal(2, Find(result, 30).Attack);
    Equal(true, result.UnsupportedMechanics.Contains("PENDING_AFTER_DRAW_IMBUE"));
    Equal(true, result.Result.DrawBoundaryReached); Equal(1L, result.Result.DrawPreviews.Single().RequestedDraws.Maximum);
    Equal(0, result.Result.HeroPowerChanges.Count); Equal<int?>(null, result.Result.ImbueCountAfter);
});
Run("real flower attendant does not confuse current power strength with historical imbue count", () =>
{
    var below = Eval(WithWolf(Fixture(Entity(30, "FIR_921", 1, "HAND", "MINION", [("COST", 3)])), 1), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Conditional, below.Verification); Equal(2, Find(below, 30).Attack); Equal(3, Find(below, 30).RemainingHealth);
    var drawing = Eval(WithWolf(Fixture(Entity(30, "FIR_921", 1, "HAND", "MINION", [("COST", 3)])), 2), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Conditional, drawing.Verification); Equal(true, drawing.Result.DrawBoundaryReached);
    Equal(0L, drawing.Result.DrawPreviews.Single().RequestedDraws.Minimum); Equal(2L, drawing.Result.DrawPreviews.Single().RequestedDraws.Maximum);
    Equal(LineVerification.Conditional, Eval(Fixture(Entity(30, "FIR_921", 1, "HAND", "MINION", [("COST", 3)])), new PlannedAction(ActionKind.PlayCard, 30)).Verification);
});
Run("real DINO_421 buffs known hand minions but keeps hidden deck targets unknown", () =>
{
    var snapshot = Fixture(Minion(21, "DINO_421", 1, 8, 8, ("DAMAGE", 7)), Minion(20, "VANILLA", 2, 1, 10),
        ObservedHand(30, "EDR_227", 4, 5, 2), Hand(31, "GAME_005", 0));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(8, Find(result, 30).Attack); Equal(5, Find(result, 30).MaximumHealth);
    Equal(4, Find(result, 30).Cost); Equal(0, Find(result, 31).Cost);
    var hidden = Eval(Replace(snapshot, Entity(40, null, 1, "DECK", "", [])), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Conditional, hidden.Verification); Equal<int?>(null, Find(hidden, 40).Attack); Equal(8, Find(hidden, 30).Attack);
    var doubled = Replace(WithWolf(Fixture(Minion(21, "EDR_227", 1, 5, 2), Minion(20, "VANILLA", 2, 3, 5)), 2),
        Entity(50, null, 1, "PLAY", "ENCHANTMENT", [("EXTRA_MINION_DEATHRATTLES_BASE", 1)]));
    Equal(LineVerification.Conditional, Eval(doubled, new PlannedAction(ActionKind.Attack, 21, 20)).Verification);
});
Run("real goldrinn can be summoned then rush with its own damage aura", () =>
{
    var result = Eval(Fixture(Hand(30, "EDR_480", 9), Minion(20, "VANILLA", 2, 1, 18)),
        new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(false, Find(result, 20).IsAlive); Equal(8, Find(result, 30).RemainingHealth);
});
Run("real magma hound can rush and split damage deterministically after the last opposing minion dies", () =>
{
    var snapshot = Fixture(ObservedHand(30, "FIR_953", 8, 5, 8), Minion(20, "VANILLA", 2, 1, 5));
    var before = JsonSerializer.Serialize(snapshot);
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(2, result.Result.AvailableMana);
    Equal(false, Find(result, 20).IsAlive); Equal(7, Find(result, 30).RemainingHealth);
    Equal(15, Find(result, 11).RemainingHealth);
    Equal(true, result.RuleCoverage.Contains("FIR_953-single-enemy-split-damage"));
    Equal(before, JsonSerializer.Serialize(snapshot));
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30, 11)).Verification);
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 11)).Verification);
});
Run("magma hound preserves random targets and does not trigger after dying silence or attacking a hero", () =>
{
    var random = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 8), Minion(20, "VANILLA", 2, 1, 8)),
        new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Conditional, random.Verification); Equal(20, Find(random, 11).RemainingHealth);
    Equal(3, Find(random, 20).RemainingHealth); Equal(true, random.UnsupportedMechanics.Contains("FIR_953_RANDOM_AFTER_ATTACK"));
    var died = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 1), Minion(20, "VANILLA", 2, 1, 5)),
        new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, died.Verification); Equal(20, Find(died, 11).RemainingHealth);
    var silent = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 8, ("SILENCED", 1)), Minion(20, "VANILLA", 2, 1, 5)),
        new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, silent.Verification); Equal(20, Find(silent, 11).RemainingHealth);
    var face = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 8)), new PlannedAction(ActionKind.Attack, 21, 11));
    Equal(LineVerification.Verified, face.Verification); Equal(15, Find(face, 11).RemainingHealth);
    var unknownBuild = Eval(Fixture(Minion(21, "FIR_953", 1, 5, 8), Minion(20, "VANILLA", 2, 1, 5)) with { ClientBuild = "999999" },
        new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Unsupported, unknownBuild.Verification); Equal(0, unknownBuild.AppliedActions);
    Equal(true, unknownBuild.UnsupportedMechanics.Contains("UNAUDITED_CARD_BUILD"));
});
Run("magma hound damage packets respect divine shield immunity and goldrinn without spell damage", () =>
{
    var snapshot = Replace(Fixture(Minion(21, "FIR_953", 1, 5, 8), Minion(20, "VANILLA", 2, 1, 10),
        Minion(22, "EDR_480", 1, 9, 9), Minion(23, "SPELLPOWER", 1, 1, 3, ("SPELLPOWER", 10))),
        Hero(11, 2, 20, 0, 3, 0, ("DIVINE_SHIELD", 1)));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(false, Find(result, 20).IsAlive);
    Equal(15, Find(result, 11).RemainingHealth); Equal(0, Find(result, 11).Armor);
    Equal(0, Find(result, 11).Flags["DIVINE_SHIELD"]);
    var immune = Eval(Replace(snapshot, Hero(11, 2, 20, 0, 3, 0, ("IMMUNE", 1), ("DIVINE_SHIELD", 1))),
        new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, immune.Verification); Equal(20, Find(immune, 11).RemainingHealth);
    Equal(3, Find(immune, 11).Armor); Equal(1, Find(immune, 11).Flags["DIVINE_SHIELD"]);
});
Run("magma hound stops damage and lifesteal packets when the opposing hero dies", () =>
{
    var snapshot = Replace(Fixture(Minion(21, "FIR_953", 1, 2, 8, ("LIFESTEAL", 1)), Minion(20, "VANILLA", 2, 1, 3),
        Minion(22, "EDR_480", 1, 9, 9)), Hero(10, 1, 30, 20), Hero(11, 2, 1));
    var result = Eval(snapshot, new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(true, result.IsLethal);
    Equal(-1, Find(result, 11).RemainingHealth); Equal(16, Find(result, 10).RemainingHealth);
    var huge = Replace(Fixture(Minion(21, "FIR_953", 1, int.MaxValue, 8), Minion(20, "VANILLA", 2, 0, 1)), Hero(11, 2, 20));
    var bounded = Eval(huge, new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Verified, bounded.Verification); Equal(0, Find(bounded, 11).RemainingHealth);
    var overflow = Eval(Replace(huge, Minion(22, "EDR_480", 1, 9, 9)), new PlannedAction(ActionKind.Attack, 21, 20));
    Equal(LineVerification.Unsupported, overflow.Verification); Equal(false, overflow.IsLethal);
    Equal(true, overflow.UnsupportedMechanics.Contains("EVALUATION_LIMIT"));
});
Run("bounded lethal search includes audited rush summons and keeps unresolved magma outcomes unverified", () =>
{
    var snapshot = Replace(Fixture(ObservedHand(30, "FIR_953", 8, 5, 8), Minion(20, "TAUNT", 2, 1, 5)), Hero(11, 2, 5));
    var result = evaluator.FindLethal(snapshot, cards, maximumDepth: 2, maximumNodes: 200);
    Equal(true, result.VerifiedLethalFound); Equal(false, result.IsExhaustive);
    Equal(ActionKind.PlayCard, result.Actions[0].Kind); Equal(30, result.Actions[0].SourceEntityId);
    Equal(ActionKind.Attack, result.Actions[1].Kind); Equal(20, result.Actions[1].TargetEntityId);
    var uncertain = Replace(snapshot, Minion(22, "VANILLA", 2, 1, 1));
    Equal(false, evaluator.FindLethal(uncertain, cards, maximumDepth: 2, maximumNodes: 200).VerifiedLethalFound);
    var secret = Replace(snapshot, Entity(40, null, 2, "SECRET", "SPELL", []));
    Equal(false, evaluator.FindLethal(secret, cards, maximumDepth: 2, maximumNodes: 200).VerifiedLethalFound);
    var reborn = Replace(snapshot, Minion(20, "TAUNT", 2, 1, 5, ("REBORN", 1)));
    Equal(false, evaluator.FindLethal(reborn, cards, maximumDepth: 2, maximumNodes: 200).VerifiedLethalFound);
});
Run("silenced or explicitly suppressed battlecries do not apply native imbue", () =>
{
    var silent = Entity(30, "EDR_852", 1, "HAND", "MINION", [("COST", 2), ("SILENCED", 1)]);
    var result = Eval(WithWolf(Fixture(silent), 2), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Verified, result.Verification); Equal(2, Find(result, 12).Flags["TAG_SCRIPT_DATA_NUM_1"]);
    Equal(0, result.Result.HeroPowerChanges.Count);
    var suppressed = Entity(30, "EDR_800", 1, "HAND", "MINION", [("COST", 4), ("BATTLECRY", 0)]);
    var noTrigger = Eval(WithWolf(Fixture(suppressed), 2), new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Verified, noTrigger.Verification); Equal(0, noTrigger.Result.HeroPowerChanges.Count);
});
Run("real agamaggan arms exactly the next card using current cost and freezes payment evidence", () =>
{
    var snapshot = Fixture(Hand(30, "EDR_489", 2), ObservedHand(31, "DINO_421", 5, 8, 8));
    var before = JsonSerializer.Serialize(snapshot);
    var armed = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30));
    Equal(LineVerification.Verified, armed.Verification); Equal<bool?>(true, armed.Result.NextCardCostsOpponentHealth);
    Equal(8, armed.Result.AvailableMana); Equal(20, Find(armed, 11).RemainingHealth);
    var paid = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Verified, paid.Verification); Equal<bool?>(false, paid.Result.NextCardCostsOpponentHealth);
    Equal(8, paid.Result.AvailableMana); Equal(15, Find(paid, 11).RemainingHealth);
    var receipt = paid.Result.OpponentHealthPayments.Single();
    Equal(31, receipt.CardEntityId); Equal("DINO_421", receipt.CardId); Equal(11, receipt.PayingHeroEntityId);
    Equal(5, receipt.Cost); Equal(20, receipt.HealthBefore); Equal(15, receipt.HealthAfter);
    Equal(before, JsonSerializer.Serialize(snapshot));
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(paid));
    Equal(5, json.RootElement.GetProperty("Result").GetProperty("OpponentHealthPayments")[0].GetProperty("Cost").GetInt32());
    try { ((IList<OpponentHealthPayment>)paid.Result.OpponentHealthPayments).Clear(); throw new Exception("Payment evidence was mutable."); }
    catch (NotSupportedException) { }
});
Run("agamaggan health payment ignores armor immunity shield damage doubling and lifesteal", () =>
{
    var beast = Entity(31, "DINO_421", 1, "HAND", "MINION", [("COST", 8), ("ATK", 8), ("HEALTH", 8), ("LIFESTEAL", 1)]);
    var snapshot = Replace(Fixture(Hand(30, "EDR_489", 1), beast, Minion(22, "EDR_480", 1, 9, 9)),
        Hero(11, 2, 20, 0, 50, 0, ("IMMUNE", 1), ("DIVINE_SHIELD", 1)));
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Verified, result.Verification); Equal(12, Find(result, 11).RemainingHealth);
    Equal(50, Find(result, 11).Armor); Equal(1, Find(result, 11).Flags["IMMUNE"]);
    Equal(1, Find(result, 11).Flags["DIVINE_SHIELD"]); Equal(25, Find(result, 10).RemainingHealth);
    Equal(9, result.Result.AvailableMana);
});
Run("hero powers retain agamaggan payment and a zero-cost card consumes it without health loss", () =>
{
    var snapshot = Fixture(Hand(30, "EDR_489", 2), Hand(31, "GAME_005", 0), Hand(32, "DINO_421", 8));
    var power = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.HeroPower, 12));
    Equal(LineVerification.Verified, power.Verification); Equal<bool?>(true, power.Result.NextCardCostsOpponentHealth);
    Equal(6, power.Result.AvailableMana); Equal(18, Find(power, 11).RemainingHealth); Equal(0, power.Result.OpponentHealthPayments.Count);
    var coin = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.HeroPower, 12), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Verified, coin.Verification); Equal<bool?>(false, coin.Result.NextCardCostsOpponentHealth);
    Equal(7, coin.Result.AvailableMana); Equal(18, Find(coin, 11).RemainingHealth);
    Equal(0, coin.Result.OpponentHealthPayments.Single().Cost);
    var spent = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.HeroPower, 12),
        new PlannedAction(ActionKind.PlayCard, 31), new PlannedAction(ActionKind.PlayCard, 32));
    Equal(LineVerification.Invalid, spent.Verification); Equal(1, spent.Result.OpponentHealthPayments.Count);
});
Run("agamaggan payment is consumed once and a second agamaggan can arm another payment", () =>
{
    var snapshot = Fixture(Hand(30, "EDR_489", 1), Hand(31, "EDR_489", 10), Hand(32, "DINO_421", 8), Hand(33, "EDR_480", 9));
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31),
        new PlannedAction(ActionKind.PlayCard, 32), new PlannedAction(ActionKind.PlayCard, 33));
    Equal(LineVerification.Verified, result.Verification); Equal(2, Find(result, 11).RemainingHealth);
    Equal(0, result.Result.AvailableMana); Equal(2, result.Result.OpponentHealthPayments.Count);
    Equal<bool?>(false, result.Result.NextCardCostsOpponentHealth);
    Equal(10, result.Result.OpponentHealthPayments[0].Cost); Equal(8, result.Result.OpponentHealthPayments[1].Cost);
});
Run("agamaggan rejects costs over ten instead of capping health or charging a mana remainder", () =>
{
    var snapshot = Fixture(Hand(30, "EDR_489", 0), Hand(31, "DINO_421", 11));
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Invalid, result.Verification); Equal(20, Find(result, 11).RemainingHealth);
    Equal(10, result.Result.AvailableMana); Equal(0, result.Result.OpponentHealthPayments.Count);
    Equal<bool?>(true, result.Result.NextCardCostsOpponentHealth);
    var unknown = Replace(snapshot, Entity(31, "DINO_421", 1, "HAND", "MINION", []));
    Equal(LineVerification.Unsupported, Eval(unknown, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31)).Verification);
});
Run("agamaggan cannot spend opponent health for an illegal target or a full minion board", () =>
{
    var target = Fixture(Hand(30, "EDR_489", 1), Hand(31, "CORE_DS1_185", 1));
    var invalid = Eval(target, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31, 999));
    Equal(LineVerification.Invalid, invalid.Verification); Equal(20, Find(invalid, 11).RemainingHealth);
    Equal(0, invalid.Result.OpponentHealthPayments.Count); Equal<bool?>(true, invalid.Result.NextCardCostsOpponentHealth);
    var full = Fixture([Hand(30, "EDR_489", 1), Hand(31, "DINO_421", 8),
        .. Enumerable.Range(40, 6).Select(id => Minion(id, "VANILLA", 1, 1, 1))]);
    var blocked = Eval(full, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Invalid, blocked.Verification); Equal(20, Find(blocked, 11).RemainingHealth);
    Equal(0, blocked.Result.OpponentHealthPayments.Count); Equal("HAND", Find(blocked, 31).Zone);
});
Run("lethal opponent health payment stops before the card's battlecry or spell effect", () =>
{
    var snapshot = Replace(Fixture(Hand(30, "EDR_489", 1), Hand(31, "EDR_226", 2), Hand(32, "CORE_DS1_185", 1)), Hero(11, 2, 1));
    var minion = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Verified, minion.Verification); Equal(true, minion.IsLethal);
    Equal(-1, Find(minion, 11).RemainingHealth); Equal(false, Find(minion, 11).IsAlive); Equal(0, minion.Result.HeroPowerChanges.Count);
    Equal(true, minion.Result.OpponentDefeatedByPayment); Equal("HAND", Find(minion, 31).Zone);
    Equal("HERO_05bp", Find(minion, 12).CardId); Equal(0, minion.UnsupportedMechanics.Count);
    var spell = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 32, 10));
    Equal(LineVerification.Verified, spell.Verification); Equal(true, spell.IsLethal);
    Equal(25, Find(spell, 10).RemainingHealth); Equal(0, Find(spell, 11).RemainingHealth);
    Equal("HAND", Find(spell, 32).Zone);
    var coin = Eval(Replace(snapshot, Hand(33, "GAME_005", 1)), new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 33));
    Equal(LineVerification.Verified, coin.Verification); Equal(true, coin.IsLethal);
    Equal(9, coin.Result.AvailableMana); Equal(0, Find(coin, 2).Flags.GetValueOrDefault("TEMP_RESOURCES"));
    Equal("HAND", Find(coin, 33).Zone);
    var trap = Eval(Replace(snapshot, Hand(34, "CORE_EX1_610", 2)), new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 34));
    Equal(LineVerification.Verified, trap.Verification); Equal(true, trap.IsLethal);
    Equal("HAND", Find(trap, 34).Zone); Equal(false, trap.RuleCoverage.Contains("explosive-trap-install-only"));
    Equal(LineVerification.Invalid, Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31),
        new PlannedAction(ActionKind.HeroPower, 12)).Verification);
});
Run("observed active agamaggan enchantments bind to the local player or hero and are consumed", () =>
{
    foreach (var (card, attached) in new[] { ("EDR_489e1", 2), ("EDR_489e2", 10) })
    {
        var effect = Entity(50, card, 1, "PLAY", "ENCHANTMENT", [("ATTACHED", attached)]);
        var snapshot = Replace(Fixture(Hand(31, "DINO_421", 8), effect), Player(2, 1, 0, 10));
        var before = JsonSerializer.Serialize(snapshot);
        var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 31));
        Equal(LineVerification.Verified, result.Verification); Equal(12, Find(result, 11).RemainingHealth);
        Equal(0, result.Result.AvailableMana); Equal(false, Find(result, 50).IsAlive); Equal("GRAVEYARD", Find(result, 50).Zone);
        Equal<bool?>(false, result.Result.NextCardCostsOpponentHealth); Equal(before, JsonSerializer.Serialize(snapshot));
    }
});
Run("stale moved or dead agamaggan enchantments never enable free cards", () =>
{
    foreach (var zone in new[] { "GRAVEYARD", "REMOVEDFROMGAME" })
    {
        var stale = Entity(50, "EDR_489e1", 1, zone, "ENCHANTMENT", [("ATTACHED", 2)]);
        var result = Eval(Replace(Fixture(Hand(31, "DINO_421", 8), stale), Player(2, 1, 0, 10)), new PlannedAction(ActionKind.PlayCard, 31));
        Equal(LineVerification.Invalid, result.Verification); Equal(20, Find(result, 11).RemainingHealth);
    }
    var dead = Entity(50, "EDR_489e1", 1, "PLAY", "ENCHANTMENT", [("ATTACHED", 2), ("HEALTH", 0)]);
    Equal(LineVerification.Invalid, Eval(Replace(Fixture(Hand(31, "DINO_421", 8), dead), Player(2, 1, 0, 10)), new PlannedAction(ActionKind.PlayCard, 31)).Verification);
});
Run("agamaggan bodies or incomplete conflicting enchantments do not prove an active payment", () =>
{
    foreach (var zone in new[] { "PLAY", "GRAVEYARD" })
    {
        var body = Entity(50, "EDR_489", 1, zone, "MINION", [("ATK", 9), ("HEALTH", 9), ("SILENCED", 1)]);
        var result = Eval(Fixture(Hand(31, "DINO_421", 8), body), new PlannedAction(ActionKind.PlayCard, 31));
        Equal(LineVerification.Unsupported, result.Verification); Equal<bool?>(null, result.Result.NextCardCostsOpponentHealth);
        Equal(20, Find(result, 11).RemainingHealth); Equal(0, result.Result.OpponentHealthPayments.Count);
    }
    var misplaced = Entity(50, "EDR_489e1", 1, "PLAY", "ENCHANTMENT", [("ATTACHED", 11)]);
    Equal(LineVerification.Unsupported, Eval(Fixture(Hand(31, "DINO_421", 8), misplaced), new PlannedAction(ActionKind.PlayCard, 31)).Verification);
    var valid = Entity(50, "EDR_489e1", 1, "PLAY", "ENCHANTMENT", [("ATTACHED", 2)]);
    var duplicate = Entity(51, "EDR_489e2", 1, "SETASIDE", "ENCHANTMENT", [("ATTACHED", 10)]);
    Equal(LineVerification.Unsupported, Eval(Fixture(Hand(31, "DINO_421", 8), valid, duplicate), new PlannedAction(ActionKind.PlayCard, 31)).Verification);
    var missingData = cards.Where(pair => pair.Key != "EDR_489e1").ToDictionary();
    Equal(LineVerification.Unsupported, evaluator.EvaluateLine(Fixture(Hand(31, "DINO_421", 8), valid), [new(ActionKind.PlayCard, 31)], missingData).Verification);
    Equal(LineVerification.Unsupported, Eval(Fixture(Hand(31, "DINO_421", 8), valid) with { ClientBuild = "999999" }, new PlannedAction(ActionKind.PlayCard, 31)).Verification);
});
Run("suppressed agamaggan battlecries and an opponent's agamaggan never arm local payment", () =>
{
    var source = Entity(30, "EDR_489", 1, "HAND", "MINION", [("COST", 1), ("BATTLECRY", 0)]);
    var result = Eval(Fixture(source, Hand(31, "DINO_421", 8)), new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Verified, result.Verification); Equal(1, result.Result.AvailableMana);
    Equal(20, Find(result, 11).RemainingHealth); Equal(0, result.Result.OpponentHealthPayments.Count);
    var opponent = Eval(Fixture(Minion(50, "EDR_489", 2, 9, 9), Hand(31, "DINO_421", 8)), new PlannedAction(ActionKind.PlayCard, 31));
    Equal(LineVerification.Verified, opponent.Verification); Equal(2, opponent.Result.AvailableMana);
});
Run("bounded lethal search includes agamaggan combos even when the following card exceeds remaining mana", () =>
{
    var snapshot = Replace(Fixture(Hand(30, "EDR_489", 10), Hand(31, "EDR_480", 9)),
        Hero(11, 2, 5, 0, 50, 0, ("IMMUNE", 1), ("DIVINE_SHIELD", 1)));
    var result = evaluator.FindLethal(snapshot, cards, maximumDepth: 2, maximumNodes: 200);
    Equal(true, result.VerifiedLethalFound); Equal(LineVerification.Verified, result.Evaluation!.Verification);
    Equal(30, result.Actions[0].SourceEntityId); Equal(31, result.Actions[1].SourceEntityId);
    Equal(0, result.Evaluation.Result.AvailableMana); Equal(-4, Find(result.Evaluation, 11).RemainingHealth);
    Equal(50, Find(result.Evaluation, 11).Armor); Equal(false, result.IsExhaustive);
});
Run("audited stat-only enchantments use current observations without double applying buffs", () =>
{
    var hand = ObservedHand(30, "EDR_227", 0, 11, 5);
    var attackBuff = Entity(50, "EDR_850pe1", 1, "SETASIDE", "ENCHANTMENT", [("ATTACHED", 30), ("2", 3)]);
    var discount = Entity(51, "EDR_850pe5", 1, "PLAY", "ENCHANTMENT", [("ATTACHED", 30)]);
    var dinosaurBuff = Entity(52, "DINO_421e", 1, "PLAY", "ENCHANTMENT", [("ATTACHED", 30)]);
    var snapshot = WithWolf(Fixture(hand, attackBuff, discount, dinosaurBuff, Minion(20, "VANILLA", 2, 1, 10)), 2);
    var result = Eval(snapshot, new PlannedAction(ActionKind.PlayCard, 30), new PlannedAction(ActionKind.Attack, 30, 20));
    Equal(LineVerification.Verified, result.Verification); Equal(11, Find(result, 30).Attack);
    Equal(5, Find(result, 30).MaximumHealth); Equal(4, Find(result, 30).RemainingHealth); Equal(10, result.Result.AvailableMana);
    Equal(true, result.RuleCoverage.Contains("observed-stat-only-enchantment:EDR_850pe1"));
    var missingStats = WithWolf(Fixture(Hand(30, "EDR_227", 4), attackBuff), 2);
    Equal(LineVerification.Conditional, Eval(missingStats, new PlannedAction(ActionKind.PlayCard, 30)).Verification);
});
Run("choice validation is explicitly selection-only and freezes the submitted option set", () =>
{
    var snapshot = ChoiceFixture();
    var selection = new List<int> { 40 };
    var result = evaluator.ValidateChoice(snapshot, selection, expectedChoiceId: 7);
    selection[0] = 999;
    Equal(LineVerification.Verified, result.Verification); Equal(true, result.IsLegal); Equal(40, result.SelectedEntityIds[0]);
    Equal(true, result.SelectionOnly); Equal(false, result.ResolvesEffects); Equal(7, result.ChoiceId);
    Equal(true, result.Assumptions.Any(a => a.Contains("未模拟选择后效果")));
    using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
    Equal(true, serialized.RootElement.GetProperty("SelectionOnly").GetBoolean());
    Equal(false, serialized.RootElement.GetProperty("ResolvesEffects").GetBoolean());
});
Run("stale unavailable duplicate foreign or out-of-range choices are rejected", () =>
{
    var snapshot = ChoiceFixture();
    Equal(LineVerification.Invalid, evaluator.ValidateChoice(snapshot, [40], 8).Verification);
    Equal(LineVerification.Invalid, evaluator.ValidateChoice(snapshot, [999], 7).Verification);
    Equal(LineVerification.Invalid, evaluator.ValidateChoice(snapshot, [40, 40], 7).Verification);
    Equal(LineVerification.Invalid, evaluator.ValidateChoice(snapshot, [], 7).Verification);
    Equal(LineVerification.Invalid, evaluator.ValidateChoice(snapshot with { Choice = snapshot.Choice! with { PlayerId = 2 } }, [40]).Verification);
    Equal(LineVerification.Unsupported, evaluator.ValidateChoice(snapshot with { Choice = snapshot.Choice! with { IsReady = false } }, [40]).Verification);
    Equal(LineVerification.Unsupported, evaluator.ValidateChoice(snapshot with { Quality = snapshot.Quality with { IsStable = false } }, [40]).Verification);
    Equal(LineVerification.Unsupported, evaluator.ValidateChoice(snapshot with { Choice = snapshot.Choice! with { CountMax = 5 } }, [40]).Verification);
    var foreign = snapshot with { Choice = snapshot.Choice! with { Options = [new(40, "EDR_227", 2, 1)] } };
    Equal(LineVerification.Invalid, evaluator.ValidateChoice(foreign, [40]).Verification);
    var mulligan = snapshot with { Phase = GamePhase.Mulligan, Choice = snapshot.Choice! with { CountMin = 0, CountMax = 2 } };
    Equal(LineVerification.Verified, evaluator.ValidateChoice(mulligan, []).Verification);
});

foreach (var (name, run) in ChoiceResolutionChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in CraftChoiceChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in RewindChoiceChecks.Cases()) Run(name, run);
foreach (var (name, run) in ImbueAndMalorneChecks.Cases()) Run(name, run);
foreach (var (name, run) in DrawResolutionChecks.Cases()) Run(name, run);
foreach (var (name, run) in DeathrattleDrawChecks.Cases()) Run(name, run);
foreach (var (name, run) in HunterDrawLineChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in AssembledRuleChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in GenerationResolutionChecks.Cases()) Run(name, run);
foreach (var (name, run) in HunterGenerationLineChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in WolfBlessingResolutionChecks.Cases()) Run(name, run);
foreach (var (name, run) in HunterWolfLineChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in QuestProgressChecks.Cases()) Run(name, run);
foreach (var (name, run) in DreamboundRaptorChecks.Cases()) Run(name, run);
foreach (var (name, run) in HunterPlayTriggerLineChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in DiscoveryPlayChecks.Cases()) Run(name, run);
foreach (var (name, run) in MapFollowupChecks.Cases()) Run(name, run);
foreach (var (name, run) in SideboardChoiceEvidenceChecks.Cases()) Run(name, run);
foreach (var (name, run) in StartingDeckBindingChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in HunterDiscoveryLineChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in HunterMapPlayChecks.Cases()) Run(name, run);
foreach (var (name, run) in MagmaSplitChecks.Cases()) Run(name, run);
foreach (var (name, run) in HunterMagmaLineChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in ExplosiveTrapChecks.Cases()) Run(name, run);
foreach (var (name, run) in HunterExplosiveTrapInstallChecks.Cases(cards)) Run(name, run);
foreach (var (name, run) in WeaponCombatChecks.Cases()) Run(name, run);
foreach (var (name, run) in TerrorEndTurnResolutionChecks.Cases()) Run(name, run);
foreach (var (name, run) in TerrorEndTurnResolutionChecks.MultiCases()) Run(name, run);
foreach (var (name, run) in DarkGiftStaticChecks.Cases()) Run(name, run);
foreach (var (name, run) in DarkGiftExpandedChecks.Cases()) Run(name, run);
foreach (var (name, run) in LethalCandidateChecks.Cases()) Run(name, run);
foreach (var (name, run) in NativeDefinitionChecks.Cases()) Run(name, run);

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
foreach (var (name, run) in EndTurnRegression.Cases()) Run(name, run);
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
    var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["CONTROLLER"] = controller.ToString(), ["ZONE"] = zone };
    // An omitted observation differs from an explicitly malformed empty CARDTYPE.
    if (!string.IsNullOrEmpty(type)) tags["CARDTYPE"] = type;
    foreach (var value in values) tags[value.Key] = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return new(id, card, EntityKind.Card, tags.ToFrozenDictionary(StringComparer.Ordinal));
}
static EntitySnapshot Minion(int id, string card, int owner, int attack, int health, params (string Key, int Value)[] extra)
    => Entity(id, card, owner, "PLAY", "MINION", [("ATK", attack), ("HEALTH", health), ("DAMAGE", 0), ("NUM_TURNS_IN_PLAY", 1), ("EXHAUSTED", 0), .. extra]);
static EntitySnapshot Hand(int id, string card, int cost) => Entity(id, card, 1, "HAND", "", [("COST", cost)]);
static EntitySnapshot ObservedHand(int id, string card, int cost, int attack, int health)
    => Entity(id, card, 1, "HAND", "MINION", [("COST", cost), ("ATK", attack), ("HEALTH", health), ("DAMAGE", 0)]);
static EntitySnapshot Wolf(int attack, int? discount = null) => Entity(12, "EDR_850p", 1, "PLAY", "HERO_POWER",
    [("COST", 2), ("EXHAUSTED", 0), ("TAG_SCRIPT_DATA_NUM_1", attack), ("TAG_SCRIPT_DATA_NUM_2", discount ?? attack)]);
static GameSnapshot WithWolf(GameSnapshot snapshot, int level) => Replace(snapshot, Wolf(level));
static GameSnapshot ChoiceFixture() => Fixture() with { Phase = GamePhase.Choice,
    Choice = new(7, 1, "GENERAL", 30, 1, 1, [new(40, "EDR_227", 1, 1), new(41, "EDR_480", 1, 2)], true) };
static EntitySnapshot Hero(int id, int owner, int maxHealth, int damage = 0, int armor = 0, int attack = 0, params (string Key, int Value)[] extra)
    => Entity(id, "HERO_" + owner, owner, "PLAY", "HERO", [("CLASS", owner == 1 ? 3 : 4), ("HEALTH", maxHealth), ("DAMAGE", damage), ("ARMOR", armor), ("ATK", attack), ("EXHAUSTED", 0), .. extra]);
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
