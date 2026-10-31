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

internal static class DecisionReplayIntegration
{
    private const string Build = "255790";
    private const string LaterCard = "PUBLIC_LATER_DECISION_REPLAY_CARD";
    private const string Key = "decision-replay-fake-key";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "decision-replay", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "sessions.db");
        var store = new SessionStore(database); await store.InitializeAsync();
        var deck = await store.SaveDeckVersionAsync("精确旧决策来源", new(2, [31], [new(120970, 2)], []), Build);
        var seed = SeedFrame();
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddHours(-1) };
        var knowledgeDatabase = Path.Combine(directory, "knowledge.db");
        var knowledge = new CoachKnowledgeStore(knowledgeDatabase, clock); await knowledge.InitializeAsync();
        var planDraft = new CoachKnowledgeDraft(deck.Id, CoachKnowledgeKind.DeckPlan, "当时计划", "PLAN_REPLAY_V1", "MAGE", true);
        var noteDraft = new CoachKnowledgeDraft(deck.Id, CoachKnowledgeKind.MatchNote, "当时笔记", "NOTE_REPLAY_V1", "MAGE", true, seed.MatchId) { EventSequence = 1 };
        var plan = await knowledge.SaveAsync(planDraft); var note = await knowledge.SaveAsync(noteDraft);

        var recorder = new SessionRecorder(store, new(deck.Id, Build, 1, PlayerClass: "HUNTER"));
        recorder.Observe(seed, 1);
        var early = seed with { EventSequence = 40, StateVersion = 2 };
        recorder.Observe(early, 40); await recorder.FlushAsync();
        var publicCard = new EntitySnapshot(30, LaterCard, EntityKind.Card, new Dictionary<string, string>
        { ["ZONE"] = "PLAY", ["CONTROLLER"] = "2", ["CARDTYPE"] = "MINION", ["ATK"] = "1", ["HEALTH"] = "1", ["DAMAGE"] = "0" }.ToFrozenDictionary());
        var later = early with { StateVersion = 3, Entities = early.Entities.Values.Where(e => e.Id != 30).Append(publicCard).ToFrozenDictionary(e => e.Id) };
        recorder.Observe(later, 41);
        var finished = later with
        {
            StateVersion = 4, EventSequence = 41, Phase = GamePhase.Complete,
            Entities = later.Entities.Values.Select(e => e.Id == 2 ? WithTags(e, ("PLAYSTATE", "WON")) : e.Id == 3 ? WithTags(e, ("PLAYSTATE", "LOST")) : e).ToFrozenDictionary(e => e.Id),
            RecentEvents = [new(41, "GameResultObserved", null, null, null, null, null, "RESULT_LATE_WON")]
        };
        recorder.Observe(finished, 42); await recorder.CompleteAsync();
        store = new SessionStore(database); await store.InitializeAsync();
        var points = await store.ListDecisionPointsAsync(seed.MatchId, includeUnstable: true);
        var earlyPoint = points.Single(p => p.StateVersion == 2); var laterPoint = points.Single(p => p.StateVersion == 3);
        var old = await store.GetHistoricalDecisionAsync(earlyPoint.Id) ?? throw new Exception("Missing early saved decision.");
        var current = await store.GetHistoricalDecisionAsync(laterPoint.Id) ?? throw new Exception("Missing later saved decision.");
        Check(await SchemaVersion(database) == 3 && old.Point == earlyPoint && !old.HasUnattributedEvents && !current.HasUnattributedEvents &&
            old.Context is { SessionId: "decision-replay", CardBuild: Build, Snapshot.StateVersion: 2, Snapshot.EventSequence: 40 } &&
            old.Context.DeckVersionId == deck.Id && old.Context.Deck?.Id == deck.Id && old.Context.Snapshot.Entities[30].CardId is null &&
            old.Context.RecentEvents.All(e => e.EventSequence < 40) && old.Context.RecentEvents.Count > 0 &&
            !JsonSerializer.Serialize(old).Contains(LaterCard, StringComparison.Ordinal) && old.Context.Snapshot.Phase == GamePhase.Main &&
            current.Context.Snapshot.Entities[30].CardId == LaterCard && current.Context.RecentEvents.Any(e => e.EventSequence == 40 && e.CardId == LaterCard) &&
            (await store.GetSessionAsync(seed.MatchId))?.Outcome == "Won",
            "Reopened schema-3 exact decision mixed the later same-sequence reveal or final result into its old snapshot/events, or hid the real later reveal.");
        pass("real recorder seed and event-free seq40 SV2 remain exact after seq40 SV3 reveal, final result and schema3 store reopen");

        clock.Now = earlyPoint.RecordedAt.AddHours(1);
        var latePlan = await knowledge.SaveAsync(planDraft with { Content = "PLAN_LATE_V2" }, plan.EntryId, plan.Version);
        var lateNote = await knowledge.SaveAsync(noteDraft with { Content = "NOTE_LATE_V2" }, note.EntryId, note.Version);
        var browsingNote = await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "后来人工打开的笔记", "NOTE_LATE_CREATED", "MAGE", true, seed.MatchId) { EventSequence = 40 });
        clock.Now = browsingNote.RecordedAt.AddMinutes(1);
        knowledge = new CoachKnowledgeStore(knowledgeDatabase, clock); await knowledge.InitializeAsync();
        Check((await knowledge.GetAsOfAsync(plan.EntryId, clock.Now))?.Content == latePlan.Content &&
            (await knowledge.GetAsOfAsync(note.EntryId, clock.Now))?.Content == lateNote.Content && browsingNote.RecordedAt > earlyPoint.RecordedAt,
            "The late-browsing contrast fixture did not expose the later revisions/new note to manual browsing.");
        var binding = new CoachLibraryBinding(old.Context.SessionId, old.Point.StateVersion, old.Context.CardBuild, old.Context.DeckVersionId,
            old.Point.RecordedAt, "MAGE", true, EventSequence: old.Point.EventSequence);
        var library = new CoachLibraryContext(store, knowledge, binding);
        var oldPlan = await library.ExecuteAsync("get_matchup_plan", new(), default);
        Check(oldPlan.ToJsonString().Contains("PLAN_REPLAY_V1", StringComparison.Ordinal) && oldPlan.ToJsonString().Contains("NOTE_REPLAY_V1", StringComparison.Ordinal) &&
            !oldPlan.ToJsonString().Contains("LATE", StringComparison.Ordinal) && DateTimeOffset.Parse(oldPlan["as_of"]!.ToString()) == earlyPoint.RecordedAt,
            "Manually browsing a later note changed the old decision's knowledge cutoff or selected the current plan version.");
        pass("old real decision RecordedAt fixes knowledge v1 even when later plan and note revisions are manually readable");

        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json"))))
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText())))
            await catalog.ImportAsync(input, Build, source: "fixture://decision-replay");
        var observed = old.Context.RecentEvents.Select(e => new ObservedGameEvent(e.EventSequence, e.Kind, e.EntityId, null, e.CardId, e.FromZone, e.ToZone, e.Summary));
        var context = CompactAdviceContextBuilder.Create(old.Context.Snapshot, old.Context.CardBuild, AdviceTaskKind.Review, old.Context.Deck!.Definition,
            recentEvents: observed) with
        {
            DeckVersionId = old.Context.DeckVersionId, AsOf = old.Point.RecordedAt, KnowledgeAsOf = old.Point.RecordedAt,
            UserQuestion = "只依据这个保存的旧决策复盘下一步。"
        };
        var before = JsonSerializer.Serialize(old);
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(old.Context.Snapshot, context.Snapshot, catalog, Build, library: library, toolCompleted: observations.Add, taskKind: AdviceTaskKind.Review);
        using var handler = new ReplayModel(context, old.Point.RecordedAt); using var http = new HttpClient(handler);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://decision-replay.invalid/v1/chat/completions"), "decision-replay-fake-model"), Key,
            context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(handler.Calls == 2 && handler.CheckedTools && advice.MatchId == earlyPoint.SessionId && advice.StateVersion == 2 && advice.KnowledgeAsOf == earlyPoint.RecordedAt &&
            advice.Verification == "verified" && advice.Actions is [{ Kind: "end_turn", SourceEntityId: 2, TargetEntityId: null }] && !advice.VerifiedLethal &&
            observations.Select(o => o.Name).SequenceEqual(["get_snapshot", "get_matchup_plan", "get_personal_stats", "evaluate_line"]) &&
            observations.All(o => o.Success && o.EventSequence == 40 && o.ResultJson is not null && o.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(o.ResultJson)))) &&
            before == JsonSerializer.Serialize(old), "The real compact host/fake HTTP bridge widened history, bypassed local action validation or mutated the frozen decision.");
        pass("compact old decision and real host tools feed fake HTTP only old plans and no future reveal or result while end turn is locally verified");

        var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db"), clock); await archive.InitializeAsync();
        var draft = new AdviceRecordDraft(context.MatchId, context.StateVersion, context.EventSequence!.Value, context.CardBuild!, context.TemplateVersion, advice.Summary)
        {
            DeckVersionId = context.DeckVersionId, Task = context.Task, UserQuestion = context.UserQuestion,
            Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks, Verification = advice.Verification,
            VerifiedLethal = advice.VerifiedLethal, Actions = advice.Actions.Select(a => new ArchivedAdviceAction(a.Kind, a.SourceEntityId, a.TargetEntityId)).ToArray(),
            Evidence = observations.Select(o => new AdviceToolEvidence(o.Name, "固定旧决策的实际本地主机查询", o.ResultHash)).ToArray(),
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds,
            ConversationArchiveIds = advice.ConversationArchiveIds, KnowledgeAsOf = advice.KnowledgeAsOf
        };
        var saved = await archive.SaveAsync(AdviceArchiveStore.Prepare(draft, [Key]));
        archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db"), clock); await archive.InitializeAsync();
        var restored = await archive.GetAsync(saved.Id) ?? throw new Exception("Missing archived replay advice.");
        Check(restored.RecordedAt > old.Point.RecordedAt && restored.Advice.MatchId == earlyPoint.SessionId && restored.Advice.DeckVersionId == deck.Id &&
            restored.Advice.CardBuild == Build && restored.Advice.StateVersion == 2 && restored.Advice.EventSequence == 40 &&
            restored.Advice.KnowledgeAsOf == earlyPoint.RecordedAt && restored.Advice.KnowledgeAsOf != browsingNote.RecordedAt &&
            restored.Advice.Verification == "verified" && restored.Advice.Actions is [{ Kind: "end_turn", SourceEntityId: 2 }] &&
            restored.Advice.Model == advice.Model && restored.Advice.AnalysisRunId == advice.AnalysisRunId && restored.Advice.RequestIds!.SequenceEqual(advice.RequestIds) &&
            restored.Advice.Evidence.Select(e => e.ResultHash).SequenceEqual(observations.Select(o => o.ResultHash)) &&
            !JsonSerializer.Serialize(restored).Contains(LaterCard, StringComparison.Ordinal) && !JsonSerializer.Serialize(restored).Contains("NOTE_LATE", StringComparison.Ordinal),
            "App-equivalent archival changed exact replay provenance or replaced historical KnowledgeAsOf with the late note/browse/save time.");
        pass("App-equivalent archive Prepare Save and reopened Get retain exact old decision deck build state sequence cutoff and host evidence");

        // Re-create the actual old schema-2 omission, rather than guessing attribution from current snapshots or source lines.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE session_events DROP COLUMN observed_state_version; UPDATE hs_storage_schema SET version=2";
            await command.ExecuteNonQueryAsync();
        }
        var migrated = new SessionStore(database); await migrated.InitializeAsync();
        var gap = await migrated.GetHistoricalDecisionAsync(earlyPoint.Id) ?? throw new Exception("Legacy migration lost the early decision.");
        var laterGap = await migrated.GetHistoricalDecisionAsync(laterPoint.Id) ?? throw new Exception("Legacy migration lost the later decision.");
        Check(await SchemaVersion(database) == 3 && gap.HasUnattributedEvents && laterGap.HasUnattributedEvents &&
            gap.Point.RecordedAt == earlyPoint.RecordedAt && gap.Context.Snapshot.Entities[30].CardId is null &&
            gap.Context.RecentEvents.Count > 0 && gap.Context.RecentEvents.All(e => e.EventSequence < 40) &&
            laterGap.Context.Snapshot.Entities[30].CardId == LaterCard && laterGap.Context.RecentEvents.All(e => e.EventSequence < 40) &&
            !JsonSerializer.Serialize(gap.Context).Contains(LaterCard, StringComparison.Ordinal),
            "Schema-2 migration guessed a same-sequence attribution, replaced an exact snapshot, or discarded safe lower-sequence history.");
        var gapView = CompactAdviceContextBuilder.Create(gap.Context.Snapshot, Build, AdviceTaskKind.Review,
            recentEvents: gap.Context.RecentEvents.Select(e => new ObservedGameEvent(e.EventSequence, e.Kind, e.EntityId, null, e.CardId, e.FromZone, e.ToZone, e.Summary)));
        Check(!gapView.Snapshot.ToJsonString().Contains(LaterCard, StringComparison.Ordinal), "Compact replay backfilled an unattributed legacy event from a later exact snapshot.");
        pass("schema2 missing observation versions migrate to explicit gaps without backfilling same-sequence future events or replacing exact snapshots");
    }

    private static EntitySnapshot WithTags(EntitySnapshot entity, params (string Key, string Value)[] updates)
    { var tags = entity.Tags.ToDictionary(); foreach (var (key, value) in updates) tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static async Task<int> SchemaVersion(string database)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "SELECT version FROM hs_storage_schema";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
    private static string Workspace()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new Exception("Missing workspace fixture root.");
    }
    private static GameSnapshot SeedFrame()
    {
        var engine = new PowerLogStateEngine(1, "decision-replay");
        foreach (var line in new[] { $"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=CARDTYPE value=HERO", "tag=ZONE value=PLAY", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0",
            "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=CARDTYPE value=HERO", "tag=ZONE value=PLAY", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete" })
            engine.ProcessLine(line);
        var frame = engine.Snapshot();
        var hidden = new EntitySnapshot(30, null, EntityKind.Card, new Dictionary<string, string> { ["CONTROLLER"] = "2", ["ZONE"] = "HAND" }.ToFrozenDictionary());
        return frame with { MatchId = "decision-replay", StateVersion = 1, EventSequence = 1,
            Entities = frame.Entities.Values.Append(hidden).ToFrozenDictionary(e => e.Id), Choice = null, ChoiceHistory = null,
            TurnHistory = null, PublicPlays = null, DeckTracking = null, RecentEvents = [] };
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class ReplayModel(AdviceContext expected, DateTimeOffset cutoff) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedTools { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token);
            Check(request.RequestUri?.Host == "decision-replay.invalid" && request.Headers.Authorization?.Parameter == Key &&
                new[] { LaterCard, "NOTE_LATE", "PLAN_LATE", "RESULT_LATE_WON", Key }.All(value => !text.Contains(value, StringComparison.Ordinal)),
                "Future reveal, later knowledge, result or credential entered a real model request body.");
            var messages = JsonNode.Parse(text)!["messages"]!.AsArray();
            var context = JsonNode.Parse(messages.First(m => m?["role"]?.ToString() == "user")!["content"]!.GetValue<string>())!;
            Check(context["MatchId"]?.ToString() == expected.MatchId && context["StateVersion"]?.GetValue<long>() == 2 && context["EventSequence"]?.GetValue<long>() == 40 &&
                context["DeckVersionId"]?.ToString() == expected.DeckVersionId && context["CardBuild"]?.ToString() == Build &&
                DateTimeOffset.Parse(context["KnowledgeAsOf"]!.ToString()) == cutoff && DateTimeOffset.Parse(context["AsOf"]!.ToString()) == cutoff &&
                JsonNode.DeepEquals(context["Snapshot"], expected.Snapshot), "HTTP context changed the exact old decision or inherited the opened note's time.");
            object Tool(string id, string name, object args) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
            object message;
            if (Calls == 1)
                message = new { role = "assistant", tool_calls = new[] { Tool("replay-snapshot", "get_snapshot", new { }), Tool("replay-plan", "get_matchup_plan", new { }),
                    Tool("replay-stats", "get_personal_stats", new { scope = "current_matchup", went_first = "current" }),
                    Tool("replay-line", "evaluate_line", new { actions = new[] { new { kind = "end_turn", source_entity_id = 2 } } }) } };
            else
            {
                Check(Calls == 2, "Replay fake model exceeded its expected bounded tool round.");
                var tools = messages.Where(m => m?["role"]?.ToString() == "tool").ToDictionary(m => m!["tool_call_id"]!.ToString(), m => JsonNode.Parse(m!["content"]!.GetValue<string>())!);
                var plan = tools["replay-plan"]; var line = tools["replay-line"];
                Check(JsonNode.DeepEquals(tools["replay-snapshot"], expected.Snapshot) && plan.ToJsonString().Contains("PLAN_REPLAY_V1", StringComparison.Ordinal) &&
                    plan.ToJsonString().Contains("NOTE_REPLAY_V1", StringComparison.Ordinal) && plan["entries"]!.AsArray().All(e => e!["Version"]!.GetValue<int>() == 1) &&
                    DateTimeOffset.Parse(plan["as_of"]!.ToString()) == cutoff && tools["replay-stats"]["stats"]?["Games"]?.GetValue<int>() == 0 &&
                    line["Verification"]?.ToString() == "Verified" && line["IsLegal"]?.GetValue<bool>() == true && line["AppliedActions"]?.GetValue<int>() == 1 &&
                    line["Result"]?["TurnEnded"]?.GetValue<bool>() == true,
                    "Historical tools did not preserve v1, excluded-current-result statistics and a genuinely host-verified legal end turn: " + line.ToJsonString());
                CheckedTools = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "仅依据保存时的局面与原计划结束回合。", reasons = new[] { "原计划仍为V1，结束回合已通过本地主机核验" },
                    risks = new[] { "复盘没有使用后来公开的信息" }, actions = new[] { new { kind = "end_turn", source_entity_id = 2 } }, verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 60, completion_tokens = 20, total_tokens = 80 } }), Encoding.UTF8, "application/json") };
        }
    }
}
