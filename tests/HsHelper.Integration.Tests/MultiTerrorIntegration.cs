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

internal static class MultiTerrorIntegration
{
    private const string Build = "255790", Template = "hunter-v23/turn";
    private const string PrivateCard = "PRIVATE_MULTI_TERROR_HAND", PrivateGift = "EDR_100t13e";
    private const string Key = "multi-terror-fixture-key", Complete = "PowerProcessor.EndCurrentTaskList() - complete";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var records = new List<JsonElement>();
        foreach (var name in new[] { "terror-end-turn", "dark-gift-static" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build && fixture.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The joint integration lost fixed primary card provenance.");
            records.AddRange(fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var directory = Path.Combine(root, "multi-terror", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(records.GroupBy(card => card.GetProperty("id").GetString()).Select(group => group.First()))))
            await catalog.ImportAsync(input, Build, source: "fixture://fixed-multi-terror-integration");

        var ordinary = await Replay.CreateAsync(directory, catalog, new()); var saved = await ordinary.SaveAsync();
        var line = await Line(saved, catalog); var preview = Preview(line);
        AssertJoint(line, [30, 31], complete: true); AssertStages(preview, [30, 31]);
        Check(preview["Branches"]!.AsArray().Count > 1 && preview["Branches"]!.AsArray().All(branch => branch!["StopReason"]?.ToString() == "AllSourcesResolved") &&
            preview["Branches"]!.AsArray().Any(branch => branch!["Combats"]!.AsArray().GroupBy(combat => combat!["AttackerEntityId"]!.GetValue<int>()).Any(group => group.Count() > 1)),
            "The preview lost correlated alternatives or prevented a surviving enemy from attacking another source.");
        Check(line["Result"]?["PendingForcedAttacks"]!.AsArray().Count == 2 && Entity(line, 11)["RemainingHealth"]?.GetValue<int>() == 20 &&
            Entity(line, 30)["RemainingHealth"]?.GetValue<int>() == 5 && Entity(line, 31)["RemainingHealth"]?.GetValue<int>() == 7 &&
            line["Result"]?["TurnEnded"]?.GetValue<bool>() == true && line["Result"]?["EndTurnTriggersResolved"]?.GetValue<bool>() == false &&
            SnapshotCodec.Serialize(saved.Frame) == saved.Original,
            "One conditional branch was installed as the authoritative main state or resolved the next player's turn.");
        pass("real parsed and reopened multi-source end turn returns one correlated preview with source-set stage witnesses while main entities remain before the triggers");

        var shield = await Replay.CreateAsync(directory, catalog, new()
        {
            Sources = [new(30, "CORE_TTN_866", 3, 10, Life: true), new(31, "CORE_TTN_866", 2, 10, Life: true)],
            Enemies = [new(40, "CORE_CS2_231", 1, 8, Shield: true)]
        });
        var shieldSaved = await shield.SaveAsync(); var shieldLine = await Line(shieldSaved, catalog); var shieldPreview = Preview(shieldLine);
        AssertJoint(shieldLine, [30, 31], true); AssertStages(shieldPreview, [30, 31]);
        var outcomes = shieldPreview["Branches"]!.AsArray();
        Check(outcomes.Count == 2 && outcomes.Select(branch => branch!["OwnHeroHealthAfter"]!.GetValue<long>()).Order().SequenceEqual([22L, 23L]),
            "Shared shield consumption did not produce the two concrete healing outcomes.");
        foreach (var branch in outcomes)
        {
            var combats = branch!["Combats"]!.AsArray();
            Check(combats.Count == 2 && combats.All(combat => combat!["AttackerEntityId"]?.GetValue<int>() == 40) &&
                combats[0]!["DamageToAttacker"]?.GetValue<long>() == 0 && combats[1]!["DamageToAttacker"]?.GetValue<long>() is 2 or 3 &&
                BranchEntity(branch, 40)["DivineShield"]?.GetValue<bool>() == false &&
                BranchEntity(branch, 40)["RemainingHealth"]!.GetValue<long>() + branch["LifestealHealing"]!.GetValue<long>() == 8 &&
                branch["OwnHeroHealthAfter"]!.GetValue<long>() == 20 + branch["LifestealHealing"]!.GetValue<long>() &&
                BranchEntity(branch, 30)["RemainingHealth"]?.GetValue<long>() == 9 && BranchEntity(branch, 31)["RemainingHealth"]?.GetValue<long>() == 9,
                "The second source regained a spent shield or combined another branch's damage and lifesteal.");
        }
        pass("one shared enemy shield is consumed only once and the same enemy attacks both sources with branch-correlated damage and actual lifesteal caps");

        var dying = await Replay.CreateAsync(directory, catalog, new()
        {
            Sources = [new(30, "CORE_TTN_866", 1, 1), new(31, "CORE_TTN_866", 8, 20)],
            Enemies = [new(40, "CORE_CS2_231", 2, 6)]
        });
        var deathLine = await Line(await dying.SaveAsync(), catalog); AssertJoint(deathLine, [30, 31], true);
        var killedFirst = Preview(deathLine)["Branches"]!.AsArray().Single(branch => branch!["SourceOrder"]![0]?.GetValue<int>() == 30)!;
        Check(killedFirst["Combats"]!.AsArray().Count == 2 && BranchEntity(killedFirst, 30)["IsAlive"]?.GetValue<bool>() == false &&
            BranchEntity(killedFirst, 31)["RemainingHealth"]?.GetValue<long>() == 18 && BranchEntity(killedFirst, 40)["IsAlive"]?.GetValue<bool>() == false &&
            killedFirst["Stages"]![0]!["StopReason"]?.ToString() == "TerrorDied" && killedFirst["Stages"]![1]!["IsComplete"]?.GetValue<bool>() == true,
            "An ordinary source death incorrectly stopped the other source or reset the surviving enemy.");
        var exhausted = Preview(deathLine)["Branches"]!.AsArray().Single(branch => branch!["SourceOrder"]![0]?.GetValue<int>() == 31)!;
        Check(exhausted["Combats"]!.AsArray().Count == 1 && exhausted["Stages"]![1]!["CombatCount"]?.GetValue<int>() == 0 &&
            BranchEntity(exhausted, 30)["IsAlive"]?.GetValue<bool>() == true,
            "An enemy killed in the first source stage attacked again in a later stage.");
        pass("ordinary source death completes just that stage while another source continues and already-dead enemies do not return for the later trigger");

        foreach (var enemy in new[] { new Body(40, "EDR_227", 1, 1, Death: true), new Body(40, "FIR_953", 1, 20) })
        {
            var unknown = await Replay.CreateAsync(directory, catalog, new() { Enemies = [enemy] });
            var unknownLine = await Line(await unknown.SaveAsync(), catalog); var unknownPreview = Preview(unknownLine);
            AssertJoint(unknownLine, [30, 31], false);
            Check(unknownPreview["Branches"]!.AsArray().Count > 0 && unknownPreview["Branches"]!.AsArray().All(branch =>
                branch!["IsComplete"]?.GetValue<bool>() == false && branch["SourceOrder"]!.AsArray().Count == 1 && branch["Stages"]!.AsArray().Count == 1 &&
                branch["Combats"]!.AsArray().Count == 1 && branch["StopReason"]?.ToString() != "AllSourcesResolved"),
                "An unknown death or after-attack script ran the next source after the unresolved prefix.");
        }
        var contradiction = await Replay.CreateAsync(directory, catalog, new() { Changes = ["TAG_CHANGE Entity=30 tag=45 value=not-a-number"] });
        var contradictoryLine = await Line(await contradiction.SaveAsync(), catalog);
        Check(Preview(contradictoryLine)["ResolvesBranches"]?.GetValue<bool>() != true && Preview(contradictoryLine)["Branches"]!.AsArray().Count == 0,
            "The joint overlay repaired a malformed current numeric-health observation with printed values.");
        pass("unknown deathrattle and after-attack effects stop the entire joint witness before another stage while malformed numeric current health cannot become exact combat");

        var capped = await Replay.CreateAsync(directory, catalog, new()
        {
            Sources = Enumerable.Range(30, 7).Select(id => new Body(id, "CORE_TTN_866", 1, 100)).ToArray(),
            Enemies = Enumerable.Range(40, 7).Select(id => new Body(id, "CORE_CS2_231", 1, 100)).ToArray()
        });
        var capSaved = await capped.SaveAsync(); var capLine = await Line(capSaved, catalog); var capPreview = Preview(capLine);
        AssertJoint(capLine, Enumerable.Range(30, 7).ToArray(), false);
        Check(capPreview["Branches"]!.AsArray().Count <= 256 && capPreview["Unknowns"]!.AsArray().Count > 0 &&
            capPreview["RequiresObservation"]?.GetValue<bool>() == true,
            "A resource-capped joint enumeration silently presented its finite subset as the complete space.");
        AssertStages(capPreview, Enumerable.Range(30, 7).ToArray());
        var large = await Replay.CreateAsync(directory, catalog, new()
        {
            Sources = [new(30, "CORE_TTN_866", 1, 65, Shield: true), new(31, "CORE_TTN_866", 2, 48, Shield: true), new(32, "CORE_TTN_866", 3, 33, Shield: true)],
            Enemies = Enumerable.Range(40, 7).Select(id => new Body(id, "CS2_172", 1 << (id - 40), 100)).ToArray()
        });
        var largeSaved = await large.SaveAsync(); var fullLine = await Line(largeSaved, catalog); var fullPreview = Preview(fullLine);
        var fullBranches = fullPreview["Branches"]!.AsArray();
        Check(fullBranches.Count > 8 && fullBranches.Count <= 256, "The real high-branch local tool was silently limited to a model display subset.");
        Console.WriteLine($"MULTI_TERROR_FULL tool_utf8={Encoding.UTF8.GetByteCount(fullLine.ToJsonString())} branches={fullBranches.Count}");
        var high = await CompleteModelAsync(largeSaved, catalog);
        Console.WriteLine($"MULTI_TERROR_SIZE tool_utf8={Encoding.UTF8.GetByteCount(high.Line.ToJsonString())} next_http_utf8={high.SecondPayloadBytes} branches={Preview(high.Line)["Branches"]!.AsArray().Count}");
        var displayed = Preview(high.Line); var projection = displayed["BranchProjection"];
        Check(displayed["Branches"]!.AsArray().Count == 8 && projection?["TotalBranches"]?.GetValue<int>() == fullBranches.Count &&
            projection["ShownBranches"]?.GetValue<int>() == 8 && projection["AllBranchesIncluded"]?.GetValue<bool>() == false &&
            projection["MaximumShownBranches"]?.GetValue<int>() == 8 && !string.IsNullOrEmpty(projection["Scope"]?.ToString()) &&
            displayed["IsEnumerationComplete"]?.GetValue<bool>() == fullPreview["IsEnumerationComplete"]?.GetValue<bool>() &&
            displayed["Branches"]!.AsArray().All(branch => fullBranches.Any(full => full!.ToJsonString() == branch!.ToJsonString())),
            "The model projection hid its subset or changed complete witness branches and calculation completeness.");
        AssertStages(displayed, [30, 31, 32]);
        Check(Encoding.UTF8.GetByteCount(high.Line.ToJsonString()) < 1024 * 1024 && high.SecondPayloadBytes < 1024 * 1024,
            "The real high-branch tool or second HTTP body exceeded the 1MiB boundary.");
        pass("global state and terminal limits remain explicit and a real high-branch evaluate-line response reaches the next fake HTTP round within the transport bound");

        var early = await ordinary.SaveAsync(); var oldBytes = early.Original;
        await ordinary.FeedAsync("TAG_CHANGE Entity=31 tag=ZONE value=GRAVEYARD", Complete);
        var later = await ordinary.SaveAsync(); var oldAgain = await new SessionStore(early.Database).GetHistoricalDecisionAsync(early.Point.Id);
        Check(oldAgain is not null && SnapshotCodec.Serialize(oldAgain.Context.Snapshot) == oldBytes &&
            oldAgain.Context.DeckVersionId == early.Deck.Id && oldAgain.Context.CardBuild == Build &&
            oldAgain.Context.Snapshot.DeckTracking?.DeckVersion == early.Deck.Id,
            "A later source departure changed the saved exact snapshot or original deck binding.");
        var oldLine = await Line(early with { Frame = oldAgain!.Context.Snapshot }, catalog); AssertJoint(oldLine, [30, 31], true);
        var current = await Line(later, catalog);
        Check(Preview(current)["SourceEntityIds"] is null && !oldLine.ToJsonString().Contains(PrivateCard, StringComparison.Ordinal) &&
            !oldLine.ToJsonString().Contains(PrivateGift, StringComparison.Ordinal) &&
            !early.Frame.Entities.ContainsKey(91) && early.Frame.Entities[90].CardId is null &&
            Preview(oldLine)["Branches"]!.AsArray().All(branch => branch!["Entities"]!.AsArray().All(entity => entity!["EntityId"]?.GetValue<int>() is not (90 or 91))),
            "A hidden opposing identity or private attachment entered a joint branch, or old source IDs followed the later frame.");
        foreach (var field in new[] { "source_order", "attacker_ids", "branch_id" })
        {
            var forged = EndTurn(); forged[field] = 30;
            await Throws<ArgumentException>(() => Line(early, catalog, new JsonArray(forged)));
        }
        pass("exact historical source sets retain their original deck state and private hand or attached bonuses never enter branch entities while caller-selected schedules are rejected");

        var completed = await CompleteModelAsync(early, catalog);
        Check(completed.Advice.Actions.Count == 0 && !completed.Advice.VerifiedLethal && completed.Archive.Advice.Actions.Count == 0 &&
            !completed.Archive.Advice.VerifiedLethal && completed.Archive.Advice.Verification != "verified" &&
            completed.Archive.Advice.HostUnsupportedMechanics?.Count > 0 && completed.Archive.Advice.Evidence.Single().ResultHash?.Length == 64,
            "A model-selected favorable branch acquired executable archived actions or verified lethal.");
        var followup = await CompleteModelAsync(early, catalog, afterEndTurn: true);
        Check(followup.Line["AppliedActions"]?.GetValue<int>() == 1 && followup.Line["Verification"]?.ToString() == "Unsupported" &&
            followup.Advice.Actions.Count == 0 && !followup.Advice.VerifiedLethal && followup.Archive.Advice.Actions.Count == 0 &&
            followup.Archive.Advice.Verification == "unsupported" && !followup.Archive.Advice.VerifiedLethal && followup.Line["IsLethal"]?.GetValue<bool>() == false &&
            Entity(followup.Line, 30)["RemainingHealth"]?.GetValue<int>() == 5,
            "An action after the unresolved end-turn boundary executed using a branch's conditional survivors.");
        pass("two-round real host validation removes forged branch lethal and post-end-turn actions while immutable archive evidence and paid trace lineage survive reopening");
    }

    private sealed record Body(int Id, string Card, int Attack, int Health, bool Life = false, bool Shield = false, bool Death = false);
    private sealed record Spec
    {
        public Body[] Sources { get; init; } = [new(30, "CORE_TTN_866", 4, 5, Life: true), new(31, "CORE_TTN_866", 2, 7, Life: true)];
        public Body[] Enemies { get; init; } = [new(40, "CS2_172", 5, 5), new(41, "CORE_CS2_231", 1, 4)];
        public string[] Changes { get; init; } = [];
    }
    private sealed record Saved(string Directory, string Database, GameSnapshot Frame, DecisionPoint Point, DeckVersion Deck, string Original);
    private sealed record Completed(JsonNode Line, StrategyAdvice Advice, ArchivedAdviceRecord Archive, int SecondPayloadBytes);

    private sealed class Replay(DeckTrackingEnricher enricher, SessionStore store, string directory, string database, DeckVersion deck)
    {
        public PowerLogStateEngine Engine { get; } = new(1, "multi-terror-" + Guid.NewGuid().ToString("N"));
        public GameSnapshot Frame { get; private set; } = null!;
        private SessionRecorder? _recorder;
        private long _line;
        public static async Task<Replay> CreateAsync(string root, CardCatalog catalog, Spec spec)
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            var database = Path.Combine(path, "session.db"); var store = new SessionStore(database); await store.InitializeAsync();
            // Synthetic starting composition exercises real version binding, not Standard deck legality.
            var deck = await store.SaveDeckVersionAsync("synthetic multi-terror evidence", new(2, [31], [new(129169, 2), new(120970, 28)], []), Build);
            var replay = new Replay(await DeckTrackingEnricher.CreateAsync(catalog, deck, Build), store, path, database, deck);
            await replay.FeedAsync("GameState.DebugPrintGame() - BuildNumber=" + Build + " FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
                "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=FIRST_PLAYER value=1", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=CLASS value=HUNTER",
                "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21", "tag=CLASS value=MAGE");
            await replay.CharacterAsync(new(11, "HERO_05", 0, 30), 1, "HERO"); await replay.FeedAsync("tag=DAMAGE value=10");
            await replay.CharacterAsync(new(21, "HERO_08", 0, 30), 2, "HERO");
            await replay.CharacterAsync(new(12, "HERO_05bp", 0, 0), 1, "HERO_POWER");
            await replay.CharacterAsync(new(22, "HERO_08bp", 0, 0), 2, "HERO_POWER");
            foreach (var body in spec.Sources) await replay.CharacterAsync(body, 1, "MINION");
            foreach (var body in spec.Enemies) await replay.CharacterAsync(body, 2, "MINION");
            await replay.FeedAsync("FULL_ENTITY - Creating ID=90 CardID=" + PrivateCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=99", "tag=HEALTH value=99", "tag=DAMAGE value=0",
                "FULL_ENTITY - Creating ID=91 CardID=" + PrivateGift, "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=90", "tag=WINDFURY value=1", "tag=DIVINE_SHIELD value=1");
            await replay.FeedAsync(spec.Changes);
            await replay.FeedAsync("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", Complete);
            Check(replay.Frame.Quality.IsComplete && replay.Frame.Quality.IsStable && replay.Frame.LocalPlayerId == 1 && replay.Frame.ActivePlayerId == 1,
                "The actual parser fixture is not a stable complete own-turn observation.");
            replay._recorder = new SessionRecorder(store, new(deck.Id, Build, 1)); await replay.RecordAsync(); return replay;
        }
        private Task CharacterAsync(Body body, int owner, string type) => FeedAsync(
            "FULL_ENTITY - Creating ID=" + body.Id + " CardID=" + body.Card, "tag=CONTROLLER value=" + owner, "tag=ZONE value=PLAY", "tag=CARDTYPE value=" + type,
            "tag=ATK value=" + body.Attack, "tag=HEALTH value=" + body.Health, "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=UNTOUCHABLE value=0",
            "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1",
            "tag=LIFESTEAL value=" + (body.Life ? 1 : 0), "tag=DIVINE_SHIELD value=" + (body.Shield ? 1 : 0), "tag=DEATHRATTLE value=" + (body.Death ? 1 : 0));
        public async Task FeedAsync(params string[] lines)
        {
            foreach (var line in lines)
            {
                _line++; Engine.ProcessLine(line); Frame = enricher.Observe(Engine.Snapshot());
                if (_recorder is not null) await RecordAsync();
            }
        }
        private Task RecordAsync() { _recorder!.Observe(Frame, _line); return _recorder.FlushAsync(); }
        public async Task<Saved> SaveAsync()
        {
            await _recorder!.CompleteAsync();
            var point = (await store.ListDecisionPointsAsync(Frame.MatchId, true)).Single(point => point.StateVersion == Frame.StateVersion && point.EventSequence == Frame.EventSequence);
            var exact = await new SessionStore(database).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact joint end-turn observation.");
            var original = SnapshotCodec.Serialize(Frame);
            Check(SnapshotCodec.Serialize(exact.Context.Snapshot) == original && exact.Context.DeckVersionId == deck.Id && exact.Context.Deck!.Code == deck.Code &&
                exact.Context.CardBuild == Build && !exact.HasUnattributedEvents, "Recording changed the exact frame or original session deck/build.");
            return new(directory, database, exact.Context.Snapshot, point, deck, original);
        }
    }

