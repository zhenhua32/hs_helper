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
using HsHelper.Storage;

internal static class StartingDeckIntegration
{
    private const string Build = "255790";
    private const string PrivateProbe = "PRIVATE_STARTING_DECK_PROBE";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var saved = await CreateSavedCaseAsync(Path.Combine(root, "starting-deck", Guid.NewGuid().ToString("N")));
        var original = (await saved.Sessions.GetHistoricalDecisionAsync(saved.Point.Id))!;
        var binding = StartingDeckContext.FromSavedDeck(original.Context.Snapshot, original.Context.CardBuild,
            original.Context.Deck, original.Context.DeckVersionId)!;
        Check(binding.DeckVersionId == saved.A.Id && (await saved.Sessions.ListDeckVersionsAsync()).Any(deck => deck.Id == saved.B.Id),
            "Real session did not retain A after a later B deck was saved.");
        var context = CompactAdviceContextBuilder.Create(saved.Frame, Build, AdviceTaskKind.Choice, saved.B.Definition);
        context = context with { DeckVersionId = saved.A.Id, KnowledgeAsOf = saved.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        var coach = new SnapshotCoach(saved.Frame, context.Snapshot, saved.Catalog, Build, startingDeck: binding);
        var result = await Evaluate(coach);
        AssertReference(result, saved.A);
        Check(context.Snapshot["imported_deck"]!["sideboards"]![0]!["cards"]![0]!["dbf_id"]!.GetValue<int>() !=
            saved.A.Definition.Sideboards[0].Cards[0].DbfId, "The adversarial model context did not actually contain deck B.");
        Check(result["SideboardEvidence"]!["SelectedCounts"]!.AsArray().Sum(count => count!["ObservedSelections"]!.GetValue<int>()) == 2 &&
            result["SideboardEvidence"]!["CurrentOffer"]!["OptionCount"]!.GetValue<int>() == 2 && !result.ToJsonString().Contains(PrivateProbe, StringComparison.Ordinal),
            "Initial deck reference changed observed selections, remaining offers or private identity filtering.");
        pass("real stored session A supplies all three initial sideboard definitions despite later deck B and forged context B without subtracting observed choices");

        foreach (var bad in new[]
        {
            binding with { MatchId = "other-match" }, binding with { StateVersion = binding.StateVersion + 1 },
            binding with { EventSequence = binding.EventSequence + 1 }, binding with { CardBuild = "255791" }, binding with { DeckVersionId = "" }
        })
            Check((await Evaluate(new SnapshotCoach(saved.Frame, context.Snapshot, saved.Catalog, Build, startingDeck: bad)))["SideboardEvidence"]?["InitialReference"] is null,
                "A cross-frame/build or unidentified starting reference entered a real tool response.");
        foreach (var mismatch in new[]
        {
            new DeckTrackingSnapshot(saved.Frame.MatchId, saved.Frame.StateVersion, saved.B.Id, Build, null, FrozenDictionary<string, int>.Empty, 0, false, []),
            new(saved.Frame.MatchId, saved.Frame.StateVersion, saved.A.Id, "255791", null, FrozenDictionary<string, int>.Empty, 0, false, [])
        })
        {
            var tracked = saved.Frame with { DeckTracking = mismatch };
            Check(StartingDeckContext.FromSavedDeck(tracked, Build, saved.A, saved.A.Id) is null &&
                (await Evaluate(new SnapshotCoach(tracked, context.Snapshot, saved.Catalog, Build, startingDeck: binding)))["SideboardEvidence"]?["InitialReference"] is null,
                "Explicit current deck tracking conflict was repaired by a different saved reference.");
        }
        Check(StartingDeckContext.FromSavedDeck(saved.Frame, Build, null, saved.A.Id) is null &&
            StartingDeckContext.FromSavedDeck(saved.Frame, Build, saved.A, null) is null &&
            StartingDeckContext.FromSavedDeck(saved.Frame, Build, saved.B, saved.A.Id) is null &&
            StartingDeckContext.FromSavedDeck(saved.Frame, Build, saved.A with { Definition = saved.B.Definition }, saved.A.Id) is null &&
            (await Evaluate(new SnapshotCoach(saved.Frame, context.Snapshot, saved.Catalog, Build)))["SideboardEvidence"]?["InitialReference"] is null,
            "Missing source, wrong saved identity, mismatched saved code/definition or context-only deck became a session reference.");
        var knowledge = new CoachKnowledgeStore(Path.Combine(saved.Directory, "knowledge.db")); await knowledge.InitializeAsync();
        var otherLibrary = new CoachLibraryContext(saved.Sessions, knowledge, new(saved.Frame.MatchId, saved.Frame.StateVersion, Build,
            saved.B.Id, saved.Point.RecordedAt, EventSequence: saved.Frame.EventSequence));
        var conflictedLibrary = await Evaluate(new SnapshotCoach(saved.Frame, context.Snapshot, saved.Catalog, Build, library: otherLibrary, startingDeck: binding));
        Check(conflictedLibrary["SideboardEvidence"]?["InitialReference"] is null && otherLibrary.Binding.DeckVersionId == saved.B.Id,
            "A different library source was rewritten or combined with original starting deck A.");
        pass("starting deck provenance rejects wrong match state event build tracking identity and saved code while context JSON alone never supplies a binding");

        var main = saved.A.Definition.Cards.ToList(); var heroes = saved.A.Definition.Heroes.ToList();
        var sideCards = saved.A.Definition.Sideboards[0].Cards.ToList();
        var boards = new List<DeckSideboard> { new(saved.A.Definition.Sideboards[0].OwnerDbfId, sideCards) };
        var producer = binding with { Definition = new(saved.A.Definition.Format, heroes, main, boards) };
        var frozen = StartingDeckBinding.FreezeForSnapshot(saved.Frame, Build, producer)!;
        var frozenCoach = new SnapshotCoach(saved.Frame, context.Snapshot, saved.Catalog, Build, startingDeck: producer);
        var before = SnapshotCodec.Serialize(saved.Frame);
        main.Clear(); heroes.Clear(); sideCards.Clear(); boards.Clear();
        AssertReference(await Evaluate(frozenCoach), saved.A);
        foreach (var write in new Action[] { () => ((IList<int>)frozen.Definition.Heroes).Clear(),
            () => ((IList<DeckCard>)frozen.Definition.Cards).Clear(), () => ((IList<DeckSideboard>)frozen.Definition.Sideboards).Clear(),
            () => ((IList<DeckCard>)frozen.Definition.Sideboards[0].Cards).Clear() })
            ThrowsReadOnly(write);
        var pendingOld = Evaluate(frozenCoach);
        var cleared = new SnapshotCoach(saved.Frame, context.Snapshot, saved.Catalog, Build);
        Check((await Evaluate(cleared))["SideboardEvidence"]?["InitialReference"] is null, "A newly cleared coach inherited the previous reference.");
        AssertReference(await pendingOld, saved.A);
        Check(SnapshotCodec.Serialize(saved.Frame) == before && saved.A.Definition.Sideboards[0].Cards.Count == 3,
            "Producer mutation or repeated/cleared evaluation changed the authoritative snapshot or stored definition.");
        pass("starting references deep-freeze producer collections and independent cleared coaches cannot inherit an older asynchronous reference result");

        var entities = saved.Frame.Entities.ToDictionary();
        var sourceTags = entities[31].Tags.ToDictionary(); sourceTags["CONTROLLER"] = "2";
        entities[31] = entities[31] with { Tags = sourceTags.ToFrozenDictionary() };
        var transferred = saved.Frame with { Entities = entities.ToFrozenDictionary(), Choice = saved.Frame.Choice! with { SourceEntityId = 31 } };
        var transferredBinding = StartingDeckContext.FromSavedDeck(transferred, Build, saved.A, saved.A.Id)!;
        var oldSource = await Evaluate(new SnapshotCoach(transferred, new(), saved.Catalog, Build, startingDeck: transferredBinding));
        AssertReference(oldSource, saved.A);
        Check(oldSource["SideboardEvidence"]!["IsCurrentSourceConfirmed"]?.GetValue<bool>() == false && oldSource["SideboardEvidence"]!["CurrentOffer"] is null &&
            oldSource["CardOutcomes"]!.AsArray().Count == 0 && oldSource["SideboardEvidence"]!["Sources"]!.AsArray().Count == 2,
            "A copied/transferred source inherited a guessed pool or used old identity to grant a fresh card.");
        pass("copied and subsequently transferred JAIL sources retain original session reference and separate frozen facts without proving inheritance current offers or remaining pools");

        var missing = new CardCatalog(Path.Combine(saved.Directory, "missing-source.db")); await missing.InitializeAsync();
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(saved.Cards.Values.Where(card => card.Id != "JAIL_831")
            .Select(card => JsonNode.Parse(card.RawJson)))))) await missing.ImportAsync(data, Build, source: "fixture://missing-jail-source");
        var missingResult = await Evaluate(new SnapshotCoach(saved.Frame, context.Snapshot, missing, Build, startingDeck: binding));
        Check(missingResult["SideboardEvidence"]?["InitialReference"] is null, "Missing fixed source definition borrowed a native JAIL identity from the deck/context.");
        var partial = new CardCatalog(Path.Combine(saved.Directory, "missing-initial-member.db")); await partial.InitializeAsync();
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(saved.Cards.Values.Where(card => card.Id != "CORE_TTN_866")
            .Select(card => JsonNode.Parse(card.RawJson)))))) await partial.ImportAsync(data, Build, source: "fixture://missing-original-member");
        var partialResult = await Evaluate(new SnapshotCoach(saved.Frame, context.Snapshot, partial, Build, startingDeck: binding));
        var partialReference = partialResult["SideboardEvidence"]!["InitialReference"]!;
        Check(partialReference["IsSessionBound"]?.GetValue<bool>() == true && partialReference["TotalCards"]?.GetValue<int>() == 3 &&
            partialReference["Cards"]!.AsArray().Single(card => card!["DbfId"]!.GetValue<int>() == 129169)!["CardId"] is null &&
            partialResult["SideboardEvidence"]!["CurrentPoolIsExact"]?.GetValue<bool>() == false,
            "An unknown original DBF was filled from current/historical choices or turned into a guessed remaining pool.");
        foreach (var hidden in new[] { saved.Frame with { IsSpectating = true },
            saved.Frame with { Quality = saved.Frame.Quality with { HasLocalPerspective = false } },
            saved.Frame with { Choice = saved.Frame.Choice! with { PlayerId = 2 } } })
        {
            var hiddenBinding = StartingDeckContext.FromSavedDeck(hidden, Build, saved.A, saved.A.Id);
            var hiddenResult = await Evaluate(new SnapshotCoach(hidden, new(), saved.Catalog, Build, startingDeck: hiddenBinding));
            Check(hiddenResult["SideboardEvidence"]?["InitialReference"] is null && !hiddenResult.ToJsonString().Contains(saved.A.Id, StringComparison.Ordinal),
                "Rejected private choice scope exported a starting deck version or its reference.");
        }
        pass("missing native source definitions and unconfirmed opposing or spectator choices do not expose session sideboard references");

        var observations = new List<CoachToolObservation>();
        coach = new(saved.Frame, context.Snapshot, saved.Catalog, Build, toolCompleted: observations.Add, startingDeck: binding);
        using var model = new StartingModel(saved.A); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://starting-deck-fixture.invalid/v1/chat/completions"), "starting-deck-model"),
            "synthetic-starting-deck-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        var observation = observations.Single();
        Check(model.Calls == 2 && observation is { Name: "evaluate_choice", Success: true, ResultHash.Length: 64, RequestId: not null, ToolCallId: "starting-deck-tool" } &&
            advice.AnalysisRunId == observation.AnalysisRunId && advice.RequestIds.Contains(observation.RequestId!) && !advice.VerifiedLethal,
            "Fake HTTP did not receive the real original deck reference with exact tool/run/request provenance.");
        var archived = await saved.Archive.SaveAsync(new(saved.Frame.MatchId, saved.Frame.StateVersion, saved.Frame.EventSequence, Build, advice.TemplateVersion!, advice.Summary)
        {
            DeckVersionId = saved.A.Id, Task = context.Task, Model = advice.Model, AnalysisRunId = advice.AnalysisRunId,
            RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            Evidence = [new("evaluate_choice", "原会话起始边栏参考，非当前池或余量。", observation.ResultHash)]
        });
        var reopened = await new AdviceArchiveStore(Path.Combine(saved.Directory, "advice-archive.db")).GetAsync(archived.Id);
        Check(reopened!.Advice.DeckVersionId == saved.A.Id && reopened.Advice.KnowledgeAsOf == saved.Point.RecordedAt &&
            reopened.Advice.Evidence.Single().ResultHash == observation.ResultHash && reopened.Advice.AnalysisRunId == advice.AnalysisRunId &&
            reopened.Advice.RequestIds!.SequenceEqual(advice.RequestIds), "Reopened archive lost the original session deck, knowledge cutoff or real tool-result hash.");
        pass("fake HTTP consumes actual starting reference and reopened archive preserves original deck cutoff and exact tool-result hash without a remaining-pool claim");
    }

    internal sealed record SavedCase(string Directory, CardCatalog Catalog, SessionStore Sessions, AdviceArchiveStore Archive,
        GameSnapshot Frame, DecisionPoint Point, DeckVersion A, DeckVersion B, IReadOnlyDictionary<string, CardRecord> Cards);

    internal static async Task<SavedCase> CreateSavedCaseAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        using var terror = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build && terror.RootElement.GetProperty("build").GetString() == Build,
            "Starting deck fixture build is not pinned.");
        var records = fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()).ToList();
        records.Add(terror.RootElement.GetProperty("cards").EnumerateArray().Single(card => card.GetProperty("id").GetString() == "CORE_TTN_866").Clone());
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(records)))
            await catalog.ImportAsync(input, Build, source: "fixture://starting-deck-integration");
        var ids = records.Select(card => card.GetProperty("id").GetString()!).ToArray();
        var cards = (await catalog.GetByIdsAsync(ids, Build)).ToDictionary(card => card.Id);
        var sessions = new SessionStore(Path.Combine(directory, "sessions.db")); await sessions.InitializeAsync();
        var definition = DeckCode.Decode(await File.ReadAllTextAsync(Path.Combine(workspace.FullName, "samples", "decks", "beast-hunter.txt")));
        var a = await sessions.SaveDeckVersionAsync("original starting deck A", definition, Build);
        var bDefinition = definition with { Sideboards = [new(125998, new[] { "EDR_227", "CORE_UNG_912", "TLC_824" }.Select(id => new DeckCard(cards[id].DbfId!.Value, 1)).ToArray())] };
        var b = await sessions.SaveDeckVersionAsync("later selected deck B", bDefinition, Build, a.Id);
        var engine = new PowerLogStateEngine(1, "starting-deck-" + Guid.NewGuid().ToString("N"));
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=8", "tag=STEP value=MAIN_ACTION", "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1",
            "tag=NUM_CARDS_IN_HAND value=0", "tag=MAXHANDSIZE value=10", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0"]);
        foreach (var id in new[] { 30, 31 }) Feed(engine, Card(id, "JAIL_831", 1, "PLAY"));
        Feed(engine, ["tag=COPIED_FROM_ENTITY_ID value=30"]);
        foreach (var (id, card) in new[] { (80, "EDR_489"), (81, "DINO_421"), (82, "EDR_227"), (90, "EDR_489"), (91, "DINO_421") })
            Feed(engine, Card(id, card, 1, "SETASIDE"));
        Feed(engine, Card(300, PrivateProbe, 2, "HAND"));
        Feed(engine, Offer(7, 30, (80, "EDR_489"), (81, "DINO_421"), (82, "EDR_227"))); Chosen(engine, 7, 80);
        Feed(engine, Offer(8, 31, (90, "EDR_489"), (91, "DINO_421"))); Chosen(engine, 8, 90);
        Feed(engine, Offer(9, 30, (81, "DINO_421"), (82, "EDR_227")));
        var frame = engine.Snapshot();
        var recorder = new SessionRecorder(sessions, new(a.Id, Build, 1)); recorder.Observe(frame, 1); await recorder.CompleteAsync();
        var point = (await sessions.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        var archive = new AdviceArchiveStore(Path.Combine(directory, "advice-archive.db")); await archive.InitializeAsync();
        return new(directory, catalog, sessions, archive, frame, point, a, b, cards);
    }

    internal static void AssertReference(JsonNode result, DeckVersion expected)
    {
        var evidence = result["SideboardEvidence"]!; var reference = evidence["InitialReference"]!;
        Check(reference?["IsSessionBound"]?.GetValue<bool>() == true && reference["IsValid"]?.GetValue<bool>() == true &&
            reference["DeckVersionId"]?.GetValue<string>() == expected.Id && reference["CardBuild"]?.GetValue<string>() == Build &&
            reference["TotalCards"]?.GetValue<int>() == 3 && reference["IsStartingReferenceOnly"]?.GetValue<bool>() == true &&
            reference["IsCurrentPool"]?.GetValue<bool>() == false && evidence["CurrentPoolIsExact"]?.GetValue<bool>() == false &&
            evidence["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && result["CanEvaluateFollowupActions"]?.GetValue<bool>() == false &&
            reference["Cards"]!.AsArray().Select(card => card!["DbfId"]!.GetValue<int>()).Order().SequenceEqual(expected.Definition.Sideboards[0].Cards.Select(card => card.DbfId).Order()) &&
            reference["Cards"]!.AsArray().All(card => card!["CardId"] is not null && card["InitialCopies"]!.GetValue<int>() == 1),
            "The real choice did not retain the exact original session reference and all three fixed-build identities without current-pool/follow-up claims.");
    }

    private static Task<JsonNode> Evaluate(SnapshotCoach coach) => coach.ExecuteAsync("evaluate_choice", new() { ["choice_id"] = 9, ["selected_entity_ids"] = new JsonArray(81) }, default);
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static IEnumerable<string> Card(int id, string card, int owner, string zone) =>
        [$"FULL_ENTITY - Creating ID={id} CardID={card}", "tag=CARDTYPE value=MINION", $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}"];
    private static IEnumerable<string> Offer(int id, int source, params (int Id, string Card)[] options)
    {
        yield return $"GameState.DebugPrintEntityChoices() - id={id} Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return $"GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id={source} cardId=JAIL_831 player=1]";
        for (var index = 0; index < options.Length; index++)
            yield return $"GameState.DebugPrintEntityChoices() - Entities[{index}]=[entityName=Anonymous id={options[index].Id} cardId={options[index].Card} player=1]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static void Chosen(PowerLogStateEngine engine, int id, int selected) => Feed(engine,
        [$"GameState.DebugPrintEntitiesChosen() - id={id} Player=1 EntitiesCount=1", $"GameState.DebugPrintEntitiesChosen() - Entities[0]={selected}", "ChoiceCardMgr.OnChoicesShown() - chosen"]);
    private static void ThrowsReadOnly(Action write) { try { write(); } catch (NotSupportedException) { return; } throw new InvalidOperationException("Mutable returned starting definition."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class StartingModel(DeckVersion expected) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            object message;
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[] { new { id = "starting-deck-tool", type = "function", function = new
                { name = "evaluate_choice", arguments = "{\"choice_id\":9,\"selected_entity_ids\":[81]}" } } } };
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool")!;
                AssertReference(JsonNode.Parse(tool["content"]!.GetValue<string>())!, expected);
                message = new { role = "assistant", content = """{"summary":"原会话边栏仅作初始参考，完整剩余池未知。","reasons":[],"risks":[],"actions":[],"verified_lethal":true}""" };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
}
