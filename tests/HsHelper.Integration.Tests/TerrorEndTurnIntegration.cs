using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class TerrorEndTurnIntegration
{
    private const string Build = "255790";
    private const string PrivateProbe = "PRIVATE_TERROR_END_TURN_PROBE";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json")));
        var catalog = new CardCatalog(Path.Combine(root, "terror-end-turn", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://terror-end-turn-integration");
        var frame = Start().Snapshot(); var before = JsonSerializer.Serialize(frame);
        var coach = Coach(frame, catalog);
        var result = await Line(coach, EndTurn());
        var preview = Preview(result);
        Check(result["Verification"]?.ToString() == "Conditional" && result["AppliedActions"]?.GetValue<int>() == 1 &&
            preview["ResolvesBranches"]?.GetValue<bool>() == true && preview["IsEnumerationComplete"]?.GetValue<bool>() == true &&
            preview["Branches"]!.AsArray().Count == 2 && preview["IsLegal"] is null && preview["HasAuthoritativeNextState"]?.GetValue<bool>() == false,
            "Parsed end turn did not reach a complete finite conditional preview: " + result.ToJsonString());
        var branches = preview["Branches"]!.AsArray();
        var early = branches.Single(branch => branch!["OwnHeroHealthAfter"]?.GetValue<long>() == 24)!;
        var late = branches.Single(branch => branch!["OwnHeroHealthAfter"]?.GetValue<long>() == 28)!;
        Check(early["Combats"]!.AsArray().Count == 1 && late["Combats"]!.AsArray().Count == 2 &&
            BranchEntity(early, 41)["IsAlive"]?.GetValue<bool>() == true && BranchEntity(late, 41)["IsAlive"]?.GetValue<bool>() == false &&
            BranchEntity(early, 30)["IsAlive"]?.GetValue<bool>() == false && BranchEntity(late, 30)["IsAlive"]?.GetValue<bool>() == false,
            "The direct line combined one order's healing with another order's survivors.");
        pass("parsed multi-enemy terror end turn preserves each combat order's correlated healing deaths and remaining board");

        Check(result["IsLethal"]?.GetValue<bool>() == false && result["Result"]?["TurnEnded"]?.GetValue<bool>() == true &&
            result["Result"]?["EndTurnTriggersResolved"]?.GetValue<bool>() == false && Entity(result, 11)["RemainingHealth"]?.GetValue<int>() == 20 &&
            Entity(result, 30)["RemainingHealth"]?.GetValue<int>() == 5 && Entity(result, 41)["RemainingHealth"]?.GetValue<int>() == 4 &&
            before == JsonSerializer.Serialize(frame), "A branch changed the authoritative line projection or source snapshot.");
        pass("terror branch enumeration leaves line entities at the observed pre-trigger boundary and cannot certify lethal or opponent start");

        var fresh = await Line(Coach(Start(handTerror: true).Snapshot(), catalog), new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 }, EndTurn());
        Check(fresh["AppliedActions"]?.GetValue<int>() == 2 && Preview(fresh)["Branches"]!.AsArray().Count == 2 &&
            fresh["Result"]?["AvailableMana"]?.GetValue<int>() == 9 && Entity(fresh, 30)["Zone"]?.ToString() == "PLAY",
            "A supported current-cost summon failed to feed its actual after-play state into the end-turn preview: " + fresh.ToJsonString());
        pass("a native terror played earlier in the same proposed line contributes its observed current stats to the end-turn branches");

        var bad = WithTags(frame, 30, ("45", "6"));
        var denied = await Line(Coach(bad, catalog), EndTurn());
        Check(Preview(denied)["ResolvesBranches"]?.GetValue<bool>() != true && Preview(denied)["Branches"]!.AsArray().Count == 0,
            "Host projection repaired a contradictory original terror health alias.");
        var duplicate = await Line(Coach(Start(secondTerror: true).Snapshot(), catalog), EndTurn());
        Check(duplicate["Result"]?["TerrorEndTurnPreviews"]!.AsArray().Count == 0 && duplicate["Result"]?["PendingForcedAttacks"]!.AsArray().Count == 2,
            "Multiple native triggers were silently reduced to one independently resolved terror.");
        pass("raw current-stat contradictions and multiple terror triggers cannot acquire a fabricated complete enumeration");

        var death = await Line(Coach(Start(secondCard: "EDR_227").Snapshot(), catalog), EndTurn());
        var deathBranches = Preview(death)["Branches"]!.AsArray();
        Check(deathBranches.Count > 0 && deathBranches.Any(branch => branch!["IsComplete"]?.GetValue<bool>() == false && branch["Combats"]!.AsArray().Count > 0) &&
            death["IsLethal"]?.GetValue<bool>() == false, "A death-trigger branch was finished beyond its known damage prefix: " + death.ToJsonString());
        pass("an unresolved minion death trigger keeps its calculated forced-combat prefix and does not fabricate a final board");

        foreach (var field in new[] { "order", "attacker_ids", "branch_id" })
        {
            var forged = EndTurn(); forged[field] = 1;
            await Throws<ArgumentException>(() => Line(coach, forged));
        }
        var wrongSource = await Line(coach, new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 30 });
        Check(wrongSource["AppliedActions"]?.GetValue<int>() == 0 && wrongSource["Result"]?["TerrorEndTurnPreviews"]!.AsArray().Count == 0 &&
            frame.Entities[90].CardId is null && !result.ToJsonString().Contains(PrivateProbe, StringComparison.Ordinal) &&
            preview["Branches"]!.AsArray().All(branch => !branch!["Entities"]!.AsArray().Any(entity => entity!["EntityId"]?.GetValue<int>() == 90)),
            "Forced-combat previews bypassed ordinary end-turn source validation or exported a hidden opposing hand.");
        pass("terror previews use the validated end-turn action reject model-selected orders and keep private entities out of every branch");

        var contextual = Start(knownContext: true, ownPower: "EDR_850p").Snapshot();
        var contextualLine = await Line(Coach(contextual, catalog), EndTurn());
        Check(Preview(contextualLine)["IsEnumerationComplete"]?.GetValue<bool>() == true && Preview(contextualLine)["Branches"]!.AsArray().Count == 2 &&
            Entity(contextualLine, 70)["Flags"]?["DAMAGE"]?.GetValue<int>() == 0 && Entity(contextualLine, 70)["WeaponDurability"]?["Remaining"]?.GetValue<int>() == 2,
            "Ordinary active hero powers, audited own trap/sidequest or already-observed stat enchantments disabled a normal complete-frame preview: " + contextualLine.ToJsonString());
        var unsafeContext = WithTags(contextual, 62, ("HEALTH_MINIMUM", "1"));
        var unsafeLine = await Line(Coach(unsafeContext, catalog), EndTurn());
        Check(Preview(unsafeLine)["ResolvesBranches"]?.GetValue<bool>() != true && Preview(unsafeLine)["Branches"]!.AsArray().Count == 0,
            "An observed attached damage modifier was omitted from the public end-turn state: " + Preview(unsafeLine).ToJsonString());
        pass("a complete hunter frame includes audited powers own secrets stat enchants and unspent passive weapons while attached damage modifiers remain unresolved");

        using var model = new BranchModel(); using var http = new HttpClient(model);
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Turn);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://terror-fixture.invalid/v1/chat/completions"), "terror-fixture"),
            "terror-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(model.Calls == 2 && model.CheckedBranches && context.TemplateVersion == "hunter-v20/turn" &&
            advice.Actions.Count == 0 && !advice.VerifiedLethal && advice.Risks.Length > 0 && JsonSerializer.Serialize(frame) == before,
            "The model promoted a favorable terror branch into executable actions or verified lethal.");
        pass("fake HTTP receives correlated terror branches but cannot promote a chosen outcome into executable verified advice");
    }

    private static SnapshotCoach Coach(GameSnapshot snapshot, CardCatalog catalog) => new(snapshot, CompactAdviceContextBuilder.Build(snapshot, Build), catalog, Build);
    private static Task<JsonNode> Line(SnapshotCoach coach, params JsonObject[] actions) => coach.ExecuteAsync("evaluate_line", new JsonObject { ["actions"] = new JsonArray(actions.Cast<JsonNode>().ToArray()) }, default);
    private static JsonObject EndTurn() => new() { ["kind"] = "end_turn", ["source_entity_id"] = 2 };
    private static JsonNode Preview(JsonNode line) => line["Result"]!["TerrorEndTurnPreviews"]!.AsArray().Single()!;
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    private static JsonNode BranchEntity(JsonNode branch, int id) => branch["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    private static GameSnapshot WithTags(GameSnapshot snapshot, int id, params (string Key, string Value)[] values)
    {
        var entities = snapshot.Entities.ToDictionary(); var tags = entities[id].Tags.ToDictionary();
        foreach (var (key, value) in values) tags[key] = value;
        entities[id] = entities[id] with { Tags = tags.ToFrozenDictionary() }; return snapshot with { Entities = entities.ToFrozenDictionary() };
    }
    private static PowerLogStateEngine Start(bool handTerror = false, bool secondTerror = false, string secondCard = "CORE_CS2_231", bool knownContext = false, string ownPower = "HERO_05bp")
    {
        var engine = new PowerLogStateEngine(1, "terror-end-turn-integration");
        Feed([$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=FIRST_PLAYER value=1", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21"]);
        Character(11, "HERO_05", 1, "HERO", 0, 30); Feed(["tag=DAMAGE value=10"]);
        Character(21, "HERO_08", 2, "HERO", 0, 30);
        Character(12, ownPower, 1, "HERO_POWER", 0, 0);
        Character(22, "HERO_08bp", 2, "HERO_POWER", 0, 0);
        Character(30, "CORE_TTN_866", 1, "MINION", 4, 5, handTerror ? "HAND" : "PLAY"); Feed(["tag=LIFESTEAL value=1", "tag=COST value=1"]);
        if (secondTerror) { Character(31, "CORE_TTN_866", 1, "MINION", 4, 5); Feed(["tag=LIFESTEAL value=1"]); }
        Character(40, "CS2_172", 2, "MINION", 5, 5);
        Character(41, secondCard, 2, "MINION", 1, 4);
        if (knownContext)
            Feed(["FULL_ENTITY - Creating ID=60 CardID=CORE_EX1_610", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1",
                "FULL_ENTITY - Creating ID=61 CardID=TLC_EVENT_400", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SIDE_QUEST value=1", "tag=QUEST_PROGRESS value=1", "tag=QUEST_PROGRESS_TOTAL value=3",
                "FULL_ENTITY - Creating ID=62 CardID=EDR_850pe1", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30", "tag=TAG_SCRIPT_DATA_NUM_1 value=2",
                "FULL_ENTITY - Creating ID=70 CardID=CS2_106", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=WEAPON", "tag=ATK value=3", "tag=HEALTH value=2", "tag=DAMAGE value=0"]);
        Feed([$"FULL_ENTITY - Creating ID=90 CardID={PrivateProbe}", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=99", "tag=HEALTH value=99", "tag=DAMAGE value=0",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
        void Feed(IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
        void Character(int id, string card, int owner, string type, int attack, int health, string zone = "PLAY") => Feed([
            $"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}", $"tag=CARDTYPE value={type}", $"tag=ATK value={attack}", $"tag=HEALTH value={health}",
            "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0", "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1"]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<JsonNode>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class BranchModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedBranches { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains(PrivateProbe, StringComparison.Ordinal), "Private opposing hand reached the model.");
            var body = JsonNode.Parse(text)!; object message;
            if (Calls == 1)
                message = new { role = "assistant", tool_calls = new[] { new { id = "terror-end-turn", type = "function", function = new { name = "evaluate_line", arguments = "{\"actions\":[{\"kind\":\"end_turn\",\"source_entity_id\":2}]}" } } } };
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var line = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(Preview(line)["Branches"]!.AsArray().Count == 2 && Preview(line)["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
                    "Model did not receive the complete correlated conditional preview.");
                CheckedBranches = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "按最有利顺序获得恢复并结束回合。", reasons = new[] { "故意把有利分支当成实际结算" },
                    risks = Array.Empty<string>(), verified_lethal = true, actions = new[] { new { kind = "end_turn", source_entity_id = 2 } } }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