    private static JsonObject EndTurn() => new() { ["kind"] = "end_turn", ["source_entity_id"] = 2 };
    private static JsonArray Actions(bool followup = false) => followup ? new(EndTurn(), new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 21 }) : new(EndTurn());
    private static Task<JsonNode> Line(Saved saved, CardCatalog catalog, JsonArray? actions = null) => new SnapshotCoach(saved.Frame,
        CompactAdviceContextBuilder.Build(saved.Frame, Build, saved.Deck.Definition), catalog, Build).ExecuteAsync("evaluate_line", new() { ["actions"] = actions ?? Actions() }, default);
    private static JsonNode Preview(JsonNode line) => line["Result"]!["TerrorEndTurnPreviews"]!.AsArray().Single()!;
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    private static JsonNode BranchEntity(JsonNode branch, int id) => branch["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    private static void AssertJoint(JsonNode line, int[] ids, bool complete)
    {
        var preview = Preview(line);
        Check(line["AppliedActions"]?.GetValue<int>() == 1 && line["Verification"]?.ToString() != "Verified" && line["IsLethal"]?.GetValue<bool>() == false &&
            preview["SourceEntityIds"]!.AsArray().Select(value => value!.GetValue<int>()).SequenceEqual(ids.Order()) &&
            preview["IsEnumerationComplete"]?.GetValue<bool>() == complete && preview["HasAuthoritativeNextState"]?.GetValue<bool>() == false &&
            preview["CanEvaluateFollowupActions"]?.GetValue<bool>() == false && preview["IsLegal"] is null,
            "The actual host tool did not return one source-bound conditional joint preview: " + line.ToJsonString());
    }
    private static void AssertStages(JsonNode preview, int[] ids)
    {
        foreach (var branch in preview["Branches"]!.AsArray())
        {
            var order = branch!["SourceOrder"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray(); var stages = branch["Stages"]!.AsArray();
            var combats = branch["Combats"]!.AsArray(); var next = 0;
            Check(order.Distinct().Count() == order.Length && order.All(ids.Contains) && stages.Count == order.Length,
                "A witness invented or repeated a source rather than describing only started stages.");
            for (var index = 0; index < stages.Count; index++)
            {
                var stage = stages[index]!; var first = stage["FirstCombatIndex"]!.GetValue<int>(); var count = stage["CombatCount"]!.GetValue<int>();
                Check(stage["SourceEntityId"]?.GetValue<int>() == order[index] && first == next && count >= 0 && first + count <= combats.Count &&
                    combats.Skip(first).Take(count).All(combat => combat!["TargetEntityId"]?.GetValue<int>() == order[index]) &&
                    combats.Skip(first).Take(count).Select(combat => combat!["AttackerEntityId"]!.GetValue<int>()).Distinct().Count() == count,
                    "Stage indexes did not refer to their own contiguous combat witness or reused one enemy within the same stage.");
                next += count;
            }
            Check(next == combats.Count && (branch["IsComplete"]?.GetValue<bool>() != true || order.Length == ids.Length && stages.All(stage => stage!["IsComplete"]?.GetValue<bool>() == true)),
                "Complete joint terminal omitted a source, or a stage described combats outside the single shared witness.");
        }
    }

    private static async Task<Completed> CompleteModelAsync(Saved saved, CardCatalog catalog, bool afterEndTurn = false)
    {
        var cards = await catalog.GetByIdsBatchedAsync(saved.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(saved.Frame, Build, AdviceTaskKind.Turn, saved.Deck.Definition, cards)
            with { KnowledgeAsOf = saved.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow, DeckVersionId = saved.Deck.Id };
        var usage = new List<ModelUsage>(); var observations = new List<CoachToolObservation>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(saved.Frame, context.Snapshot, catalog, Build, toolCompleted: observations.Add);
        using var model = new Model(afterEndTurn); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://multi-terror-fixture.invalid/v1/chat/completions"), "multi-terror-fake", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usage.Add));
        Check(context.TemplateVersion == Template && model.Calls == 2 && model.Line is not null && observations is [{ Name: "evaluate_line", Success: true }] &&
            observations[0].ResultHash == Hash(observations[0].ResultJson!) && observations[0].AnalysisRunId == advice.AnalysisRunId && observations[0].RequestId == usage[0].RequestId &&
            observations[0].EventSequence == saved.Frame.EventSequence && usage.Count == 2 && usage.All(item => item.Success && item.ChargedTokens == 30 && item.PromptTemplateVersion == Template) &&
            budget.Snapshot(saved.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            advice.RequestIds.SequenceEqual(usage.Select(item => item.RequestId)) && advice.KnowledgeAsOf == saved.Point.RecordedAt &&
            SnapshotCodec.Serialize(saved.Frame) == saved.Original, "Actual HTTP tools final host usage or frozen provenance changed.");
        var archivePath = Path.Combine(saved.Directory, "archive-" + Guid.NewGuid().ToString("N") + ".db");
        var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync();
        var draft = AdviceArchiveStore.Prepare(new(saved.Frame.MatchId, saved.Frame.StateVersion, saved.Frame.EventSequence, Build, advice.TemplateVersion!, advice.Summary)
        {
            DeckVersionId = saved.Deck.Id, Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, Reasons = advice.Reasons, Risks = advice.Risks,
            Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = observations.Select(item => new AdviceToolEvidence(item.Name, "同一联合分支的阶段与交换；不证明实际顺序或权威后继。", item.ResultHash)).ToArray(),
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
            HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns, HostUnsupportedMechanics = advice.HostUnsupportedMechanics,
            HostLimitationsTruncated = advice.HostLimitationsTruncated
        }, [Key]);
        var archived = await archive.SaveAsync(draft); var loaded = await new AdviceArchiveStore(archivePath).GetAsync(archived.Id) ?? throw new InvalidOperationException("Missing joint archive.");
        Check(JsonSerializer.Serialize(loaded.Advice) == JsonSerializer.Serialize(archived.Advice) && loaded.Advice.VisibilityPolicy == "attachments-v1" && loaded.Advice.DeckVersionId == saved.Deck.Id &&
            loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) && loaded.Advice.Evidence.Single().ResultHash == observations[0].ResultHash &&
            !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal), "Archive lost source or evidence linkage, or persisted the credential.");
        var tracePath = Path.Combine(saved.Directory, "trace-" + Guid.NewGuid().ToString("N") + ".db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usage.Select(item => new ModelRequestTrace(item.RequestId, item.MatchId, item.StateVersion, item.Model, item.Round,
            item.InputTokens, item.OutputTokens, item.TotalTokens, item.ChargedTokens, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = Build, Task = context.Task, PromptTemplateVersion = item.PromptTemplateVersion!, AnalysisRunId = item.AnalysisRunId, EventSequence = item.EventSequence, DeckVersionId = saved.Deck.Id }),
        observations.Select(item => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), saved.Frame.MatchId, saved.Frame.StateVersion, item.Name, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = item.ResultHash, Model = item.Model, AnalysisRunId = item.AnalysisRunId, RequestId = item.RequestId, ToolCallId = item.ToolCallId, EventSequence = item.EventSequence }));
        var reopened = new AnalysisTraceStore(tracePath); var totals = await reopened.GetBudgetTotalsAsync(saved.Frame.MatchId); var tools = await reopened.ListToolsAsync(saved.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && tools.Count == 1 && tools[0].RequestId == usage[0].RequestId && tools[0].ResultHash == observations[0].ResultHash &&
            tools[0].AnalysisRunId == advice.AnalysisRunId && tools[0].EventSequence == saved.Frame.EventSequence && !JsonSerializer.Serialize(tools).Contains(Key, StringComparison.Ordinal),
            "Paid trace ledger or actual tool evidence changed after reopening.");
        var exact = await new SessionStore(saved.Database).GetHistoricalDecisionAsync(saved.Point.Id);
        Check(exact is not null && SnapshotCodec.Serialize(exact.Context.Snapshot) == saved.Original, "A branch or final advice rewrote the exact saved observation.");
        return new(model.Line!, advice, loaded, model.SecondPayloadBytes);
    }
    private sealed class Model(bool followup) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Line { get; private set; }
        public int SecondPayloadBytes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(cancellationToken); var payload = JsonNode.Parse(text)!;
            var data = new JsonArray(payload["messages"]!.AsArray().Where(item => item?["role"]?.ToString() != "system").Select(item => item!.DeepClone()).ToArray()).ToJsonString();
            Check(!text.Contains(PrivateCard, StringComparison.Ordinal) && !data.Contains(PrivateGift, StringComparison.Ordinal) && !text.Contains(Key, StringComparison.Ordinal),
                "Actual HTTP leaked a hidden opposing card or attached bonus.");
            var system = payload["messages"]!.AsArray().First(message => message?["role"]?.ToString() == "system")!["content"]!.GetValue<string>();
            Check(system.Contains("SourceEntityIds", StringComparison.Ordinal) && system.Contains("SourceOrder", StringComparison.Ordinal) && system.Contains("FirstCombatIndex", StringComparison.Ordinal) &&
                system.Contains("见证", StringComparison.Ordinal) && system.Contains("不是触发顺序", StringComparison.Ordinal) && system.Contains("不继续跨回合动作", StringComparison.Ordinal),
                "Actual task prompt lost joint source-set witness and authority semantics.");
            Check(payload["tools"]!.AsArray().Any(tool => tool?["function"]?["name"]?.ToString() == "evaluate_line" &&
                tool["function"]?["description"]?.ToString().Contains("联合相关分支", StringComparison.Ordinal) == true), "Actual tool definition omitted the correlated multi-source contract.");
            object message;
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[] { new { id = "multi-terror-line", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions = Actions(followup) }) } } } };
            else
            {
                SecondPayloadBytes = Encoding.UTF8.GetByteCount(text);
                var result = payload["messages"]!.AsArray().Single(item => item?["tool_call_id"]?.ToString() == "multi-terror-line")!;
                Line = JsonNode.Parse(result["content"]!.GetValue<string>())!;
                Check(Preview(Line)["SourceEntityIds"]!.AsArray().Count >= 2 && Preview(Line)["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
                    "The next real model round did not receive one joint conditional preview.");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "选择最有利的恐魔结算顺序即可斩杀。" + Key,
                    reasons = new[] { "故意把条件分支当实际局面，交真实主机拒绝。" }, alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(),
                    unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions = Actions(followup), verified_lethal = true, verification = "verified" }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task<JsonNode>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
