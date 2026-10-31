using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class ExplosiveTrapIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_EXPLOSIVE_TRAP_INTEGRATION";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "explosive-trap-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Trap integration fixture is not pinned to the audited build.");
        var catalog = new CardCatalog(Path.Combine(root, "explosive-trap", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://explosive-trap-integration");
        var engine = Start(); var frame = engine.Snapshot(); var before = JsonSerializer.Serialize(frame);
        Check(frame.ActivePlayerId == 2 && frame.Entities[30] is { CardId: "CORE_EX1_610", Zone: "SECRET", ControllerId: 1 },
            "Parser fixture did not reach the opponent turn with a known own active Trap.");
        var observations = new List<CoachToolObservation>();
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Turn);
        var coach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, toolCompleted: observations.Add);
        var direct = await Preview(coach);
        Check(direct["TriggerActive"]?.GetValue<bool>() == true && direct["ResolvesDirectDamage"]?.GetValue<bool>() == true &&
            direct["TrapConsumed"]?.GetValue<bool>() == true && direct["DamagePerEnemy"]?.GetValue<long>() == 2 &&
            direct["Targets"]!.AsArray().Select(target => target!["EntityId"]!.GetValue<int>()).Order().SequenceEqual([21, 40, 41]) &&
            Target(direct, 21)["HealthAfter"]?.GetValue<long>() == 29 && Target(direct, 21)["ArmorAfter"]?.GetValue<long>() == 0 &&
            Target(direct, 40)["HealthAfter"]?.GetValue<long>() == 0 && Target(direct, 40)["IsAliveAfter"]?.GetValue<bool>() == false &&
            Target(direct, 41)["HealthAfter"]?.GetValue<long>() == 4 && Target(direct, 41)["DivineShieldAfter"]?.GetValue<bool>() == false &&
            direct["AttackerSurvivesDirectDamage"]?.GetValue<bool>() == false && direct["AttackStoppedByDirectDamage"]?.GetValue<bool>() == true &&
            direct["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && direct["HasAuthoritativeNextState"]?.GetValue<bool>() == false &&
            observations.Single() is { Name: "evaluate_explosive_trap", Success: true, ResultHash.Length: 64 } && before == JsonSerializer.Serialize(frame),
            "Parsed Trap did not calculate its simultaneous direct shield/armor damage or mutated the observed frame: " + direct.ToJsonString());
        pass("parsed own Explosive Trap reaches pre-combat enemy AoE shield armor and attacker-death consequences without applying ordinary attack damage");

        foreach (var aggregate in new[] { false, true })
        {
            var spellFrame = Start(spellDamage: true, aggregateSpellDamage: aggregate).Snapshot(); var spellBefore = JsonSerializer.Serialize(spellFrame);
            var spell = await Preview(Coach(spellFrame, catalog));
            Check(spell["ResolvesDirectDamage"]?.GetValue<bool>() == true && spell["SpellDamage"]?.GetValue<long>() == 1 &&
                spell["DamagePerEnemy"]?.GetValue<long>() == 3 && Target(spell, 21)["HealthAfter"]?.GetValue<long>() == 28 &&
                Target(spell, 41)["HealthAfter"]?.GetValue<long>() == 4 && spell["Targets"]!.AsArray().All(target => target!["EntityId"]!.GetValue<int>() != 50) &&
                spellBefore == JsonSerializer.Serialize(spellFrame), "Native current owner Spell Damage was omitted double-counted or applied to the owner's board: " + spell.ToJsonString());
        }
        pass("native current Kobold Spell Damage and an equivalent owner aggregate each give three Trap damage without double counting");

        foreach (var (id, name, alias, aggregate) in new[] { (50, "SPELLPOWER", "192", false), (2, "CURRENT_SPELLPOWER", "291", true) })
        {
            var spellFrame = Start(spellDamage: true, aggregateSpellDamage: aggregate).Snapshot(); var entities = spellFrame.Entities.ToDictionary();
            var tags = entities[id].Tags.ToDictionary(); tags[name] = "1"; tags[alias] = "0";
            entities[id] = entities[id] with { Tags = tags.ToFrozenDictionary() };
            var uncertain = await Preview(Coach(spellFrame with { Entities = entities.ToFrozenDictionary() }, catalog));
            Check(uncertain["ResolvesDirectDamage"]?.GetValue<bool>() != true && uncertain["DamagePerEnemy"] is null && uncertain["Targets"]!.AsArray().Count == 0,
                "A raw Spell Damage alias conflict was replaced with a printed sum or an authoritative aggregate: " + uncertain.ToJsonString());
        }
        var wrongTypeFrame = Start(spellDamage: true).Snapshot(); var wrongTypeEntities = wrongTypeFrame.Entities.ToDictionary();
        var wrongTypeTags = wrongTypeEntities[50].Tags.ToDictionary(); wrongTypeTags["202"] = "SPELL";
        wrongTypeEntities[50] = wrongTypeEntities[50] with { Tags = wrongTypeTags.ToFrozenDictionary() };
        var wrongType = await Preview(Coach(wrongTypeFrame with { Entities = wrongTypeEntities.ToFrozenDictionary() }, catalog));
        Check(wrongType["ResolvesDirectDamage"]?.GetValue<bool>() != true && wrongType["DamagePerEnemy"] is null && wrongType["Targets"]!.AsArray().Count == 0,
            "Conflicting current type evidence removed a native Spell Damage body from the calculation instead of preserving uncertainty: " + wrongType.ToJsonString());
        pass("conflicting current body or player Spell Damage remains unknown instead of repairing it from native text or another total");

        var stopped = await Preview(Coach(Start(attackerHealth: 4, enemyHeroHealth: 1).Snapshot(), catalog));
        Check(stopped["ResolvesDirectDamage"]?.GetValue<bool>() == true && Target(stopped, 21)["HealthAfter"]?.GetValue<long>() == 0 &&
            stopped["AttackerSurvivesDirectDamage"]?.GetValue<bool>() == true && stopped["AttackStoppedByDirectDamage"]?.GetValue<bool>() == true &&
            stopped["HasAuthoritativeNextState"]?.GetValue<bool>() == false,
            "Direct death of the attacking player's hero allowed the surviving minion's ordinary attack to continue: " + stopped.ToJsonString());
        var pending = await Preview(Coach(Start(attackerCardId: "EDR_227").Snapshot(), catalog));
        Check(pending["ResolvesDirectDamage"]?.GetValue<bool>() == true && Target(pending, 40)["HealthAfter"]?.GetValue<long>() == 0 &&
            pending["RequiresObservation"]?.GetValue<bool>() == true && pending["Unknowns"]!.AsArray().Count > 0 &&
            pending["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && pending["HasAuthoritativeNextState"]?.GetValue<bool>() == false,
            "A killed native deathrattle attacker lost its actual direct damage or acquired a fabricated complete death/attack resolution: " + pending.ToJsonString());
        pass("direct attacking-hero death stops ordinary combat while a killed deathrattle body retains damage and an explicit unresolved continuation");

        var ordinaryHero = await Preview(Coach(HeroAttackFrame(), catalog), 21);
        Check(ordinaryHero["ResolvesDirectDamage"]?.GetValue<bool>() == true && Target(ordinaryHero, 21)["Damage"]?.GetValue<long>() == 2 &&
            Target(ordinaryHero, 21)["HealthAfter"]?.GetValue<long>() == 29 && ordinaryHero["AttackStoppedByDirectDamage"]?.GetValue<bool>() == false,
            "An ordinary public incoming hero lost its supported direct damage path.");
        foreach (var (protectedFrame, exactImmunity) in new[] { (HeroAttackFrame(immunityFlag: true), false),
            (HeroAttackFrame(weapon: "CORE_LOOT_222"), false), (HeroAttackFrame(weapon: "DS1_188"), true),
            (HeroAttackFrame(weapon: "DS1_188", weaponDamage: 1), true) })
        {
            var protectedHero = await Preview(Coach(protectedFrame, catalog), 21);
            Check(protectedHero["ResolvesDirectDamage"]?.GetValue<bool>() != true && protectedHero["AttackStoppedByDirectDamage"] is null &&
                !protectedHero["Targets"]!.AsArray().Any(target => target!["EntityId"]!.GetValue<int>() == 21) ||
                protectedHero["ResolvesDirectDamage"]?.GetValue<bool>() == true &&
                Target(protectedHero, 21)["Damage"]?.GetValue<long>() == 0 && Target(protectedHero, 21)["HealthAfter"]?.GetValue<long>() == 30 &&
                Target(protectedHero, 21)["ArmorAfter"]?.GetValue<long>() == 1,
                "An incoming hero's observed or native equipped-weapon attacking immunity was ignored: " + protectedHero.ToJsonString());
            if (exactImmunity) Check(protectedHero["ResolvesDirectDamage"]?.GetValue<bool>() == true,
                "The officially documented native live Longbow interaction was reduced to unknown instead of computing its immune zero-damage packet: " + protectedHero.ToJsonString());
            var notAttacking = await Preview(Coach(protectedFrame, catalog));
            Check(notAttacking["ResolvesDirectDamage"]?.GetValue<bool>() == true && Target(notAttacking, 21)["Damage"]?.GetValue<long>() == 2 &&
                Target(notAttacking, 21)["HealthAfter"]?.GetValue<long>() == 29,
                "A minion attack incorrectly activated its controller hero's while-attacking immunity: " + notAttacking.ToJsonString());
        }
        var depletedBow = await Preview(Coach(HeroAttackFrame(weapon: "DS1_188", weaponDamage: 2), catalog), 21);
        Check(depletedBow["ResolvesDirectDamage"]?.GetValue<bool>() != true || Target(depletedBow, 21)["Damage"]?.GetValue<long>() == 2 &&
            Target(depletedBow, 21)["HealthAfter"]?.GetValue<long>() == 29,
            "An exhausted zero-current-durability Longbow still granted attacking immunity: " + depletedBow.ToJsonString());
        pass("incoming-hero attacking immunity from its current flag or native equipped bow cannot be ignored or applied when only its minion attacks");

        var ownTurn = await Preview(Coach(Start(ownTurn: true).Snapshot(), catalog));
        Check(ownTurn["TriggerActive"]?.GetValue<bool>() != true && (ownTurn["Targets"] is null || ownTurn["Targets"]!.AsArray().Count == 0),
            "The Trap activated during its owner's turn.");
        foreach (var hidden in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } } })
        {
            var denied = await Preview(Coach(hidden, catalog));
            Check(denied["TriggerActive"]?.GetValue<bool>() != true && (denied["Targets"] is null || denied["Targets"]!.AsArray().Count == 0) &&
                !denied.ToJsonString().Contains("CORE_EX1_610", StringComparison.Ordinal), "An unconfirmed perspective exposed own private Trap identity or damage.");
        }
        foreach (var args in new[] { new JsonObject { ["source_entity_id"] = 90, ["attacker_entity_id"] = 40 },
            new JsonObject { ["source_entity_id"] = 30, ["attacker_entity_id"] = 90 } })
        {
            var denied = await coach.ExecuteAsync("evaluate_explosive_trap", args, default);
            Check(denied["TriggerActive"]?.GetValue<bool>() != true && !denied.ToJsonString().Contains(Probe, StringComparison.Ordinal) &&
                (denied["Targets"] is null || denied["Targets"]!.AsArray().Count == 0), "A hidden enemy hand became an installed source or public attacker.");
        }
        pass("own-turn spectator uncertain-perspective and hidden-hand inputs cannot activate or expose an installed Trap preview");

        var secretFrame = Start(opposingSecret: true).Snapshot();
        var secret = await Preview(Coach(secretFrame, catalog));
        var opposingSource = await Coach(secretFrame, catalog).ExecuteAsync("evaluate_explosive_trap",
            new() { ["source_entity_id"] = 91, ["attacker_entity_id"] = 40 }, default);
        Check(secretFrame.Entities[91].CardId is null && secret["ResolvesDirectDamage"]?.GetValue<bool>() == true &&
            secret["DamagePerEnemy"]?.GetValue<long>() == 2 && secret["AttackStoppedByDirectDamage"]?.GetValue<bool>() == true &&
            !secret.ToJsonString().Contains(Probe, StringComparison.Ordinal) && !direct.ToJsonString().Contains(Probe, StringComparison.Ordinal) &&
            opposingSource["TriggerActive"]?.GetValue<bool>() != true && (opposingSource["Targets"] is null || opposingSource["Targets"]!.AsArray().Count == 0) &&
            !opposingSource.ToJsonString().Contains(Probe, StringComparison.Ordinal),
            "An opponent Secret leaked or activated on its owner's turn rather than leaving the same direct Trap consequences: " + secret.ToJsonString());
        pass("a hidden opposing actual Secret stays private and cannot activate on its owner's current attacking turn");

        foreach (var key in new[] { "target_entity_id", "targets", "damage", "spell_damage", "turn", "attacker_health" })
            await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_explosive_trap",
                new() { ["source_entity_id"] = 30, ["attacker_entity_id"] = 40, [key] = 11 }, default));
        var deckCoach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, taskKind: AdviceTaskKind.DeckPlan);
        Check(!deckCoach.Capabilities().Tools.Any(tool => tool.Name == "evaluate_explosive_trap"), "Deck planning offered a live incoming-attack tool.");
        await Throws<ArgumentException>(() => Preview(deckCoach));
        pass("Trap schema forbids model-defined targets damage timing or current stats and is unavailable to deck planning");

        using var model = new IncomingAttackModel(); using var http = new HttpClient(model);
        try
        {
            _ = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://explosive-trap-fixture.invalid/v1/chat/completions"), "trap-fixture"),
                "trap-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
            throw new InvalidOperationException("A hypothetical opposing attack survived the host's invalid-action rejection.");
        }
        catch (InvalidDataException error) when (error.Message == "模型提出的动作未通过本地合法性校验。") { }
        Check(model.Calls == 2 && model.CheckedPreview && before == JsonSerializer.Serialize(frame),
            "The model did not receive the direct conditional preview before its invalid opponent action was rejected.");
        pass("fake HTTP can inspect direct Trap consequences but cannot promote the opposing hypothetical attack into executable actions or verified lethal");
    }

    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog) => new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build);
    private static Task<JsonNode> Preview(SnapshotCoach coach, int attacker = 40) => coach.ExecuteAsync("evaluate_explosive_trap",
        new() { ["source_entity_id"] = 30, ["attacker_entity_id"] = attacker }, default);
    private static JsonNode Target(JsonNode outcome, int id) => outcome["Targets"]!.AsArray().Single(target => target!["EntityId"]!.GetValue<int>() == id)!;
    private static GameSnapshot HeroAttackFrame(string? weapon = null, bool immunityFlag = false, int weaponDamage = 0)
    {
        var engine = Start(); Feed(engine, ["TAG_CHANGE Entity=21 tag=ATK value=1"]);
        if (immunityFlag) Feed(engine, ["TAG_CHANGE Entity=21 tag=IMMUNE_WHILE_ATTACKING value=1"]);
        if (weapon is not null) Feed(engine, [$"FULL_ENTITY - Creating ID=60 CardID={weapon}", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY",
            "tag=CARDTYPE value=WEAPON", $"tag=ATK value={(weapon == "DS1_188" ? 5 : 1)}", $"tag=HEALTH value={(weapon == "DS1_188" ? 2 : 3)}", $"tag=DAMAGE value={weaponDamage}"]);
        Feed(engine, ["PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine.Snapshot();
    }
    private static PowerLogStateEngine Start(bool ownTurn = false, bool spellDamage = false, bool aggregateSpellDamage = false,
        bool opposingSecret = false, int attackerHealth = 2, int enemyHeroHealth = 30, string attackerCardId = "CS2_172")
    {
        var engine = new PowerLogStateEngine(1, "explosive-trap-integration");
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME",
            "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1",
            "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=NUM_CARDS_IN_HAND value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Character(11, "HERO_05", 1, "HERO", 0, 30), "tag=DAMAGE value=20", .. Character(21, "HERO_08", 2, "HERO", 0, enemyHeroHealth), "tag=ARMOR value=1",
            "FULL_ENTITY - Creating ID=30 CardID=CORE_EX1_610", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL",
            "tag=SECRET value=1", "tag=ZONE_POSITION value=1", "tag=COST value=2", .. Character(40, attackerCardId, 2, "MINION", 3, attackerHealth),
            .. Character(41, "CS2_172", 2, "MINION", 1, 4), "tag=DIVINE_SHIELD value=1"]);
        if (spellDamage) Feed(engine, [.. Character(50, "CORE_CS2_142", 1, "MINION", 2, 2), "tag=SPELLPOWER value=1"]);
        if (aggregateSpellDamage) Feed(engine, ["TAG_CHANGE Entity=2 tag=CURRENT_SPELLPOWER value=1"]);
        Feed(engine, [$"FULL_ENTITY - Creating ID=90 CardID={Probe}", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION"]);
        if (opposingSecret) Feed(engine, [$"FULL_ENTITY - Creating ID=91 CardID={Probe}_SECRET", "tag=CONTROLLER value=2", "tag=ZONE value=SECRET",
            "tag=CARDTYPE value=SPELL", "tag=SECRET value=1", "tag=QUEST value=0", "tag=SIDE_QUEST value=0", "tag=ZONE_POSITION value=1"]);
        Feed(engine, ["TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION"]);
        if (!ownTurn) Feed(engine, ["TAG_CHANGE Entity=1 tag=TURN value=2", "TAG_CHANGE Entity=2 tag=CURRENT_PLAYER value=0",
            "TAG_CHANGE Entity=3 tag=CURRENT_PLAYER value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION"]);
        Feed(engine, ["PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static string[] Character(int id, string card, int owner, string type, int attack, int health) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", $"tag=CARDTYPE value={type}",
            $"tag=ATK value={attack}", $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0",
            "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1"];
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<JsonNode>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class IncomingAttackModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedPreview { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains(Probe, StringComparison.Ordinal), "Private Trap fixture identities entered the model request.");
            var body = JsonNode.Parse(text)!; object message;
            if (Calls == 1)
            {
                Check(text.Contains("evaluate_explosive_trap", StringComparison.Ordinal), "The incoming Trap preview was not offered to the model.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "incoming-trap", type = "function",
                    function = new { name = "evaluate_explosive_trap", arguments = "{\"source_entity_id\":30,\"attacker_entity_id\":40}" } } } };
            }
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(result["ResolvesDirectDamage"]?.GetValue<bool>() == true && result["AttackStoppedByDirectDamage"]?.GetValue<bool>() == true &&
                    result["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && result["HasAuthoritativeNextState"]?.GetValue<bool>() == false,
                    "The model did not receive direct damage with the explicit no-authoritative-state boundary.");
                CheckedPreview = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "把对手假设攻击及奥秘预览当成未来已发生结果。",
                    reasons = new[] { "刻意尝试执行敌方动作" }, risks = Array.Empty<string>(),
                    actions = new[] { new { kind = "attack", source_entity_id = 40, target_entity_id = 11 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
