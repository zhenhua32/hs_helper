using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class WeaponCombatIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_WEAPON_COMBAT_INTEGRATION";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "weapon-combat-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Weapon fixture lost its audited build.");
        var catalog = new CardCatalog(Path.Combine(root, "weapon-combat", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://weapon-combat-integration");

        var frame = Start().Snapshot(); var before = JsonSerializer.Serialize(frame);
        Check(frame.Quality is { IsStable: true, IsComplete: true, HasLocalPerspective: true } && frame.ActivePlayerId == 1 &&
            frame.Entities[31] is { CardId: "CORE_CS2_106", ControllerId: 1, Zone: "PLAY" } &&
            WeaponDurabilityEvidence.Resolve(frame, 31) is { IsKnown: true, Remaining: 2, Mode: WeaponDurabilityMode.ModernHealthDamage },
            "Parser fixture did not produce the observed modern equipped weapon.");
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build, toolCompleted: observations.Add);
        var attack = await Line(coach, Attack(40)); var result = attack["Result"]!;
        Check(Verified(attack, 1) && Entity(result, 40)["RemainingHealth"]?.GetValue<int>() == 7 &&
            Entity(result, 11)["RemainingHealth"]?.GetValue<int>() == 19 && Entity(result, 11)["Attack"]?.GetValue<int>() == 3 &&
            Entity(result, 31)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 1 &&
            Entity(result, 31)["WeaponDurability"]?["Mode"]?.ToString() == "ModernHealthDamage" &&
            Entity(result, 31)["Flags"]?["HEALTH"]?.GetValue<int>() == 2 && Entity(result, 31)["Flags"]?["DAMAGE"]?.GetValue<int>() == 1 &&
            Entity(result, 31)["Flags"]?["DURABILITY"] is null && result["AvailableMana"]?.GetValue<int>() == 10 &&
            observations.Single() is { Name: "evaluate_line", Success: true, ResultHash.Length: 64 } && before == JsonSerializer.Serialize(frame),
            "Modern parsed combat failed to spend HEALTH-DAMAGE once or changed the input: " + attack.ToJsonString());
        pass("parsed modern weapon combat reaches the host with current damage retaliation and one observed health-resource use");

        var last = await Line(Coach(Start(weaponDamage: 1, heroAttack: 5).Snapshot(), catalog), Attack(40));
        Check(Verified(last, 1) && Entity(last["Result"]!, 11)["Attack"]?.GetValue<int>() == 2 &&
            Entity(last["Result"]!, 40)["RemainingHealth"]?.GetValue<int>() == 5 && GraveWeapon(last["Result"]!, 31, 0, 2),
            "Last weapon use retained the weapon attack contribution or erased independent hero attack: " + last.ToJsonString());
        var falseLethalFrame = Start(weaponDamage: 1, staleDurability: true, heroWindfury: true, enemyHeroHealth: 6).Snapshot();
        var falseLethalBefore = JsonSerializer.Serialize(falseLethalFrame);
        var falseLethal = await Line(Coach(falseLethalFrame, catalog), Attack(), Attack());
        Check(WeaponDurabilityEvidence.Resolve(falseLethalFrame, 31).Remaining == 1 && !Verified(falseLethal, 2) &&
            falseLethal["IsLethal"]?.GetValue<bool>() == false && falseLethal["AppliedActions"]?.GetValue<int>() == 1 &&
            Entity(falseLethal["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 3 &&
            Entity(falseLethal["Result"]!, 11)["Attack"]?.GetValue<int>() == 0 && GraveWeapon(falseLethal["Result"]!, 31, 0, 2) &&
            falseLethalBefore == JsonSerializer.Serialize(falseLethalFrame),
            "Deprecated DURABILITY manufactured a second attack or a verified six-damage lethal: " + falseLethal.ToJsonString());
        pass("last-use destruction removes only weapon attack and stale legacy durability cannot create a second attack or false lethal");

        var missingFrame = Start(weaponDamage: null, staleDurability: true).Snapshot();
        var missing = await Line(Coach(missingFrame, catalog), Attack());
        Check(!WeaponDurabilityEvidence.Resolve(missingFrame, 31).IsKnown && NoAttack(missing) &&
            Entity(missing["Result"]!, 31)["WeaponDurability"]?["Remaining"] is null &&
            Entity(missing["Result"]!, 31)["Flags"]?["DAMAGE"] is null,
            "Missing modern DAMAGE was silently repaired by legacy durability or zero: " + missing.ToJsonString());
        foreach (var (name, value) in new[] { ("45", "3"), ("44", "1"), ("47", "9"), ("202", "MINION"),
            ("50", "2"), ("49", "HAND"), ("53", "999"), ("DAMAGE", "bad"), ("IMMUNE", "bad") })
        {
            var uncertain = WithTags(frame, 31, (name, value));
            var denied = await Line(Coach(uncertain, catalog), Attack());
            Check(NoAttack(denied), "Raw equipped-weapon " + name + " conflict entered combat: " + denied.ToJsonString());
        }
        var duplicate = await Line(Coach(Start(duplicateWeapon: true).Snapshot(), catalog), Attack());
        Check(NoAttack(duplicate), "Two same-owner equipped weapons were reduced to whichever appeared first: " + duplicate.ToJsonString());
        foreach (var uncertain in new[] { WithoutTags(frame, 31, "CONTROLLER"),
            WithTags(frame, 31, ("CONTROLLER", "2"), ("50", "1")), WithTags(frame, 31, ("ZONE", "GRAVEYARD"), ("49", "1")) })
        {
            var denied = await Line(Coach(uncertain, catalog), Attack(), Attack());
            Check(NoAttack(denied), "Unknown/reverse owner or zone evidence made an equipped weapon disappear before combat: " + denied.ToJsonString());
        }
        pass("missing modern damage contradictory raw aliases and multiple equipped weapons stop before combat instead of selecting or repairing evidence");

        var doom = await Line(Coach(Start(weaponCard: "CORE_EX1_567", weaponHealth: 8, weaponDamage: 6, weaponAttack: 2, heroAttack: 2).Snapshot(), catalog), Attack(), Attack());
        Check(Verified(doom, 2) && Entity(doom["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 26 &&
            Entity(doom["Result"]!, 11)["Attack"]?.GetValue<int>() == 0 && GraveWeapon(doom["Result"]!, 31, 0, 8) &&
            doom["Result"]?["AvailableMana"]?.GetValue<int>() == 10,
            "Equipped native Doomhammer re-applied Overload or lost Windfury/resource use: " + doom.ToJsonString());
        var exhaustedWindfury = await Line(Coach(Start(weaponCard: "CORE_EX1_567", weaponHealth: 8, weaponDamage: 7, weaponAttack: 2, heroAttack: 4).Snapshot(), catalog), Attack(), Attack());
        Check(exhaustedWindfury["AppliedActions"]?.GetValue<int>() == 1 &&
            Entity(exhaustedWindfury["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 26 &&
            Entity(exhaustedWindfury["Result"]!, 11)["Attack"]?.GetValue<int>() == 2,
            "Destroyed weapon Windfury authorized a further base-attack hit: " + exhaustedWindfury.ToJsonString());
        var independent = await Line(Coach(Start(weaponDamage: 1, heroAttack: 5, heroWindfury: true).Snapshot(), catalog), Attack(), Attack());
        Check(Verified(independent, 2) && Entity(independent["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 23 &&
            Entity(independent["Result"]!, 11)["Attack"]?.GetValue<int>() == 2,
            "Independent current hero Windfury disappeared with the destroyed weapon: " + independent.ToJsonString());
        var replacedWindfury = await Line(Coach(Start(weaponCard: "CORE_EX1_567", weaponHealth: 8, weaponDamage: 6,
            weaponAttack: 2, heroAttack: 2, heroWindfury: true, handWeapon: true, enemyHeroHealth: 6).Snapshot(), catalog), Attack(), Play(), Attack());
        Check(replacedWindfury["Verification"]?.ToString() != "Verified" && replacedWindfury["AppliedActions"]?.GetValue<int>() == 2 &&
            replacedWindfury["IsLethal"]?.GetValue<bool>() == false && replacedWindfury["Result"]?["WeaponResolutionBoundaryReached"]?.GetValue<bool>() == true &&
            Entity(replacedWindfury["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 4 &&
            Entity(replacedWindfury["Result"]!, 33)["Zone"]?.ToString() == "HAND" && GraveWeapon(replacedWindfury["Result"]!, 31, 1, 7),
            "Replacing a weapon reused its ambiguously inherited raw hero Windfury for another attack: " + replacedWindfury.ToJsonString());
        pass("native equipped weapon Windfury lasts only while equipped while independent hero Windfury preserves the remaining base attack");

        var equipFrame = Start(equipped: false, handWeapon: true, heroAttack: 2).Snapshot();
        Check(!equipFrame.Entities[33].Tags.ContainsKey("DAMAGE"), "Fresh-equipment fixture accidentally supplied the missing hand damage.");
        var equip = await Line(Coach(equipFrame, catalog), Play(), Attack(40)); result = equip["Result"]!;
        Check(Verified(equip, 2) && result["AvailableMana"]?.GetValue<int>() == 9 &&
            Entity(result, 11)["Attack"]?.GetValue<int>() == 6 && Entity(result, 11)["RemainingHealth"]?.GetValue<int>() == 19 &&
            Entity(result, 40)["RemainingHealth"]?.GetValue<int>() == 4 &&
            Entity(result, 33)["Zone"]?.ToString() == "PLAY" && Entity(result, 33)["Attack"]?.GetValue<int>() == 4 &&
            Entity(result, 33)["MaximumHealth"]?.GetValue<int>() == 3 && Entity(result, 33)["Flags"]?["DAMAGE"]?.GetValue<int>() == 1 &&
            Entity(result, 33)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 2,
            "Simple equipment did not use current hand cost/attack/health and fresh zero damage: " + equip.ToJsonString());
        var fresh = await Line(Coach(equipFrame, catalog), Play());
        Check(Verified(fresh, 1) && Entity(fresh["Result"]!, 33)["Flags"]?["DAMAGE"]?.GetValue<int>() == 0 &&
            Entity(fresh["Result"]!, 33)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 3,
            "Fresh equipment did not establish new DAMAGE zero before its first use.");
        var replacement = await Line(Coach(Start(handWeapon: true, weaponDamage: 1, heroAttack: 5).Snapshot(), catalog), Play(), Attack());
        Check(Verified(replacement, 2) && GraveWeapon(replacement["Result"]!, 31, 1, 1) &&
            Entity(replacement["Result"]!, 11)["Attack"]?.GetValue<int>() == 6 && Entity(replacement["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 24 &&
            Entity(replacement["Result"]!, 33)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 2,
            "Simple replacement retained old weapon attack or spent the wrong weapon: " + replacement.ToJsonString());
        pass("simple native equipment and replacement use observed hand values establish fresh damage zero and update hero attack before ordinary combat");

        var drFrame = Start(handWeapon: true, weaponCard: "BAR_330", weaponDamage: 1, heroAttack: 5).Snapshot();
        var dr = await Line(Coach(drFrame, catalog), Play(), Attack()); result = dr["Result"]!;
        Check(dr["Verification"]?.ToString() != "Verified" && dr["IsLethal"]?.GetValue<bool>() == false &&
            dr["AppliedActions"]?.GetValue<int>() == 1 && result["WeaponResolutionBoundaryReached"]?.GetValue<bool>() == true &&
            result["AvailableMana"]?.GetValue<int>() == 9 && GraveWeapon(result, 31, 1, 1) && Entity(result, 11)["Attack"]?.GetValue<int>() == 2 &&
            Entity(result, 21)["RemainingHealth"]?.GetValue<int>() == 30 && Entity(result, 33)["Zone"]?.ToString() == "HAND" &&
            Entity(result, 33)["Attack"]?.GetValue<int>() == 4 && Entity(result, 33)["Flags"]?["DAMAGE"] is null &&
            dr["UnsupportedMechanics"]!.AsArray().Any(item => item?.ToString() == "AWAIT_WEAPON_SNAPSHOT"),
            "Unknown old weapon deathrattle invented a finished replacement or permitted followup combat: " + dr.ToJsonString());
        pass("old weapon deathrattle replacement records payment and removal but retains the hand observation and blocks every following action");

        var lethalPayFrame = Start(handWeapon: true, payment: true, enemyHeroHealth: 1, handCost: 2).Snapshot();
        var lethalPay = await Line(Coach(lethalPayFrame, catalog), Play()); result = lethalPay["Result"]!;
        Check(Verified(lethalPay, 1) && lethalPay["IsLethal"]?.GetValue<bool>() == true && result["OpponentDefeatedByPayment"]?.GetValue<bool>() == true &&
            result["OpponentHealthPayments"]!.AsArray().Count == 1 && result["OpponentHealthPayments"]![0]?["Cost"]?.GetValue<int>() == 2 &&
            result["AvailableMana"]?.GetValue<int>() == 10 && Entity(result, 31)["Zone"]?.ToString() == "PLAY" &&
            Entity(result, 31)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 2 &&
            Entity(result, 33)["Zone"]?.ToString() == "HAND" && Entity(result, 11)["Attack"]?.GetValue<int>() == 3,
            "Lethal alternate payment equipped the card or destroyed the old weapon after the game had ended: " + lethalPay.ToJsonString());
        var healthPay = await Line(Coach(Start(handWeapon: true, payment: true).Snapshot(), catalog), Play());
        Check(Verified(healthPay, 1) && healthPay["Result"]?["AvailableMana"]?.GetValue<int>() == 10 &&
            Entity(healthPay["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 29 && Entity(healthPay["Result"]!, 33)["Zone"]?.ToString() == "PLAY",
            "Nonlethal alternate payment spent mana instead of the observed current hand cost.");
        foreach (var card in new[] { "CORE_EX1_567", "CORE_ICC_064" })
        {
            var unsupported = await Line(Coach(Start(handWeapon: true, handCard: card, payment: true).Snapshot(), catalog), Play());
            Check(unsupported["AppliedActions"]?.GetValue<int>() == 0 && unsupported["Verification"]?.ToString() == "Unsupported" &&
                unsupported["Result"]?["AvailableMana"]?.GetValue<int>() == 10 && unsupported["Result"]?["NextCardCostsOpponentHealth"]?.GetValue<bool>() == true &&
                unsupported["Result"]?["OpponentHealthPayments"]!.AsArray().Count == 0 &&
                Entity(unsupported["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 30 && Entity(unsupported["Result"]!, 31)["Zone"]?.ToString() == "PLAY" &&
                Entity(unsupported["Result"]!, 33)["Zone"]?.ToString() == "HAND",
                "An unsupported hand weapon Battlecry/Overload consumed payment or changed equipment: " + unsupported.ToJsonString());
        }
        pass("alternate lethal payment stops before equipment while unsupported hand Battlecry and Overload do not consume health mana or the payment effect");

        foreach (var field in new[] { "ATK", "HEALTH", "COST" })
        {
            var unobserved = WithoutTags(equipFrame, 33, field);
            var rejected = await Line(Coach(unobserved, catalog), Play());
            Check(rejected["AppliedActions"]?.GetValue<int>() == 0 && rejected["Result"]?["AvailableMana"]?.GetValue<int>() == 10 &&
                Entity(rejected["Result"]!, 33)["Zone"]?.ToString() == "HAND" && Entity(rejected["Result"]!, 11)["Attack"]?.GetValue<int>() == 2,
                "Missing current hand " + field + " was filled from printed weapon data: " + rejected.ToJsonString());
        }
        foreach (var uncertain in new[] { WithTags(equipFrame, 33, ("LIFESTEAL", "1"), ("685", "0")),
            WithTags(equipFrame, 33, ("COST", "1"), ("48", "9")), WithTags(equipFrame, 33, ("IMMUNE", "bad")) })
        {
            var rejected = await Line(Coach(uncertain, catalog), Play());
            Check(rejected["AppliedActions"]?.GetValue<int>() == 0 && rejected["Result"]?["AvailableMana"]?.GetValue<int>() == 10 &&
                Entity(rejected["Result"]!, 33)["Zone"]?.ToString() == "HAND", "Uncertain current hand weapon flags or cost were paid and equipped: " + rejected.ToJsonString());
        }
        var suppliedTarget = await Line(Coach(Start(handWeapon: true, payment: true).Snapshot(), catalog), new JsonObject
            { ["kind"] = "play", ["source_entity_id"] = 33, ["target_entity_id"] = 21 });
        Check(suppliedTarget["AppliedActions"]?.GetValue<int>() == 0 && suppliedTarget["Result"]?["NextCardCostsOpponentHealth"]?.GetValue<bool>() == true &&
            suppliedTarget["Result"]?["OpponentHealthPayments"]!.AsArray().Count == 0, "An invalid equipment target paid before rejection.");
        pass("equipment requires current attack health and cost and rejects an invented target before any payment");

        var opponentFrame = Start(opposingWeapon: true).Snapshot();
        var opponent = await Line(Coach(opponentFrame, catalog), Attack());
        Check(Verified(opponent, 1) && Entity(opponent["Result"]!, 11)["RemainingHealth"]?.GetValue<int>() == 20 &&
            Entity(opponent["Result"]!, 30)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 2 &&
            Entity(opponent["Result"]!, 30)["Flags"]?["DAMAGE"]?.GetValue<int>() == 2 && opponentFrame.Entities[90].CardId is null &&
            !HasEntity(opponent["Result"]!, 90) && !opponent.ToJsonString().Contains(Probe, StringComparison.Ordinal),
            "An opponent weapon retaliated or spent durability or private hand identity escaped the host: " + opponent.ToJsonString());
        foreach (var key in new[] { "weapon_health", "weapon_remaining", "weapon_entity_id", "controller_id" })
        {
            var top = Actions(Attack()); top[key] = 99; await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_line", top, default));
            var forged = Attack(); forged[key] = 99; await Throws<ArgumentException>(() => Line(coach, forged));
        }
        pass("opponent public weapons remain unspent private hand identities stay hidden and model parameters cannot forge weapon resources or ownership");

        var privacyEngine = Start(handWeapon: true, weaponDamage: 1, heroAttack: 5);
        Feed(privacyEngine, [$"FULL_ENTITY - Creating ID=60 CardID={Probe}_OLD_OWN_GRAVE", "tag=CONTROLLER value=1", "tag=ZONE value=GRAVEYARD",
            "tag=CARDTYPE value=WEAPON", "tag=ATK value=9", "tag=HEALTH value=2", "tag=DAMAGE value=1",
            $"FULL_ENTITY - Creating ID=61 CardID={Probe}_OLD_ENEMY_GRAVE", "tag=CONTROLLER value=2", "tag=ZONE value=GRAVEYARD",
            "tag=CARDTYPE value=WEAPON", "tag=ATK value=9", "tag=HEALTH value=2", "tag=DAMAGE value=1",
            $"FULL_ENTITY - Creating ID=62 CardID={Probe}_DECK", "tag=CONTROLLER value=2", "tag=ZONE value=DECK", "tag=CARDTYPE value=WEAPON",
            $"FULL_ENTITY - Creating ID=63 CardID={Probe}_SETASIDE", "tag=CONTROLLER value=2", "tag=ZONE value=SETASIDE", "tag=CARDTYPE value=WEAPON",
            "TAG_CHANGE Entity=33 tag=HEALTH value=1", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var privacyFrame = privacyEngine.Snapshot(); var privacyBefore = JsonSerializer.Serialize(privacyFrame);
        Check(privacyFrame.Entities[60].CardId is not null && privacyFrame.Entities[61].CardId is not null &&
            new[] { 90, 62, 63 }.All(id => privacyFrame.Entities[id].CardId is null), "Privacy fixture lost its old public graves or hidden opponent zones.");
        var privacy = await Line(Coach(privacyFrame, catalog), Play(), Attack()); result = privacy["Result"]!;
        Check(Verified(privacy, 2) && GraveWeapon(result, 31, 1, 1) && GraveWeapon(result, 33, 0, 1) &&
            Entity(result, 31)["CardId"]?.ToString() == "CORE_CS2_106" && Entity(result, 33)["CardId"]?.ToString() == "CORE_CS2_106" &&
            new[] { 60, 61, 62, 63, 90 }.All(id => !HasEntity(result, id)) && !privacy.ToJsonString().Contains(Probe, StringComparison.Ordinal) &&
            privacyBefore == JsonSerializer.Serialize(privacyFrame),
            "Grave weapon projection exposed unrelated old graves/private cards or omitted this line's known public weapon transitions: " + privacy.ToJsonString());
        pass("grave projection retains only this line's originally public or confirmed-own-hand weapon transitions while old graves and private zones stay out");

        using var model = new FalseLethalModel(); using var http = new HttpClient(model);
        var context = CompactAdviceContextBuilder.Create(falseLethalFrame, Build, AdviceTaskKind.Turn);
        var falseLethalCoach = new SnapshotCoach(falseLethalFrame, context.Snapshot, catalog, Build);
        try
        {
            _ = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://weapon-combat-fixture.invalid/v1/chat/completions"), "weapon-fixture"),
                "weapon-fixture-unused-key", context, falseLethalCoach.ExecuteAsync, () => true, capabilities: falseLethalCoach.Capabilities());
            throw new InvalidOperationException("The model's known post-break invalid attack survived action validation.");
        }
        catch (InvalidDataException error) when (error.Message == "模型提出的动作未通过本地合法性校验。") { }
        Check(model.Calls == 2 && model.CheckedResult && falseLethalBefore == JsonSerializer.Serialize(falseLethalFrame),
            "Fake HTTP did not receive the first-hit weapon destruction before host rejection or changed the observed frame.");
        pass("fake HTTP receives the modern last-use result and cannot assert the stale-durability double attack as executable verified lethal");
    }

    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog) => new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build);
    private static Task<JsonNode> Line(SnapshotCoach coach, params JsonObject[] actions) => coach.ExecuteAsync("evaluate_line", Actions(actions), default);
    private static JsonObject Actions(params JsonObject[] actions) => new() { ["actions"] = new JsonArray(actions.Cast<JsonNode>().ToArray()) };
    private static JsonObject Attack(int target = 21) => new() { ["kind"] = "attack", ["source_entity_id"] = 11, ["target_entity_id"] = target };
    private static JsonObject Play() => new() { ["kind"] = "play", ["source_entity_id"] = 33 };
    private static JsonNode Entity(JsonNode result, int id) => result["Entities"]!.AsArray().Single(entity => entity!["EntityId"]!.GetValue<int>() == id)!;
    private static bool HasEntity(JsonNode result, int id) => result["Entities"]!.AsArray().Any(entity => entity!["EntityId"]!.GetValue<int>() == id);
    private static bool GraveWeapon(JsonNode result, int id, int remaining, int damage) =>
        Entity(result, id)["Zone"]?.ToString() == "GRAVEYARD" && Entity(result, id)["IsAlive"]?.GetValue<bool>() == false &&
        Entity(result, id)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == remaining && Entity(result, id)["Flags"]?["DAMAGE"]?.GetValue<int>() == damage;
    private static bool Verified(JsonNode line, int count) => line["Verification"]?.ToString() == "Verified" && line["AppliedActions"]?.GetValue<int>() == count;
    private static bool NoAttack(JsonNode line) => line["Verification"]?.ToString() != "Verified" && line["AppliedActions"]?.GetValue<int>() == 0 &&
        line["IsLethal"]?.GetValue<bool>() == false && Entity(line["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 30;
    private static GameSnapshot WithTags(GameSnapshot frame, int id, params (string Key, string Value)[] changes)
    {
        var entities = frame.Entities.ToDictionary(); var tags = entities[id].Tags.ToDictionary();
        foreach (var (key, value) in changes) tags[key] = value;
        entities[id] = entities[id] with { Tags = tags.ToFrozenDictionary() }; return frame with { Entities = entities.ToFrozenDictionary() };
    }
    private static GameSnapshot WithoutTags(GameSnapshot frame, int id, params string[] keys)
    {
        var entities = frame.Entities.ToDictionary(); var tags = entities[id].Tags.Where(tag => !keys.Contains(tag.Key)).ToFrozenDictionary();
        entities[id] = entities[id] with { Tags = tags }; return frame with { Entities = entities.ToFrozenDictionary() };
    }
    private static PowerLogStateEngine Start(bool equipped = true, int? weaponDamage = 0, bool staleDurability = false,
        bool heroWindfury = false, int heroAttack = 3, int enemyHeroHealth = 30, bool duplicateWeapon = false,
        bool handWeapon = false, string handCard = "CORE_CS2_106", int handCost = 1, bool payment = false,
        string weaponCard = "CORE_CS2_106", int weaponHealth = 2, int weaponAttack = 3, bool opposingWeapon = false)
    {
        var engine = new PowerLogStateEngine(1, "weapon-combat-integration");
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME",
            "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1",
            "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            $"tag=NUM_CARDS_IN_HAND value={(handWeapon ? 1 : 0)}", "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Character(11, "HERO_05", 1, "HERO", heroAttack, 30), "tag=DAMAGE value=10", $"tag=WINDFURY value={(heroWindfury ? 1 : 0)}",
            .. Character(21, "HERO_08", 2, "HERO", 0, enemyHeroHealth), .. Character(40, "CORE_CS2_231", 2, "MINION", 1, 10)]);
        if (equipped)
        {
            Feed(engine, Weapon(31, weaponCard, 1, weaponAttack, weaponHealth, weaponDamage));
            if (staleDurability) Feed(engine, ["tag=DURABILITY value=2"]);
        }
        if (duplicateWeapon) Feed(engine, Weapon(32, "CS2_106", 1, 3, 2, 0));
        if (opposingWeapon) Feed(engine, Weapon(30, "CS2_106", 2, 8, 4, 2));
        if (handWeapon) Feed(engine, [$"FULL_ENTITY - Creating ID=33 CardID={handCard}", "tag=CONTROLLER value=1", "tag=ZONE value=HAND",
            "tag=CARDTYPE value=WEAPON", "tag=ATK value=4", "tag=HEALTH value=3", $"tag=COST value={handCost}"]);
        if (payment) Feed(engine, ["FULL_ENTITY - Creating ID=50 CardID=EDR_489e1", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=2"]);
        Feed(engine, [$"FULL_ENTITY - Creating ID=90 CardID={Probe}", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=WEAPON",
            "tag=ATK value=99", "tag=HEALTH value=99", "tag=DAMAGE value=0", "TAG_CHANGE Entity=1 tag=TURN value=1",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static IEnumerable<string> Weapon(int id, string card, int owner, int attack, int health, int? damage)
    {
        yield return $"FULL_ENTITY - Creating ID={id} CardID={card}"; yield return $"tag=CONTROLLER value={owner}";
        yield return "tag=ZONE value=PLAY"; yield return "tag=CARDTYPE value=WEAPON"; yield return $"tag=ATK value={attack}";
        yield return $"tag=HEALTH value={health}"; if (damage is { } value) yield return $"tag=DAMAGE value={value}";
    }
    private static string[] Character(int id, string card, int owner, string type, int attack, int health) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", $"tag=CARDTYPE value={type}",
            $"tag=ATK value={attack}", $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0",
            "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1"];
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<JsonNode>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class FalseLethalModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedResult { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains(Probe, StringComparison.Ordinal), "Private opposing hand entered the model request.");
            var body = JsonNode.Parse(text)!; object message;
            if (Calls == 1)
                message = new { role = "assistant", tool_calls = new[] { new { id = "modern-weapon", type = "function", function = new {
                    name = "evaluate_line", arguments = "{\"actions\":[{\"kind\":\"attack\",\"source_entity_id\":11,\"target_entity_id\":21},{\"kind\":\"attack\",\"source_entity_id\":11,\"target_entity_id\":21}]}" } } } };
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var line = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(line["AppliedActions"]?.GetValue<int>() == 1 && line["IsLethal"]?.GetValue<bool>() == false &&
                    Entity(line["Result"]!, 11)["Attack"]?.GetValue<int>() == 0 && Entity(line["Result"]!, 21)["RemainingHealth"]?.GetValue<int>() == 3,
                    "The fake model did not receive the actual last-use weapon break: " + line.ToJsonString());
                CheckedResult = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "刻意把旧耐久当成第二次三伤攻击。",
                    reasons = new[] { "对抗武器损毁后旧攻击力回填" }, risks = Array.Empty<string>(), actions = new[] {
                        new { kind = "attack", source_entity_id = 11, target_entity_id = 21 }, new { kind = "attack", source_entity_id = 11, target_entity_id = 21 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
