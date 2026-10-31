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

internal static class ArchiveBrowsingIntegration
{
    private const string Match = "archive-browsing-match";
    private const string Deck = "archive-browsing-deck";
    private const string Build = "255790";
    private static readonly DateTimeOffset Start = new(2037, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset KnowledgeAt = Start.AddMinutes(30);
    private static readonly DateTimeOffset DecisionAt = Start.AddHours(1);
    private static readonly DateTimeOffset BrowseAt = Start.AddHours(3);

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "archive-browsing", Guid.NewGuid().ToString("N"));
        var database = Path.Combine(directory, "archive.db"); var clock = new Clock { Now = Start };
        var store = new AdviceArchiveStore(database, clock); await store.InitializeAsync();
        var originals = new List<ArchivedAdviceRecord>();
        for (var i = 0; i < 205; i++)
        {
            clock.Now = Start.AddSeconds(i);
            var draft = Draft(i == 0 ? "OLDEST_LITERAL_%_[]_ANSWER" : "ELIGIBLE_ARCHIVE_" + i, version: 1 + i % 10, sequence: 1 + i % 100) with
            {
                UserQuestion = i == 1 ? "QUESTION_LITERAL_%_[]_ONLY" : "当时的问题 " + i,
                Model = "prior-fixture-model", AnalysisRunId = "prior-run-" + i, RequestIds = ["prior-request-" + i]
            };
            originals.Add(await store.SaveAsync(draft, "original-" + i.ToString("D3")));
        }
        var pinnedBrowse = Start.AddMinutes(5);
        var pages = new List<AdviceArchivePage>();
        for (var page = 1; page <= 3; page++) pages.Add(await store.ListPageAsOfAsync(pinnedBrowse, new(MatchId: Match), pageNumber: page, pageSize: 100));
        Check(pages.SelectMany(page => page.Records).Select(record => record.Id).Distinct().Count() == 205 &&
            pages.Select(page => page.Records.Count).SequenceEqual([100, 100, 5]) && pages[0].HasMore && pages[1].HasMore && !pages[2].HasMore &&
            !pages[0].HasPrevious && pages[1].HasPrevious && pages[2].HasPrevious && pages.All(page => page.AsOf == pinnedBrowse) &&
            pages[2].Records[^1].Id == originals[0].Id && (await store.ListAsync(new(MatchId: Match))).All(record => record.Id != originals[0].Id),
            "The paged browser did not expose the real oldest entry beyond the legacy hundred-record list.");
        var openedOld = await store.GetAsync(pages[2].Records[^1].Id);
        Check(openedOld == originals[0] || openedOld?.Id == originals[0].Id && openedOld.Advice.Summary == originals[0].Advice.Summary,
            "Opening an old page row resolved a different archived result.");
        pass("archive browsing exposes all 205 immutable records and opens the real oldest result beyond the previous hundred-record limit");

