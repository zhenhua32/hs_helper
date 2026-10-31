using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class AssembledEffectIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_ASSEMBLED_EFFECT_PROBE";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        if (workspace is null) throw new InvalidOperationException("Pinned assembly fixtures not found.");
        var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var path in new[] { Path.Combine("HsHelper.Cards.Tests", "Fixtures", "assembled-card-text-255790.json"),
            Path.Combine("HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json") })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.FullName, "tests", path)));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Unpinned assembly fixture.");
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
                definitions[raw.GetProperty("id").GetString()!] = raw.Clone();
        }
        var catalog = await Catalog(root, "complete", definitions.Values);
        var donorDbf = definitions["CORE_REV_308"].GetProperty("dbfId").GetInt32();
        var baseText = definitions["FIR_953"].GetProperty("text").GetString()!;
        var rebuiltText = baseText.Split('&')[0].Trim() + " " + definitions["CORE_REV_308"].GetProperty("text").GetString()!.Split('&')[1].Trim();

        var engine = MainEngine(definitions, donorDbf, custom: true);
        var frame = engine.Snapshot();
        var before = JsonSerializer.Serialize(frame);
        var observations = new List<CoachToolObservation>();
        var coach = Coach(frame, catalog, observations.Add);
        var custom = await Effect(coach, 30);
        var native = await Effect(coach, 31);
        Check(frame.Entities[30].GetIntTag("CRAFTABLE_FIRST_HALF_DBID") == 0 &&
            frame.Entities[30].GetIntTag("CRAFTABLE_SECOND_HALF_DBID") == donorDbf &&
            frame.Entities.Values.All(entity => entity.CardId != "CORE_REV_308") &&
            custom["effect"]?["is_resolved"]?.GetValue<bool>() == true && custom["effect"]?["is_offer"]?.GetValue<bool>() == false &&
            custom["effect"]?["text"]?.ToString() == rebuiltText && custom["effect"]?["effect_card_id"]?.ToString() == "CORE_REV_308" &&
            custom["can_evaluate_followup_actions"]?.GetValue<bool>() == false &&
            native["effect"]?["is_resolved"]?.GetValue<bool>() == true && native["effect"]?["text"]?.ToString() == baseText &&
            (await catalog.GetByIdsAsync(["FIR_953"], Build)).Single().Text == baseText &&
            observations.Count == 2 && observations.All(item => item is { Name: "get_entity_effect", Success: true, ResultHash.Length: 64 }) &&
            observations[0].ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(custom.ToJsonString()))) &&
            before == JsonSerializer.Serialize(frame),
            "Parser/DBF lazy lookup lost per-entity text, modified the shared native definition or omitted an auditable result hash.");
        pass("parsed assembled half DBFs lazily reconstruct one entity while a same-card native entity and the original snapshot remain unchanged");

        var privacyEngine = MainEngine(definitions, donorDbf, custom: true);
        Feed(privacyEngine, [$"FULL_ENTITY - Creating ID=90 CardID={Probe}", "tag=CONTROLLER value=2", "tag=ZONE value=HAND",
            "tag=4572 value=0", $"tag=4573 value={donorDbf}"]);
        Feed(privacyEngine, Card(definitions, 50, "FIR_953", "PLAY", 2));
        Feed(privacyEngine, ["TAG_CHANGE Entity=50 tag=4572 value=0", $"TAG_CHANGE Entity=50 tag=4573 value={donorDbf}",
            "PowerProcessor.EndCurrentTaskList() - complete"]);
        var privateFrame = privacyEngine.Snapshot();
        var hidden = await Effect(Coach(privateFrame, catalog), 90);
        var watchingCoach = Coach(privateFrame with { IsSpectating = true }, catalog);
        var watchingHand = await Effect(watchingCoach, 30);
        var publicPlay = await Effect(watchingCoach, 50);
        Check(privateFrame.Entities[90].CardId is null && hidden["error"] is not null && watchingHand["error"] is not null &&
            hidden.AsObject().Count == 1 && watchingHand.AsObject().Count == 1 &&
            !hidden.ToJsonString().Contains(Probe, StringComparison.Ordinal) && !watchingHand.ToJsonString().Contains("FIR_953", StringComparison.Ordinal) &&
            publicPlay["effect"]?["is_resolved"]?.GetValue<bool>() == true && publicPlay["effect"]?["text"]?.ToString() == rebuiltText,
            "The entity-effect tool exposed a hidden hand or denied publicly visible assembled text in spectator mode.");
        pass("entity-effect lookup rejects hidden opposing and spectator hand identities while allowing public PLAY text");

        var baseOnly = await Catalog(root, "missing-donor", [definitions["FIR_953"]]);
        var missing = await Effect(Coach(frame, baseOnly), 30);
        var unknownBuild = await Effect(Coach(frame with { ClientBuild = null }, catalog), 30);
        Check(missing["effect"]?["is_resolved"]?.GetValue<bool>() == false && missing["effect"]?["text"] is null &&
            missing["effect"]?["unknowns"]!.AsArray().Count > 0 &&
            unknownBuild["effect"]?["is_resolved"]?.GetValue<bool>() == false && unknownBuild["effect"]?["text"] is null,
            "Missing donor/build evidence fell back to the native base text.");
        try
        {
            _ = Coach(frame with { ClientBuild = "255791" }, catalog);
            throw new InvalidOperationException("Mismatched snapshot/catalog versions were accepted.");
        }
        catch (ArgumentException) { }
        pass("missing donor data unknown client build and mismatched snapshot versions never fall back to native text");

        var halfEngine = Start("offered-halves");
        Feed(halfEngine, Card(definitions, 30, "BE_044", "PLAY"));
        Feed(halfEngine, Card(definitions, 80, "FIR_953", "HAND"));
        Feed(halfEngine, ["TAG_CHANGE Entity=80 tag=CREATOR value=30", "TAG_CHANGE Entity=80 tag=COST value=4",
            "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1"]);
        foreach (var (id, card, first, second) in new[] { (60, "BE_349", 0, 1), (61, "BE_349", 1, 0),
            (62, "CORE_BAR_878", 0, 1), (63, "CORE_BAR_878", 1, 0) })
        {
            Feed(halfEngine, Card(definitions, id, card, "SETASIDE"));
            Feed(halfEngine, [$"TAG_CHANGE Entity={id} tag=4572 value={first}", $"TAG_CHANGE Entity={id} tag=4573 value={second}",
                $"TAG_CHANGE Entity={id} tag=4694 value=80", $"TAG_CHANGE Entity={id} tag=4957 value=80"]);
        }
        Feed(halfEngine, Offer());
        var halfFrame = halfEngine.Snapshot();
        var halfBefore = JsonSerializer.Serialize(halfFrame);
        var halfCoach = Coach(halfFrame, catalog);
        var trigger = await Effect(halfCoach, 60);
        var effect = await Effect(halfCoach, 61);
        var selected = await halfCoach.ExecuteAsync("evaluate_choice",
            new() { ["choice_id"] = 7, ["selected_entity_ids"] = new JsonArray(60) }, default);
        Check(trigger["effect"]?["is_offer"]?.GetValue<bool>() == true && trigger["effect"]?["is_resolved"]?.GetValue<bool>() == false &&
            trigger["effect"]?["text"] is null && trigger["effect"]?["offered_half"]?.ToString() == "Trigger" &&
            trigger["effect"]?["offered_half_text"]?.ToString() == definitions["BE_349"].GetProperty("text").GetString()!.Split('&')[0].Trim() &&
            effect["effect"]?["offered_half"]?.ToString() == "Effect" &&
            selected["Crafting"]?["Stage"]?.ToString() == "HalfSelection" && selected["Crafting"]?["AddedToHand"]?.GetValue<bool>() == false &&
            selected["Crafting"]?["OfferedHalf"]?.ToString() == "Trigger" &&
            selected["Crafting"]?["PrimaryEntityId"] is null && selected["Crafting"]?["ResultingCost"] is null &&
            selected["HandCountAfter"]?.GetValue<int>() == 1 && selected["CardOutcomes"]!.AsArray().Count == 0 &&
            halfBefore == JsonSerializer.Serialize(halfFrame), "Parsed half buttons acquired a new card or inferred a primary from cost/creator/unreviewed links.");
        pass("four parsed Assemble buttons expose their exact trigger or effect halves without adding a card or guessing a primary minion");

        var nativeEngine = MainEngine(definitions, donorDbf, custom: false);
        var nativeCoach = Coach(nativeEngine.Snapshot(), catalog);
        var nativeLine = await nativeCoach.ExecuteAsync("evaluate_line", LineActions(), default);
        var blocked = await coach.ExecuteAsync("evaluate_line", LineActions(), default);
        Check(nativeLine["Verification"]?.ToString() == "Verified" && nativeLine["AppliedActions"]?.GetValue<int>() == 2 &&
            nativeLine["IsLethal"]?.GetValue<bool>() == true &&
            blocked["Verification"]?.ToString() == "Unsupported" && blocked["AppliedActions"]?.GetValue<int>() == 0 &&
            blocked["UnsupportedMechanics"]!.AsArray().Any(item => item?.ToString() == "CUSTOM_ASSEMBLED_SOURCE") &&
            !blocked["UnsupportedMechanics"]!.AsArray().Any(item => item?.ToString() == "UNKNOWN_CARD_EFFECT") &&
            blocked["IsLethal"]?.GetValue<bool>() == false,
            "The native positive control was not covered, or assembled FIR953 reused its native attack-after-damage effect.");
        pass("a covered native FIR953 lethal control succeeds while the assembled same-card source is stopped before any action");

        var modelObservations = new List<CoachToolObservation>();
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Turn);
        var modelCoach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, toolCompleted: modelObservations.Add,
            taskKind: AdviceTaskKind.Turn);
        using var model = new NativeFallbackModel(rebuiltText);
        using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://assembled-fixture.invalid/v1/chat/completions"), "assembled-fixture"),
            "assembled-fixture-unused-key", context, modelCoach.ExecuteAsync, () => true, capabilities: modelCoach.Capabilities());
        Check(model.Calls == 2 && model.CheckedEffect && advice.Actions.Count == 0 && !advice.VerifiedLethal && advice.Risks.Length > 0 &&
            modelObservations.Any(item => item is { Name: "get_entity_effect", Success: true, ResultHash.Length: 64 }),
            "A model's native play/attack/lethal proposal survived host validation after receiving the assembled text.");
        pass("fake model entity-effect lookup cannot make base-native play attack or lethal proposals executable for an assembled source");
    }

    private static JsonObject LineActions() => new() { ["actions"] = new JsonArray(
        new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 },
        new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 40 }) };
    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog, Action<CoachToolObservation>? completed = null) =>
        new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build, toolCompleted: completed);
    private static Task<JsonNode> Effect(SnapshotCoach coach, int entity) =>
        coach.ExecuteAsync("get_entity_effect", new() { ["entity_id"] = entity }, default);
    private static async Task<CardCatalog> Catalog(string root, string name, IEnumerable<JsonElement> cards)
    {
        var catalog = new CardCatalog(Path.Combine(root, "assembled-effects", name, "cards.db"));
        await catalog.InitializeAsync();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cards.ToArray())));
        await catalog.ImportAsync(input, Build, source: "fixture://assembled-effect-" + name);
        return catalog;
    }
    private static PowerLogStateEngine MainEngine(IReadOnlyDictionary<string, JsonElement> cards, int donor, bool custom)
    {
        var engine = Start(custom ? "custom" : "native");
        Feed(engine, Card(cards, 30, "FIR_953", "HAND"));
        Feed(engine, ["TAG_CHANGE Entity=30 tag=4572 value=0", $"TAG_CHANGE Entity=30 tag=4573 value={(custom ? donor : 0)}"]);
        Feed(engine, Card(cards, 31, "FIR_953", "HAND"));
        Feed(engine, Card(cards, 40, "CORE_UNG_912", "PLAY", 2));
        Feed(engine, ["TAG_CHANGE Entity=40 tag=ATK value=0", "TAG_CHANGE Entity=40 tag=HEALTH value=1",
            "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=2", "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static PowerLogStateEngine Start(string name)
    {
        var engine = new PowerLogStateEngine(1, "assembled-effects-" + name);
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED",
            "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=MAXHANDSIZE value=10", "tag=NUM_CARDS_IN_HAND value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Hero(11, "HERO_05", 1, 30), .. Hero(21, "HERO_08", 2, 5),
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION",
            "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static string[] Hero(int id, string card, int owner, int health) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO",
            $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0"];
    private static IEnumerable<string> Card(IReadOnlyDictionary<string, JsonElement> definitions, int id, string cardId, string zone, int owner = 1)
    {
        var card = definitions[cardId];
        yield return $"FULL_ENTITY - Creating ID={id} CardID={cardId}";
        yield return $"tag=CONTROLLER value={owner}";
        yield return $"tag=ZONE value={zone}";
        yield return $"tag=CARDTYPE value={card.GetProperty("type").GetString()}";
        foreach (var (property, tag) in new[] { ("cost", "COST"), ("attack", "ATK"), ("health", "HEALTH"), ("durability", "DURABILITY") })
            if (card.TryGetProperty(property, out var value)) yield return $"tag={tag} value={value.GetInt32()}";
        yield return "tag=DAMAGE value=0";
        yield return "tag=NUM_ATTACKS_THIS_TURN value=0";
        yield return "tag=NUM_TURNS_IN_PLAY value=1";
        if (card.TryGetProperty("race", out var race) && race.GetString() == "BEAST") yield return "tag=CARDRACE value=20";
        if (card.TryGetProperty("mechanics", out var mechanics) && mechanics.EnumerateArray().Any(item => item.GetString() == "RUSH")) yield return "tag=RUSH value=1";
    }
    private static IEnumerable<string> Offer()
    {
        yield return "GameState.DebugPrintEntityChoices() - id=7 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 player=1]";
        foreach (var (index, id, card) in new[] { (0, 60, "BE_349"), (1, 61, "BE_349"), (2, 62, "CORE_BAR_878"), (3, 63, "CORE_BAR_878") })
            yield return $"GameState.DebugPrintEntityChoices() - Entities[{index}]=[entityName=Anonymous id={id} zone=SETASIDE cardId={card} player=1]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class NativeFallbackModel(string expectedText) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedEffect { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var requestText = await request.Content!.ReadAsStringAsync(token);
            Check(!requestText.Contains(Probe, StringComparison.Ordinal), "Private assembled data entered a model request.");
            var body = JsonNode.Parse(requestText)!;
            object message;
            if (Calls == 1)
            {
                Check(requestText.Contains("get_entity_effect", StringComparison.Ordinal), "Entity-effect tool was not offered to the model.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "assembled-effect", type = "function",
                    function = new { name = "get_entity_effect", arguments = "{\"entity_id\":30}" } } } };
            }
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(result["effect"]?["is_resolved"]?.GetValue<bool>() == true && result["effect"]?["text"]?.ToString() == expectedText &&
                    result["can_evaluate_followup_actions"]?.GetValue<bool>() == false, "Model did not receive the entity's assembled effect.");
                CheckedEffect = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "按原版熔岩猎犬出牌并攻击，宣称原生溅射斩杀。",
                    reasons = new[] { "刻意提交与工具已知拼装文字不符的原生动作" }, risks = Array.Empty<string>(),
                    actions = new object[] { new { kind = "play", source_entity_id = 30 },
                        new { kind = "attack", source_entity_id = 30, target_entity_id = 40 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
