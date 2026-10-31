using System.Net;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Coaching;
using HsHelper.Ingestion;
using HsHelper.Rules;
using HsHelper.Storage;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--export-fixture"))
{
    Console.Error.WriteLine("Usage: HsHelper.Integration.Tests [--export-fixture DIRECTORY]");
    return 2;
}
var workspace = FindWorkspace();
var temp = Path.Combine(workspace, ".local", "integration-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var catalog = new CardCatalog(Path.Combine(temp, "cards.db"));
var storePath = Path.Combine(temp, "history.db");
var store = new SessionStore(storePath);
await catalog.InitializeAsync(); await store.InitializeAsync();
using (var cards = Utf8(SyntheticFixture.CardsJson()))
    await catalog.ImportAsync(cards, SyntheticFixture.Build, source: "synthetic://acceptance-only");
var rule = new CardPoolRules("synthetic-acceptance-only", SyntheticFixture.Build, 2,
    DateTimeOffset.Parse("2020-01-01T00:00:00Z"), DateTimeOffset.Parse("2099-01-01T00:00:00Z"),
    "synthetic://acceptance-only", ["SYNTHETIC"], [], [], [], true,
    "FICTIONAL FIXTURE: does not describe any real Standard card pool or game build.");
using (var rules = Utf8(JsonSerializer.Serialize(rule))) await catalog.ImportPoolRulesAsync(rules);
var firstDeck = await store.SaveDeckVersionAsync("synthetic hunter 30", SyntheticFixture.Deck(), SyntheticFixture.Build);
var rankedPath = Path.Combine(temp, "synthetic-ranked.Power.log");
var casualPath = Path.Combine(temp, "synthetic-casual.Power.log");
await File.WriteAllTextAsync(rankedPath, SyntheticFixture.Log(), new UTF8Encoding(false));
await File.WriteAllTextAsync(casualPath, SyntheticFixture.Log("GT_CASUAL", won: false), new UTF8Encoding(false));
var first = await ReplayAndRecord(rankedPath, catalog, store, firstDeck);
var count = 0;

Check(first.ChangedStates > 350 && first.Flushes > 2, "pipeline did not continuously observe and flush changed states");
Check(first.Checkpoints["opening"].LocalPlayerId == 1 && first.Checkpoints["opening"].PerspectiveSource == "initial_private_hand", "automatic opening perspective failed");
Check(first.Checkpoints["opening"].Quality.IsComplete && first.Checkpoints["opening"].Quality.IsStable, "opening quality is incomplete");
Check(first.Checkpoints["opening"].Hand.Count() == 3, "opening hand is not three cards");
Check(first.Checkpoints["complete"].LocalOutcome == "Won", "game end was not parsed");
Pass("anonymous generated Power.log traverses auto engine, enrichment and continuous recording");

var described = await catalog.DescribeDeckAsync(firstDeck.Definition, firstDeck.CardBuild);
Check(described.Cards.Sum(c => c.Count) == 30 && described.Legality == CardLegality.Legal, "synthetic 30-card deck is not validated by its explicit fictional rules");
Check(DeckCode.Encode(DeckCode.Decode(firstDeck.Code)) == firstDeck.Code, "saved deck code changed");
var opening = first.Checkpoints["opening"];
var drawn = first.Checkpoints["drawn"];
AssertComposition(opening, 27, new Dictionary<string, int> { [SyntheticFixture.CardId(1)] = 0, [SyntheticFixture.CardId(2)] = 1 });
AssertComposition(drawn, 26, new Dictionary<string, int> { [SyntheticFixture.CardId(1)] = 0, [SyntheticFixture.CardId(2)] = 0 });
Check(drawn.DeckTracking!.KnownRemaining.Where(p => p.Key != SyntheticFixture.CardId(1) && p.Key != SyntheticFixture.CardId(2)).All(p => p.Value == 2), "remaining pair counts changed");
Check(drawn.DeckTracking.KnownRemaining.Count == 13, "exact remainder should contain thirteen complete pairs");
Pass("pinned 30-card deck code and exact 27-to-26 card remainder survive draw and play");

var restarted = new SessionStore(storePath); await restarted.InitializeAsync();
var sessionId = drawn.MatchId;
var timeline = await restarted.ListDecisionPointsAsync(sessionId, includeUnstable: true);
Check(timeline.Count >= 9, "timeline lost stable checkpoints");
foreach (var point in timeline)
{
    var restored = await restarted.GetDecisionSnapshotAsync(point.Id) ?? throw new Exception("missing timeline snapshot");
    Check(first.States.TryGetValue((point.EventSequence, point.StateVersion), out var expected), "saved checkpoint was not emitted by replay");
    Check(SnapshotCodec.Serialize(restored) == SnapshotCodec.Serialize(expected!), $"restarted state differs at {point.EventSequence}/{point.StateVersion}");
}
Pass("reopened SQLite timeline matches every saved engine checkpoint including frozen maps and tracking");

var before = first.Checkpoints["private-probe"];
var beforeContext = await restarted.GetHistoricalContextAtAsync(sessionId, before.EventSequence, 10000) ?? throw new Exception("missing historical context");
var future = first.Checkpoints["enemy-public"];
Check(beforeContext.Snapshot.Entities[200].CardId is null && beforeContext.Snapshot.Entities[201].CardId is null, "hidden enemy identity leaked");
Check(future.Entities[200].CardId == SyntheticFixture.FutureEnemy && future.Entities[201].CardId is null, "public and private enemy identities were conflated");
var historicalJson = SnapshotCodec.Serialize(beforeContext.Snapshot);
Check(!historicalJson.Contains(SyntheticFixture.FutureEnemy) && !historicalJson.Contains(SyntheticFixture.PrivateProbe), "future identity leaked into snapshot");
Check(beforeContext.RecentEvents.All(e => e.EventSequence <= before.EventSequence && !e.DataJson.Contains(SyntheticFixture.FutureEnemy) && !e.DataJson.Contains(SyntheticFixture.PrivateProbe)), "historical events leaked future/private data");
var allEvents = await restarted.GetEventsAtAsync(sessionId, long.MaxValue, 10000);
Check(allEvents.All(e => !e.DataJson.Contains(SyntheticFixture.PrivateProbe) && e.CardId != SyntheticFixture.PrivateProbe), "private enemy probe persisted in history");
Check(allEvents.Any(e => e.CardId == SyntheticFixture.FutureEnemy), "later public reveal was not recorded");
AssertComposition(beforeContext.Snapshot, 26, new Dictionary<string, int> { [SyntheticFixture.CardId(1)] = 0, [SyntheticFixture.CardId(2)] = 0 });
Check((await restarted.GetHistoricalContextAtAsync(sessionId, opening.EventSequence))!.Snapshot.DeckTracking!.TotalCards == 27, "later tracking overwrote earlier composition");
Pass("as-of historical context isolates later public reveals and private enemy identities while retaining earlier composition");

var played = first.Checkpoints["played"];
var playedLedger = played.PublicPlays ?? throw new Exception("enrichment lost public-play ledger");
Check(playedLedger.MatchId == played.MatchId && playedLedger.StateVersion == played.StateVersion && playedLedger.IsComplete && playedLedger.Warnings.Count == 0, "public-play metadata is incomplete or belongs to another state");
Check(playedLedger.Plays.Count == 1 && playedLedger.Plays[0].EntityId == 100 && playedLedger.Plays[0].CardId == SyntheticFixture.CardId(1) && playedLedger.Plays[0].ControllerId == 1, "own public play lost identity/controller/sequence");
Check(playedLedger.Plays[0].Sequence > 0 && playedLedger.Plays[0].Sequence <= played.EventSequence, "public-play sequence is outside its snapshot");
var unresolved = first.Checkpoints["enemy-unresolved-play"].PublicPlays ?? throw new Exception("missing unresolved-play ledger");
Check(unresolved.Plays.Count == 2 && unresolved.Plays[1].CardId is null && !unresolved.IsComplete && unresolved.Warnings.Count > 0, "unknown public play was invented or lost warnings");
Check(future.PublicPlays is { IsComplete: true, Plays.Count: 2 } && future.PublicPlays.Plays[1].CardId == SyntheticFixture.FutureEnemy, "explicit later public reveal did not resolve this version");
Check(playedLedger.Plays.Count == 1 && unresolved.Plays[1].CardId is null, "later reveal mutated an earlier public ledger");
var restoredPlayed = (await restarted.GetHistoricalContextAtAsync(sessionId, played.EventSequence))!.Snapshot;
var restoredLedger = restoredPlayed.PublicPlays ?? throw new Exception("storage codec omitted read-only public ledger metadata");
Check(JsonSerializer.Serialize(restoredLedger) == JsonSerializer.Serialize(playedLedger), "stored public ledger lost match/version/completeness/warnings/plays");
var restoredUnresolved = (await restarted.GetHistoricalContextAtAsync(sessionId, first.Checkpoints["enemy-unresolved-play"].EventSequence))!.Snapshot.PublicPlays!;
Check(restoredUnresolved.Warnings.SequenceEqual(unresolved.Warnings) && !restoredUnresolved.IsComplete && restoredUnresolved.Plays[1].CardId is null, "saved unresolved metadata or warnings were backfilled");
Pass("PublicPlays serializes match/version/completeness/warnings and immutable as-of identities across restart");

var pointCount = timeline.Count;
var duplicate = await ReplayAndRecord(rankedPath, catalog, restarted, firstDeck);
Check((await restarted.ListSessionsAsync()).Count == 1, "second import duplicated session");
Check((await restarted.ListDecisionPointsAsync(sessionId, true)).Count == pointCount &&
    (await restarted.GetEventsAtAsync(sessionId, long.MaxValue, 10000)).Count == allEvents.Count, "second import duplicated timeline/events");
foreach (var checkpoint in first.Checkpoints)
    Check(SnapshotCodec.Serialize(checkpoint.Value) == SnapshotCodec.Serialize(duplicate.Checkpoints[checkpoint.Key]), "replay is nondeterministic");
Pass("second import is idempotent and deterministic across all named checkpoints");

using (var updatedCards = Utf8(SyntheticFixture.CardsJson(changed: true)))
    await catalog.ImportAsync(updatedCards, SyntheticFixture.NewBuild, source: "synthetic://new-version-only");
var newer = await restarted.SaveDeckVersionAsync("synthetic hunter 30", SyntheticFixture.Deck(changed: true), SyntheticFixture.NewBuild, firstDeck.Id);
var oldContext = await restarted.GetHistoricalContextAtAsync(sessionId, drawn.EventSequence) ?? throw new Exception("missing old context");
Check(newer.Id != firstDeck.Id && newer.PreviousVersionId == firstDeck.Id, "new deck did not receive a new version");
Check(oldContext.CardBuild == SyntheticFixture.Build && oldContext.DeckVersionId == firstDeck.Id && oldContext.Deck!.Code == firstDeck.Code, "new deck/build changed old session binding");
var historicalCard = (await catalog.GetByIdsAsync([SyntheticFixture.CardId(1)], oldContext.CardBuild)).Single();
var activeCard = (await catalog.GetByIdsAsync([SyntheticFixture.CardId(1)])).Single();
Check(historicalCard.Name == "synthetic original 01" && activeCard.Name == "synthetic new 01", "historical card lookup followed active pointer");
Check(SnapshotCodec.Serialize(oldContext.Snapshot) == SnapshotCodec.Serialize(drawn), "saving a new deck changed the old snapshot");
Pass("new deck and active card build leave old sessions, composition and card definitions fixed");

await ReplayAndRecord(casualPath, catalog, restarted, firstDeck);
var rankedFilter = new SessionFilter(DeckVersionId: firstDeck.Id, CardBuild: SyntheticFixture.Build,
    Format: "standard", PlayerClass: "hunter", OpponentClass: "mage", Region: "cn", WentFirst: true, GameType: "GT_RANKED");
var rankedStats = await restarted.GetStatsAsync(rankedFilter);
var casualStats = await restarted.GetStatsAsync(rankedFilter with { GameType = "GT_CASUAL" });
Check(rankedStats.Games == 1 && rankedStats.Wins == 1 && rankedStats.Losses == 0, "casual game contaminated ranked results");
Check(casualStats.Games == 1 && casualStats.Wins == 0 && casualStats.Losses == 1, "casual filter lost its match");
Check((await restarted.ListSessionsAsync(rankedFilter)).Single().GameType == "GT_RANKED", "ranked session filtering failed");
Pass("GT_RANKED statistics exclude casual games with the same deck, format, region and classes");

var unboundStore = new SessionStore(Path.Combine(temp, "unbound-history.db")); await unboundStore.InitializeAsync();
var unbound = await ReplayAndRecord(rankedPath, catalog, unboundStore, deck: null);
var unboundEnd = unbound.Checkpoints["complete"];
Check(unboundEnd.DeckTracking is null && unboundEnd.PublicPlays is { IsComplete: true, Plays.Count: 2 }, "public plays depend on a bound deck");
var reopenedUnbound = new SessionStore(Path.Combine(temp, "unbound-history.db")); await reopenedUnbound.InitializeAsync();
var unboundContext = (await reopenedUnbound.GetHistoricalContextAtAsync(unboundEnd.MatchId, unboundEnd.EventSequence))!;
Check(unboundContext.Deck is null && unboundContext.DeckVersionId is null && unboundContext.Snapshot.PublicPlays!.Plays.Count == 2, "unbound recording lost public plays or invented a deck");
Pass("unbound games retain durable public plays while deck composition remains unknown");

var cardMap = (await catalog.GetByIdsAsync(firstDeck.Definition.Cards.Select(c => SyntheticFixture.CardId(c.DbfId - 1000))
    .Concat(["SYN_HERO_05", "SYN_HERO_08"]), SyntheticFixture.Build)).ToDictionary(c => c.Id);
var evaluation = new LineEvaluator().EvaluateLine(before, [new(ActionKind.PlayCard, 100), new(ActionKind.Attack, 100, 20)], cardMap);
var actual = first.Checkpoints["attacked"];
Check(evaluation.Verification == LineVerification.Verified && evaluation.AppliedActions == 2 && !evaluation.IsLethal, "simple deterministic sequence was not verified");
Check(evaluation.Result.AvailableMana == 0 && evaluation.Result.Entities.Single(e => e.EntityId == 20).RemainingHealth == actual.Entities[20].RemainingHealth &&
    evaluation.Result.Entities.Single(e => e.EntityId == 100).Zone == actual.Entities[100].Zone &&
    evaluation.Result.Entities.Single(e => e.EntityId == 100).AttacksUsed == actual.Entities[100].GetIntTag("NUM_ATTACKS_THIS_TURN"), "simulation differs from corresponding later synthetic observations");
Pass("rules validate summon-cost, charge and attack against corresponding later log observations");

var handler = new FakeModelHandler();
using var http = new HttpClient(handler);
var calls = new List<string>();
var pinnedNode = CompactAdviceContextBuilder.Build(before, oldContext.CardBuild, firstDeck.Definition, cardMap.Values);
var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://synthetic.invalid/v1/chat/completions"), "synthetic-fake-model", MaxToolRounds: 2),
    "synthetic-dummy-key", new(sessionId, before.StateVersion, "turn", pinnedNode), async (name, arguments, token) =>
    {
        calls.Add(name);
        if (name == "get_snapshot") return pinnedNode.DeepClone();
        if (name == "get_cards")
        {
            var ids = arguments["card_ids"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
            var found = await catalog.GetByIdsAsync(ids, oldContext.CardBuild, cancellationToken: token);
            return JsonSerializer.SerializeToNode(new { build = oldContext.CardBuild, cards = found.Select(c => new { c.Id, c.Name, c.Text, c.Cost }), missing = ids.Except(found.Select(c => c.Id)).ToArray() })!;
        }
        throw new Exception("unexpected tool");
    }, () => true);
Check(handler.Requests.Count == 2 && calls.SequenceEqual(["get_snapshot", "get_cards"]), "fake tool loop did not complete exactly two calls");
Check(advice.MatchId == sessionId && advice.StateVersion == before.StateVersion && advice.Summary == "synthetic：使用冲锋随从施加压力", "advice lost pinned version");
Check(handler.Requests[1].Contains("synthetic original 01") && handler.Requests[1].Contains("SYN_MISSING"), "tool response lost pinned card data or missing-card limitation");
Check(handler.Requests.All(body => !body.Contains(SyntheticFixture.FutureEnemy) && !body.Contains(SyntheticFixture.PrivateProbe) && !body.Contains("synthetic-dummy-key") && !body.Contains("GameAccountId")), "model payload contains future/private data or credentials");
Pass("fake HTTP AdviceService get_snapshot/get_cards loop uses historical build and excludes hidden/future data");

var observedTools = new List<(string Name, bool Success)>();
var coach = new SnapshotCoach(before, pinnedNode, catalog, SyntheticFixture.Build,
    (name, _, success) => observedTools.Add((name, success)));
var odds = await coach.ExecuteAsync("calculate_draw_odds", new JsonObject { ["card_id"] = SyntheticFixture.CardId(3), ["draws"] = 2 }, default);
Check(odds["is_exact"]!.GetValue<bool>() && Math.Abs(odds["probability"]!.GetValue<double>() - DrawProbability.AtLeastOne(26, 2, 2)) < 1e-12, "coach odds do not use pinned exact remaining deck");
var toolEvents = await coach.ExecuteAsync("get_recent_events", new(), default);
Check(!toolEvents.ToJsonString().Contains(SyntheticFixture.FutureEnemy) && !toolEvents.ToJsonString().Contains(SyntheticFixture.PrivateProbe), "coach event tool leaked later/private identities");
var choiceCoach = new SnapshotCoach(before with { Phase = GamePhase.Choice }, pinnedNode, catalog, SyntheticFixture.Build);
var choiceOdds = await choiceCoach.ExecuteAsync("calculate_draw_odds", new JsonObject { ["card_id"] = SyntheticFixture.CardId(3), ["draws"] = 2 }, default);
Check(!choiceOdds["is_exact"]!.GetValue<bool>() && choiceOdds["probability"] is null, "choice phase produced exact draw odds");
var unknownCoach = new SnapshotCoach(before, pinnedNode, catalog, cardBuild: null);
Check((await unknownCoach.ExecuteAsync("get_cards", new JsonObject { ["card_ids"] = new JsonArray(SyntheticFixture.CardId(1)) }, default))["error"] is not null, "missing build did not preserve unknown rules");
await Throws<ArgumentException>(() => coach.ExecuteAsync("calculate_draw_odds", new JsonObject { ["card_id"] = "x", ["draws"] = -1 }, default));
await Throws<ArgumentException>(() => coach.ExecuteAsync("get_snapshot", new JsonObject { ["unexpected"] = true }, default));
Check(observedTools.TakeLast(2).All(t => !t.Success), "failed tool validation was logged as successful");
Pass("SnapshotCoach validates parameters and gates probabilities on exact stable tracking, phase and build");

observedTools.Clear();
using var bridgeHandler = new BridgeModelHandler();
using var bridgeHttp = new HttpClient(bridgeHandler);
var budget = new AnalysisBudget(new(MaxRequestsPerMatch: 4, MaxTokensPerMatch: 1_000_000));
var usages = new List<ModelUsage>();
var bridged = await new AdviceService(bridgeHttp).AnalyzeAsync(new(new Uri("https://synthetic.invalid/v1/chat/completions"), "synthetic-fake-model"),
    "synthetic-dummy-key", new(sessionId, before.StateVersion, "turn", pinnedNode), coach.ExecuteAsync, () => true,
    capabilities: coach.Capabilities(budget, usages.Add));
Check(bridged.Verification == "verified" && bridged.Actions.Count == 2 && !bridged.VerifiedLethal, "host did not verify concrete actions or trusted a model lethal claim");
Check(observedTools.Select(t => t.Name).SequenceEqual(["get_snapshot", "get_cards", "get_recent_events", "calculate_draw_odds", "evaluate_line", "find_lethal"]) && observedTools.All(t => t.Success), "registered coaching tools did not traverse the model loop");
var bridgeRequest = JsonNode.Parse(bridgeHandler.Requests[1])!;
var toolMessages = bridgeRequest["messages"]!.AsArray().Where(m => m!["role"]?.GetValue<string>() == "tool")
    .ToDictionary(m => m!["tool_call_id"]!.GetValue<string>(), m => JsonNode.Parse(m!["content"]!.GetValue<string>())!);
Check(toolMessages["bridge-line"]["Verification"]!.GetValue<string>() == "Verified" &&
    !toolMessages["bridge-lethal"]["VerifiedLethalFound"]!.GetValue<bool>() && !toolMessages["bridge-lethal"]["IsExhaustive"]!.GetValue<bool>(), "rule tool results were lost or search claimed exhaustive coverage");
Check(usages.Count == 2 && budget.Snapshot(sessionId).RequestsStarted == 2 && budget.Snapshot(sessionId).InFlightRequests == 0, "coaching calls did not settle model usage budget");
Check(bridgeHandler.Requests.All(body => !body.Contains(SyntheticFixture.FutureEnemy) && !body.Contains(SyntheticFixture.PrivateProbe)), "coaching model loop leaked future/private data");
Pass("full coaching tool loop bridges draw odds, rules/search, host action validation and model budgets");

var secretEntity = new EntitySnapshot(999, null, EntityKind.Card,
    new Dictionary<string, string> { ["CONTROLLER"] = "2", ["ZONE"] = "SECRET", ["CARDTYPE"] = "5" }.ToFrozenDictionary());
var conditionalSnapshot = before with { Entities = before.Entities.Append(new(999, secretEntity)).ToFrozenDictionary() };
var conditionalNode = CompactAdviceContextBuilder.Build(conditionalSnapshot, SyntheticFixture.Build, firstDeck.Definition, cardMap.Values);
var conditionalCoach = new SnapshotCoach(conditionalSnapshot, conditionalNode, catalog, SyntheticFixture.Build);
using var conditionalHandler = new BridgeModelHandler(requestTools: false);
using var conditionalHttp = new HttpClient(conditionalHandler);
var conditionalAdvice = await new AdviceService(conditionalHttp).AnalyzeAsync(new(new Uri("https://synthetic.invalid/v1/chat/completions"), "synthetic-fake-model"),
    "synthetic-dummy-key", new(sessionId, before.StateVersion, "turn", conditionalNode), conditionalCoach.ExecuteAsync, () => true,
    capabilities: conditionalCoach.Capabilities());
Check(conditionalAdvice.Verification == "conditional" && conditionalAdvice.Actions.Count == 0 && !conditionalAdvice.VerifiedLethal && conditionalAdvice.Risks.Count() > 0,
    "conditional secret interaction escaped as verified concrete actions");
Pass("uncovered secrets downgrade model plans and remove concrete actions and lethal claims");

await CoachLibraryIntegration.RunAsync(temp, catalog, firstDeck, before, Pass);
await CoachPrivacyIntegration.RunAsync(catalog, before, Pass);
await ChoiceEffectIntegration.RunAsync(temp, Pass);
await AdviceLimitationsIntegration.RunAsync(temp, Pass);
await CliHistoryIntegration.RunAsync(temp, Pass);
await CliHistoryIntegration.RunModelCapabilitiesAsync(temp, Pass);
await CliHistoryIntegration.RunLargeCardsAsync(temp, Pass);
await LargeCardCoachingIntegration.RunAsync(temp, Pass);
await DarkGiftStaticIntegration.RunAsync(temp, Pass);
await DarkGiftExpandedIntegration.RunAsync(temp, Pass);
await FindLethalCandidateIntegration.RunAsync(temp, Pass);
await MultiStageChoiceIntegration.RunAsync(temp, Pass);
await HunterHistoryIntegration.RunAsync(temp, Pass);
await AssembledEffectIntegration.RunAsync(temp, Pass);
await HunterRandomEffectIntegration.RunAsync(temp, Pass);
await HunterPlayTriggerIntegration.RunAsync(temp, Pass);
await NativeDiscoveryIntegration.RunAsync(temp, Pass);
await MapLifecycleIntegration.RunAsync(temp, Pass);
await SideboardChoiceEvidenceIntegration.RunAsync(temp, Pass);
await StartingDeckIntegration.RunAsync(temp, Pass);
await MagmaSplitIntegration.RunAsync(temp, Pass);
await ExplosiveTrapIntegration.RunAsync(temp, Pass);
await WeaponCombatIntegration.RunAsync(temp, Pass);
await HistoryPagingIntegration.RunAsync(temp, Pass);
await TerrorEndTurnIntegration.RunAsync(temp, Pass);
await CoachLibraryRetrievalIntegration.RunAsync(temp, Pass);
await ArchiveBrowsingIntegration.RunAsync(temp, Pass);
await DecisionReplayIntegration.RunAsync(temp, Pass);

if (args.Length == 2)
{
    var output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(output);
    var exports = new Dictionary<string, string>
    {
        ["snapshot.json"] = historicalJson,
        ["deck.json"] = JsonSerializer.Serialize(new { synthetic = true, build = firstDeck.CardBuild, code = firstDeck.Code, definition = firstDeck.Definition }),
        ["cards.json"] = SyntheticFixture.CardsJson(includeFuture: false),
        ["manifest.json"] = JsonSerializer.Serialize(new { synthetic = true, purpose = "Anonymous fictional model-integration fixture; no real match, user log, account or credential.", build = SyntheticFixture.Build, stateVersion = before.StateVersion, files = new[] { "snapshot.json", "deck.json", "cards.json" } })
    };
    foreach (var file in exports)
    {
        Check(!file.Value.Contains(SyntheticFixture.FutureEnemy) && !file.Value.Contains(SyntheticFixture.PrivateProbe), "export contains later/private enemy identity");
        await using var stream = new FileStream(Path.Combine(output, file.Key), FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(file.Value));
    }
    Console.WriteLine($"Exported synthetic, filtered model fixture: {output}");
}
Console.WriteLine($"Integration: {count} end-to-end checks passed (generated synthetic log + fake HTTP; no external calls).");
return 0;

void Pass(string label) { count++; Console.WriteLine("PASS " + label); }
static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static string FindWorkspace()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run this acceptance project inside the hs_helper workspace.");
}
static void AssertComposition(GameSnapshot snapshot, int total, IReadOnlyDictionary<string, int> counts)
{
    var tracking = snapshot.DeckTracking ?? throw new Exception("missing persisted tracking");
    Check(tracking.IsExact && tracking.TotalCards == total && tracking.UnknownCount == 0 && tracking.KnownRemaining.Values.Sum() == total, "deck composition is not exact: " + string.Join(";", tracking.Warnings));
    foreach (var pair in counts) Check(tracking.KnownRemaining.GetValueOrDefault(pair.Key) == pair.Value, "remaining count differs: " + pair.Key);
}
static async Task<ReplayResult> ReplayAndRecord(string path, CardCatalog catalog, SessionStore store, DeckVersion? deck)
{
    var engine = new PowerLogStateEngine(sessionId: LogSourceIdentity.Create(path, 0, deck?.Id), autoDetectPerspective: true);
    var build = deck?.CardBuild ?? SyntheticFixture.Build;
    var enricher = await DeckTrackingEnricher.CreateAsync(catalog, deck, build);
    var pipeline = new LogRecordingPipeline(store, deck?.Id, build);
    var states = new Dictionary<(long, long), GameSnapshot>();
    var checkpoints = new Dictionary<string, GameSnapshot>(StringComparer.Ordinal);
    GameSnapshot? last = null;
    var changes = 0; var flushes = 0; long lineNumber = 0;
    await foreach (var line in SharedLogReplay.ReadLinesAsync(path))
    {
        lineNumber++;
        if (line.StartsWith("# SYNTHETIC_CHECKPOINT ", StringComparison.Ordinal))
        { checkpoints.Add(line[23..], last ?? throw new Exception("checkpoint before game")); continue; }
        if (!engine.ProcessLine(line)) continue;
        changes++;
        last = enricher.Observe(engine.Snapshot());
        states[(last.EventSequence, last.StateVersion)] = last;
        pipeline.Observe(last, lineNumber);
        if (changes % 50 == 0 || last.Quality.IsStable)
        { await pipeline.FlushAsync(); flushes++; }
    }
    await pipeline.CompleteAsync();
    Check(pipeline.RecordedMatchCount == 1 && pipeline.UnconfirmedMatchCount == 0, "pipeline did not bind exactly one confirmed match");
    return new(states, checkpoints, changes, flushes);
}

