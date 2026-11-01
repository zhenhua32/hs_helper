using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class ChoiceEffectIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_CHOICE_EFFECT_PROBE";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var catalog = new CardCatalog(Path.Combine(root, "choice-effects", "cards.db"));
        await catalog.InitializeAsync();
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText()));
        await catalog.ImportAsync(input, Build, source: "fixture://choice-effect-integration");
        var frame = Frame();
        var before = JsonSerializer.Serialize(frame);
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Choice);
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, context.Snapshot, catalog, Build,
            toolCompleted: observations.Add, taskKind: AdviceTaskKind.Choice);
        var args = new JsonObject { ["choice_id"] = 77, ["selected_entity_ids"] = new JsonArray(80) };
        var result = await coach.ExecuteAsync("evaluate_choice", args, default);
        Check(result["IsLegal"]!.GetValue<bool>() && result["ResolvesEffects"]!.GetValue<bool>() &&
            !result["SelectionOnly"]!.GetValue<bool>() && !result["CanEvaluateFollowupActions"]!.GetValue<bool>(),
            "Choice tool did not distinguish direct effects from selection-only or follow-up simulation.");
        Check(result["CardOutcomes"]![0]!["AddedToHand"]!.GetValue<bool>() &&
            result["CardOutcomes"]![0]!["ResultingCost"] is null && result["HandCountAfter"]!.GetValue<int>() == 2 &&
            result["DirectDeckChange"]!["TotalCardsAfter"]!.GetValue<int>() == 2 &&
            result["DirectDeckChange"]!["KnownRemainingAfter"]!["EDR_227"]!.GetValue<int>() == 2,
            "Tracking tool invented current cost, failed to add one card, or removed unchosen options.");
        Check(!result.ToJsonString().Contains(Probe, StringComparison.Ordinal) && JsonSerializer.Serialize(frame) == before,
            "Choice tool exported hidden identity or mutated its authoritative frame.");
        Check(observations.Single().Name == "evaluate_choice" && observations[0].Success && observations[0].ResultHash?.Length == 64,
            "Choice result did not retain normal tool audit provenance.");
        pass("fixed-build choice tool projects Tracking hand/deck changes without hidden identities or invented current costs");

        foreach (var badArgs in new[]
        {
            new JsonObject { ["choice_id"] = 77, ["selected_entity_ids"] = new JsonArray(80), ["source_card"] = "CORE_DS1_184" },
            new JsonObject { ["choice_id"] = 77, ["selected_entity_ids"] = new JsonArray("80") }
        }) await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_choice", badArgs, default));
        var stale = await coach.ExecuteAsync("evaluate_choice", new() { ["choice_id"] = 78, ["selected_entity_ids"] = new JsonArray(80) }, default);
        Check(stale["Verification"]!.GetValue<string>() == "Invalid" && !stale["ResolvesEffects"]!.GetValue<bool>(),
            "A stale choice ID still resolved card effects.");
        var opposing = frame with { Choice = frame.Choice! with { PlayerId = 2 } };
        var opposingCoach = new SnapshotCoach(opposing, new JsonObject(), catalog, Build, taskKind: AdviceTaskKind.Choice);
        var rejected = await opposingCoach.ExecuteAsync("evaluate_choice", args, default);
        Check(!rejected["ResolvesEffects"]!.GetValue<bool>() && rejected["SourceCardId"] is null &&
            rejected["CardOutcomes"]!.AsArray().Count == 0, "Opponent choice exposed private effect details.");
        pass("choice-effect host rejects invented source parameters, wrong IDs and opposing choices before resolving effects");

        using var handler = new ChoiceModel();
        using var http = new HttpClient(handler);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://choice-fixture.invalid/v1/chat/completions"), "choice-fixture"),
            "choice-fixture-unused-key", context, coach.ExecuteAsync, () => true,
            capabilities: coach.Capabilities());
        Check(handler.Calls == 2 && handler.CheckedEffect && advice.Actions is [{ Kind: "choose", SourceEntityId: 80 }] &&
            !advice.VerifiedLethal && context.TemplateVersion == "hunter-v22/choice",
            "Fake model choice loop lost its scoped template, legal chosen action or direct-effect boundaries.");
        Check(observations.Last().Name == "evaluate_choice" && observations.Last().RequestId is not null &&
            observations.Last().EventSequence == frame.EventSequence, "Model choice effect call lost request/state provenance.");
        pass("fake HTTP choice coach uses direct-effect evidence while host validation keeps subsequent actions and lethal unproven");

        using (var mixedModel = new ChoiceModel("mixed"))
        using (var mixedHttp = new HttpClient(mixedModel))
        {
            var mixed = await new AdviceService(mixedHttp).AnalyzeAsync(new(new Uri("https://choice-fixture.invalid/v1/chat/completions"), "choice-fixture"),
                "choice-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
            Check(mixed.Actions.Count == 0 && !mixed.VerifiedLethal && mixed.Risks.Any(),
                "Direct-choice preview made a mixed choose/play sequence executable.");
        }
        using (var futureModel = new ChoiceModel("play"))
        using (var futureHttp = new HttpClient(futureModel))
            await Throws<InvalidDataException>(() => new AdviceService(futureHttp).AnalyzeAsync(
                new(new Uri("https://choice-fixture.invalid/v1/chat/completions"), "choice-fixture"),
                "choice-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities()));
        pass("choice-effect previews never turn model lethal claims or invented following plays into executable actions");

        var main = frame with { Phase = GamePhase.Main, Choice = null, DeckTracking = null };
        var mainContext = CompactAdviceContextBuilder.Create(main, Build, AdviceTaskKind.Turn);
        var mainCoach = new SnapshotCoach(main, mainContext.Snapshot, catalog, Build, taskKind: AdviceTaskKind.Turn);
        var ended = await mainCoach.ExecuteAsync("evaluate_line", new() { ["actions"] = new JsonArray(
            new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) }, default);
        Check(ended["IsLegal"]!.GetValue<bool>() && ended["Result"]!["TurnEnded"]!.GetValue<bool>(),
            "Host did not route a legal current-player end_turn into local rules.");
        var invalidEnd = await mainCoach.Capabilities().ValidateActions!([new("end_turn", 3)], default);
        Check(invalidEnd.Status == AdviceValidationStatus.Invalid && !invalidEnd.IsLegal,
            "Host validation accepted the opposing player as an end-turn source.");
        using (var model = new EndTurnModel())
        using (var turnHttp = new HttpClient(model))
        {
            var turnAdvice = await new AdviceService(turnHttp).AnalyzeAsync(
                new(new Uri("https://choice-fixture.invalid/v1/chat/completions"), "turn-fixture"), "choice-fixture-unused-key",
                mainContext, mainCoach.ExecuteAsync, () => true, capabilities: mainCoach.Capabilities());
            Check(model.Calls == 2 && turnAdvice.Actions is [{ Kind: "end_turn", SourceEntityId: 2, TargetEntityId: null }] &&
                !turnAdvice.VerifiedLethal, "Model parsing and host validation disagreed about end-turn source fields.");
        }
        pass("end-turn tool and host action validation bind the current player and keep the opponent turn unmodeled");

        var spectator = main with { IsSpectating = true, DeckTracking = frame.DeckTracking };
        var watching = new SnapshotCoach(spectator, CompactAdviceContextBuilder.Build(spectator, Build), catalog, Build, taskKind: AdviceTaskKind.Turn);
        var watchResult = await watching.ExecuteAsync("evaluate_line", new() { ["actions"] = new JsonArray(
            new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) }, default);
        var watchOdds = await watching.ExecuteAsync("calculate_draw_odds", new() { ["card_id"] = "EDR_227", ["draws"] = 1 }, default);
        Check(watchResult["Verification"]!.GetValue<string>() == "Unsupported" &&
            watchResult["Result"]!["Entities"]!.AsArray().All(entity => entity?["Zone"]?.ToString() is not ("HAND" or "DECK" or "SECRET")) &&
            !watchOdds["is_exact"]!.GetValue<bool>() && watchOdds["probability"] is null && !watchResult.ToJsonString().Contains(Probe, StringComparison.Ordinal),
            "A tool call bypassed spectator privacy or used a private deck despite the compact context boundary.");
        pass("spectator tool calls cannot validate actions, reveal private hand entities or calculate private deck odds");
    }

    private static GameSnapshot Frame()
    {
        var entities = new[]
        {
            Entity(2, null, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("RESOURCES", "5"), ("RESOURCES_USED", "0"),
                ("NUM_CARDS_IN_HAND", "1"), ("MAXHANDSIZE", "10"), ("NUM_CARDS_IN_DECK", "3")),
            Entity(3, null, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "20")),
            Entity(10, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("HEALTH", "30"), ("DAMAGE", "0"), ("ARMOR", "0"), ("ATK", "0")),
            Entity(20, "HERO_08", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("HEALTH", "30"), ("DAMAGE", "0"), ("ARMOR", "0"), ("ATK", "0")),
            Entity(70, "CORE_DS1_184", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "GRAVEYARD"), ("CARDTYPE", "SPELL")),
            Entity(80, "EDR_480", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "DECK"), ("CARDTYPE", "MINION"), ("COST", "0")),
            Entity(81, "EDR_227", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "DECK"), ("CARDTYPE", "MINION")),
            Entity(82, null, EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "DECK"), ("CARDTYPE", "MINION")),
            Entity(90, "EDR_800", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "HAND"), ("CARDTYPE", "MINION")),
            Entity(95, Probe, EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "HAND"), ("CARDTYPE", "MINION"))
        };
        return new("choice-effect-fixture", 8, 100, 4, 1, 1, GamePhase.Choice,
            entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        {
            ClientBuild = Build,
            Choice = new(77, 1, "GENERAL", 70, 1, 1, [new(80, "EDR_480", 1, 1), new(81, "EDR_227", 1, 2)], true) { SourceCardId = "CORE_DS1_184" },
            DeckTracking = new("choice-effect-fixture", 8, "choice-deck", Build, 3,
                new Dictionary<string, int> { ["EDR_480"] = 1, ["EDR_227"] = 2 }, 0, true, [])
        };
    }

    private static EntitySnapshot Entity(int id, string? cardId, EntityKind kind, params (string Key, string Value)[] tags) =>
        new(id, cardId, kind, tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private sealed class ChoiceModel(string mode = "choose") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedEffect { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains(Probe, StringComparison.Ordinal), "Hidden identity entered choice model request.");
            var body = JsonNode.Parse(text)!;
            object message;
            if (Calls == 1)
            {
                Check(text.Contains("hunter-v22/choice", StringComparison.Ordinal) && text.Contains("evaluate_choice", StringComparison.Ordinal) &&
                    text.Contains("DiscoveryBoundaryReached", StringComparison.Ordinal) && text.Contains("战吼被抑制不证明独立回溯已移除", StringComparison.Ordinal),
                    "Choice template or registered effect tool was missing from request.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "choice-direct", type = "function",
                    function = new { name = "evaluate_choice", arguments = "{\"choice_id\":77,\"selected_entity_ids\":[80]}" } } } };
            }
            else
            {
                var response = body["messages"]!.AsArray().Single(node => node?["role"]?.ToString() == "tool");
                var effect = JsonNode.Parse(response!["content"]!.GetValue<string>())!;
                Check(effect["ResolvesEffects"]!.GetValue<bool>() && effect["CardOutcomes"]![0]!["ResultingCost"] is null &&
                    !effect["CanEvaluateFollowupActions"]!.GetValue<bool>(), "Model did not receive scoped direct consequences.");
                CheckedEffect = true;
                object[] actions = mode switch
                {
                    "mixed" => [new { kind = "choose", source_entity_id = 80 }, new { kind = "play", source_entity_id = 80 }],
                    "play" => [new { kind = "play", source_entity_id = 80 }],
                    _ => [new { kind = "choose", source_entity_id = 80 }]
                };
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "选择戈德林，直接入手费用仍待结算。",
                    reasons = new[] { "工具只核算所选牌离开牌库与直接入手" }, risks = new[] { "后续费用和触发未知" },
                    actions, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 }
            }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class EndTurnModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var prompt = body["messages"]![0]!["content"]!.GetValue<string>();
            Check(prompt.Contains("end_turn必须携带", StringComparison.Ordinal) && !prompt.Contains("end_turn不携带", StringComparison.Ordinal),
                "Action contract contradicted the current-player end-turn protocol.");
            object message;
            if (Calls == 1)
                message = new { role = "assistant", tool_calls = new[] { new { id = "end-boundary", type = "function",
                    function = new { name = "evaluate_line", arguments = "{\"actions\":[{\"kind\":\"end_turn\",\"source_entity_id\":2}]}" } } } };
            else
            {
                var tool = body["messages"]!.AsArray().Single(node => node?["role"]?.ToString() == "tool");
                var evaluated = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(evaluated["Result"]!["TurnEnded"]!.GetValue<bool>(), "End-turn model did not receive the local boundary result.");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "结束当前回合。",
                    reasons = new[] { "只核对本回合末边界" }, risks = new[] { "对手起手未模拟" },
                    actions = new[] { new { kind = "end_turn", source_entity_id = 2 } } }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
