using System.Collections.Frozen;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

internal static class CoachLibraryIntegration
{
    private const string FakeKey = "synthetic-dummy-key";
    private const string FakeEndpoint = "https://synthetic.invalid/v1/chat/completions";
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(1);
    private static readonly DateTimeOffset T2 = T0.AddHours(2);

    public static async Task RunAsync(string root, CardCatalog catalog, DeckVersion originalDeck,
        GameSnapshot originalFrame, Action<string> pass)
    {
        var path = Path.Combine(root, "library-integration"); Directory.CreateDirectory(path);
        var statsPath = Path.Combine(path, "stats.db");
        var sessions = new SessionStore(statsPath); await sessions.InitializeAsync();
        var deck = await sessions.SaveDeckVersionAsync(originalDeck.Name, originalDeck.Definition, originalDeck.CardBuild);
        var frame = originalFrame with { MatchId = "library-current", DeckTracking = null, PublicPlays = null };
        await SeedStatsAsync(sessions, statsPath, deck, frame);
        var clock = new IntegrationClock { Now = T0 };
        var knowledgePath = Path.Combine(path, "knowledge.db");
        var knowledge = new CoachKnowledgeStore(knowledgePath, clock); await knowledge.InitializeAsync();
        var planDraft = new CoachKnowledgeDraft(deck.Id, CoachKnowledgeKind.DeckPlan, "t0 plan", "PLAN_T0_KEEP_PRESSURE", "MAGE", true);
        var plan = await knowledge.SaveAsync(planDraft);
        var note = await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "t0 note", "NOTE_T0_VISIBLE_ONLY", "MAGE", true,
            frame.MatchId) { EventSequence = frame.EventSequence - 1 });
        await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "later turn anchor", "NOTE_FUTURE_SEQUENCE", "MAGE", true,
            frame.MatchId) { EventSequence = frame.EventSequence + 50 });
        await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, "other matchup", "OUT_OF_SCOPE_PRIEST", "PRIEST", true));
        await knowledge.SaveAsync(new("other-deck-version", CoachKnowledgeKind.DeckPlan, "other deck", "OUT_OF_SCOPE_DECK"));
        var binding = new CoachLibraryBinding(frame.MatchId, frame.StateVersion, SyntheticFixture.Build, deck.Id, T1,
            "MAGE", true, EventSequence: frame.EventSequence);
        var library = new CoachLibraryContext(sessions, knowledge, binding);
        clock.Now = T2;
        await knowledge.SaveAsync(planDraft with { Content = "PLAN_T2_FUTURE_REVISION" }, plan.EntryId, plan.Version);
        await knowledge.DeleteAsync(note.EntryId, note.Version);
        await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, "future creation", "PLAN_CREATED_T2", "MAGE", true));
        var reopenedKnowledge = new CoachKnowledgeStore(knowledgePath, clock); await reopenedKnowledge.InitializeAsync();
        library = new CoachLibraryContext(sessions, reopenedKnowledge, binding);
        var oldPlan = await library.ExecuteAsync("get_matchup_plan", new(), default);
        var planText = oldPlan.ToJsonString();
        Check(planText.Contains("PLAN_T0_KEEP_PRESSURE") && planText.Contains("NOTE_T0_VISIBLE_ONLY"), "Pinned library lost pre-cutoff plan or deleted-later note");
        foreach (var forbidden in new[] { "PLAN_T2", "PLAN_CREATED_T2", "NOTE_FUTURE_SEQUENCE", "OUT_OF_SCOPE" })
            Check(!planText.Contains(forbidden, StringComparison.Ordinal), "Pinned library exposed future or wrong-scope knowledge: " + forbidden);
        Check(oldPlan["entries"]!.AsArray().Count == 2 && oldPlan["entries"]!.AsArray().All(entry => entry!["Version"]!.GetValue<int>() == 1), "Historical knowledge followed a later version pointer");
        var liveLibrary = new CoachLibraryContext(sessions, reopenedKnowledge, binding with { AsOf = T2.AddMinutes(1) });
        var livePlan = (await liveLibrary.ExecuteAsync("get_matchup_plan", new(), default)).ToJsonString();
        Check(livePlan.Contains("PLAN_T2_FUTURE_REVISION") && livePlan.Contains("PLAN_CREATED_T2") && !livePlan.Contains("NOTE_T0_VISIBLE_ONLY"), "Current library ignored append-only revisions or deletion");
        pass("t0 user plans and notes remain fixed at historical t1 after t2 revisions, deletion, restart and scope changes");

        var noSequence = new CoachLibraryContext(sessions, reopenedKnowledge, binding with { EventSequence = null });
        var noSequencePlan = (await noSequence.ExecuteAsync("get_matchup_plan", new(), default)).ToJsonString();
        Check(noSequencePlan.Contains("PLAN_T0_KEEP_PRESSURE") && !noSequencePlan.Contains("NOTE_T0_VISIBLE_ONLY") && !noSequencePlan.Contains("NOTE_FUTURE_SEQUENCE"),
            "Unknown historical event sequence disclosed an anchored same-match note");
        pass("same-match note anchors cannot cross the historical event sequence or an unknown cutoff");

        var statsArgs = new JsonObject { ["scope"] = "current_matchup", ["went_first"] = "current" };
        var oldStats = await library.ExecuteAsync("get_personal_stats", statsArgs, default);
        AssertStats(oldStats, games: 2, wins: 1, losses: 0, unknown: 1);
        AssertStats(await liveLibrary.ExecuteAsync("get_personal_stats", statsArgs, default), games: 3, wins: 3, losses: 0, unknown: 0);
        var finalStats = await sessions.GetStatsAsync(new(CardBuild: SyntheticFixture.Build, DeckVersionId: deck.Id,
            OpponentClass: "MAGE", WentFirst: true, Format: "STANDARD", PlayerClass: "HUNTER", Region: "CN", GameType: "GT_RANKED"));
        Check(finalStats.Games == 4 && finalStats.Wins == 3 && finalStats.Losses == 1, "Stats fixture cannot distinguish final current/late results from historical evidence");
        var unbound = new CoachLibraryContext(sessions, reopenedKnowledge, binding with { DeckVersionId = null });
        Check((await unbound.ExecuteAsync("get_matchup_plan", new(), default))["missing"] is not null &&
            (await unbound.ExecuteAsync("get_personal_stats", statsArgs, default))["missing"] is not null, "Unbound context invented deck-specific plan or statistics");
        await Throws<ArgumentException>(() => library.ExecuteAsync("get_personal_stats", new JsonObject { ["scope"] = "current_matchup", ["went_first"] = "current", ["as_of"] = T2.ToString("O") }, default));
        pass("personal-stats tool uses as-of observations, excludes selected match and future completions, and keeps late results unknown");

        var choice = new CurrentChoiceSnapshot(77, 1, "GENERAL", 100, 1, 1,
            [new(5000, SyntheticFixture.CardId(1), 1, 0), new(5001, SyntheticFixture.CardId(2), 1, 1)], true);
        var choiceFrame = frame with { Phase = GamePhase.Choice, Choice = choice };
        var knownCards = await catalog.GetByIdsAsync([SyntheticFixture.CardId(1), SyntheticFixture.CardId(2)], SyntheticFixture.Build);
        var context = CompactAdviceContextBuilder.Create(choiceFrame, SyntheticFixture.Build, AdviceTaskKind.Choice, deck.Definition, knownCards);
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(choiceFrame, context.Snapshot, catalog, SyntheticFixture.Build, library: library,
            toolCompleted: observations.Add);
        using var handler = new LibraryChoiceModelHandler();
        using var http = new HttpClient(handler);
        var budget = new AnalysisBudget(new(MaxRequestsPerMatch: 4, MaxTokensPerMatch: 1_000_000));
        var usages = new List<ModelUsage>();
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri(FakeEndpoint), "synthetic-fake-model"), FakeKey,
            context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        Check(advice.Verification == "verified" && advice.Actions.Count == 1 && advice.Actions[0] is { Kind: "choose", SourceEntityId: 5000 } &&
            !advice.VerifiedLethal && advice.Risks.Any(risk => risk.Contains("未模拟")), "Host did not verify only choice membership/cardinality or trusted claimed lethal");
        Check(handler.Requests.Count == 2 && observations.Select(value => value.Name).SequenceEqual(
            ["get_snapshot", "get_matchup_plan", "get_personal_stats", "get_cards", "validate_choice"]) && observations.All(value => value.Success),
            "Fake model did not traverse compact context, personal library, historical stats, pinned cards and choice verification");
        foreach (var observation in observations)
            Check(observation.ResultJson is not null && observation.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation.ResultJson))), "Tool evidence hash does not bind its actual result");
        var toolResults = JsonNode.Parse(handler.Requests[1])!["messages"]!.AsArray().Where(message => message?["role"]?.GetValue<string>() == "tool")
            .ToDictionary(message => message!["tool_call_id"]!.GetValue<string>(), message => JsonNode.Parse(message!["content"]!.GetValue<string>())!);
        Check(JsonNode.DeepEquals(toolResults["library-snapshot"], context.Snapshot), "Lazy snapshot expanded back into a full storage snapshot");
        Check(toolResults["library-plan"].ToJsonString().Contains("PLAN_T0_KEEP_PRESSURE") && !toolResults["library-plan"].ToJsonString().Contains("PLAN_T2"), "Future plan entered model tool messages");
        AssertStats(toolResults["library-stats"], 2, 1, 0, 1);
        Check(toolResults["library-cards"]["build"]!.GetValue<string>() == SyntheticFixture.Build &&
            toolResults["library-cards"].ToJsonString().Contains("synthetic original 01"), "Model card lookup followed the active newer build");
        Check(toolResults["library-choice"]["Verification"]!.GetValue<string>() == "Verified" &&
            toolResults["library-choice"]["SelectionOnly"]!.GetValue<bool>() && !toolResults["library-choice"]["ResolvesEffects"]!.GetValue<bool>(), "Choice tool claimed effect simulation");
        Check(handler.Requests.All(body => !body.Contains(FakeKey) && !body.Contains("PLAN_T2") && !body.Contains("PLAN_CREATED_T2") &&
            !body.Contains("NOTE_FUTURE_SEQUENCE") && !body.Contains(SyntheticFixture.FutureEnemy) && !body.Contains(SyntheticFixture.PrivateProbe)), "HTTP context leaked credentials, private cards or future knowledge");
        Check(usages.Count == 2 && budget.Snapshot(frame.MatchId).RequestsStarted == 2 && budget.Snapshot(frame.MatchId).InFlightRequests == 0, "Library/choice bridge did not settle HTTP usage");
        pass("fake HTTP jointly queries compact snapshot, historical personal context, version-pinned cards and locally verified own choice");

        await Throws<ArgumentException>(() => Task.FromResult(new SnapshotCoach(choiceFrame, context.Snapshot, catalog, SyntheticFixture.Build,
            library: new(sessions, reopenedKnowledge, binding with { EventSequence = frame.EventSequence + 1 }))));
        var invalidChoice = await coach.ExecuteAsync("validate_choice", new JsonObject { ["choice_id"] = 77, ["selected_entity_ids"] = new JsonArray(5999) }, default);
        Check(invalidChoice["Verification"]!.GetValue<string>() == "Invalid" && !invalidChoice["IsLegal"]!.GetValue<bool>(), "Foreign choice candidate was accepted");
        using var invalidHandler = new LibraryChoiceModelHandler(requestTools: false, selectedEntity: 5999);
        using var invalidHttp = new HttpClient(invalidHandler);
        await Throws<InvalidDataException>(() => new AdviceService(invalidHttp).AnalyzeAsync(new(new Uri(FakeEndpoint), "synthetic-fake-model"), FakeKey,
            context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities()));
        pass("library bindings cannot change event cutoff and model choose actions cannot bypass host candidate validation");

        var archivePath = Path.Combine(path, "advice.db");
        var archive = new AdviceArchiveStore(archivePath, clock); await archive.InitializeAsync();
        var successful = observations.Where(observation => observation.Success).Take(5).ToArray();
        var draft = new AdviceRecordDraft(frame.MatchId, frame.StateVersion, frame.EventSequence, SyntheticFixture.Build, context.TemplateVersion, advice.Summary)
        {
            DeckVersionId = deck.Id, Task = context.Task, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
            Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = successful.Select(observation => new AdviceToolEvidence(observation.Name, "本地工具已完成；" + FakeKey, observation.ResultHash)).ToArray()
        };
        var saved = await archive.SaveAsync(draft, sensitiveValues: [FakeKey, FakeEndpoint]);
        var duplicate = await archive.SaveAsync(draft, sensitiveValues: [FakeKey, FakeEndpoint]);
        var reopenedArchive = new AdviceArchiveStore(archivePath, clock); await reopenedArchive.InitializeAsync();
        var restored = await reopenedArchive.GetAsync(saved.Id) ?? throw new Exception("Completed model advice was not archived");
        Check(restored.Advice.MatchId == frame.MatchId && restored.Advice.StateVersion == frame.StateVersion && restored.Advice.EventSequence == frame.EventSequence &&
            restored.Advice.CardBuild == SyntheticFixture.Build && restored.Advice.PromptTemplateVersion == context.TemplateVersion && restored.Advice.DeckVersionId == deck.Id,
            "Archive changed fixed sequence/build/deck/template binding");
        Check(duplicate.Id == saved.Id && duplicate.RecordedAt == saved.RecordedAt && restored.Advice.Evidence.Select(evidence => evidence.ResultHash).SequenceEqual(successful.Select(observation => observation.ResultHash)), "Archive retries duplicated advice or lost tool evidence hashes");
        var archiveJson = JsonSerializer.Serialize(restored);
        Check(!archiveJson.Contains(FakeKey) && !archiveJson.Contains(FakeEndpoint) && !archiveJson.Contains("Bearer fixture") &&
            !archiveJson.Contains("ResultJson") && !archiveJson.Contains("\"messages\""), "Archive retained credential, HTTP envelope or raw tool bodies");
        Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(archivePath)).Contains(FakeKey), "Sensitive value reached SQLite pages before sanitization");
        Check((await reopenedArchive.ListAsync(new(MatchId: frame.MatchId, ThroughEventSequence: frame.EventSequence - 1))).Count == 0 &&
            (await reopenedArchive.ListAsync(new(MatchId: frame.MatchId, StateVersion: frame.StateVersion, CardBuild: SyntheticFixture.Build, DeckVersionId: deck.Id))).Count == 1,
            "Archive as-of query lost exact binding or disclosed advice after sequence cutoff");
        await Throws<InvalidDataException>(() => reopenedArchive.SaveAsync(draft with { Summary = "different immutable advice" }, saved.Id, [FakeKey, FakeEndpoint]));
        pass("completed advice survives archive restart with fixed sequence/build/template, tool-result SHA256 evidence and secret filtering");
    }

    private static async Task SeedStatsAsync(SessionStore store, string path, DeckVersion deck, GameSnapshot template)
    {
        var recorder = new SessionRecorder(store, new(deck.Id, SyntheticFixture.Build, 1, PlayerClass: "HUNTER"));
        foreach (var state in new[]
        {
            StatsFrame(template, "library-past", 1, "WON"), StatsFrame(template, "library-late", 1, null), StatsFrame(template, "library-late", 2, "WON"),
            StatsFrame(template, "library-current", 1, "LOST"), StatsFrame(template, "library-future", 1, null, GamePhase.Main), StatsFrame(template, "library-future", 2, "WON")
        }) recorder.Observe(state, state.EventSequence);
        await recorder.CompleteAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_snapshots SET recorded_at=CASE WHEN event_seq=2 THEN $future ELSE $past END;
            UPDATE game_sessions SET started_at=$start,ended_at=CASE WHEN id='library-future' THEN $future ELSE $past END;
            """;
        command.Parameters.AddWithValue("$start", T0.AddHours(-1).ToString("O"));
        command.Parameters.AddWithValue("$past", T0.ToString("O"));
        command.Parameters.AddWithValue("$future", T2.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static GameSnapshot StatsFrame(GameSnapshot template, string matchId, long sequence, string? outcome, GamePhase phase = GamePhase.Complete)
    {
        var entities = template.Entities.ToDictionary();
        foreach (var entity in entities.Values.Where(entity => entity.Kind is EntityKind.Game or EntityKind.Player).ToArray())
        {
            var tags = entity.Tags.ToDictionary(); tags.Remove("PLAYSTATE");
            if (entity.Kind == EntityKind.Game) tags["STATE"] = phase == GamePhase.Complete ? "COMPLETE" : "RUNNING";
            if (entity.GetIntTag("PLAYER_ID") == 1 && outcome is not null) tags["PLAYSTATE"] = outcome;
            entities[entity.Id] = entity with { Tags = tags.ToFrozenDictionary() };
        }
        return template with { MatchId = matchId, EventSequence = sequence, StateVersion = sequence, Phase = phase,
            Entities = entities.ToFrozenDictionary(), DeckTracking = null, PublicPlays = null, Choice = null, RecentEvents = [],
            Format = "STANDARD", GameType = "GT_RANKED", Quality = new(true, true, true, true, 0, []) };
    }
    private static void AssertStats(JsonNode result, int games, int wins, int losses, int unknown)
    {
        var stats = result["stats"] ?? throw new Exception("Historical statistics tool returned no sample");
        Check(stats["Games"]!.GetValue<int>() == games && stats["Wins"]!.GetValue<int>() == wins &&
            stats["Losses"]!.GetValue<int>() == losses && stats["Unknown"]!.GetValue<int>() == unknown,
            "Historical stats bridge used final/current/future outcomes: " + stats.ToJsonString());
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class IntegrationClock : TimeProvider
    { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class LibraryChoiceModelHandler(bool requestTools = true, int selectedEntity = 5000) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            Check(request.Headers.Authorization?.ToString() == "Bearer " + FakeKey, "Fake credential left its HTTP authorization header");
            object Tool(string id, string name, object args) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
            object message = Requests.Count == 1 && requestTools
                ? new { role = "assistant", tool_calls = new[]
                    {
                        Tool("library-snapshot", "get_snapshot", new { }), Tool("library-plan", "get_matchup_plan", new { }),
                        Tool("library-stats", "get_personal_stats", new { scope = "current_matchup", went_first = "current" }),
                        Tool("library-cards", "get_cards", new { card_ids = new[] { "SYN_CARD_01" } }),
                        Tool("library-choice", "validate_choice", new { choice_id = 77, selected_entity_ids = new[] { 5000 } })
                    } }
                : new { role = "assistant", content = JsonSerializer.Serialize(new
                    {
                        summary = "synthetic：选择已验证候选；" + FakeKey + " " + FakeEndpoint,
                        reasons = new[] { "依据当时计划与可见候选；api_key=" + FakeKey }, alternative = "Bearer fixture-private-secret",
                        risks = new[] { "选择后牌效未模拟" }, actions = new[] { new { kind = "choose", source_entity_id = selectedEntity } }, verified_lethal = true
                    }) };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Requests.Count == 1 && requestTools ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 80, completion_tokens = 20, total_tokens = 100 } }), Encoding.UTF8, "application/json") };
        }
    }
}
