using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class HunterRandomEffectIntegration
{
    private const string Build = "255790";
    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        var definitions = fixture.RootElement.GetProperty("cards").EnumerateArray().ToDictionary(card => card.GetProperty("id").GetString()!, card => card.Clone());
        var catalog = new CardCatalog(Path.Combine(root, "hunter-random-effects", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definitions.Values))))
            await catalog.ImportAsync(input, Build, source: "fixture://hunter-random-effects");
        var engine = Start();
        Feed(engine, Card(definitions, 30, "CORE_UNG_912", "HAND"));
        Feed(engine, ["TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var macaw = engine.Snapshot(); var original = JsonSerializer.Serialize(macaw); var macawCoach = Coach(macaw, catalog);
        var generated = await macawCoach.ExecuteAsync("evaluate_generation", new() { ["source_entity_id"] = 30 }, default);
        var line = await macawCoach.ExecuteAsync("evaluate_line", new() { ["actions"] = new JsonArray(
            new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 }, new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) }, default);
        Check(generated["ResolvesEffects"]?.GetValue<bool>() == true && Exact(generated["RequestedCards"], 1) && Exact(generated["CardsAddedToHand"], 1) &&
            generated["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && line["Verification"]?.ToString() == "Unsupported" &&
            line["AppliedActions"]?.GetValue<int>() == 1 && line["Result"]?["GenerationBoundaryReached"]?.GetValue<bool>() == true &&
            line["Result"]?["GenerationPreviews"]?.AsArray().Count == 1 && original == JsonSerializer.Serialize(macaw),
            "Parser-to-host Macaw generation created a usable card or ignored the pending boundary.");
        pass("parsed Macaw source reaches direct generated-card counts and an action boundary without creating a new hand entity");

        var wolfEngine = Start();
        Feed(wolfEngine, Card(definitions, 12, "EDR_850p", "PLAY"));
        Feed(wolfEngine, Card(definitions, 30, "EDR_227", "HAND"));
        Feed(wolfEngine, Card(definitions, 31, "EDR_480", "HAND"));
        Feed(wolfEngine, ["TAG_CHANGE Entity=12 tag=TAG_SCRIPT_DATA_NUM_1 value=2", "TAG_CHANGE Entity=12 tag=TAG_SCRIPT_DATA_NUM_2 value=2",
            "TAG_CHANGE Entity=12 tag=EXHAUSTED value=0", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=2", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var wolfFrame = wolfEngine.Snapshot(); var wolfBefore = JsonSerializer.Serialize(wolfFrame); var wolfCoach = Coach(wolfFrame, catalog);
        var wolf = await wolfCoach.ExecuteAsync("evaluate_wolf_blessing", new() { ["source_entity_id"] = 12 }, default);
        Check(wolf["ResolvesEffects"]?.GetValue<bool>() == true && wolf["IsCandidateSetComplete"]?.GetValue<bool>() == true && wolf["Branches"]!.AsArray().Count == 2 &&
            wolf["CanChooseBranch"]?.GetValue<bool>() == false && wolf["HasAuthoritativeNextState"]?.GetValue<bool>() == false && wolfBefore == JsonSerializer.Serialize(wolfFrame),
            "Parsed Wolf blessing did not preserve the mutually exclusive branch boundary.");
        pass("parsed Wolf parameters and two observed Beast hands reach distinct direct buff and discount branches");

        foreach (var (name, arguments) in new[]
        {
            ("evaluate_generation", new JsonObject { ["source_entity_id"] = 30, ["card_id"] = "EDR_480" }),
            ("evaluate_wolf_blessing", new JsonObject { ["source_entity_id"] = 12, ["target_entity_id"] = 30 }),
            ("evaluate_wolf_blessing", new JsonObject { ["source_entity_id"] = 12, ["level"] = 99 })
        })
        {
            try { await wolfCoach.ExecuteAsync(name, arguments, default); throw new Exception("Forged random parameters reached the tool."); }
            catch (ArgumentException) { }
        }
        foreach (var rejected in new[] { wolfFrame with { IsSpectating = true }, wolfFrame with { Quality = wolfFrame.Quality with { HasLocalPerspective = false } } })
        {
            var hidden = await Coach(rejected, catalog).ExecuteAsync("evaluate_wolf_blessing", new() { ["source_entity_id"] = 12 }, default);
            Check(hidden["Branches"]!.AsArray().Count == 0 && hidden["SourceCardId"] is null, "Rejected private Wolf preview disclosed hand identities.");
        }
        pass("host schemas reject forged random targets identities and levels while spectator and unknown perspectives expose no private branches");

        var context = CompactAdviceContextBuilder.Create(wolfFrame, Build, AdviceTaskKind.Turn);
        var observations = new List<CoachToolObservation>();
        var modelCoach = new SnapshotCoach(wolfFrame, context.Snapshot, catalog, Build, toolCompleted: observations.Add, taskKind: AdviceTaskKind.Turn);
        using var model = new RandomOutcomeModel(); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://random-fixture.invalid/v1/chat/completions"), "random-fixture"),
            "random-fixture-unused-key", context, modelCoach.ExecuteAsync, () => true, capabilities: modelCoach.Capabilities());
        Check(model.Calls == 2 && advice.Actions.Count == 0 && !advice.VerifiedLethal && advice.Risks.Length > 0 &&
            context.TemplateVersion == "hunter-v19/turn" && observations.Single() is { Name: "evaluate_wolf_blessing", Success: true, ResultHash.Length: 64 },
            "A model's selected Wolf discount and following play survived host action validation.");
        pass("a fake model cannot turn a displayed Wolf branch into an executable followup or verified lethal");
    }

    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog) => new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build);
    private static bool Exact(JsonNode? range, long value) => range?["Minimum"]?.GetValue<long>() == value && range?["Maximum"]?.GetValue<long>() == value;
    private static PowerLogStateEngine Start()
    {
        var engine = new PowerLogStateEngine(1, "random-effect-" + Guid.NewGuid().ToString("N"));
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME",
            "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=HERO_POWER_ENTITY value=12",
            "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=MAXHANDSIZE value=10", "tag=NUM_CARDS_IN_HAND value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Hero(11, "HERO_05", 1), .. Hero(21, "HERO_08", 2),
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION",
            "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static string[] Hero(int id, string card, int owner) => [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}",
        "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0"];
    private static IEnumerable<string> Card(IReadOnlyDictionary<string, JsonElement> definitions, int id, string cardId, string zone)
    {
        var card = definitions[cardId];
        yield return $"FULL_ENTITY - Creating ID={id} CardID={cardId}";
        yield return "tag=CONTROLLER value=1"; yield return $"tag=ZONE value={zone}"; yield return $"tag=CARDTYPE value={card.GetProperty("type").GetString()}";
        foreach (var (property, tag) in new[] { ("cost", "COST"), ("attack", "ATK"), ("health", "HEALTH") })
            if (card.TryGetProperty(property, out var value)) yield return $"tag={tag} value={value.GetInt32()}";
        if (card.TryGetProperty("race", out var race)) yield return $"tag=CARDRACE value={race.GetString()}";
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class RandomOutcomeModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); var body = JsonNode.Parse(text)!; object message;
            if (Calls == 1)
            {
                Check(text.Contains("evaluate_wolf_blessing", StringComparison.Ordinal) && text.Contains("不能指定随机命中", StringComparison.Ordinal), "The model did not receive the random-branch policy or tool.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "wolf-branches", type = "function", function = new { name = "evaluate_wolf_blessing", arguments = "{\"source_entity_id\":12}" } } } };
            }
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(result["Branches"]!.AsArray().Count == 2 && result["CanChooseBranch"]?.GetValue<bool>() == false, "The model did not receive the mutually exclusive branches.");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "假定命中迅猛龙后继续出牌并宣称斩杀。", reasons = new[] { "故意指定随机结果" }, risks = Array.Empty<string>(),
                    actions = new object[] { new { kind = "hero_power", source_entity_id = 12 }, new { kind = "play", source_entity_id = 30 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
