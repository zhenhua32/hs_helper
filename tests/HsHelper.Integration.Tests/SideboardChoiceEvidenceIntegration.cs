using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Rules;

internal static class SideboardChoiceEvidenceIntegration
{
    private const string Build = "255790";
    private const string Probe = "PRIVATE_SIDEBOARD_INTEGRATION";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        var catalog = new CardCatalog(Path.Combine(root, "sideboard-choice-evidence", "cards.db"));
        await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://sideboard-choice-evidence");
        var cards = (await catalog.GetByIdsAsync(["JAIL_831", "EDR_489", "DINO_421", "EDR_227"], Build)).ToDictionary(card => card.Id);
        var engine = Start();
        Feed(engine, Offer(7, 30, (80, "EDR_489"), (81, "DINO_421"), (82, "EDR_227"))); Choose(engine, 7, 80);
        Feed(engine, Offer(8, 31, (90, "EDR_489"), (91, "DINO_421"))); Choose(engine, 8, 90);
        Feed(engine, Offer(9, 30, (81, "DINO_421"), (82, "EDR_227")));
        var frame = engine.Snapshot(); var before = JsonSerializer.Serialize(frame);
        var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Choice);
        // An unrelated context reference must not silently become a current sideboard balance.
        context.Snapshot["host_context"] = new JsonObject { ["deck"] = new JsonObject { ["sideboards"] = new JsonArray(new JsonObject { ["owner_dbf_id"] = 125998, ["cards"] = new JsonArray(114020, 114020, 114020) }) } };
        var observed = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, toolCompleted: observed.Add, taskKind: AdviceTaskKind.Choice);
        var result = await Evaluate(coach);
        var evidence = result["SideboardEvidence"]!;
        Check(evidence["ResolvesEvidence"]!.GetValue<bool>() && evidence["IsHistoryComplete"]!.GetValue<bool>() &&
            evidence["Sources"]!.AsArray().Select(source => source!["SourceEntityId"]!.GetValue<int>()).SequenceEqual([30, 31]) &&
            evidence["SelectedCounts"]!.AsArray().Single()!["ObservedSelections"]!.GetValue<int>() == 2 &&
            evidence["SelectedCounts"]![0]!["CardId"]!.GetValue<string>() == "EDR_489" &&
            evidence["CurrentOffer"]!["OptionCount"]!.GetValue<int>() == 2 && evidence["CurrentOffer"]!["IsExactOffer"]!.GetValue<bool>() &&
            !evidence["CurrentPoolIsExact"]!.GetValue<bool>() && evidence["InitialReference"] is null &&
            result["CardOutcomes"]!.AsArray().Count == 1 && result["CardOutcomes"]![0]!["GrantedCostReduction"]!.GetValue<int>() == 3 &&
            result["CardOutcomes"]![0]!["ResultingCost"] is null && !result["CanEvaluateFollowupActions"]!.GetValue<bool>() &&
            !result.ToJsonString().Contains(Probe, StringComparison.Ordinal) && JsonSerializer.Serialize(frame) == before,
            "JAIL evaluate_choice lost source-bound historical choices or converted a context reference/current offer into an exact pool or final cost.");
        var evaluated = new LineEvaluator().EvaluateChoice(frame, [81], cards, 9);
        var evidenceOnly = evaluated with { CardOutcomes = [] };
        Check(evidenceOnly.SideboardEvidence?.ResolvesEvidence == true && !evidenceOnly.ResolvesEffects && evidenceOnly.SelectionOnly,
            "Historical sideboard facts alone were classified as an actually resolving card effect.");
        var unknownOption = frame with { Choice = frame.Choice! with { Options = frame.Choice.Options.Select(option => option.EntityId == 81 ? option with { CardId = null } : option).ToArray() } };
        var unknownSelected = await Evaluate(new SnapshotCoach(unknownOption, new(), catalog, Build));
        Check(unknownSelected["SideboardEvidence"]?["ResolvesEvidence"]?.GetValue<bool>() == true &&
            unknownSelected["SideboardEvidence"]?["CurrentOffer"]?["HasCompleteCardIdentities"]?.GetValue<bool>() == false &&
            unknownSelected["CardOutcomes"]!.AsArray().Count == 0 && unknownSelected["ResolvesEffects"]?.GetValue<bool>() == false,
            "Actual JAIL evidence was either lost for an unknown selected identity or promoted into card effects.");
        pass("parsed JAIL choices reach evaluate_choice as actual source groups and current offers without converting facts into effects or remaining pool");

        foreach (var hidden in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
            frame with { Choice = frame.Choice! with { PlayerId = 2 } } })
        {
            var rejected = await Evaluate(new SnapshotCoach(hidden, new(), catalog, Build));
            Check(rejected["SideboardEvidence"] is null && rejected["CardOutcomes"]!.AsArray().Count == 0 &&
                !rejected.ToJsonString().Contains("EDR_489", StringComparison.Ordinal), "Unconfirmed choice disclosed private historical sideboard identities.");
        }
        var transformed = frame.Entities.ToDictionary(); transformed[30] = transformed[30] with { CardId = "EDR_489" };
        var stale = await Evaluate(new SnapshotCoach(frame with { Entities = transformed.ToFrozenDictionary() }, new(), catalog, Build));
        Check(stale["SideboardEvidence"]?["IsCurrentSourceConfirmed"]?.GetValue<bool>() == false &&
            stale["SideboardEvidence"]?["SelectedCounts"]?[0]?["ObservedSelections"]?.GetValue<int>() == 2 &&
            stale["SideboardEvidence"]?["CurrentOffer"] is null && stale["SourceCardId"] is null && stale["CardOutcomes"]!.AsArray().Count == 0 &&
            stale["ResolvesEffects"]?.GetValue<bool>() == false,
            "A transformed current source borrowed its earlier JAIL identity to resolve a current native effect.");
        var conflicting = frame.Entities.ToDictionary();
        conflicting[30] = conflicting[30] with { Tags = conflicting[30].Tags.Append(new KeyValuePair<string, string>("202", "SPELL")).ToFrozenDictionary() };
        var invalidType = await Evaluate(new SnapshotCoach(frame with { Entities = conflicting.ToFrozenDictionary() }, new(), catalog, Build));
        Check(invalidType["SideboardEvidence"]?["IsCurrentSourceConfirmed"]?.GetValue<bool>() == false &&
            invalidType["CardOutcomes"]!.AsArray().Count == 0 && invalidType["ResolvesEffects"]?.GetValue<bool>() == false,
            "Historical JAIL facts repaired an explicit current type-alias contradiction and reused the native Battlecry.");
        foreach (var forged in new[] { new JsonObject { ["choice_id"] = 9, ["selected_entity_ids"] = new JsonArray(81), ["sideboard_remaining"] = 3 },
            new JsonObject { ["choice_id"] = 9, ["selected_entity_ids"] = new JsonArray(81), ["initial_deck"] = new JsonObject() } })
            await Throws<ArgumentException>(() => coach.ExecuteAsync("evaluate_choice", forged, default));
        pass("JAIL evidence remains private and current transformed sources cannot borrow past identities or model-defined pool counts");

        var history = frame.ChoiceHistory!;
        var gap = frame with { ChoiceHistory = new(history.MatchId, history.StateVersion, history.EventSequence, history.Entries, false, 0, ["missing choice rows"]) };
        var incomplete = await Evaluate(new SnapshotCoach(gap, new(), catalog, Build));
        Check(incomplete["SideboardEvidence"]!["IsHistoryComplete"]!.GetValue<bool>() == false &&
            incomplete["SideboardEvidence"]!["SelectedCounts"]![0]!["ObservedSelections"]!.GetValue<int>() == 2 &&
            incomplete["SideboardEvidence"]!["SelectedCounts"]![0]!["IsLowerBound"]!.GetValue<bool>() &&
            incomplete["SideboardEvidence"]!["CurrentOffer"]!["OptionCount"]!.GetValue<int>() == 2,
            "A historical gap became zero previous selections or destroyed a separately observed current offer.");
        var future = frame with { ChoiceHistory = new(history.MatchId, history.StateVersion, history.EventSequence + 1, history.Entries, true, 0, []) };
        var futureResult = await Evaluate(new SnapshotCoach(future, new(), catalog, Build));
        Check(futureResult["SideboardEvidence"]!["Sources"]!.AsArray().Count == 0 &&
            futureResult["SideboardEvidence"]!["CurrentOffer"]!["OptionCount"]!.GetValue<int>() == 2,
            "A later ledger entered old sideboard evidence or missing history was filled from current options.");
        pass("JAIL history gaps preserve observed selection lower bounds while future ledgers are excluded independently of current exact offers");

        var consumedEntry = new ChoiceHistoryEntry(9, 1, "GENERAL", 30, "JAIL_831", 1, 1, frame.Choice!.OfferedAtEventSequence,
            frame.EventSequence, frame.Choice.Options, [], [81], true);
        var consumed = frame with { ChoiceHistory = new(history.MatchId, history.StateVersion, history.EventSequence,
            history.Entries.Append(consumedEntry).ToArray(), true, 0, []) };
        var staleReady = await Evaluate(new SnapshotCoach(consumed, new(), catalog, Build));
        Check(staleReady["Verification"]?.ToString() == "Unsupported" && staleReady["CardOutcomes"]!.AsArray().Count == 0 &&
            staleReady["ResolvesEffects"]?.GetValue<bool>() == false && staleReady["HandCountAfter"] is null &&
            staleReady["SideboardEvidence"]!["CurrentOffer"] is null && staleReady["SideboardEvidence"]!["Sources"]!.AsArray()
                .Any(source => source!["Selections"]!.AsArray().Any(selection => selection!["ChoiceId"]!.GetValue<int>() == 9)),
            "An already chosen current JAIL choice used a residual Ready interface to add the card a second time.");
        foreach (var entries in new[] { history.Entries.Concat(new[] { consumedEntry, consumedEntry }).ToArray(),
            history.Entries.Append(new ChoiceHistoryEntry(9, 1, "GENERAL", 30, "JAIL_831", 1, 1, frame.Choice.OfferedAtEventSequence,
                frame.EventSequence, frame.Choice.Options, [], [], false)).ToArray() })
        {
            var ambiguous = frame with { ChoiceHistory = new(history.MatchId, history.StateVersion, history.EventSequence, entries, false, 0, []) };
            var ambiguousResult = await Evaluate(new SnapshotCoach(ambiguous, new(), catalog, Build));
            Check(ambiguousResult["CardOutcomes"]!.AsArray().Count == 0 && ambiguousResult["ResolvesEffects"]?.GetValue<bool>() == false &&
                ambiguousResult["SideboardEvidence"]?["CurrentChoiceHasHistory"]?.GetValue<bool>() == true &&
                ambiguousResult["SideboardEvidence"]?["CurrentOffer"] is null,
                "Duplicate or incomplete consumed-choice rows were removed from counts and thereby reopened a stale Ready offer.");
        }
        pass("a same-frame actual chosen JAIL record consumes a stale Ready offer without granting a second card or counting facts as effects");

        for (var id = 200; id < 210; id++) Feed(engine, Card(id, "HAND_FILLER_" + id, 1, "HAND", "SPELL"));
        Feed(engine, ["TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=10", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var full = engine.Snapshot(); var fullBefore = JsonSerializer.Serialize(full);
        var fullCoach = new SnapshotCoach(full, new(), catalog, Build);
        var fullResult = await Evaluate(fullCoach); var repeated = await Evaluate(fullCoach);
        Check(fullResult["CardOutcomes"]![0]!["AddedToHand"]!.GetValue<bool>() == false && fullResult["HandCountAfter"]!.GetValue<int>() == 10 &&
            fullResult["SideboardEvidence"]!["SelectedCounts"]![0]!["ObservedSelections"]!.GetValue<int>() == 2 &&
            JsonNode.DeepEquals(fullResult, repeated) && JsonSerializer.Serialize(full) == fullBefore,
            "Full-hand or repeated preview calls changed actual chosen counts, invented a receipt or mutated the bound frame.");
        pass("full-hand and repeated JAIL previews keep actual chosen history fixed without counting the pending selection or inventing a receipt");

        using var model = new SideboardModel(); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://sideboard-fixture.invalid/v1/chat/completions"), "sideboard-fixture"),
            "sideboard-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(model.Calls == 2 && advice.Actions is [{ Kind: "choose", SourceEntityId: 81 }] && !advice.VerifiedLethal &&
            context.TemplateVersion == "hunter-v18/choice" && AdviceTaskTemplates.Get(AdviceTaskKind.Turn).Version == "hunter-v18/turn" &&
            observed.Last() is { Name: "evaluate_choice", Success: true, ResultHash.Length: 64, RequestId: not null },
            "The v16 model loop lost its sideboard scope policy or promoted historical choice facts into a lethal proof.");
        pass("v16 fake HTTP receives JAIL evidence with an explicit incomplete-pool policy while only the current legal choice survives validation");
    }

    private static Task<JsonNode> Evaluate(SnapshotCoach coach) => coach.ExecuteAsync("evaluate_choice", new() { ["choice_id"] = 9, ["selected_entity_ids"] = new JsonArray(81) }, default);
    private static PowerLogStateEngine Start()
    {
        var engine = new PowerLogStateEngine(1, "sideboard-integration");
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=8", "tag=STEP value=MAIN_ACTION", "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1",
            "tag=NUM_CARDS_IN_HAND value=0", "tag=MAXHANDSIZE value=10", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0"]);
        Feed(engine, Card(30, "JAIL_831", 1, "PLAY", "MINION")); Feed(engine, Card(31, "JAIL_831", 1, "PLAY", "MINION"));
        Feed(engine, ["tag=COPIED_FROM_ENTITY_ID value=30"]);
        foreach (var (id, card) in new[] { (80, "EDR_489"), (81, "DINO_421"), (82, "EDR_227"), (90, "EDR_489"), (91, "DINO_421") })
            Feed(engine, Card(id, card, 1, "SETASIDE", "MINION"));
        Feed(engine, Card(300, Probe, 2, "HAND", "MINION")); Feed(engine, Card(301, Probe, 1, "DECK", "MINION"));
        Feed(engine, ["PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static IEnumerable<string> Card(int id, string card, int owner, string zone, string type) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}", $"tag=CARDTYPE value={type}"];
    private static IEnumerable<string> Offer(int id, int source, params (int Id, string Card)[] options)
    {
        yield return $"GameState.DebugPrintEntityChoices() - id={id} Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return $"GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id={source} cardId=JAIL_831 player=1]";
        for (var index = 0; index < options.Length; index++)
            yield return $"GameState.DebugPrintEntityChoices() - Entities[{index}]=[entityName=Anonymous id={options[index].Id} cardId={options[index].Card} player=1]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static void Choose(PowerLogStateEngine engine, int choice, int selected) => Feed(engine,
        [$"GameState.DebugPrintEntitiesChosen() - id={choice} Player=1 EntitiesCount=1", $"GameState.DebugPrintEntitiesChosen() - Entities[0]={selected}", "ChoiceCardMgr.OnChoicesShown() - chosen"]);
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class SideboardModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); object message;
            if (Calls == 1)
            {
                Check(text.Contains("hunter-v18/choice", StringComparison.Ordinal) && text.Contains("SideboardEvidence", StringComparison.Ordinal) &&
                    text.Contains("不是当前完整边栏池", StringComparison.Ordinal) && text.Contains("次数仅为已观察下限", StringComparison.Ordinal),
                    "The v16 model policy did not distinguish actual sideboard evidence from a complete current pool.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "sideboard-evidence", type = "function", function = new { name = "evaluate_choice", arguments = "{\"choice_id\":9,\"selected_entity_ids\":[81]}" } } } };
            }
            else
            {
                var tool = JsonNode.Parse(text)!["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(result["SideboardEvidence"]!["CurrentOffer"]!["OptionCount"]!.GetValue<int>() == 2 &&
                    result["SideboardEvidence"]!["CurrentPoolIsExact"]!.GetValue<bool>() == false && !result["CanEvaluateFollowupActions"]!.GetValue<bool>(),
                    "The model did not receive the bounded current-offer evidence.");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "比较本次两个实际候选，选择震地雷龙。", reasons = new[] { "历史记录已列明，完整边栏余量仍未知" }, risks = new[] { "最终入手费用等待结算" },
                    actions = new object[] { new { kind = "choose", source_entity_id = 81 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
}
