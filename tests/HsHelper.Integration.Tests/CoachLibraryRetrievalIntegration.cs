using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

internal static class CoachLibraryRetrievalIntegration
{
    private static readonly DateTimeOffset Start = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Deck = "retrieval-deck";
    private const string Match = "library-retrieval-current";
    private const string Build = "255790";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "library-retrieval", Guid.NewGuid().ToString("N"));
        var sessions = new SessionStore(Path.Combine(directory, "sessions.db")); await sessions.InitializeAsync();
        var binding = new CoachLibraryBinding(Match, 7, Build, Deck, Start.AddHours(1), "MAGE", true, EventSequence: 50);
        async Task<(CoachKnowledgeStore Store, Clock Clock)> NewStore(string name)
        {
            var clock = new Clock { Now = Start }; var store = new CoachKnowledgeStore(Path.Combine(directory, name + ".db"), clock);
            await store.InitializeAsync(); return (store, clock);
        }
        CoachLibraryContext Library(CoachKnowledgeStore store, CoachLibraryBinding? pinned = null) => new(sessions, store, pinned ?? binding);

        var (crowded, _) = await NewStore("crowded-notes");
        var oldPlan = await crowded.SaveAsync(Plan("OLD_PLAN_SURVIVES", "OLD_PLAN_SURVIVES"));
        for (var i = 0; i < 101; i++) await crowded.SaveAsync(Note("crowded-" + i, "RECENT_NOTE_" + i));
        foreach (var expectedNotes in new[] { 101, 201 })
        {
            if (expectedNotes == 201) for (var i = 101; i < 201; i++) await crowded.SaveAsync(Note("crowded-" + i, "RECENT_NOTE_" + i));
            var result = await Query(Library(crowded));
            Check(Entries(result).Count == 20 && OfKind(result, "DeckPlan").Single()["EntryId"]?.ToString() == oldPlan.EntryId &&
                OfKind(result, "MatchNote").Length == 19 && result["entries_truncated"]?.GetValue<bool>() == true &&
                result["retrieval"]?["deck_plans"]?["returned"]?.GetValue<int>() == 1 &&
                result["retrieval"]?["match_notes"]?["at_least_matches"]?.GetValue<int>() == 21,
                expectedNotes + " newer notes crowded an old plan out of the bounded tool response: " + result.ToJsonString());
        }
        pass("101 and 201 newer match notes cannot crowd an older applicable deck plan out of model retrieval");

        var (balanced, _) = await NewStore("balanced");
        for (var i = 0; i < 25; i++) await balanced.SaveAsync(Plan("plan-" + i, i == 24 ? new string('p', 1700) : "PLAN_" + i));
        for (var i = 0; i < 30; i++) await balanced.SaveAsync(Note("note-" + i, "NOTE_" + i));
        var both = await Query(Library(balanced)); var bothEntries = Entries(both);
        Check(bothEntries.Count == 20 && bothEntries.Take(10).All(entry => entry?["Kind"]?.ToString() == "DeckPlan") &&
            bothEntries.Skip(10).All(entry => entry?["Kind"]?.ToString() == "MatchNote") &&
            bothEntries[0]?["Title"]?.ToString() == "plan-24" && bothEntries[10]?["Title"]?.ToString() == "note-29" &&
            bothEntries[0]?["content"]?.GetValue<string>().Length == 1600 && bothEntries[0]?["content_truncated"]?.GetValue<bool>() == true &&
            both["retrieval"]?["initial_per_kind_quota"]?.GetValue<int>() == 10 &&
            new[] { "deck_plans", "match_notes" }.All(kind => both["retrieval"]?[kind]?["returned"]?.GetValue<int>() == 10 &&
                both["retrieval"]?[kind]?["truncated"]?.GetValue<bool>() == true && both["retrieval"]?[kind]?["at_least_matches"]?.GetValue<int>() == 21),
            "Both kinds lost their ten-entry quota, within-kind order, content bound or honest lower-bound count.");
        Check(bothEntries.All(entry => new[] { "EntryId", "Version", "RecordedAt", "Source", "Kind", "Title", "EventSequence", "OpponentClass", "WentFirst", "MatchId", "content", "content_truncated" }
            .All(key => entry!.AsObject().ContainsKey(key))), "A preexisting library output field disappeared.");
        pass("separate bounded kind queries preserve ten plans and ten notes their internal order old fields and 1600-character content truncation");

        foreach (var (plans, notes, expectedPlans, expectedNotes) in new[] { (25, 3, 17, 3), (0, 25, 0, 20), (25, 0, 20, 0), (0, 0, 0, 0) })
        {
            var (store, _) = await NewStore($"fill-{plans}-{notes}");
            for (var i = 0; i < plans; i++) await store.SaveAsync(Plan("fill-plan-" + i, "FILL_PLAN_" + i));
            for (var i = 0; i < notes; i++) await store.SaveAsync(Note("fill-note-" + i, "FILL_NOTE_" + i));
            var result = await Query(Library(store));
            Check(OfKind(result, "DeckPlan").Length == expectedPlans && OfKind(result, "MatchNote").Length == expectedNotes &&
                Entries(result).Count == expectedPlans + expectedNotes && (Entries(result).Count != 0 || result["missing"] is not null),
                "Unused kind quota was not deterministically filled or empty results invented entries.");
        }
        pass("unused per-kind quotas deterministically fill from remaining plans then notes including single-kind and empty libraries");

        var (ranked, _) = await NewStore("applicability");
        var exactPlan = await ranked.SaveAsync(Plan("old exact plan", "EXACT_PLAN", "MAGE", true));
        var exactNote = await ranked.SaveAsync(Note("old exact note", "EXACT_NOTE", "MAGE", true));
        await ranked.SaveAsync(Plan("class only", "CLASS_ONLY", "MAGE"));
        await ranked.SaveAsync(Plan("first only", "FIRST_ONLY", first: true));
        for (var i = 0; i < 35; i++)
        {
            await ranked.SaveAsync(Plan("general plan " + i, "GENERAL_PLAN_" + i));
            await ranked.SaveAsync(Note("general note " + i, "GENERAL_NOTE_" + i));
        }
        await ranked.SaveAsync(Plan("wrong opponent", "FORBIDDEN_PRIEST", "PRIEST", true));
        await ranked.SaveAsync(Note("wrong first", "FORBIDDEN_SECOND", "MAGE", false));
        var relevant = await Query(Library(ranked)); var rankedPlans = OfKind(relevant, "DeckPlan");
        Check(rankedPlans[0]["EntryId"]?.ToString() == exactPlan.EntryId && OfKind(relevant, "MatchNote")[0]["EntryId"]?.ToString() == exactNote.EntryId &&
            rankedPlans[1]["content"]?.ToString() == "FIRST_ONLY" && rankedPlans[2]["content"]?.ToString() == "CLASS_ONLY" &&
            !relevant.ToJsonString().Contains("FORBIDDEN_", StringComparison.Ordinal),
            "Applicability was ranked after LIMIT or contradictory opponent/first scopes leaked into the response.");
        pass("old exact matchup and first-player plans and notes rank before newer general entries before either kind limit is applied");

        var (anchored, _) = await NewStore("event-cutoff");
        await anchored.SaveAsync(Plan("old legal plan", "LEGAL_OLD_PLAN"));
        await anchored.SaveAsync(Note("old legal note", "LEGAL_OLD_NOTE", match: Match, sequence: 40));
        await anchored.SaveAsync(Note("unanchored same match", "LEGAL_NO_ANCHOR", match: Match));
        await anchored.SaveAsync(Note("other match sequence", "LEGAL_OTHER_MATCH", match: "older-match", sequence: 9999));
        for (var i = 0; i < 500; i++) await anchored.SaveAsync(Note("future event " + i, "FORBIDDEN_FUTURE_EVENT_" + i, match: Match, sequence: 51 + i));
        var past = await Query(Library(anchored)); var pastText = past.ToJsonString();
        Check(Entries(past).Count == 4 && pastText.Contains("LEGAL_OLD_PLAN", StringComparison.Ordinal) && pastText.Contains("LEGAL_OLD_NOTE", StringComparison.Ordinal) &&
            pastText.Contains("LEGAL_NO_ANCHOR", StringComparison.Ordinal) && pastText.Contains("LEGAL_OTHER_MATCH", StringComparison.Ordinal) &&
            !pastText.Contains("FORBIDDEN_FUTURE_EVENT_", StringComparison.Ordinal) && past["entries_truncated"]?.GetValue<bool>() == false &&
            past["retrieval"]?["match_notes"]?["at_least_matches"]?.GetValue<int>() == 3,
            "Five hundred future anchors crowded legal older notes out before host event filtering: " + past.ToJsonString());
        var noCutoff = await Query(Library(anchored, binding with { EventSequence = null }));
        Check(Entries(noCutoff).Count == 3 && !noCutoff.ToJsonString().Contains("LEGAL_OLD_NOTE", StringComparison.Ordinal) &&
            noCutoff.ToJsonString().Contains("LEGAL_NO_ANCHOR", StringComparison.Ordinal) && noCutoff.ToJsonString().Contains("LEGAL_OTHER_MATCH", StringComparison.Ordinal) &&
            !noCutoff.ToJsonString().Contains("FORBIDDEN_FUTURE_EVENT_", StringComparison.Ordinal), "A null current-match cutoff exposed anchored notes or removed the existing unanchored policy.");
        pass("SQL event cutoff filters 500 future anchors before kind limits while unknown cutoff preserves only unanchored same-match and eligible other-match notes");

        var (versioned, versionClock) = await NewStore("versions-and-scope");
        var draft = Plan("moved plan", "HISTORICAL_MATCHING_PLAN", "MAGE", true);
        var moved = await versioned.SaveAsync(draft);
        var removed = await versioned.SaveAsync(Note("later deleted", "HISTORICAL_DELETED_NOTE", "MAGE", true));
        await versioned.SaveAsync(Plan("wrong deck", "FORBIDDEN_WRONG_DECK") with { DeckVersionId = "other-deck" });
        versionClock.Now = Start.AddMinutes(30);
        var changed = await versioned.SaveAsync(draft with { OpponentClass = "PRIEST", Content = "CURRENT_WRONG_SCOPE" }, moved.EntryId, moved.Version);
        await versioned.DeleteAsync(removed.EntryId, removed.Version);
        versionClock.Now = Start.AddHours(2);
        await versioned.SaveAsync(draft with { Content = "FUTURE_RESTORED_SCOPE" }, moved.EntryId, changed.Version);
        await versioned.SaveAsync(Plan("future creation", "FORBIDDEN_FUTURE_CREATION"));
        var reopened = new CoachKnowledgeStore(Path.Combine(directory, "versions-and-scope.db"), versionClock); await reopened.InitializeAsync();
        var old = await Query(Library(reopened, binding with { AsOf = Start.AddMinutes(1) }));
        var current = await Query(Library(reopened));
        Check(Entries(old).Count == 2 && Entries(old).All(entry => entry?["Version"]?.GetValue<int>() == 1) &&
            old.ToJsonString().Contains("HISTORICAL_MATCHING_PLAN", StringComparison.Ordinal) && old.ToJsonString().Contains("HISTORICAL_DELETED_NOTE", StringComparison.Ordinal) &&
            Entries(current).Count == 0 && current["missing"] is not null && !current.ToJsonString().Contains("FUTURE_", StringComparison.Ordinal) &&
            !current.ToJsonString().Contains("HISTORICAL_", StringComparison.Ordinal) && !old.ToJsonString().Contains("FORBIDDEN_", StringComparison.Ordinal),
            "As-of versions scope changes or tombstones resurrected an old eligible revision or exposed future/wrong-deck data.");
        pass("latest-version selection precedes applicability and deletion so historical queries survive reopening without reviving superseded scopes or future revisions");

        var library = Library(crowded);
        foreach (var key in new[] { "kind", "page_number", "page_size", "event_sequence", "as_of", "deck_version_id", "title_contains" })
            await Throws<ArgumentException>(() => library.ExecuteAsync("get_matchup_plan", new() { [key] = "forged" }, default));
        var firstQuery = await Query(library); Entries(firstQuery)[0]!["content"] = "CALLER_MUTATION";
        var again = await Query(library);
        Check(again["match_id"]?.ToString() == Match && again["state_version"]?.GetValue<long>() == 7 &&
            DateTimeOffset.Parse(again["as_of"]!.ToString()) == binding.AsOf && again["deck_version"]?.ToString() == Deck &&
            !again.ToJsonString().Contains("CALLER_MUTATION", StringComparison.Ordinal) && again.ToJsonString().Contains("OLD_PLAN_SURVIVES", StringComparison.Ordinal),
            "Caller mutated persisted context or retrieval moved its pinned analysis binding.");
        var unbound = await Query(Library(crowded, binding with { DeckVersionId = null }));
        Check(unbound["missing"] is not null && unbound["entries"] is null, "Unbound deck context queried arbitrary saved data.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => library.ExecuteAsync("get_matchup_plan", new(), cancelled.Token));
        pass("library schema remains parameter-free model callers cannot change kind quota scope title time or event cutoff and responses cannot mutate stored content");

        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        var frame = Frame(); var context = CompactAdviceContextBuilder.Create(frame, Build, AdviceTaskKind.Review);
        var observations = new List<CoachToolObservation>();
        var coach = new SnapshotCoach(frame, context.Snapshot, catalog, Build, library: Library(anchored), toolCompleted: observations.Add);
        using var handler = new RetrievalModel(); using var http = new HttpClient(handler);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://library-retrieval-fixture.invalid/v1/chat/completions"), "retrieval-fixture"),
            "retrieval-fixture-unused-key", context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities());
        Check(handler.Calls == 2 && handler.CheckedTool && advice.Actions.Count == 0 && !advice.VerifiedLethal &&
            observations.Single() is { Name: "get_matchup_plan", Success: true, ResultHash.Length: 64 }, "Fake model did not receive pinned per-kind data with bounded history and auditable tool evidence.");
        pass("fake HTTP retrieves the fixed-frame per-kind library tool with legal older notes and no future anchors or model-defined cutoff");
    }

    private static CoachKnowledgeDraft Plan(string title, string content, string? opponent = null, bool? first = null) => new(Deck, CoachKnowledgeKind.DeckPlan, title, content, opponent, first);
    private static CoachKnowledgeDraft Note(string title, string content, string? opponent = null, bool? first = null, string? match = null, long? sequence = null) =>
        new(Deck, CoachKnowledgeKind.MatchNote, title, content, opponent, first, match) { EventSequence = sequence };
    private static Task<JsonNode> Query(CoachLibraryContext library) => library.ExecuteAsync("get_matchup_plan", new(), default);
    private static JsonArray Entries(JsonNode result) => result["entries"]!.AsArray();
    private static JsonNode[] OfKind(JsonNode result, string kind) => Entries(result).Where(entry => entry?["Kind"]?.ToString() == kind).Select(entry => entry!).ToArray();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<JsonNode>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private static GameSnapshot Frame() => new(Match, 7, 50, 1, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["CURRENT_PLAYER"] = "1", ["CONTROLLER"] = "1", ["HERO_ENTITY"] = "11" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["CURRENT_PLAYER"] = "0", ["CONTROLLER"] = "2", ["HERO_ENTITY"] = "21" }.ToFrozenDictionary()),
            Hero(11, 1), Hero(21, 2) }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = Build };
    private static EntitySnapshot Hero(int id, int owner) => new(id, "HERO_0" + owner, EntityKind.Card, new Dictionary<string, string> {
        ["CARDTYPE"] = "HERO", ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["HEALTH"] = "30", ["DAMAGE"] = "0", ["ARMOR"] = "0", ["ATK"] = "0" }.ToFrozenDictionary());

    private sealed class RetrievalModel : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CheckedTool { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token);
            Check(!text.Contains("FORBIDDEN_FUTURE_EVENT_", StringComparison.Ordinal) && !text.Contains("retrieval-fixture-unused-key", StringComparison.Ordinal),
                "Future knowledge or a credential entered the model request body.");
            var body = JsonNode.Parse(text)!; object message;
            if (Calls == 1)
            {
                var function = body["tools"]!.AsArray().Single(tool => tool?["function"]?["name"]?.ToString() == "get_matchup_plan")!["function"]!;
                Check(function["parameters"]?["additionalProperties"]?.GetValue<bool>() == false && function["parameters"]?["properties"]!.AsObject().Count == 0,
                    "Model schema acquired a cutoff kind or pagination override.");
                message = new { role = "assistant", tool_calls = new[] { new { id = "retrieval", type = "function", function = new { name = "get_matchup_plan", arguments = "{}" } } } };
            }
            else
            {
                var tool = body["messages"]!.AsArray().Single(item => item?["role"]?.ToString() == "tool");
                var result = JsonNode.Parse(tool!["content"]!.GetValue<string>())!;
                Check(Entries(result).Count == 4 && result.ToJsonString().Contains("LEGAL_OLD_PLAN", StringComparison.Ordinal) &&
                    result.ToJsonString().Contains("LEGAL_OLD_NOTE", StringComparison.Ordinal) && !result.ToJsonString().Contains("FORBIDDEN_", StringComparison.Ordinal) &&
                    result["entries_truncated"]?.GetValue<bool>() == false && result["as_of"]!.ToString().StartsWith("2035-01-01T01:00:00", StringComparison.Ordinal),
                    "The model did not receive the fixed legal per-kind results: " + result.ToJsonString());
                CheckedTool = true;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "参考该时点已保存的计划和合法笔记。", reasons = new[] { "个人资料只作参考" }, risks = new[] { "个人样本不证明当前对手持牌" }, actions = Array.Empty<object>(), verified_lethal = false }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 40, completion_tokens = 20, total_tokens = 60 } }), Encoding.UTF8, "application/json") };
        }
    }
}
