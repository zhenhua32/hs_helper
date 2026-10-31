using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class HunterPlayTriggerIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_PLAY_TRIGGER_HAND";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var definitions = new JsonArray();
        foreach (var name in new[] { "beast-hunter-255790.json", "craft-choice-255790.json" })
        {
            using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name)));
            foreach (var card in input.RootElement.GetProperty("cards").EnumerateArray()) definitions.Add(JsonNode.Parse(card.GetRawText()));
        }
        var catalog = new CardCatalog(Path.Combine(root, "hunter-play-triggers", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(definitions.ToJsonString())))
            await catalog.ImportAsync(input, Build, source: "fixture://hunter-play-triggers");
        var engine = new PowerLogStateEngine(1, "play-trigger-fixture");
        foreach (var line in Lines()) engine.ProcessLine(line);
        var frame = engine.Snapshot(); var original = JsonSerializer.Serialize(frame);
        var observations = new List<CoachToolObservation>();
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Turn);
        var coach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, toolCompleted: observations.Add, taskKind: AdviceTaskKind.Turn);
        var result = await coach.ExecuteAsync("evaluate_play_triggers", new() { ["source_entity_id"] = 30 }, default);
        var quest = result["QuestProgress"]!.AsArray().Single()!;
        Check(quest["QuestEntityId"]!.GetValue<int>() == 50 && quest["CurrentProgress"]!.GetValue<int>() == 2 &&
            quest["Increment"]!.GetValue<int>() == 1 && quest["ResultingProgress"]!.GetValue<int>() == 3 &&
            quest["RequiresRewardChoice"]!.GetValue<bool>() && result["Raptor"]!["Triggers"]!.AsArray()
                .Any(trigger => trigger!["SourceEntityId"]!.GetValue<int>() == 40 && trigger["TargetEntityId"]!.GetValue<int>() == 30) &&
            result["CanEvaluateFollowupActions"]!.GetValue<bool>() == false && result["HasAuthoritativeNextState"]!.GetValue<bool>() == false &&
            !result.ToJsonString().Contains(Probe, StringComparison.Ordinal) && JsonSerializer.Serialize(frame) == original,
            "Parsed own hand, active quest or Raptor did not reach the bound direct preview, or the preview leaked or mutated private state.");
        pass("parsed own hand automatically binds active quest progress and Raptor source without creating a reward or authoritative state");

        var definition = SnapshotCoach.Definitions.Single(tool => tool.Name == "evaluate_play_triggers");
        Check(definition.Parameters["properties"]!.AsObject().Select(pair => pair.Key).SequenceEqual(["source_entity_id"]) &&
            definition.Parameters["additionalProperties"]!.GetValue<bool>() == false,
            "Play-trigger schema exposed model-defined progress, sources or random results.");
        foreach (var name in new[] { "quest_entity_id", "progress", "total", "target_entity_id", "trigger_source_entity_id", "random_effect", "bonus_effect" })
        {
            var arguments = new JsonObject { ["source_entity_id"] = 30, [name] = 99 };
            Check(!definition.ValidateArguments(arguments), "The schema accepted an extra play-trigger parameter.");
            await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_play_triggers", arguments, default));
        }
        foreach (var arguments in new[] { new JsonObject(), new JsonObject { ["source_entity_id"] = "30" }, new JsonObject { ["source_entity_id"] = 0 } })
            await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_play_triggers", arguments, default));
        pass("play-trigger schema and host reject fabricated progress random effects targets and trigger sources");

        foreach (var hidden in new[]
        {
            frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
            frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { IsStable = false } },
            frame with { Quality = frame.Quality with { OpenBlockCount = 1 } }, frame with { Phase = GamePhase.Choice }
        })
        {
            var rejected = await new SnapshotCoach(hidden, new(), catalog, Build).ExecuteAsync("evaluate_play_triggers", new() { ["source_entity_id"] = 30 }, default);
            Check(rejected["error"] is not null && rejected["SourceCardId"] is null && rejected["QuestProgress"] is null && rejected["Raptor"] is null,
                "A private or incomplete play-trigger request exported identities or trigger candidates.");
        }
        foreach (var id in new[] { 90, 91, 40, 999 })
        {
            var rejected = await coach.ExecuteAsync("evaluate_play_triggers", new() { ["source_entity_id"] = id }, default);
            Check(rejected["error"] is not null && !rejected.ToJsonString().Contains(Probe, StringComparison.Ordinal), "A non-own-HAND source reached the trigger preview.");
        }
        var unknownEntities = frame.Entities.ToDictionary(); unknownEntities[30] = unknownEntities[30] with { CardId = null };
        var unknown = frame with { Entities = unknownEntities.ToFrozenDictionary() };
        var unknownResult = await new SnapshotCoach(unknown, new(), catalog, Build).ExecuteAsync("evaluate_play_triggers", new() { ["source_entity_id"] = 30 }, default);
        Check(unknownResult["error"] is not null && unknownResult["Raptor"] is null, "An unidentified hand exposed attached trigger evidence.");
        var planning = new SnapshotCoach(frame, new(), catalog, Build, taskKind: AdviceTaskKind.DeckPlan);
        Check(planning.Capabilities().Tools.Count == 0, "DeckPlan exposed live trigger tools.");
        await Throws<ArgumentException>(() => planning.ExecuteAsync("evaluate_play_triggers", new() { ["source_entity_id"] = 30 }, default));
        pass("play-trigger privacy blocks spectator uncertain opposing and unidentified sources and the tool is unavailable to DeckPlan");

        using var model = new TriggerModel(); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://play-trigger-fixture.invalid/v1/chat/completions"), "trigger-fixture"),
            "trigger-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(model.Calls == 2 && advice.Actions.Count == 0 && !advice.VerifiedLethal && advice.Risks.Length > 0 &&
            context.TemplateVersion == "hunter-v14/turn" && AdviceTaskTemplates.Get(AdviceTaskKind.Choice).Version == "hunter-v14/choice" &&
            observations.Last() is { Name: "evaluate_play_triggers", Success: true, ResultHash.Length: 64, RequestId: not null } &&
            observations.Last().EventSequence == frame.EventSequence,
            "Trigger preview allowed an invented following action or lost its request-bound v9 policy.");
        pass("fake model receives v14 direct-trigger evidence while host action validation rejects reward or random followups and lethal claims");
    }

    private static IEnumerable<string> Lines()
    {
        yield return $"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED";
        foreach (var line in new[] { "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=NUM_CARDS_IN_HAND value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21" }) yield return line;
        foreach (var line in Card(11, "HERO_05", 1, "PLAY", "HERO", 30, 0)) yield return line;
        foreach (var line in Card(21, "HERO_08", 2, "PLAY", "HERO", 30, 0)) yield return line;
        foreach (var line in Card(30, "EDR_227", 1, "HAND", "MINION", 2, 5, "BEAST")) yield return line;
        yield return "tag=COST value=4"; yield return "tag=RUSH value=1"; yield return "tag=DEATHRATTLE value=1";
        foreach (var line in Card(40, "EDR_849", 1, "PLAY", "MINION", 1, 2, "BEAST")) yield return line;
        yield return "tag=SILENCED value=0"; yield return "tag=DORMANT value=0";
        foreach (var line in Card(50, "TLC_EVENT_400", 1, "SECRET", "SPELL", 0, 0)) yield return line;
        yield return "tag=SIDE_QUEST value=1"; yield return "tag=QUEST_PROGRESS value=2"; yield return "tag=QUEST_PROGRESS_TOTAL value=3";
        foreach (var line in Card(90, Probe, 2, "HAND", "MINION", 1, 1, "BEAST")) yield return line;
        foreach (var line in Card(91, "TLC_EVENT_400", 2, "SECRET", "SPELL", 0, 0)) yield return line;
        yield return "tag=QUEST_PROGRESS value=2"; yield return "tag=QUEST_PROGRESS_TOTAL value=3";
        yield return "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION";
        yield return "PowerProcessor.EndCurrentTaskList() - complete";
    }
    private static IEnumerable<string> Card(int id, string card, int owner, string zone, string type, int health, int attack, string? race = null)
    {
        yield return $"FULL_ENTITY - Creating ID={id} CardID={card}"; yield return $"tag=CONTROLLER value={owner}";
        yield return $"tag=ZONE value={zone}"; yield return $"tag=CARDTYPE value={type}";
        yield return $"tag=HEALTH value={health}"; yield return "tag=DAMAGE value=0"; yield return $"tag=ATK value={attack}";
        if (type == "HERO") yield return "tag=ARMOR value=0";
        if (race is not null) yield return $"tag=CARDRACE value={race}";
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class TriggerModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); object message;
            if (Calls == 1)
            {
                Check(text.Contains("hunter-v14/turn", StringComparison.Ordinal) && text.Contains("evaluate_play_triggers", StringComparison.Ordinal) &&
                    text.Contains("不能自行给定进度", StringComparison.Ordinal) && text.Contains("DiscoveryBoundaryReached", StringComparison.Ordinal) &&
                    text.Contains("各段不能拼成已发生的局面", StringComparison.Ordinal), "The model did not receive the v14 trigger tool and boundary policy.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "trigger-preview", type = "function", function = new { name = "evaluate_play_triggers", arguments = "{\"source_entity_id\":30}" } } } };
            }
            else
            {
                var body = JsonNode.Parse(text)!;
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(result["QuestProgress"]![0]!["RequiresRewardChoice"]!.GetValue<bool>() && result["CanEvaluateFollowupActions"]!.GetValue<bool>() == false,
                    "The model did not receive the pending reward boundary.");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "假定获得有利随机词条和任务成品后继续结束回合。", reasons = new[] { "故意越过待结算边界" }, risks = Array.Empty<string>(),
                    actions = new object[] { new { kind = "play", source_entity_id = 30 }, new { kind = "end_turn", source_entity_id = 2 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