internal sealed record ReplayResult(Dictionary<(long, long), GameSnapshot> States,
    Dictionary<string, GameSnapshot> Checkpoints, int ChangedStates, int Flushes);

internal sealed class FakeModelHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (request.Headers.Authorization?.ToString() != "Bearer synthetic-dummy-key") throw new Exception("fake credential is not confined to authorization header");
        object message = Requests.Count == 1
            ? new { role = "assistant", tool_calls = new object[]
                {
                    new { id = "synthetic-snapshot", type = "function", function = new { name = "get_snapshot", arguments = "{}" } },
                    new { id = "synthetic-cards", type = "function", function = new { name = "get_cards", arguments = "{\"card_ids\":[\"SYN_CARD_01\",\"SYN_MISSING\"]}" } }
                } }
            : new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "synthetic：使用冲锋随从施加压力", reasons = new[] { "可见手牌与当前费用明确" }, alternative = (string?)null, risks = new[] { "对手其余手牌未知；这不是完整牌效斩杀证明" } }) };
        var json = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Requests.Count == 1 ? "tool_calls" : "stop", message } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

internal sealed class BridgeModelHandler(bool requestTools = true) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        object Tool(string id, string name, object args) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
        var actions = new object[] { new { kind = "play", source_entity_id = 100 }, new { kind = "attack", source_entity_id = 100, target_entity_id = 20 } };
        object message = Requests.Count == 1 && requestTools
            ? new { role = "assistant", tool_calls = new[]
                {
                    Tool("bridge-snapshot", "get_snapshot", new { }),
                    Tool("bridge-cards", "get_cards", new { card_ids = new[] { "SYN_CARD_01" } }),
                    Tool("bridge-events", "get_recent_events", new { }),
                    Tool("bridge-odds", "calculate_draw_odds", new { card_id = "SYN_CARD_03", draws = 2 }),
                    Tool("bridge-line", "evaluate_line", new { actions }),
                    Tool("bridge-lethal", "find_lethal", new { maximum_depth = 2, maximum_nodes = 50 })
                } }
            : new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "synthetic：候选冲锋随从后攻击", reasons = new[] { "采用工具校验结果" }, risks = Array.Empty<string>(), actions, verified_lethal = true }) };
        var json = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Requests.Count == 1 && requestTools ? "tool_calls" : "stop", message } },
            usage = new { prompt_tokens = 40, completion_tokens = 20, total_tokens = 60 } });
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
