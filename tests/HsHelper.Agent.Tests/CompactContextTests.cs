using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

static class CompactContextTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("compact context removes deck slots, hidden identities, names and raw JSON", () => Sync(Privacy));
        yield return ("compact context preserves live modifiers, complete card text and unknown quality", () => Sync(DynamicState));
        yield return ("compact context uses only current own confirmed choices", () => Sync(Choices));
        yield return ("invalid choice identities never enter model context and choice requests make zero HTTP", ChoicePrivacyBoundaries);
        yield return ("spectator contexts omit both players' private cards tracking and private events", () => Sync(SpectatorPrivacy));
        yield return ("confirmed choices copy option lists and existing entity ownership is authoritative", () => Sync(ChoiceCopy));
        yield return ("compact context filters future events and retains public play multiplicity", () => Sync(EventBoundary));
        yield return ("compact context fails closed on unconfirmed perspectives and stale tracking", () => Sync(UnconfirmedPerspective));
        yield return ("future reveal ledgers and cross-state deck composition never enter old model contexts", () => Sync(FutureNestedState));
        yield return ("compact context is substantially smaller and does not mutate the local snapshot", () => Sync(Compression));
        yield return ("all five task templates are versioned and selected in the system prompt", TaskTemplates);
        yield return ("unconfirmed choice task cannot reach HTTP", ChoiceGuard);
        yield return ("wrong-phase mulligan and residual choice tasks cannot reach HTTP", TaskPhaseGuards);
        yield return ("Chinese user questions retain original text and task metadata", UserQuestionRoundtrip);
        yield return ("oversized and null-containing questions fail before HTTP", InvalidQuestions);
        yield return ("lazy snapshot tool returns the same compact view", LazySnapshot);
    }

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static EntitySnapshot Entity(int id, string? card, string zone, int controller, params (string, string)[] extra)
        => new(id, card, EntityKind.Card, new Dictionary<string, string>(extra.Select(pair => new KeyValuePair<string, string>(pair.Item1, pair.Item2)))
        { ["ZONE"] = zone, ["CONTROLLER"] = controller.ToString() }.ToFrozenDictionary());
    private static GameSnapshot State() => new("compact-fixture", 100, 100, 5, 1, 1, GamePhase.Main,
        new[]
        {
            new EntitySnapshot(1, null, EntityKind.Game, new Dictionary<string, string> { ["TURN"] = "5", ["STATE"] = "RUNNING" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["RESOURCES"] = "8", ["RESOURCES_USED"] = "3", ["PLAYER_NAME"] = "PRIVATE_PLAYER_NAME", ["PLAYER_LABEL"] = "PRIVATE_PLAYER_NAME_2", ["ACCOUNT_ID"] = "777777" }.ToFrozenDictionary()),
            Entity(10, "A", "HAND", 1, ("COST", "0"), ("ATK", "7"), ("HEALTH", "9"), ("DAMAGE", "2"), ("TAUNT", "1"), ("COSTS_HEALTH", "1"), ("CUSTOM_NUMERIC_MODIFIER", "17")),
            Entity(11, "B", "PLAY", 2, ("CARDTYPE", "MINION"), ("DIVINE_SHIELD", "1")),
            Entity(20, "PRIVATE_ENEMY_HAND", "HAND", 2, ("COST", "99")),
            Entity(21, "PRIVATE_SECRET", "SECRET", 2, ("COST", "88")),
            Entity(30, "PRIVATE_OWN_DECK", "DECK", 1),
            Entity(31, "PRIVATE_ENEMY_DECK", "DECK", 2),
        }.ToFrozenDictionary(entity => entity.Id), new(true, false, false, true, 1, ["匿名记录存在缺口，费用未知保持未知"]))
    { ClientBuild = "123", Format = "Standard", EventHistoryTruncatedThrough = 30 };
    private static CardRecord Card(string id = "A") => new(id, 10, "测试卡牌", "完整牌效：抽一张牌；若受伤则获得+2攻击力。" + new string('效', 500),
        "HUNTER", "TEST", "MINION", 4, true, [], [], """{"attack":2,"health":3,"mechanics":["BATTLECRY"],"private_blob":"DO_NOT_COPY_RAW_JSON"}""");
    private static DeckDefinition Deck() => new(2, [31], [new(10, 2)], []);

    private static void Privacy()
    {
        var state = State();
        var compact = CompactAdviceContextBuilder.Build(state, "123", Deck(), [Card()]);
        var text = compact.ToJsonString();
        foreach (var forbidden in new[] { "PRIVATE_ENEMY_HAND", "PRIVATE_SECRET", "PRIVATE_OWN_DECK", "PRIVATE_ENEMY_DECK", "PRIVATE_PLAYER_NAME", "777777", "DO_NOT_COPY_RAW_JSON", "RawJson", "RemainingHealth" })
            Check(!text.Contains(forbidden, StringComparison.Ordinal), "Forbidden context field: " + forbidden);
        Check(compact["entities"]!.AsArray().Count == 4 && compact["zone_counts"]!.AsArray().Count == 5, "Private slots leaked or aggregate counts vanished");
        Check(compact["imported_deck"]!["cards"]![0]!["count"]!.GetValue<int>() == 2, "Imported starting deck context disappeared");
    }

    private static void DynamicState()
    {
        var compact = CompactAdviceContextBuilder.Build(State(), "123", cards: [Card()]);
        var entity = compact["entities"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == 10)!;
        Check(entity["tags"]!["COST"]!.GetValue<string>() == "0" && entity["tags"]!["ATK"]!.GetValue<string>() == "7" &&
            entity["tags"]!["DAMAGE"]!.GetValue<string>() == "2" && entity["tags"]!["CUSTOM_NUMERIC_MODIFIER"]!.GetValue<string>() == "17", "Dynamic rule-relevant state was lost");
        Check(compact["cards"]![0]!["text"]!.GetValue<string>() == Card().Text && compact["cards"]![0]!["attack"]!.GetValue<int>() == 2,
            "Full card effect or base attributes were truncated");
        Check(compact["quality"]!["complete"]!.GetValue<bool>() == false && compact["quality"]!["open_blocks"]!.GetValue<int>() == 1 &&
            compact["quality"]!["history_truncated_through"]!.GetValue<int>() == 30 && compact["quality"]!["warnings"]!.AsArray().Count == 1,
            "Missing information was presented as exact");
        var malformed = CompactAdviceContextBuilder.Build(State(), null, cards: [Card() with { RawJson = "[]" }]);
        Check(malformed["cards"]![0]!["text"]!.GetValue<string>() == Card().Text, "Malformed optional card metadata lost the text effect");
    }

    private static void Choices()
    {
        var own = ChoiceState();
        var compact = CompactAdviceContextBuilder.Create(own, "123", AdviceTaskKind.Choice);
        Check(compact.Snapshot["choice"]!["confirmed"]!.GetValue<bool>() && compact.TemplateVersion.EndsWith("/choice"), "Ready own choice missing");
        foreach (var other in new[] { own with { Choice = own.Choice! with { PlayerId = 2 } }, own with { Choice = own.Choice! with { IsReady = false } }, own with { LocalPlayerId = null } })
        {
            Check(CompactAdviceContextBuilder.Build(other, "123")["choice"] is null, "Unconfirmed or opponent choice leaked");
            try { CompactAdviceContextBuilder.Create(other, "123", AdviceTaskKind.Choice); throw new Exception("Unconfirmed choice task accepted"); }
            catch (InvalidOperationException) { }
        }
    }

    private static GameSnapshot ChoiceState() => State() with
    {
        Phase = GamePhase.Choice, Quality = new(true, true, true, true, 0, []),
        Choice = new(7, 1, "GENERAL", 10, 1, 1, [new(90, "CHOICE_ONLY_PROBE", 1, 0)], true)
    };

    private static async Task ChoicePrivacyBoundaries()
    {
        var own = ChoiceState();
        var duplicatePlayers = own.Entities.Values.Append(new EntitySnapshot(200, null, EntityKind.Player,
            new Dictionary<string, string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary())).ToFrozenDictionary(entity => entity.Id);
        foreach (var rejected in new[]
        {
            own with { IsSpectating = true }, own with { Quality = own.Quality with { IsComplete = false } },
            own with { Quality = own.Quality with { IsStable = false } }, own with { Quality = own.Quality with { OpenBlockCount = 1 } },
            own with { Quality = own.Quality with { HasLocalPerspective = false } }, own with { LocalPlayerId = null },
            own with { Entities = duplicatePlayers }, own with { Choice = own.Choice! with { IsReady = false } },
            own with { Choice = own.Choice! with { PlayerId = 2 } }, own with { Choice = own.Choice! with { CountMin = -1 } },
            own with { Choice = own.Choice! with { CountMax = 2 } },
            own with { Choice = own.Choice! with { Options = [new(90, "CHOICE_ONLY_PROBE", 2, 0)] } },
            own with { Choice = own.Choice! with { Options = [new(90, "CHOICE_ONLY_PROBE", 1, 0), new(90, "CHOICE_ONLY_PROBE", 1, 1)] } },
            own with { Entities = own.Entities.Values.Append(Entity(90, null, "SETASIDE", 2)).ToFrozenDictionary(entity => entity.Id) },
            own with { Entities = own.Entities.Values.Append(Entity(90, "CONFLICTING_IDENTITY", "SETASIDE", 1)).ToFrozenDictionary(entity => entity.Id) }
        })
        {
            var projected = CompactAdviceContextBuilder.Build(rejected, "123", cards: [Card("CHOICE_ONLY_PROBE")]);
            Check(projected["choice"] is null && !projected.ToJsonString().Contains("CHOICE_ONLY_PROBE", StringComparison.Ordinal),
                "A rejected choice identity entered the initial or lazy model context.");
            var handler = new FakeHandler((_, _) => Reply(Final()));
            try { await Analyze(handler, CompactAdviceContextBuilder.Create(rejected, "123", AdviceTaskKind.Choice)); throw new Exception("Rejected choice reached HTTP."); }
            catch (InvalidOperationException) { Check(handler.Calls == 0, "Choice qualification occurred after a provider request."); }
        }
    }

    private static void SpectatorPrivacy()
    {
        var original = ChoiceState();
        var spectator = original with
        {
            IsSpectating = true,
            DeckTracking = new(original.MatchId, original.StateVersion, "deck", "123", 1,
                new Dictionary<string, int> { ["SPECTATOR_PRIVATE_DECK"] = 1 }, 0, true, []),
            RecentEvents = [new(original.EventSequence, "CardRevealed", 10, 1, "SPECTATOR_PRIVATE_EVENT", null, null, "anonymous")]
        };
        var compact = CompactAdviceContextBuilder.Build(spectator, "123", cards: [Card("A"), Card("SPECTATOR_PRIVATE_DECK"), Card("CHOICE_ONLY_PROBE")]);
        var text = compact.ToJsonString();
        Check(compact["local_player_id"] is null && compact["choice"] is null && compact["deck_tracking"] is null &&
            compact["entities"]!.AsArray().All(entity => entity!["id"]!.GetValue<int>() != 10) &&
            !text.Contains("PRIVATE_", StringComparison.Ordinal) && !text.Contains("CHOICE_ONLY_PROBE", StringComparison.Ordinal),
            "Spectating reused a manually supplied player ID to export private cards, deck or recent events.");
        Check(compact["entities"]!.AsArray().Any(entity => entity!["id"]!.GetValue<int>() == 11), "Spectating discarded public board information.");
    }

    private static void ChoiceCopy()
    {
        var frame = ChoiceState(); var options = frame.Choice!.Options.ToList();
        frame = frame with { Choice = frame.Choice with { Options = options } };
        var confirmed = ChoiceVisibility.ConfirmedOwn(frame)!;
        options.Clear();
        Check(confirmed.Options.Count == 1, "A mutable producer list changed already confirmed options.");
        try { ((IList<ChoiceOptionSnapshot>)confirmed.Options).Clear(); throw new Exception("Confirmed options allowed consumer writes."); }
        catch (NotSupportedException) { }
        var opposing = ChoiceState();
        opposing = opposing with { Entities = opposing.Entities.Values.Append(Entity(90, null, "SETASIDE", 2)).ToFrozenDictionary(entity => entity.Id) };
        Check(ChoiceVisibility.ConfirmedOwn(opposing) is null, "An option's claimed owner overrode its existing opposing entity.");
    }

    private static void EventBoundary()
    {
        var state = State() with { PublicPlays = new("compact-fixture", 100,
            [new(20, 50, "PAST_PUBLIC", 2), new(30, 50, "PAST_PUBLIC", 2), new(101, 60, "FUTURE_PLAY", 2)], true, []) };
        var events = new[]
        {
            new ObservedGameEvent(80, "ZoneChanged", 50, 2, "PAST_PUBLIC", "PLAY", "HAND", "PRIVATE_PLAYER_NAME DataJson"),
            new ObservedGameEvent(90, "CardRevealed", 20, 2, "PRIVATE_ENEMY_HAND", null, null, "PRIVATE_PLAYER_NAME"),
            new ObservedGameEvent(101, "ZoneChanged", 80, 2, "FUTURE_EVENT", "HAND", "PLAY", "future")
        };
        var compact = CompactAdviceContextBuilder.Build(state, "123", recentEvents: events);
        var json = compact.ToJsonString();
        Check(!json.Contains("FUTURE_") && !json.Contains("PRIVATE_") && !json.Contains("DataJson"), "Future/private identity or raw event text leaked");
        Check(compact["public_plays"]!["counts"]![0]!["count"]!.GetValue<int>() == 2 && json.Contains("PAST_PUBLIC"), "Repeated public plays or bounced public identity vanished");
        Check(!compact["public_plays"]!["complete"]!.GetValue<bool>(), "Dropping a future record still advertised a complete as-of ledger");
    }

    private static void UnconfirmedPerspective()
    {
        var state = State();
        var unknown = Entity(999, "PRIVATE_UNKNOWN_KIND", "HAND", 2) with { Kind = EntityKind.Unknown };
        state = state with { Entities = state.Entities.Values.Append(unknown).ToFrozenDictionary(entity => entity.Id),
            Quality = state.Quality with { HasLocalPerspective = false },
            RecentEvents = [new(100, "CardRevealed", 10, 1, "PRIVATE_PREVIOUS_HAND", null, null, "anonymous")] };
        var compact = CompactAdviceContextBuilder.Build(state, "123");
        Check(compact["local_player_id"] is null && !compact.ToJsonString().Contains("PRIVATE_"), "Unconfirmed or unknown-kind private data leaked");
        state = State() with { DeckTracking = new("another-match", 99, "deck", "123", 2,
            new Dictionary<string, int> { ["A"] = 2 }, 0, true, []), PublicPlays = new("another-match", 99, [new(1, 50, "OTHER_MATCH", 2)], true, []) };
        compact = CompactAdviceContextBuilder.Build(state, "123");
        Check(compact["deck_tracking"] is null && compact["public_plays"] is null && !compact["quality"]!["context_complete"]!.GetValue<bool>(),
            "Stale or cross-match tracking content remained available to the model");
    }

    private static void FutureNestedState()
    {
        var source = State();
        var future = source with
        {
            PublicPlays = new(source.MatchId, source.StateVersion + 10, [new(80, 50, "FUTURE_RESOLVED_IDENTITY", 2)], true, []),
            DeckTracking = new(source.MatchId, source.StateVersion + 10, "deck", "123", 1,
                new Dictionary<string, int> { ["FUTURE_REMAINING_CARD"] = 1 }, 0, true, [])
        };
        var compact = CompactAdviceContextBuilder.Build(future, "123", cards:
            [Card("FUTURE_RESOLVED_IDENTITY"), Card("FUTURE_REMAINING_CARD")]);
        var text = compact.ToJsonString();
        Check(!text.Contains("FUTURE_RESOLVED_IDENTITY") && !text.Contains("FUTURE_REMAINING_CARD") &&
            compact["public_plays"] is null && compact["deck_tracking"] is null && compact["cards"]!.AsArray().Count == 0,
            "An old-sequence record resolved in a future ledger backfilled past model knowledge");
        Check(compact["quality"]!["context_limits"]!.AsArray().Count == 2 && compact["quality"]!["warnings"]!.AsArray().Count == 3,
            "Dropped mismatched evidence did not retain explicit quality limitations");
        foreach (var tracking in new[]
        {
            future.DeckTracking! with { StateVersion = source.StateVersion - 1 },
            future.DeckTracking! with { StateVersion = source.StateVersion, MatchId = "another-match" },
            future.DeckTracking! with { StateVersion = source.StateVersion, CardBuild = "another-build" }
        })
        {
            var result = CompactAdviceContextBuilder.Build(source with { DeckTracking = tracking }, "123");
            Check(result["deck_tracking"] is null && !result.ToJsonString().Contains("FUTURE_REMAINING_CARD"), "Cross-state, cross-match or cross-build composition leaked");
        }
    }

    private static void Compression()
    {
        var state = State();
        var entities = state.Entities.Values.Concat(Enumerable.Range(1000, 300).Select(id => Entity(id, "UNUSED_SLOT", "DECK", id % 2 + 1,
            ("ZONE_POSITION", id.ToString()), ("COST", "8"), ("ATK", "9"), ("HEALTH", "10")))).ToFrozenDictionary(entity => entity.Id);
        state = state with { Entities = entities };
        var originalBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(state));
        var compact = CompactAdviceContextBuilder.Build(state, "123");
        var compactBytes = Encoding.UTF8.GetByteCount(compact.ToJsonString());
        Check(compactBytes < originalBytes * 0.2, $"Insufficient compaction: {compactBytes}/{originalBytes} bytes");
        Check(state.Entities.Count == 308 && state.Entities[10].Cost == 0 && state.Entities[20].CardId == "PRIVATE_ENEMY_HAND", "Builder mutated local validation input");
        Console.WriteLine($"Compact fixture bytes {compactBytes}/{originalBytes} ({100.0 * compactBytes / originalBytes:F1}%)");
    }

    private static async Task TaskTemplates()
    {
        foreach (var kind in Enum.GetValues<AdviceTaskKind>())
        {
            var state = State() with { Phase = kind switch { AdviceTaskKind.Mulligan => GamePhase.Mulligan, AdviceTaskKind.Choice => GamePhase.Choice, _ => GamePhase.Main },
                Choice = new(7, 1, "GENERAL", 10, 1, 1, [new(90, "OPTION", 1, 0)], true) };
            if (kind == AdviceTaskKind.Choice) state = state with { Quality = new(true, true, true, true, 0, []) };
            var context = CompactAdviceContextBuilder.Create(state, "123", kind, Deck(), [Card()]);
            var handler = new FakeHandler((_, _) => Reply(Final()));
            await Analyze(handler, context);
            Check(handler.Requests[0].Body.Contains(AdviceTaskTemplates.Get(kind).Version, StringComparison.Ordinal), "Missing task template version");
            var prompt = JsonNode.Parse(handler.Requests[0].Body)!["messages"]![0]!["content"]!.GetValue<string>();
            Check(prompt.Contains(AdviceTaskTemplates.Get(kind).Instructions, StringComparison.Ordinal), "Wrong system task template");
        }
        Check(AdviceTaskTemplates.Resolve("套牌计划") == AdviceTaskKind.DeckPlan && AdviceTaskTemplates.Resolve("局面复盘") == AdviceTaskKind.Review, "Task label aliases lost");
    }

    private static async Task ChoiceGuard()
    {
        var handler = new FakeHandler((_, _) => Reply(Final()));
        try { await Analyze(handler, new("fixture", 1, "choice", new JsonObject())); throw new Exception("Unconfirmed choice reached provider"); }
        catch (InvalidOperationException) { Check(handler.Calls == 0, "Guard occurred after HTTP"); }
    }

    private static async Task TaskPhaseGuards()
    {
        var handler = new FakeHandler((_, _) => Reply(Final()));
        foreach (var phase in new[] { GamePhase.Main, GamePhase.Complete, GamePhase.Starting, GamePhase.Unknown })
        {
            var state = State() with { Phase = phase, Choice = new(7, 1, "GENERAL", 10, 1, 1, [new(90, "OPTION", 1, 0)], true) };
            Check(CompactAdviceContextBuilder.Build(state, "123")["choice"] is null, "Residual choice was exposed outside a choice/mulligan phase");
            foreach (var task in new[] { AdviceTaskKind.Mulligan, AdviceTaskKind.Choice })
            {
                try { await Analyze(handler, CompactAdviceContextBuilder.Create(state, "123", task)); throw new Exception("Wrong-phase task was accepted"); }
                catch (InvalidOperationException) { Check(handler.Calls == 0, "Wrong-phase task reached HTTP"); }
            }
        }
        try { CompactAdviceContextBuilder.Create(State() with { Phase = GamePhase.Choice }, "123", AdviceTaskKind.Mulligan); throw new Exception("Choice phase was mistaken for initial mulligan"); }
        catch (InvalidOperationException) { }
    }

    private static async Task UserQuestionRoundtrip()
    {
        const string question = "这回合先打哪张？\n请解释资源分配，并指出仍未知的信息。";
        var context = CompactAdviceContextBuilder.Create(State(), "123", AdviceTaskKind.Turn) with { UserQuestion = question };
        var handler = new FakeHandler((_, _) => Reply(Final()));
        var advice = await Analyze(handler, context);
        var message = JsonNode.Parse(handler.Requests.Single().Body)!["messages"]![1]!["content"]!.GetValue<string>();
        var sent = JsonNode.Parse(message)!;
        Check(sent["UserQuestion"]!.GetValue<string>() == question && message.Contains("这回合先打哪张", StringComparison.Ordinal), "Question text was dropped, normalized or Unicode-escaped");
        Check(advice.UserQuestion == question && advice.TaskType == nameof(AdviceTaskKind.Turn) && advice.TemplateVersion == context.TemplateVersion,
            "Response metadata lost the original question or fixed task template");
    }

    private static async Task InvalidQuestions()
    {
        foreach (var question in new[] { new string('问', 1001), "无效\0提问" })
        {
            var handler = new FakeHandler((_, _) => Reply(Final()));
            var context = CompactAdviceContextBuilder.Create(State(), "123", AdviceTaskKind.Turn) with { UserQuestion = question };
            try { await Analyze(handler, context); throw new Exception("Invalid question reached HTTP"); }
            catch (ArgumentException) { Check(handler.Calls == 0, "Question validation occurred after HTTP"); }
        }
    }

    private static async Task LazySnapshot()
    {
        var context = CompactAdviceContextBuilder.Create(State(), "123", AdviceTaskKind.Turn, Deck(), [Card()]);
        var handler = new FakeHandler((round, _) => Reply(round == 1 ? """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","function":{"name":"get_snapshot","arguments":"{}"}}]}}]}""" : Final()));
        await new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings(), "fixture-key", context,
            (_, _, _) => Task.FromResult<JsonNode>(context.Snapshot.DeepClone()), () => true);
        var messages = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray();
        var user = messages.Single(node => node?["role"]?.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        var tool = messages.Single(node => node?["role"]?.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
        Check(JsonNode.DeepEquals(JsonNode.Parse(tool), context.Snapshot) && !tool.Contains("PRIVATE_ENEMY"), "Lazy snapshot expanded or leaked hidden data");
        Check(user.Contains("完整牌效", StringComparison.Ordinal) && tool.Contains("完整牌效", StringComparison.Ordinal) &&
            !user.Contains("\\u5B8C", StringComparison.OrdinalIgnoreCase) && !tool.Contains("\\u5B8C", StringComparison.OrdinalIgnoreCase),
            "Nested message content still contains literal Unicode escape sequences instead of Chinese");
    }
    private static ModelSettings Settings() => new(new Uri("https://model.example/v1/chat/completions"), "fixture-model");
    private static Task<StrategyAdvice> Analyze(FakeHandler handler, AdviceContext context) => new AdviceService(new HttpClient(handler)).AnalyzeAsync(Settings(), "fixture-key", context,
        (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true);
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static string Final() => """{"choices":[{"finish_reason":"stop","message":{"content":"{\"summary\":\"测试\",\"reasons\":[],\"risks\":[]}"}}]}""";
}
