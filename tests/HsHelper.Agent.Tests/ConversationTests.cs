using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

static class ConversationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("conversation has separate historical roles and current snapshot authority", Roles);
        yield return ("future mismatched duplicate and oversized conversation fails before HTTP", Boundaries);
        yield return ("conversation caller mutations cannot change later model rounds", ImmutableMessages);
        yield return ("analysis HTTP usage and coach observations have exact shared provenance", Provenance);
        yield return ("failed HTTP retains a unique analysis and request source", FailedSource);
        yield return ("SQLite conversation builder excludes other bindings and future answers", ArchiveBuilder);
        yield return ("historical followup cannot inherit a live answer that used later notes", KnowledgeHorizon);
        yield return ("model URLs are rejected before any HTTP or usage recording", ModelUrl);
        yield return ("credential-bearing model names fail safely without rejecting short-token substrings", ModelCredential);
    }
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly ModelSettings Settings = new(new Uri("https://fixture.invalid/v1/chat/completions"), "fixture-model");
    private static AdviceContext Context() => new("conversation-match", 5, "turn", new JsonObject
    { ["event_sequence"] = 5, ["card_build"] = "255790", ["known_current_fact"] = "CURRENT_SNAPSHOT" })
    { CardBuild = "255790", DeckVersionId = "deck-a", EventSequence = 5, AsOf = Cutoff, KnowledgeAsOf = Cutoff, UserQuestion = "为什么不采用刚才的方案？" };
    private static AdviceConversationTurn Turn() => new("archive-old", "conversation-match", 4, 4, "255790", "deck-a", Cutoff.AddMinutes(-1),
        "上一轮问题", "OLD_OPINION_MAY_BE_WRONG") { Model = "prior-model", AnalysisRunId = "prior-run", RequestIds = ["prior-request"], KnowledgeAsOf = Cutoff.AddMinutes(-1) };
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Response(bool tool = false, string? summary = null, string toolName = "get_snapshot") => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = tool
            ? new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "call-1", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = toolName, ["arguments"] = "{}" } }) }
            : new JsonObject { ["content"] = new JsonObject { ["summary"] = summary ?? "继续依据当前局面判断", ["reasons"] = new JsonArray("当前信息优先"), ["risks"] = new JsonArray("前答可能有误") }.ToJsonString() } }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 15, ["completion_tokens"] = 3, ["total_tokens"] = 18 }
    }.ToJsonString();
    private static Task<StrategyAdvice> Analyze(FakeHandler handler, AdviceContext context, AgentCapabilities? capabilities = null,
        Func<string, JsonObject, CancellationToken, Task<JsonNode>>? execute = null)
        => new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings, "fake-credential", context,
            execute ?? ((_, _, _) => Task.FromResult<JsonNode>(new JsonObject())), () => true, capabilities: capabilities);

    private static async Task Roles()
    {
        var handler = new FakeHandler((_, body) =>
        {
            var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
            Check(messages.Select(m => m!["role"]!.GetValue<string>()).SequenceEqual(new[] { "system", "user", "assistant", "user" }), "Historical roles were not separated.");
            Check(messages[0]!["content"]!.GetValue<string>().Contains("前答不是事实", StringComparison.Ordinal), "History lacked a non-fact boundary.");
            var prior = JsonNode.Parse(messages[2]!["content"]!.GetValue<string>())!;
            Check(!prior["is_current_fact"]!.GetValue<bool>() && prior["source"]!["ArchiveId"]!.GetValue<string>() == "archive-old", "Prior answer lost its source or became a current fact.");
            var current = JsonNode.Parse(messages[^1]!["content"]!.GetValue<string>())!;
            Check(current["Snapshot"]!["known_current_fact"]!.GetValue<string>() == "CURRENT_SNAPSHOT" && current["Conversation"]!.AsArray().Count == 0 &&
                current["UserQuestion"]!.GetValue<string>().Contains("刚才", StringComparison.Ordinal), "Current frame/question was overwritten or history duplicated.");
            return Reply(Response());
        });
        var result = await Analyze(handler, Context() with { Conversation = [Turn()] });
        Check(result.ConversationArchiveIds.SequenceEqual(new[] { "archive-old" }) && result.Model == Settings.Model && result.AnalysisRunId != "prior-run" &&
            result.RequestIds.Single() == result.Usage.Single().RequestId, "Advice did not retain exact history/request provenance.");
    }
    private static async Task Boundaries()
    {
        var turn = Turn();
        var invalid = new[]
        {
            Context() with { Conversation = [turn with { MatchId = "another-match" }] },
            Context() with { Conversation = [turn with { DeckVersionId = null }] },
            Context() with { Conversation = [turn with { CardBuild = "255791" }] },
            Context() with { Conversation = [turn with { StateVersion = 6 }] },
            Context() with { Conversation = [turn with { EventSequence = 6 }] },
            Context() with { Conversation = [turn with { RecordedAt = Cutoff.AddTicks(1) }] },
            Context() with { Conversation = [turn, turn] },
            Context() with { Conversation = Enumerable.Range(0, 5).Select(i => turn with { ArchiveId = "archive-" + i }).ToArray() },
            Context() with { Conversation = [turn with { Answer = new string('x', 2001) }] },
            Context() with { Conversation = [turn with { UserQuestion = new string('x', 1001) }] },
            Context() with { Conversation = [turn], AsOf = null },
            Context() with { Conversation = [turn], EventSequence = null },
            Context() with { Conversation = [turn], KnowledgeAsOf = null },
            Context() with { Conversation = [turn with { KnowledgeAsOf = null }] },
            Context() with { Conversation = [turn with { KnowledgeAsOf = Cutoff.AddTicks(1) }] },
            Context() with { Conversation = [turn], CardBuild = "different-current-build" },
            Context() with { EventSequence = 6 },
            Context() with { CardBuild = "different-current-build" },
            Context() with { Conversation = [turn with { ArchiveId = "newer", RecordedAt = Cutoff }, turn] }
        };
        foreach (var context in invalid)
        {
            var handler = new FakeHandler((_, _) => Reply(Response()));
            await Throws<ArgumentException>(() => Analyze(handler, context));
            Check(handler.Calls == 0, "Invalid conversation reached the model.");
        }
    }
    private static async Task ImmutableMessages()
    {
        var ids = new List<string> { "prior-request" }; var turns = new List<AdviceConversationTurn> { Turn() with { RequestIds = ids } };
        var handler = new FakeHandler((round, body) =>
        {
            Check(body.Contains("OLD_OPINION_MAY_BE_WRONG", StringComparison.Ordinal) && !body.Contains("CHANGED_AFTER_SEND", StringComparison.Ordinal), "Caller mutation changed pinned history.");
            if (round == 1) { turns[0] = Turn() with { Answer = "CHANGED_AFTER_SEND" }; ids[0] = "CHANGED_AFTER_SEND"; }
            return Reply(Response(round == 1));
        });
        var advice = await Analyze(handler, Context() with { Conversation = turns });
        Check(handler.Calls == 2 && advice.ConversationArchiveIds.Single() == "archive-old", "Tool round did not preserve frozen conversation.");
    }
    private static async Task Provenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "hs-helper-conversation-tests", Guid.NewGuid().ToString("N"));
        var catalog = new CardCatalog(Path.Combine(root, "cards.db")); await catalog.InitializeAsync();
        var snapshot = new GameSnapshot("conversation-match", 5, 5, 1, 1, 1, GamePhase.Main,
            FrozenDictionary<int, EntitySnapshot>.Empty, new(true, true, true, true, 0, []));
        var context = CompactAdviceContextBuilder.Create(snapshot, "255790", AdviceTaskKind.Turn) with { KnowledgeAsOf = DateTimeOffset.UtcNow };
        var observed = new List<CoachToolObservation>(); var usage = new List<ModelUsage>();
        var coach = new SnapshotCoach(snapshot, context.Snapshot, catalog, "255790", toolCompleted: observed.Add);
        var handler = new FakeHandler((round, _) => Reply(Response(round == 1)));
        var advice = await Analyze(handler, context, coach.Capabilities(onUsage: usage.Add), coach.ExecuteAsync);
        Check(usage.Count == 2 && usage.All(u => u.AnalysisRunId == advice.AnalysisRunId && u.EventSequence == 5 && u.CardBuild == "255790" &&
            u.PromptTemplateVersion == advice.TemplateVersion) && usage.Select(u => u.RequestId).Distinct().Count() == 2, "HTTP rounds lost their run, frame or template source.");
        Check(observed is [{ Name: "get_snapshot", ToolCallId: "call-1", EventSequence: 5 }] && observed[0].AnalysisRunId == advice.AnalysisRunId &&
            observed[0].RequestId == usage[0].RequestId && observed[0].Model == Settings.Model, "Coach tool source did not match the HTTP round that requested it.");
        var traces = new AnalysisTraceStore(Path.Combine(root, "traces.db")); await traces.InitializeAsync();
        await traces.AppendBatchAsync(usage.Select(u => new ModelRequestTrace(u.RequestId, u.MatchId, u.StateVersion, u.Model, u.Round,
            u.InputTokens, u.OutputTokens, u.TotalTokens, u.ChargedTokens, u.ElapsedMilliseconds, AnalysisTraceStatus.Completed, u.ConservativeCharge)
        { AnalysisRunId = u.AnalysisRunId, EventSequence = u.EventSequence, CardBuild = u.CardBuild, DeckVersionId = u.DeckVersionId,
            PromptTemplateVersion = u.PromptTemplateVersion! }), observed.Select((o, i) => new ToolExecutionTrace("observed-" + i,
                snapshot.MatchId, snapshot.StateVersion, o.Name, o.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { Model = o.Model, AnalysisRunId = o.AnalysisRunId, RequestId = o.RequestId, ToolCallId = o.ToolCallId, EventSequence = o.EventSequence }));
        var archive = new AdviceArchiveStore(Path.Combine(root, "archive.db")); await archive.InitializeAsync();
        var saved = await archive.SaveAsync(new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, "255790", advice.TemplateVersion!, advice.Summary)
        { Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, ConversationArchiveIds = advice.ConversationArchiveIds, KnowledgeAsOf = advice.KnowledgeAsOf });
        var persistedRequests = await traces.ListRequestsAsync(snapshot.MatchId); var persistedTool = (await traces.ListToolsAsync(snapshot.MatchId)).Single();
        Check(saved.Advice.RequestIds!.ToHashSet(StringComparer.Ordinal).SetEquals(persistedRequests.Select(r => r.RequestId)) &&
            persistedRequests.All(r => r.AnalysisRunId == saved.Advice.AnalysisRunId) && persistedTool.AnalysisRunId == saved.Advice.AnalysisRunId &&
            saved.Advice.RequestIds!.Contains(persistedTool.RequestId!), "Saved advice did not join its precise persisted HTTP/tool sources.");
        var followup = await ConversationHistoryBuilder.AttachAsync(context, archive, null, DateTimeOffset.UtcNow);
        Check(followup.Conversation.Single() is { } previous && previous.ArchiveId == saved.Id && previous.AnalysisRunId == advice.AnalysisRunId &&
            previous.RequestIds.SequenceEqual(advice.RequestIds), "Next-turn conversation lost archive-to-request provenance.");
        var another = await Analyze(new FakeHandler((_, _) => Reply(Response())), context);
        Check(another.AnalysisRunId != advice.AnalysisRunId && !advice.RequestIds.Contains(another.RequestIds.Single()), "Separate analyses reused source identifiers.");
    }
    private static async Task FailedSource()
    {
        var usage = new List<ModelUsage>(); var handler = new FakeHandler((_, _) => Reply("private raw response", HttpStatusCode.Unauthorized));
        await Throws<HttpRequestException>(() => Analyze(handler, Context(), new() { OnUsage = usage.Add }));
        Check(usage is [{ Success: false, ChargedTokens: 0 }] && !string.IsNullOrEmpty(usage[0].AnalysisRunId) &&
            !string.IsNullOrEmpty(usage[0].RequestId) && usage[0].EventSequence == 5, "Failed request had no stable source attribution.");
    }
    private static async Task ArchiveBuilder()
    {
        var path = Path.Combine(Path.GetTempPath(), "hs-helper-conversation-tests", Guid.NewGuid().ToString("N"), "archive.db");
        var clock = new Clock { Now = Cutoff.AddHours(-1) }; var archive = new AdviceArchiveStore(path, clock); await archive.InitializeAsync();
        AdviceRecordDraft Draft(string answer, long version, long sequence) => new("conversation-match", version, sequence, "255790", "hunter-v3/turn", answer)
        { DeckVersionId = "deck-a", UserQuestion = "问题 " + answer, KnowledgeAsOf = clock.Now };
        var first = await archive.SaveAsync(Draft("answer-one", 1, 1)); clock.Now = clock.Now.AddMinutes(1);
        var second = await archive.SaveAsync(Draft("answer-two", 2, 2) with { Model = "source-model", AnalysisRunId = "source-run", RequestIds = ["source-request"] }); clock.Now = clock.Now.AddMinutes(1);
        var third = await archive.SaveAsync(Draft("answer-three", 3, 3)); clock.Now = clock.Now.AddMinutes(1);
        await archive.SaveAsync(Draft("FUTURE_EVENT", 4, 6)); await archive.SaveAsync(Draft("FUTURE_VERSION", 6, 4));
        await archive.SaveAsync(Draft("OTHER_DECK", 4, 4) with { DeckVersionId = "deck-b" });
        await archive.SaveAsync(Draft("OTHER_BUILD", 4, 4) with { CardBuild = "255791" });
        await archive.SaveAsync(Draft("OTHER_MATCH", 4, 4) with { MatchId = "another-match" });
        clock.Now = Cutoff.AddDays(1); await archive.SaveAsync(Draft("FUTURE_RECORDING", 4, 4));
        var context = await ConversationHistoryBuilder.AttachAsync(Context(), archive, "deck-a", Cutoff, 2);
        Check(context.ConversationTruncated && context.Conversation.Select(t => t.ArchiveId).SequenceEqual(new[] { second.Id, third.Id }) &&
            context.Conversation[0].RequestIds.Single() == "source-request" && context.Conversation[1].Model is null, "Archive history crossed a binding/time limit or attributed unknown legacy sources.");
        var before = await ConversationHistoryBuilder.AttachAsync(Context() with { KnowledgeAsOf = first.Advice.KnowledgeAsOf }, archive, "deck-a", first.RecordedAt);
        Check(before.Conversation.Count == 1 && before.Conversation[0].ArchiveId == first.Id, "Older as-of included a later answer.");
        await Analyze(new FakeHandler((_, _) => Reply(Response())), context);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private static async Task KnowledgeHorizon()
    {
        var root = Path.Combine(Path.GetTempPath(), "hs-helper-conversation-tests", Guid.NewGuid().ToString("N"));
        var clock = new Clock { Now = Cutoff.AddHours(-1) };
        var knowledge = new CoachKnowledgeStore(Path.Combine(root, "knowledge.db"), clock); await knowledge.InitializeAsync();
        var plan = new CoachKnowledgeDraft("deck-a", CoachKnowledgeKind.DeckPlan, "对阵计划", "OLD_PLAN");
        var original = await knowledge.SaveAsync(plan);
        clock.Now = Cutoff.AddMinutes(30); await knowledge.SaveAsync(plan with { Content = "FUTURE_NOTE" }, original.EntryId, original.Version);
        var sessions = new SessionStore(Path.Combine(root, "sessions.db")); await sessions.InitializeAsync();
        var cards = new CardCatalog(Path.Combine(root, "cards.db")); await cards.InitializeAsync();
        var snapshot = new GameSnapshot("conversation-match", 5, 5, 1, 1, 1, GamePhase.Main,
            FrozenDictionary<int, EntitySnapshot>.Empty, new(true, true, true, true, 0, []));
        var baseContext = CompactAdviceContextBuilder.Create(snapshot, "255790", AdviceTaskKind.Turn) with { DeckVersionId = "deck-a" };
        var liveCutoff = Cutoff.AddMinutes(45);
        var liveContext = baseContext with { AsOf = liveCutoff, KnowledgeAsOf = liveCutoff };
        var liveLibrary = new CoachLibraryContext(sessions, knowledge, new(snapshot.MatchId, 5, "255790", "deck-a", liveCutoff, EventSequence: 5));
        var liveCoach = new SnapshotCoach(snapshot, liveContext.Snapshot, cards, "255790", library: liveLibrary);
        var liveHandler = new FakeHandler((round, body) =>
        {
            if (round == 2) Check(body.Contains("FUTURE_NOTE", StringComparison.Ordinal), "Live fixture did not actually read the later plan revision.");
            return Reply(Response(round == 1, "基于新增计划 FUTURE_NOTE", "get_matchup_plan"));
        });
        var liveAdvice = await Analyze(liveHandler, liveContext, liveCoach.Capabilities(), liveCoach.ExecuteAsync);
        clock.Now = Cutoff.AddHours(1);
        var archive = new AdviceArchiveStore(Path.Combine(root, "archive.db"), clock); await archive.InitializeAsync();
        var liveRecord = await archive.SaveAsync(new(snapshot.MatchId, 5, 5, "255790", liveAdvice.TemplateVersion!, liveAdvice.Summary)
        { DeckVersionId = "deck-a", KnowledgeAsOf = liveAdvice.KnowledgeAsOf, Model = liveAdvice.Model,
            AnalysisRunId = liveAdvice.AnalysisRunId, RequestIds = liveAdvice.RequestIds });
        var historicalContext = baseContext with { KnowledgeAsOf = Cutoff };
        var initiallyEmpty = await ConversationHistoryBuilder.AttachAsync(historicalContext, archive, "deck-a", Cutoff.AddHours(2));
        Check(initiallyEmpty.Conversation.Count == 0 && (await archive.GetAsync(liveRecord.Id)) is not null,
            "Later live opinion on the exact same state was admitted to old-point history or erased instead of filtered.");
        var historicalRecord = await archive.SaveAsync(new(snapshot.MatchId, 5, 5, "255790", "hunter-v3/turn", "历史回答 OLD_PLAN")
        { DeckVersionId = "deck-a", KnowledgeAsOf = Cutoff });
        var followup = await ConversationHistoryBuilder.AttachAsync(historicalContext, archive, "deck-a", Cutoff.AddHours(2));
        Check(followup.Conversation.Single().ArchiveId == historicalRecord.Id, "Eligible same-point historical followup was lost.");
        var historicalLibrary = new CoachLibraryContext(sessions, knowledge, new(snapshot.MatchId, 5, "255790", "deck-a", Cutoff, EventSequence: 5));
        var historicalCoach = new SnapshotCoach(snapshot, followup.Snapshot, cards, "255790", library: historicalLibrary);
        var mismatchedCoach = new SnapshotCoach(snapshot, followup.Snapshot, cards, "255790", library: liveLibrary);
        await Throws<InvalidDataException>(() => Analyze(new FakeHandler((_, _) => Reply(Response(true, toolName: "get_matchup_plan"))),
            followup, mismatchedCoach.Capabilities(), mismatchedCoach.ExecuteAsync));
        await Analyze(new FakeHandler((round, body) =>
        {
            Check(!body.Contains("FUTURE_NOTE", StringComparison.Ordinal) && body.Contains("OLD_PLAN", StringComparison.Ordinal),
                "Historical followup received later note text through either prior answers or fresh tools.");
            return Reply(Response(round == 1, toolName: "get_matchup_plan"));
        }), followup, historicalCoach.Capabilities(), historicalCoach.ExecuteAsync);
    }
    private static async Task ModelUrl()
    {
        const string value = "https://private-model.example/path";
        var handler = new FakeHandler((_, _) => Reply(Response())); var usage = new List<ModelUsage>();
        var error = await Throws<ArgumentException>(() => new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings with { Model = value },
            "fake-credential", Context(), (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true,
            capabilities: new() { OnUsage = usage.Add }));
        Check(handler.Calls == 0 && usage.Count == 0 && !error.ToString().Contains(value, StringComparison.Ordinal), "Model URL escaped into HTTP, usage or an error message.");
    }
    private static async Task ModelCredential()
    {
        foreach (var (model, key) in new[] { ("prefix-fake-private-credential-suffix", "fake-private-credential"), ("x", "x") })
        {
            var handler = new FakeHandler((_, _) => Reply(Response())); var usage = new List<ModelUsage>();
            var error = await Throws<ArgumentException>(() => new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings with { Model = model },
                key, Context(), (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true, capabilities: new() { OnUsage = usage.Add }));
            Check(handler.Calls == 0 && usage.Count == 0 && !error.Message.Contains(model, StringComparison.Ordinal) &&
                (key.Length < 8 || !error.ToString().Contains(key, StringComparison.Ordinal)), "Credential-like model name leaked or reached the provider.");
        }
        var valid = new FakeHandler((_, _) => Reply(Response()));
        await new AdviceService(new HttpClient(valid)).AnalyzeAsync(Settings with { Model = "fixture-x-model" }, "x", Context(),
            (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true);
        Check(valid.Calls == 1, "A short ordinary token substring was incorrectly treated as a credential-bearing model.");
    }
}