        clock.Now = Start.AddMinutes(10);
        var wrongMatch = await store.SaveAsync(Draft("FORBIDDEN_OTHER_MATCH") with { MatchId = "different-archive-match" });
        var wrongDeck = await store.SaveAsync(Draft("FORBIDDEN_OTHER_DECK") with { DeckVersionId = "different-archive-deck" });
        var unbound = await store.SaveAsync(Draft("UNBOUND_REAL_RECORD") with { DeckVersionId = null });
        var oldBuild = await store.SaveAsync(Draft("OLD_BUILD_REAL_RECORD") with { CardBuild = "255789", Model = null, AnalysisRunId = null, RequestIds = null });
        var futureState = await store.SaveAsync(Draft("FORBIDDEN_FUTURE_STATE", version: 11));
        var futureEvent = await store.SaveAsync(Draft("FORBIDDEN_FUTURE_EVENT", sequence: 101));
        var legacy = await store.SaveAsync(Draft("LEGACY_UNKNOWN_SOURCE") with { Task = "", Model = null, AnalysisRunId = null,
            RequestIds = null, ConversationArchiveIds = null, KnowledgeAsOf = null, UserQuestion = null });
        const string legacyRawProbe = "SYNTHETIC_UNMAPPED_RAW_FIELD_NEVER_DISPLAY";
        await MakeLegacyPayloadAsync(database, legacy, legacyRawProbe);
        var deckPlanning = await store.SaveAsync(Draft("DECK_PLANNING_ONLY", version: 0, sequence: 0) with { MatchId = "deck-" + Deck, Task = "deck_plan" });
        const string secret = "synthetic-archive-browse-secret";
        const string privateProbe = "SYNTHETIC_PRIVATE_ARCHIVE_PROBE";
        var sanitized = await store.SaveAsync(Draft("SAFE_REDACTED_RECORD " + secret) with
        {
            KnowledgeAsOf = null, UserQuestion = privateProbe,
            Evidence = [new("get_snapshot", "api_key=" + secret + " https://archive-fixture.invalid/private")]
        }, sensitiveValues: [secret, privateProbe]);
        foreach (var raw in new[] { "{\"choices\":[{\"message\":\"" + privateProbe + "\"}]}",
            "D 12:34:56.000 GameState.DebugPrintPower() - " + privateProbe })
            await Throws<ArgumentException>(() => store.SaveAsync(Draft(raw)));
        var oldBuildOpened = await store.GetAsync(oldBuild.Id); var legacyOpened = await store.GetAsync(legacy.Id);
        var sanitizedOpened = await store.GetAsync(sanitized.Id);
        Check(oldBuildOpened?.Advice.CardBuild == "255789" && oldBuildOpened.Advice.Model is null &&
            legacyOpened?.Advice is { Task: "", Model: null, AnalysisRunId: null, RequestIds: null, ConversationArchiveIds: null, KnowledgeAsOf: null, UserQuestion: null } &&
            !JsonSerializer.Serialize(legacyOpened).Contains(legacyRawProbe, StringComparison.Ordinal) &&
            sanitizedOpened is not null && !JsonSerializer.Serialize(sanitizedOpened).Contains(secret, StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(sanitizedOpened).Contains(privateProbe, StringComparison.Ordinal) &&
            !Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)).Contains(secret, StringComparison.Ordinal),
            "Browsing fabricated current build/provenance for an old row or exposed data rejected/redacted before persistence.");
        pass("old-build and legacy unknown-source archives retain their actual fields while raw HTTP logs and supplied private values never reach displayed records");

        clock.Now = Start.AddMinutes(45);
        var laterKnowledge = await store.SaveAsync(Draft("FORBIDDEN_LATER_KNOWLEDGE_SAME_STATE") with
        { KnowledgeAsOf = KnowledgeAt.AddTicks(1), Model = "later-live-model", AnalysisRunId = "later-live-run", RequestIds = ["later-live-request"] });
        clock.Now = Start.AddHours(2);
        var laterReview = await store.SaveAsync(Draft("FORBIDDEN_LATER_REVIEW_SAME_STATE") with
        { Task = "review", KnowledgeAsOf = KnowledgeAt, UserQuestion = "人工查看同一个旧决策后的复盘问题" });
        var late = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match, StateVersion: 10, ThroughEventSequence: 100,
            CardBuild: Build, DeckVersionId: Deck), pageSize: 200);
        Check(late.Records.Any(record => record.Id == laterKnowledge.Id) && late.Records.Any(record => record.Id == laterReview.Id) &&
            (await store.GetAsync(laterReview.Id))?.RecordedAt > DecisionAt && (await store.GetAsync(laterKnowledge.Id))?.Advice.KnowledgeAsOf > KnowledgeAt,
            "Manual browsing hid later analysis of an old state or replaced its real recording/knowledge timestamps.");
        pass("manual archive browsing can open later reviews and later-knowledge answers to an old state without rewriting either time axis");

        var emptyTask = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match) { Task = "" }, pageSize: 200);
        var reviewTask = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match) { Task = "review" }, pageSize: 200);
        var upperTask = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match) { Task = "TURN" }, pageSize: 200);
        var summarySearch = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match) { TextContains = "OLDEST_LITERAL_%_[]" });
        var questionSearch = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match) { TextContains = "QUESTION_LITERAL_%_[]" });
        var wildcardSearch = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match) { TextContains = "DOES_NOT_EXIST_%_[]" });
        Check(emptyTask.Records.Select(record => record.Id).SequenceEqual([legacy.Id]) && reviewTask.Records.Select(record => record.Id).SequenceEqual([laterReview.Id]) &&
            upperTask.Records.Count == 0 && summarySearch.Records.Single().Id == originals[0].Id && questionSearch.Records.Single().Id == originals[1].Id && wildcardSearch.Records.Count == 0,
            "Browser task filtering treated the empty task as all tasks or interpreted literal summary/question text as wildcard syntax.");
        pass("archive task filters are exact including legacy empty tasks and text search finds old summary or question literals without wildcard expansion");

        var capped = await store.ListPageAsOfAsync(BrowseAt, new(MatchId: Match, RecordedBefore: DecisionAt), pageSize: 200);
        var reverseCap = await store.ListPageAsOfAsync(DecisionAt, new(MatchId: Match, RecordedBefore: BrowseAt), pageSize: 200);
        Check(capped.AsOf == DecisionAt && reverseCap.AsOf == DecisionAt && capped.Records.Select(record => record.Id).SequenceEqual(reverseCap.Records.Select(record => record.Id)) &&
            capped.Records.All(record => record.RecordedAt <= DecisionAt) && capped.Records.All(record => record.Id != laterReview.Id),
            "The browser widened one recording cutoff by substituting its other later time bound.");
        var stillPinned = await store.ListPageAsOfAsync(pinnedBrowse, new(MatchId: Match), pageNumber: 3, pageSize: 100);
        Check(stillPinned.Records.Select(record => record.Id).SequenceEqual(pages[2].Records.Select(record => record.Id)),
            "Newly recorded reviews shifted pages pinned to the original browse cutoff.");
        pass("archive pages use the earlier recording cutoff and remain stable when later reviews are appended");

        var reopened = new AdviceArchiveStore(database, clock); await reopened.InitializeAsync();
        var context = Context(); var originalContext = JsonSerializer.Serialize(context);
        foreach (var id in new[] { openedOld!.Id, laterReview.Id, laterKnowledge.Id, legacy.Id, wrongDeck.Id, oldBuild.Id })
            Check(await reopened.GetAsync(id) is not null, "A manually selected archived result could not be opened after restart.");
        var attached = await ConversationHistoryBuilder.AttachAsync(context, reopened, Deck, DecisionAt);
        var expected = originals.TakeLast(4).Select(record => record.Id).ToArray();
        Check(attached.Conversation.Select(turn => turn.ArchiveId).SequenceEqual(expected) && attached.Conversation.Count == 4 && attached.ConversationTruncated &&
            attached.MatchId == context.MatchId && attached.CardBuild == Build && attached.StateVersion == 10 && attached.EventSequence == 100 &&
            attached.KnowledgeAsOf == KnowledgeAt && attached.AsOf == DecisionAt && attached.DeckVersionId == Deck &&
            attached.Conversation.All(turn => turn.MatchId == Match && turn.DeckVersionId == Deck && turn.CardBuild == Build &&
                turn.StateVersion <= 10 && turn.EventSequence <= 100 && turn.RecordedAt <= DecisionAt && turn.KnowledgeAsOf <= KnowledgeAt) &&
            JsonSerializer.Serialize(context) == originalContext,
            "Opening a browse result changed the fixed old decision or attached another scope/time/knowledge horizon.");
        var withoutDeck = await ConversationHistoryBuilder.AttachAsync(context with { DeckVersionId = null }, reopened, null, DecisionAt);
        Check(withoutDeck.Conversation.Count == 1 && withoutDeck.Conversation[0].ArchiveId == unbound.Id, "An unbound followup mixed the real deck's archived conversation.");
        var beforeOld = await ConversationHistoryBuilder.AttachAsync(context with { KnowledgeAsOf = Start }, reopened, Deck, originals[0].RecordedAt);
        Check(beforeOld.Conversation.Count == 1 && beforeOld.Conversation[0].ArchiveId == originals[0].Id && !beforeOld.ConversationTruncated,
            "Following up at the first record inherited a later eligible record.");
        pass("opening old or later browse records leaves historical followup pinned to its original match deck build state event recording and knowledge cutoffs with at most four eligible rounds");

        var realMatchPage = await reopened.ListPageAsOfAsync(BrowseAt, new(MatchId: Match, DeckVersionId: Deck), pageSize: 200);
        var planningPage = await reopened.ListPageAsync(new(MatchId: "deck-" + Deck, DeckVersionId: Deck), pageSize: 200);
        var planningContext = Context() with { MatchId = "deck-" + Deck, StateVersion = 0, EventSequence = 0, Task = "deck_plan",
            Snapshot = new JsonObject { ["event_sequence"] = 0, ["card_build"] = Build, ["known_current_fact"] = "DECK_PLANNING_FRAME" } };
        var planning = await ConversationHistoryBuilder.AttachAsync(planningContext, reopened, Deck, DecisionAt);
        Check(realMatchPage.Records.All(record => record.Id != deckPlanning.Id) && planningPage.Records.Single().Id == deckPlanning.Id &&
            planning.Conversation.Count == 1 && planning.Conversation[0].ArchiveId == deckPlanning.Id && attached.Conversation.All(turn => turn.ArchiveId != deckPlanning.Id) &&
            !attached.Conversation.Any(turn => new[] { wrongMatch.Id, wrongDeck.Id, oldBuild.Id, legacy.Id, futureState.Id, futureEvent.Id, laterKnowledge.Id, laterReview.Id, sanitized.Id }.Contains(turn.ArchiveId)),
            "A deck planning pseudo-match was treated as the deck's real match or scope-ineligible records entered history.");
        pass("deck-id planning archives and that deck's actual match archives remain separate during browsing and conversation attachment");

        using var handler = new HistoryModel(expected, forbidden: ["FORBIDDEN_", "LEGACY_UNKNOWN_SOURCE", "OLD_BUILD_REAL_RECORD", "DECK_PLANNING_ONLY", secret, privateProbe, legacyRawProbe]);
        using var http = new HttpClient(handler);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://archive-browsing-fixture.invalid/v1/chat/completions"), "archive-browsing-fixture"),
            "archive-browsing-unused-key", attached, (_, _, _) => throw new InvalidOperationException("The fake model should not call tools."), () => true);
        Check(handler.Calls == 1 && handler.CheckedHistory && advice.ConversationArchiveIds.SequenceEqual(expected) && advice.KnowledgeAsOf == KnowledgeAt &&
            advice.MatchId == Match && advice.StateVersion == 10 && advice.Actions.Count == 0 && !advice.VerifiedLethal,
            "Fake HTTP lost eligible conversation source IDs or widened the old decision's knowledge scope.");
        pass("fake HTTP receives only the four eligible chronological opinions with exact provenance and the unchanged authoritative old-decision snapshot");

        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json")));
        var catalog = new CardCatalog(Path.Combine(directory, "verified-end-turn-cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://archive-verified-end-turn");
        var endFrame = EndTurnFrame(); var endFrameBefore = JsonSerializer.Serialize(endFrame);
        var endContext = CompactAdviceContextBuilder.Create(endFrame, Build, AdviceTaskKind.Turn) with { AsOf = BrowseAt, KnowledgeAsOf = KnowledgeAt };
        var endObservations = new List<CoachToolObservation>();
        var endCoach = new SnapshotCoach(endFrame, endContext.Snapshot, catalog, Build, toolCompleted: endObservations.Add);
        using var endHandler = new EndTurnModel(); using var endHttp = new HttpClient(endHandler);
        var endAdvice = await new AdviceService(endHttp).AnalyzeAsync(new(new Uri("https://archive-end-turn-fixture.invalid/v1/chat/completions"), "archive-end-turn-fixture"),
            "archive-end-turn-unused-key", endContext, endCoach.ExecuteAsync, () => true, capabilities: endCoach.Capabilities());
        Check(endHandler.Calls == 2 && endHandler.CheckedVerifiedLine && endAdvice.Verification == "verified" &&
            endAdvice.Actions is [{ Kind: "end_turn", SourceEntityId: 2, TargetEntityId: null }] && !endAdvice.VerifiedLethal &&
            endObservations.Single() is { Name: "evaluate_line", Success: true, ResultHash.Length: 64 } && endFrameBefore == JsonSerializer.Serialize(endFrame),
            "A real host-verified legal end-turn action was removed, acquired a target or bypassed validation before archival.");
        // Match MainWindow.ArchiveAdviceAsync: preserve the host result and action source in the allowlisted draft.
        var endDraft = new AdviceRecordDraft(endFrame.MatchId, endFrame.StateVersion, endFrame.EventSequence, Build, endContext.TemplateVersion, endAdvice.Summary)
        {
            DeckVersionId = Deck, Task = endContext.Task, UserQuestion = endContext.UserQuestion, Reasons = endAdvice.Reasons, Alternative = endAdvice.Alternative,
            Risks = endAdvice.Risks, Verification = endAdvice.Verification, VerifiedLethal = endAdvice.VerifiedLethal,
            Actions = endAdvice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = endObservations.Select(observation => new AdviceToolEvidence(observation.Name, "实际主机合法性核验通过", observation.ResultHash)).ToArray(),
            Model = endAdvice.Model, AnalysisRunId = endAdvice.AnalysisRunId, RequestIds = endAdvice.RequestIds,
            ConversationArchiveIds = endAdvice.ConversationArchiveIds, KnowledgeAsOf = endAdvice.KnowledgeAsOf
        };
        var preparedEnd = AdviceArchiveStore.Prepare(endDraft, ["archive-end-turn-unused-key"]);
        var savedEnd = await reopened.SaveAsync(preparedEnd);
        var restoredEnd = await new AdviceArchiveStore(database, clock).GetAsync(savedEnd.Id);
        Check(restoredEnd?.Advice is { Verification: "verified", VerifiedLethal: false, Actions: [{ Kind: "end_turn", SourceEntityId: 2, TargetEntityId: null }] } &&
            restoredEnd.Advice.MatchId == endFrame.MatchId && restoredEnd.Advice.StateVersion == endFrame.StateVersion &&
            restoredEnd.Advice.EventSequence == endFrame.EventSequence && restoredEnd.Advice.Model == endAdvice.Model &&
            restoredEnd.Advice.RequestIds!.SequenceEqual(endAdvice.RequestIds) && restoredEnd.Advice.Evidence.Single().ResultHash == endObservations.Single().ResultHash,
            "App-equivalent archival rejected or changed the validated own PLAYER source or its exact provenance.");
        var legacyEnd = await reopened.SaveAsync(endDraft with { Summary = "旧版未记录来源的结束回合", Actions = [new("end_turn", null)] });
        Check((await reopened.GetAsync(legacyEnd.Id))?.Advice.Actions is [{ Kind: "end_turn", SourceEntityId: null, TargetEntityId: null }],
            "Legacy source-less archived end-turn compatibility was lost.");
        foreach (var invalidAction in new[] { new ArchivedAdviceAction("end_turn", 0), new("end_turn", -1), new("end_turn", 2, 11), new("end_turn", null, 11) })
            await Throws<ArgumentException>(() => reopened.SaveAsync(endDraft with { Actions = [invalidAction] }));
        pass("real fake-HTTP host-verified legal end turn retains its positive own PLAYER source through App-equivalent Prepare Save and Get while legacy null sources remain readable and invalid sources or targets are rejected");
    }

    private static AdviceRecordDraft Draft(string summary, long version = 10, long sequence = 100) => new(Match, version, sequence, Build, "hunter-v13/turn", summary)
    { DeckVersionId = Deck, Task = "turn", UserQuestion = "当时的问题", KnowledgeAsOf = Start };
    private static AdviceContext Context() => new(Match, 10, "review", new JsonObject
    { ["event_sequence"] = 100, ["card_build"] = Build, ["known_current_fact"] = "OLD_DECISION_FRAME" })
    { CardBuild = Build, DeckVersionId = Deck, EventSequence = 100, AsOf = DecisionAt, KnowledgeAsOf = KnowledgeAt, UserQuestion = "仍只针对这个旧决策继续解释。" };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<ArchivedAdviceRecord>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private static GameSnapshot EndTurnFrame()
    {
        var engine = new PowerLogStateEngine(1, "archive-verified-end-turn");
        foreach (var line in new[] { $"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=CARDTYPE value=HERO", "tag=ZONE value=PLAY", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0",
            "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=CARDTYPE value=HERO", "tag=ZONE value=PLAY", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete" })
            engine.ProcessLine(line);
        return engine.Snapshot();
    }
    private static async Task MakeLegacyPayloadAsync(string database, ArchivedAdviceRecord legacy, string rawProbe)
    {
        // A synthetic preexisting payload exercises legacy missing fields and typed display allowlisting.
        var payload = JsonSerializer.SerializeToNode(legacy.Advice)!.AsObject(); payload.Remove("Task");
        payload["RawHttpBody"] = rawProbe; payload["RawLogLine"] = rawProbe;
        var json = payload.ToJsonString();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE coach_advice_records SET payload_json=$json,content_hash=$hash WHERE id=$id";
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        command.Parameters.AddWithValue("$id", legacy.Id); await command.ExecuteNonQueryAsync();
    }

    private sealed class HistoryModel(IReadOnlyList<string> expected, IReadOnlyList<string> forbidden) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedHistory { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token);
            Check(forbidden.All(probe => !text.Contains(probe, StringComparison.Ordinal)) && !text.Contains("archive-browsing-unused-key", StringComparison.Ordinal),
                "A manually opened excluded archive, private value or credential entered the model body.");
            var messages = JsonNode.Parse(text)!["messages"]!.AsArray();
            var prior = messages.Where(message => message?["role"]?.ToString() == "assistant").Select(message => JsonNode.Parse(message!["content"]!.GetValue<string>())!).ToArray();
            Check(prior.Length == 4 && prior.Select(opinion => opinion["source"]!["ArchiveId"]!.GetValue<string>()).SequenceEqual(expected) &&
                prior.All(opinion => opinion["is_current_fact"]?.GetValue<bool>() == false && opinion["source"]?["MatchId"]?.ToString() == Match &&
                    opinion["source"]?["DeckVersionId"]?.ToString() == Deck && opinion["source"]?["CardBuild"]?.ToString() == Build &&
                    opinion["source"]?["Model"]?.ToString() == "prior-fixture-model" && opinion["source"]?["RequestIds"]!.AsArray().Count == 1),
                "Historical opinions lost exact source attribution, scope or the non-fact boundary.");
            var current = JsonNode.Parse(messages[^1]!["content"]!.GetValue<string>())!;
            Check(current["MatchId"]?.ToString() == Match && current["StateVersion"]?.GetValue<long>() == 10 && current["EventSequence"]?.GetValue<long>() == 100 &&
                DateTimeOffset.Parse(current["KnowledgeAsOf"]!.ToString()) == KnowledgeAt && DateTimeOffset.Parse(current["AsOf"]!.ToString()) == DecisionAt &&
                current["Snapshot"]?["known_current_fact"]?.ToString() == "OLD_DECISION_FRAME" && current["Conversation"]!.AsArray().Count == 0,
                "Browsing replaced the old decision with the opened record or duplicated history into current facts.");
            CheckedHistory = true;
            var message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "只依据固定旧决策解释，前答仍是意见。", reasons = new[] { "当前固定局面优先" }, risks = new[] { "不沿用后来才知道的资料" } }) };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message } },
                usage = new { prompt_tokens = 40, completion_tokens = 20, total_tokens = 60 } }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class EndTurnModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedVerifiedLine { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; object message;
            if (Calls == 1)
                message = new { role = "assistant", tool_calls = new[] { new { id = "verified-end-turn", type = "function", function = new {
                    name = "evaluate_line", arguments = "{\"actions\":[{\"kind\":\"end_turn\",\"source_entity_id\":2}]}" } } } };
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var line = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(line["Verification"]?.ToString() == "Verified" && line["IsLegal"]?.GetValue<bool>() == true &&
                    line["AppliedActions"]?.GetValue<int>() == 1 && line["Result"]?["TurnEnded"]?.GetValue<bool>() == true &&
                    line["Result"]?["EndTurnTriggersResolved"]?.GetValue<bool>() == true && line["IsLethal"]?.GetValue<bool>() == false,
                    "The archive test did not obtain a real complete and legal host-verified end turn: " + line.ToJsonString());
                CheckedVerifiedLine = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "按已核验的当前我方玩家实体结束回合。", reasons = new[] { "普通结束回合已由主机验证" },
                    risks = Array.Empty<string>(), actions = new[] { new { kind = "end_turn", source_entity_id = 2 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 40, completion_tokens = 20, total_tokens = 60 } }), Encoding.UTF8, "application/json") };
        }
    }
}
