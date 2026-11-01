using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Rules;
using HsHelper.Storage;

internal static class RepeatBattlecryIntegration
{
    private const string Build = "255790", Template = "hunter-v25/turn", Key = "repeat-battlecry-fake-key";
    private const string Gift = "EDR_100t7e", Private = "PRIVATE_REPEAT_BATTLECRY_PROBE";
    private const string Complete = "PowerProcessor.EndCurrentTaskList() - complete";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "repeat-battlecry", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var records = await RecordsAsync(); var catalog = await CatalogAsync(directory, records.Values);
        await RunChecksAsync(directory, records, catalog, pass);
    }

    private sealed record Spec
    {
        public string Source { get; init; } = "EDR_852";
        public string? GiftId { get; init; } = Gift;
        public string First { get; init; } = "0";
        public string Donor { get; init; } = "121051";
        public int Mana { get; init; } = 5;
        public int SourceCost { get; init; } = 2;
        public int SourceAttack { get; init; } = 2;
        public int SourceHealth { get; init; } = 3;
        public int EnemyHealth { get; init; } = 5;
        public int BoarCost { get; init; } = 5;
        public int BoarAttack { get; init; } = 1;
        public int BoarHealth { get; init; } = 1;
        public bool Assembled { get; init; }
        public bool Pair { get; init; }
        public bool PlayOnly { get; init; }
        public bool IncludeBoar { get; init; } = true;
        public bool PrivateSource { get; init; }
        public bool Raptor { get; init; }
        public bool Goldrinn { get; init; }
        public bool Quest { get; init; }
        public bool Secret { get; init; }
        public bool Duplicate { get; init; }
        public bool Mixed { get; init; }
        public bool EnemyShield { get; init; }
        public bool AllowIncomplete { get; init; }
        public bool OmitBattlecry { get; init; }
        public string? Target { get; init; }
        public int TargetAttack { get; init; } = 1;
        public int TargetHealth { get; init; } = 1;
        public bool UnknownDeath { get; init; }
        public string[] Changes { get; init; } = [];
        public JsonArray? Actions { get; init; }
    }
    private sealed record Saved(string Directory, string Database, GameSnapshot Frame, DecisionPoint Point, string Original);
    private sealed record Finished(JsonNode Line, JsonNode Snapshot, JsonNode Effect, JsonNode? Search, StrategyAdvice? Advice,
        ArchivedAdviceRecord? Archive, IReadOnlyList<CoachToolObservation> Tools, JsonNode? Direct);

    private static async Task<Dictionary<string, JsonElement>> RecordsAsync()
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        if (workspace is null) throw new DirectoryNotFoundException("The fixed public test fixtures must belong to this workspace.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in new[] { "battlecry-repeat", "assembled-scripts", "native-definitions", "lethal-search", "dark-gift-static", "terror-end-turn", "quest-progress" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build && fixture.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The real repeat pipeline must import complete fixed primary definitions, not fabricated cards.");
            foreach (var record in fixture.RootElement.GetProperty("cards").EnumerateArray()) result[record.GetProperty("id").GetString()!] = record.Clone();
        }
        return result;
    }
    private static async Task<CardCatalog> CatalogAsync(string root, IEnumerable<JsonElement> records)
    {
        var catalog = new CardCatalog(Path.Combine(root, "cards-" + Guid.NewGuid().ToString("N") + ".db")); await catalog.InitializeAsync();
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(records));
        await catalog.ImportAsync(stream, Build, source: "fixture://fixed-observed-double-battlecry"); return catalog;
    }
    private sealed class Replay(DeckTrackingEnricher enricher, SessionStore store, string directory, string database)
    {
        private readonly PowerLogStateEngine _engine = new(1, "repeat-battlecry-" + Guid.NewGuid().ToString("N"));
        private SessionRecorder? _recorder; private long _line;
        public GameSnapshot Frame { get; private set; } = null!;
        public static async Task<Replay> CreateAsync(string root, CardCatalog catalog, Spec spec)
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "sessions.db"); var store = new SessionStore(database); await store.InitializeAsync();
            var replay = new Replay(await DeckTrackingEnricher.CreateAsync(catalog, null, Build), store, directory, database);
            await replay.FeedAsync("GameState.DebugPrintGame() - BuildNumber=" + Build + " FormatType=FT_STANDARD GameType=GT_RANKED",
                "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
                "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=HERO_POWER_ENTITY value=12",
                "tag=RESOURCES value=" + spec.Mana, "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=3527 value=2",
                "tag=MAXHANDSIZE value=10", "tag=NUM_CARDS_IN_DECK value=0", "tag=FATIGUE value=2",
                "tag=NUM_CARDS_IN_HAND value=" + ((spec.PrivateSource ? 0 : 1) + (spec.IncludeBoar ? 1 : 0) + (spec.Pair ? 1 : 0)),
                "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
            await replay.CharacterAsync(11, "HERO_05", 1, "PLAY", "HERO", 0, 30, 0); await replay.FeedAsync("tag=CLASS value=HUNTER");
            await replay.CharacterAsync(21, "HERO_08", 2, "PLAY", "HERO", 0, spec.EnemyHealth, 0);
            await replay.FeedAsync("tag=CLASS value=MAGE", "tag=DIVINE_SHIELD value=" + (spec.EnemyShield ? 1 : 0));
            await replay.FeedAsync("FULL_ENTITY - Creating ID=12 CardID=EDR_850p", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER",
                "tag=COST value=2", "tag=EXHAUSTED value=0", "tag=TAG_SCRIPT_DATA_NUM_1 value=2", "tag=TAG_SCRIPT_DATA_NUM_2 value=2");
            await replay.CharacterAsync(30, spec.PrivateSource ? Private : spec.Source, spec.PrivateSource ? 2 : 1, "HAND", "MINION",
                spec.SourceAttack, spec.SourceHealth, spec.SourceCost);
            if (!spec.OmitBattlecry) await replay.FeedAsync("tag=BATTLECRY value=1");
            if (spec.Source == "EDR_800") await replay.FeedAsync("tag=TAUNT value=1", "tag=DIVINE_SHIELD value=1");
            if (spec.Source is "FIR_953" or "EDR_227" or "DINO_421") await replay.FeedAsync("tag=CARDRACE value=BEAST");
            if (spec.Assembled) await replay.FeedAsync("tag=4572 value=" + spec.First, "tag=4573 value=" + spec.Donor);
            if (spec.IncludeBoar)
            {
                await replay.CharacterAsync(31, "CS2_171", 1, "HAND", "MINION", spec.BoarAttack, spec.BoarHealth, spec.BoarCost);
                await replay.FeedAsync("tag=CHARGE value=1", "tag=CARDRACE value=BEAST");
            }
            if (spec.Pair)
            {
                await replay.CharacterAsync(32, spec.Source, 1, "HAND", "MINION", spec.SourceAttack, spec.SourceHealth, spec.SourceCost);
                await replay.FeedAsync("tag=BATTLECRY value=1", "tag=4572 value=0", "tag=4573 value=66858");
                await replay.EnchantmentAsync(52, Gift, 1, 32);
            }
            if (spec.GiftId is not null) await replay.EnchantmentAsync(50, spec.GiftId, spec.PrivateSource ? 2 : 1, 30);
            if (spec.Duplicate) await replay.EnchantmentAsync(51, Gift, 1, 30);
            if (spec.Mixed) await replay.EnchantmentAsync(53, "EDR_100t5e", 1, 30);
            if (spec.Target is not null)
            {
                await replay.CharacterAsync(40, spec.Target, 2, "PLAY", "MINION", spec.TargetAttack, spec.TargetHealth, 0);
                if (spec.UnknownDeath) await replay.FeedAsync("tag=DEATHRATTLE value=1");
            }
            if (spec.Raptor) { await replay.CharacterAsync(60, "EDR_849", 1, "PLAY", "MINION", 1, 5, 0); await replay.FeedAsync("tag=CARDRACE value=BEAST", "tag=TRIGGER_VISUAL value=1"); }
            if (spec.Goldrinn) { await replay.CharacterAsync(62, "EDR_480", 1, "PLAY", "MINION", 3, 8, 0); await replay.FeedAsync("tag=CARDRACE value=BEAST", "tag=RUSH value=1", "tag=AURA value=1"); }
            if (spec.Quest) await replay.FeedAsync("FULL_ENTITY - Creating ID=70 CardID=TLC_EVENT_400", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL",
                "tag=SIDE_QUEST value=1", "tag=QUEST_PROGRESS value=0", "tag=QUEST_PROGRESS_TOTAL value=3");
            if (spec.Secret) await replay.FeedAsync("FULL_ENTITY - Creating ID=71 CardID=" + Private, "tag=CONTROLLER value=2", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1");
            await replay.FeedAsync("FULL_ENTITY - Creating ID=90 CardID=" + Private, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION",
                "FULL_ENTITY - Creating ID=91 CardID=EDR_100t13e", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=90");
            await replay.FeedAsync(spec.Changes); await replay.FeedAsync("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", Complete);
            Check((spec.AllowIncomplete || replay.Frame.Quality.IsComplete && replay.Frame.Quality.IsStable) && !SnapshotCodec.Serialize(replay.Frame).Contains(Private, StringComparison.Ordinal),
                "The real parser fixture lost stable perspective or privacy.");
            replay._recorder = new(store, new(null, Build, 1)); await replay.RecordAsync(); return replay;
        }
        private Task CharacterAsync(int id, string card, int owner, string zone, string type, int attack, int health, int cost) => FeedAsync(
            "FULL_ENTITY - Creating ID=" + id + " CardID=" + card, "tag=CONTROLLER value=" + owner, "tag=ZONE value=" + zone, "tag=CARDTYPE value=" + type,
            "tag=ATK value=" + attack, "tag=HEALTH value=" + health, "tag=DAMAGE value=0", "tag=COST value=" + cost,
            "tag=ARMOR value=0", "tag=SILENCED value=0", "tag=DORMANT value=0", "tag=IMMUNE value=0", "tag=UNTOUCHABLE value=0", "tag=FROZEN value=0",
            "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=DIVINE_SHIELD value=0", "tag=REBORN value=0", "tag=LIFESTEAL value=0", "tag=DEATHRATTLE value=0");
        private Task EnchantmentAsync(int id, string card, int owner, int target) => FeedAsync("FULL_ENTITY - Creating ID=" + id + " CardID=" + card,
            "tag=CONTROLLER value=" + owner, "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=" + target);
        public async Task FeedAsync(params string[] lines)
        { foreach (var line in lines) { _line++; _engine.ProcessLine(line); Frame = enricher.Observe(_engine.Snapshot()); if (_recorder is not null) await RecordAsync(); } }
        private Task RecordAsync() { _recorder!.Observe(Frame, _line); return _recorder.FlushAsync(); }
        public async Task<Saved> SaveAsync()
        {
            await _recorder!.CompleteAsync(); var point = (await store.ListDecisionPointsAsync(Frame.MatchId, true)).Single(point => point.StateVersion == Frame.StateVersion && point.EventSequence == Frame.EventSequence);
            var exact = await new SessionStore(database).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact repeat frame.");
            Check(SnapshotCodec.Serialize(exact.Context.Snapshot) == SnapshotCodec.Serialize(Frame) && exact.Context.CardBuild == Build && !exact.HasUnattributedEvents,
                "Reopened exact source frame changed or borrowed later events.");
            return new(directory, database, exact.Context.Snapshot, point, SnapshotCodec.Serialize(Frame));
        }
    }

    private static Spec Assembled() => new() { Source = "EDR_102", SourceCost = 4, SourceAttack = 5, SourceHealth = 4,
        BoarCost = 1, EnemyHealth = 3, Assembled = true };
    private static Spec Archer(string id = "CS2_189") => new() { Source = id, SourceCost = 1, SourceAttack = 1, SourceHealth = 1,
        Mana = 1, IncludeBoar = false, EnemyHealth = 2, PlayOnly = true };
    private static string? DirectTool(Spec spec) => spec.Assembled ? null : spec.Source switch
    { "FIR_921" or "EDR_226" => "evaluate_draw", "CORE_UNG_912" => "evaluate_generation", "BE_044" => "evaluate_play_triggers", _ => null };
    private static JsonArray Executions(JsonNode line) => line["Result"]?["BattlecryExecutions"]?.AsArray() ?? new();
    private static JsonNode Execution(JsonNode line, int id = 30) => Executions(line).SingleOrDefault(value => value!["SourceEntityId"]?.GetValue<int>() == id)
        ?? throw new InvalidOperationException("The expected atomic Battlecry execution was absent: " + line.ToJsonString());
    private static void AssertExecution(Finished value, Saved saved, int planned = 2, int source = 30, int enchantment = 50)
    {
        var execution = Execution(value.Line, source);
        Check(execution["MatchId"]?.ToString() == saved.Frame.MatchId && execution["StateVersion"]?.GetValue<long>() == saved.Frame.StateVersion &&
            execution["EventSequence"]?.GetValue<long>() == saved.Frame.EventSequence && execution["CardBuild"]?.ToString() == Build &&
            execution["SourceCardId"]?.ToString() == saved.Frame.Entities[source].CardId && execution["ActionOrdinal"]?.GetValue<int>() >= 1 &&
            execution["IsMultiplicityResolved"]?.GetValue<bool>() == true && execution["IsEnabled"]?.GetValue<bool>() == (planned > 0) &&
            execution["PlannedActivations"]?.GetValue<int>() == planned && execution["StartedActivations"]?.GetValue<int>() == planned &&
            execution["CompletedActivations"]?.GetValue<int>() == planned && execution["RemainingActivations"]?.GetValue<int>() == 0 &&
            execution["IsComplete"]?.GetValue<bool>() == true && (planned == 0 || execution["EnchantmentEntityIds"]!.AsArray().Any(id => id!.GetValue<int>() == enchantment)) &&
            execution["Activations"]!.AsArray().Count == planned && execution["Activations"]!.AsArray().Select(step => step!["ActivationOrdinal"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(1, planned)),
            "The typed atomic repeat lost its frame binding or counted the card as multiple plays: " + execution.ToJsonString());
    }

    private static async Task RunChecksAsync(string directory, Dictionary<string, JsonElement> records, CardCatalog catalog, Action<string> pass)
    {
        foreach (var id in new[] { "EDR_852", "EDR_800" })
        {
            var spec = new Spec { Source = id }; var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync();
            var result = await PipelineAsync(item, catalog, spec, search: true); AssertVerified(result, true); AssertExecution(result, item);
            Check(result.Search?["VerifiedLethalFound"]?.GetValue<bool>() == true && result.Search["ExploredNodes"]?.GetValue<int>() > 0 &&
                result.Line["AppliedActions"]?.GetValue<int>() == 4 && result.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 0 &&
                result.Line["Result"]?["ImbueCountBefore"]?.GetValue<int>() == 2 && result.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 4 &&
                result.Line["Result"]?["HeroPowerChanges"]!.AsArray().Count == 2 && Entity(result.Line, 12)["Flags"]?["TAG_SCRIPT_DATA_NUM_1"]?.GetValue<int>() == 4 &&
                Entity(result.Line, 31)["Attack"]?.GetValue<int>() == 5 && Entity(result.Line, 31)["Cost"]?.GetValue<int>() == 1 &&
                Entity(result.Line, 30)["Zone"]?.ToString() == "PLAY" && Entity(result.Line, 31)["AttacksUsed"]?.GetValue<int>() == 1,
                "The actual double-Imbue chain paid or summoned twice, skipped either Imbue, or could not complete the five-mana five-damage search.");
        }
        pass("both actual parsed native Imbue Battlecries repeat twice and real search final host archive and paid traces close the five-mana Wolf-four charge lethal");

        foreach (var donor in new[] { "131236", "40567", "102196", "66858", "121051" })
        {
            var spec = Assembled() with { Donor = donor }; var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync();
            var result = await PipelineAsync(item, catalog, spec, search: donor == "121051"); AssertVerified(result, true); AssertExecution(result, item);
            var effect = result.Line["Result"]!["AssembledEffects"]!.AsArray().Single()!; var target = effect["Targets"]!.AsArray().Single()!;
            Check(effect["ExecutedCount"]?.GetValue<int>() == 2 && target["EntityId"]?.GetValue<int>() == 31 && target["AttackBefore"]?.GetValue<int>() == 1 &&
                target["AttackAfter"]?.GetValue<int>() == 3 && target["MaximumHealthAfter"]?.GetValue<int>() == 3 && Entity(result.Line, 31)["Cost"]?.GetValue<int>() == 1 &&
                Entity(result.Line, 30)["Attack"]?.GetValue<int>() == 5 && result.Line["Result"]?["DiscoveryBoundaryReached"]?.GetValue<bool>() == false,
                "The real double assembled Battlecry added bonuses twice per activation, buffed its own departed body, or resurrected native Discover.");
        }
        var pairSpec = Assembled() with { Pair = true, Mana = 9, EnemyHealth = 5 };
        var pair = await (await Replay.CreateAsync(directory, catalog, pairSpec)).SaveAsync(); var paired = await PipelineAsync(pair, catalog, pairSpec);
        AssertVerified(paired, true); AssertExecution(paired, pair); AssertExecution(paired, pair, source: 32, enchantment: 52);
        Check(Executions(paired.Line).Count(value => value!["PlannedActivations"]?.GetValue<int>() == 2) == 2 && Entity(paired.Line, 31)["Attack"]?.GetValue<int>() == 5 &&
            Entity(paired.Line, 30)["Attack"]?.GetValue<int>() == 5 && Entity(paired.Line, 32)["Attack"]?.GetValue<int>() == 7 &&
            paired.Line["Result"]!["AssembledEffects"]!.AsArray().Select(effect => effect!["SecondHalfDbfId"]!.GetValue<int>()).SequenceEqual([121051, 66858]),
            "Same-CardId bodies shared their repeat source, effective donor, or current hand target state.");
        pass("all five exact assembled handbuff donors repeat their atomic buff twice while two same-base entities retain separate gifts recipes and sequential current targets");

        foreach (var id in new[] { "CS2_189", "CORE_CS2_189" })
        {
            var spec = Archer(id); var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var result = await PipelineAsync(item, catalog, spec);
            AssertVerified(result, true); AssertExecution(result, item);
        }
        var shieldSpec = Archer() with { EnemyShield = true }; var shield = await (await Replay.CreateAsync(directory, catalog, shieldSpec)).SaveAsync();
        var shielded = await PipelineAsync(shield, catalog, shieldSpec); AssertVerified(shielded, false); AssertExecution(shielded, shield);
        Check(Entity(shielded.Line, 21)["RemainingHealth"]?.GetValue<int>() == 1 && Entity(shielded.Line, 21)["Flags"]?["DIVINE_SHIELD"]?.GetValue<int>() == 0,
            "The two Archer Battlecries became a single two-damage packet or regenerated a spent shield.");
        foreach (var shieldBlocksFirst in new[] { false, true })
        {
            var lifeSpec = Archer() with { EnemyHealth = 3, EnemyShield = shieldBlocksFirst,
                Changes = ["TAG_CHANGE Entity=30 tag=LIFESTEAL value=1", "TAG_CHANGE Entity=11 tag=DAMAGE value=10"] };
            var lifeItem = await (await Replay.CreateAsync(directory, catalog, lifeSpec)).SaveAsync(); var life = await PipelineAsync(lifeItem, catalog, lifeSpec);
            AssertVerified(life, false); AssertExecution(life, lifeItem);
            Check(Entity(life.Line, 11)["RemainingHealth"]?.GetValue<int>() == (shieldBlocksFirst ? 21 : 22),
                "Observed Lifesteal was limited to combat or healed for a shield-blocked Battlecry packet.");
        }
        var poisonSpec = Archer() with { Target = "CORE_CS2_231", TargetHealth = 8, EnemyHealth = 20, Changes = ["TAG_CHANGE Entity=30 tag=POISONOUS value=1"] };
        var poisonItem = await (await Replay.CreateAsync(directory, catalog, poisonSpec)).SaveAsync(); var poison = await PipelineAsync(poisonItem, catalog, poisonSpec);
        AssertVerified(poison, false); AssertExecution(poison, poisonItem);
        Check(!HasProjectedEntity(poison.Line, 40) && Execution(poison.Line)["Activations"]!.AsArray()[0]!["AffectedEntityIds"]!.AsArray().Single()!.GetValue<int>() == 40 &&
            Execution(poison.Line)["Activations"]!.AsArray()[1]!["Status"]?.ToString() == "Fizzled",
            "A real noncombat poisonous packet did not destroy its damaged minion or the second packet retargeted.");
        foreach (var observedRace in new[] { "20", "BEAST" })
        {
            var goldSpec = Archer() with { EnemyHealth = 4, Goldrinn = true, Changes = ["TAG_CHANGE Entity=30 tag=CARDRACE value=" + observedRace] };
            var goldItem = await (await Replay.CreateAsync(directory, catalog, goldSpec)).SaveAsync(); var gold = await PipelineAsync(goldItem, catalog, goldSpec);
            AssertVerified(gold, true); AssertExecution(gold, goldItem);
            Check(!HasProjectedEntity(gold.Line, 21) && Entity(gold.Line, 30)["Flags"]?["CARDRACE"]?.GetValue<int>() == 20 &&
                gold.Line["RuleCoverage"]!.AsArray().Any(coverage => coverage?.ToString() == "goldrinn-friendly-beast-double-damage"),
                "Current Beast Battlecry damage ignored an audited friendly Goldrinn or treated doubling as extra activations: " + observedRace);
        }
        var killSpec = Archer() with { Target = "CORE_CS2_231", EnemyHealth = 20 };
        var killed = await (await Replay.CreateAsync(directory, catalog, killSpec)).SaveAsync(); var fizzled = await PipelineAsync(killed, catalog, killSpec);
        AssertVerified(fizzled, false); AssertExecution(fizzled, killed);
        Check(!HasProjectedEntity(fizzled.Line, 40) && Execution(fizzled.Line)["Activations"]!.AsArray()[1]!["Status"]?.ToString() == "Fizzled" &&
            Entity(fizzled.Line, 21)["RemainingHealth"]?.GetValue<int>() == 20, "A dead selected target was replaced by a second target rather than fizzled.");
        var unknownSpec = killSpec with { UnknownDeath = true }; var unknownItem = await (await Replay.CreateAsync(directory, catalog, unknownSpec)).SaveAsync();
        var unknown = await PipelineAsync(unknownItem, catalog, unknownSpec); AssertUnverified(unknown);
        Check(Execution(unknown.Line)["StartedActivations"]?.GetValue<int>() == 1 && Execution(unknown.Line)["IsComplete"]?.GetValue<bool>() == false,
            "Unknown death effects were crossed to execute the second Archer activation.");
        var gameEndSpec = Archer() with { EnemyHealth = 1 }; var gameEndItem = await (await Replay.CreateAsync(directory, catalog, gameEndSpec)).SaveAsync();
        var gameEnd = await PipelineAsync(gameEndItem, catalog, gameEndSpec); AssertVerified(gameEnd, true);
        Check(Execution(gameEnd.Line)["StartedActivations"]?.GetValue<int>() == 1 && Execution(gameEnd.Line)["Activations"]!.AsArray().Count == 1,
            "A second activation was invented after the first killed the enemy hero.");
        pass("Archer aliases use separate same-target damage packets with shield consumption ordinary-death fizzle unknown-death stop and game-end stop through the actual host");

        foreach (var original in new[] { new Spec(), Assembled(), Archer() })
        foreach (var change in new[] { "TAG_CHANGE Entity=30 tag=BATTLECRY value=0", "TAG_CHANGE Entity=30 tag=SILENCED value=1" })
        {
            var spec = original with { PlayOnly = true, Changes = [change], Actions = original.Source is "CS2_189" or "CORE_CS2_189" ? Actions(("play", 30, null)) : null };
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync();
            var result = await PipelineAsync(item, catalog, spec); AssertVerified(result, false); AssertExecution(result, item, planned: 0);
            Check(result.Line["AppliedActions"]?.GetValue<int>() == 1 && result.Line["Result"]?["AvailableMana"]?.GetValue<int>() == spec.Mana - spec.SourceCost &&
                (!spec.IncludeBoar || Entity(result.Line, 31)["Attack"]?.GetValue<int>() == spec.BoarAttack), "Suppression was restored by Summon or reapplied a bonus anyway.");
        }
        var inferredNative = new Spec { OmitBattlecry = true }; var inferredItem = await (await Replay.CreateAsync(directory, catalog, inferredNative)).SaveAsync();
        var inferred = await PipelineAsync(inferredItem, catalog, inferredNative); AssertVerified(inferred, true); AssertExecution(inferred, inferredItem);
        pass("explicit Battlecry zero or silence yields zero executions before Summon while normal fixed native Battlecry needs no fabricated once-count marker");

        var questSpec = Assembled() with { Source = "FIR_953", First = "113822", PlayOnly = true, Quest = true };
        var questItem = await (await Replay.CreateAsync(directory, catalog, questSpec)).SaveAsync(); var quest = await PipelineAsync(questItem, catalog, questSpec);
        AssertVerified(quest, false); AssertExecution(quest, questItem);
        Check(quest.Line["Result"]!["QuestProgressPreviews"]!.AsArray().Single()!["Increment"]?.GetValue<int>() == 1 &&
            Entity(quest.Line, 70)["Flags"]?["QUEST_PROGRESS"]?.GetValue<int>() == 1 && quest.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 1,
            "Two Battlecries paid played or incremented the quest twice.");
        var raptorSpec = new Spec { PlayOnly = true, Raptor = true }; var raptorItem = await (await Replay.CreateAsync(directory, catalog, raptorSpec)).SaveAsync();
        var raptor = await PipelineAsync(raptorItem, catalog, raptorSpec); AssertUnverified(raptor); AssertExecution(raptor, raptorItem);
        var trigger = raptor.Line["Result"]!["RaptorPreviews"]!.AsArray().Single()!["TriggerCount"]!;
        Check(trigger["Minimum"]?.GetValue<int>() == 1 && trigger["Maximum"]?.GetValue<int>() == 1 && raptor.Line["Result"]?["RaptorBoundaryReached"]?.GetValue<bool>() == true,
            "The after-play random effect was duplicated or crossed because the Battlecry repeated.");
        foreach (var spec in new[]
        {
            new Spec { Source = "CORE_UNG_912", SourceCost = 1, IncludeBoar = false, PlayOnly = true },
            new Spec { Source = "FIR_921", SourceCost = 3, IncludeBoar = false, PlayOnly = true },
            new Spec { Source = "BE_044", SourceCost = 4, SourceAttack = 5, SourceHealth = 4, IncludeBoar = false, PlayOnly = true }
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var result = await PipelineAsync(item, catalog, spec); AssertUnverified(result);
            var execution = Execution(result.Line);
            Check(execution["PlannedActivations"]?.GetValue<int>() == 2 && execution["StartedActivations"]?.GetValue<int>() == 1 &&
                execution["IsComplete"]?.GetValue<bool>() == false && execution["Activations"]!.AsArray().Count == 1 && result.Line["AppliedActions"]?.GetValue<int>() == 1,
                "An unobserved generated card draw or choice was crossed to claim both Battlecries completed.");
            var direct = spec.Source == "BE_044" ? result.Direct?["Discovery"] : result.Direct;
            Check(direct?["BattlecryMultiplicity"]?["Activations"]?.GetValue<int>() == 2 && direct["IsSingleBattlecryActivationPreview"]?.GetValue<bool>() == true,
                "The actual direct tool hid its known repeated schedule or misrepresented a first-activation preview as the whole Battlecry.");
        }
        var zeroDrawSpec = new Spec { Source = "FIR_921", SourceCost = 3, IncludeBoar = false, PlayOnly = true, Changes = ["TAG_CHANGE Entity=2 tag=3527 value=1"] };
        var zeroDrawItem = await (await Replay.CreateAsync(directory, catalog, zeroDrawSpec)).SaveAsync(); var zeroDraw = await PipelineAsync(zeroDrawItem, catalog, zeroDrawSpec);
        AssertVerified(zeroDraw, false); AssertExecution(zeroDraw, zeroDrawItem);
        Check(zeroDraw.Line["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == false && zeroDraw.Line["Result"]!["DrawPreviews"]!.AsArray().Count == 2 &&
            zeroDraw.Line["Result"]!["DrawPreviews"]!.AsArray().All(preview => preview!["RequestedDraws"]?["Maximum"]?.GetValue<long>() == 0),
            "Known unmet FIR921 conditions acquired cards or could not finish two zero-draw activations.");
        pass("payment summon and quest or Raptor after-play remain one event and draw random generation or Discover stop the repeated script at its first observation boundary");

        foreach (var variant in new[] { "gift-dbf", "gift-text", "gift-mechanics", "body-dbf", "body-text", "missing-gift" })
        {
            var altered = records.ToDictionary(pair => pair.Key, pair => JsonNode.Parse(pair.Value.GetRawText())!.AsObject());
            if (variant == "gift-dbf") altered[Gift]["dbfId"] = 113884;
            if (variant == "gift-text") altered[Gift]["text"] = "本随从的战吼触发三次。";
            if (variant == "gift-mechanics") altered[Gift]["mechanics"] = new JsonArray("DEATHRATTLE");
            if (variant == "body-dbf") altered["EDR_852"]["dbfId"] = 114936;
            if (variant == "body-text") altered["EDR_852"]["text"] = "<b>战吼：</b>造成10点伤害。";
            if (variant == "missing-gift") altered.Remove(Gift);
            var mutated = await CatalogAsync(directory, altered.Values.Select(value => JsonSerializer.SerializeToElement(value)));
            var item = await (await Replay.CreateAsync(directory, mutated, new())).SaveAsync(); AssertUnverified(await PipelineAsync(item, mutated, new()));
        }
        foreach (var spec in new[]
        {
            new Spec { GiftId = "EDR_100t7e1" }, new Spec { Duplicate = true }, new Spec { Mixed = true }, new Spec { Secret = true },
            Assembled() with { Assembled = false, PlayOnly = true },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=EXTRA_BATTLECRIES_BASE value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=1112 value=1"] }, new Spec { Changes = ["TAG_CHANGE Entity=2 tag=1126 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=EXTRA_BATTLECRIES_BASE value=0", "TAG_CHANGE Entity=2 tag=411 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=1126 value=unknown"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=188 value=2"] }, new Spec { Changes = ["TAG_CHANGE Entity=30 tag=218 value=0"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=48 value=unknown"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=45 value=unknown"] },
            Assembled() with { Changes = ["TAG_CHANGE Entity=31 tag=47 value=unknown"] },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=40 value=999"], AllowIncomplete = true },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=202 value=SPELL"] },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=CONTROLLER value=2"] },
            Archer() with { EnemyHealth = 3, Changes = ["TAG_CHANGE Entity=30 tag=LIFESTEAL value=2", "TAG_CHANGE Entity=11 tag=DAMAGE value=10"] },
            Archer() with { Target = "CORE_CS2_231", TargetHealth = 8, EnemyHealth = 20, Changes = ["TAG_CHANGE Entity=30 tag=POISONOUS value=2"] }
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); AssertUnverified(await PipelineAsync(item, catalog, spec));
        }
        pass("bad whole source metadata aliases costs attachment ownership duplicates mixed gifts t7e1 and unknown global counts never become a guessed repeat formula or executable final lethal");

        var historySpec = new Spec { GiftId = null }; var replay = await Replay.CreateAsync(directory, catalog, historySpec); var early = await replay.SaveAsync();
        await replay.FeedAsync("FULL_ENTITY - Creating ID=50 CardID=" + Gift, "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30", Complete);
        var late = await replay.SaveAsync(); var old = await new SessionStore(early.Database).GetHistoricalDecisionAsync(early.Point.Id);
        Check(old is not null && SnapshotCodec.Serialize(old.Context.Snapshot) == early.Original && !old.Context.Snapshot.Entities.ContainsKey(50) && late.Frame.Entities[50].CardId == Gift,
            "An older exact point borrowed the later attached repeat source.");
        var once = await PipelineAsync(early, catalog, historySpec); AssertUnverified(once);
        Check(Execution(once.Line)["PlannedActivations"]?.GetValue<int>() == 1 && Entity(once.Line, 12)["Flags"]?["TAG_SCRIPT_DATA_NUM_1"]?.GetValue<int>() == 3,
            "The old ordinary Battlecry saw a future gift rather than its original one Imbue.");
        var repeated = await PipelineAsync(late, catalog, new()); AssertVerified(repeated, true); AssertExecution(repeated, late);
        var privateSpec = new Spec { PrivateSource = true }; var hidden = await (await Replay.CreateAsync(directory, catalog, privateSpec)).SaveAsync();
        var privateResult = await PipelineAsync(hidden, catalog, privateSpec); AssertUnverified(privateResult);
        Check(hidden.Frame.Entities[30].CardId is null && !hidden.Frame.Entities.ContainsKey(50) && privateResult.Effect["error"] is not null &&
            Executions(privateResult.Line).Count == 0 && !privateResult.Snapshot.ToJsonString().Contains(Private, StringComparison.Ordinal),
            "A private opponent body or its attached repeat mechanism acquired current entity effects or an executable repetition.");
        var wrongBuildRejected = false;
        try { _ = new SnapshotCoach(late.Frame, CompactAdviceContextBuilder.Build(late.Frame, "255791"), catalog, "255791"); }
        catch (ArgumentException) { wrongBuildRejected = true; }
        Check(wrongBuildRejected, "A different build accepted the current repeat evidence.");
        pass("exact earlier SQLite points keep one activation later points gain only their actual gift and wrong-build or private identities stay outside tools HTTP and archives");

        foreach (var id in new[] { "EDR_227", "DINO_421" })
        {
            var spec = new Spec { Source = id, SourceAttack = id == "DINO_421" ? 8 : 5, SourceHealth = id == "DINO_421" ? 8 : 2,
                SourceCost = 4, BoarCost = 1, EnemyHealth = 20, Target = "CORE_CS2_231", TargetAttack = id == "DINO_421" ? 1 : 3, TargetHealth = 10,
                Changes = ["TAG_CHANGE Entity=30 tag=ZONE value=PLAY", "TAG_CHANGE Entity=30 tag=BATTLECRY value=0", "TAG_CHANGE Entity=30 tag=DEATHRATTLE value=1",
                    "TAG_CHANGE Entity=30 tag=DAMAGE value=" + (id == "DINO_421" ? 7 : 0), "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=1"], Actions = Actions(("attack", 30, 40)) };
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var result = await PipelineAsync(item, catalog, spec);
            Check(result.Line["IsLethal"]?.GetValue<bool>() == false && Executions(result.Line).Count == 0 && !HasProjectedEntity(result.Line, 30) &&
                (id == "EDR_227" ? result.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 3 : Entity(result.Line, 31)["Attack"]?.GetValue<int>() == 4),
                "A double-Battlecry gift became a duplicate deathrattle or introduced a symbolic hidden-deck assumption.");
        }
        pass("the actual death path never promotes a Battlecry-only gift into double native Imbue deathrattles or a second DINO stat buff");
    }

    private static JsonArray Plan(Spec spec)
    {
        if (spec.Actions is not null) return (JsonArray)spec.Actions.DeepClone();
        var actions = Actions(("play", 30, spec.Source is "CS2_189" or "CORE_CS2_189" ? spec.Target is null ? 21 : 40 : null));
        if (spec.PlayOnly) return actions;
        if (spec.Pair) actions.Add(Action("play", 32));
        if (!spec.Assembled) actions.Add(Action("hero_power", 12));
        actions.Add(Action("play", 31)); actions.Add(Action("attack", 31, 21)); return actions;
    }
    private static JsonObject Action(string kind, int source, int? target = null) => new() { ["kind"] = kind, ["source_entity_id"] = source, ["target_entity_id"] = target };
    private static JsonArray Actions(params (string Kind, int Source, int? Target)[] actions) => new(actions.Select(action => (JsonNode)Action(action.Kind, action.Source, action.Target)).ToArray());
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    // The real coach deliberately omits removed characters from its public result.
    private static bool HasProjectedEntity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Any(entity => entity!["EntityId"]?.GetValue<int>() == id);
    private static void AssertVerified(Finished value, bool lethal) => Check(value.Line["Verification"]?.ToString() == "Verified" && value.Line["IsLethal"]?.GetValue<bool>() == lethal &&
        value.Advice is { Verification: "verified" } && value.Advice.VerifiedLethal == lethal && value.Advice.Actions.Count > 0 &&
        value.Archive!.Advice.Verification == "verified" && value.Archive.Advice.VerifiedLethal == lethal,
        "The real final host did not independently verify the repeated known script: " + value.Line.ToJsonString());
    private static void AssertUnverified(Finished value) => Check(value.Line["Verification"]?.ToString() != "Verified" && value.Line["IsLethal"]?.GetValue<bool>() == false &&
        value.Search?["VerifiedLethalFound"]?.GetValue<bool>() != true && (value.Advice is null || !value.Advice.VerifiedLethal && value.Advice.Actions.Count == 0) &&
        (value.Archive is null || !value.Archive.Advice.VerifiedLethal && value.Archive.Advice.Actions.Count == 0), "An unknown repetition became executable verified advice.");

    private static async Task<Finished> PipelineAsync(Saved saved, CardCatalog catalog, Spec spec, bool search = false)
    {
        var visible = await catalog.GetByIdsBatchedAsync(saved.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(saved.Frame, Build, AdviceTaskKind.Turn, cards: visible)
            with { KnowledgeAsOf = saved.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        Check(context.TemplateVersion == Template && context.VisibilityPolicy == "attachments-v1", "The real repeat template or privacy stamp changed.");
        var tools = new List<CoachToolObservation>(); var usage = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(saved.Frame, context.Snapshot, catalog, Build, toolCompleted: tools.Add);
        using var model = new Model(spec, search); using var http = new HttpClient(model); StrategyAdvice? advice = null;
        try { advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://repeat-battlecry-fixture.invalid/v1/chat/completions"), "repeat-battlecry-fake", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usage.Add)); }
        catch (InvalidDataException error) when (model.Calls == 2 && model.Line?["Verification"]?.ToString() == "Invalid" && error.Message == "模型提出的动作未通过本地合法性校验。") { }
        Check(model.Calls == 2 && model.Line is not null && model.Snapshot is not null && model.Effect is not null && tools.Count == (search ? 4 : 3) + (DirectTool(spec) is null ? 0 : 1) &&
            tools.All(item => item.Success && item.ResultHash == Hash(item.ResultJson!) && item.RequestId == usage[0].RequestId &&
                item.AnalysisRunId == usage[0].AnalysisRunId && item.EventSequence == saved.Frame.EventSequence) &&
            usage.Count == 2 && usage.All(item => item.Success && item.ChargedTokens == 30 && item.CardBuild == Build && item.PromptTemplateVersion == Template) &&
            budget.Snapshot(saved.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } && SnapshotCodec.Serialize(saved.Frame) == saved.Original,
            "Real tools paid usage provenance or immutable observed frame changed.");
        ArchivedAdviceRecord? loaded = null;
        if (advice is not null)
        {
            var path = Path.Combine(saved.Directory, "archive-" + Guid.NewGuid().ToString("N") + ".db"); var archive = new AdviceArchiveStore(path); await archive.InitializeAsync();
            var draft = AdviceArchiveStore.Prepare(new(saved.Frame.MatchId, saved.Frame.StateVersion, saved.Frame.EventSequence, Build, advice.TemplateVersion!, advice.Summary)
            {
                Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, Reasons = advice.Reasons, Risks = advice.Risks, Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
                Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
                Evidence = tools.Select(item => new AdviceToolEvidence(item.Name, "当前准确附魔与原子战吼计算；不是重复使用牌或新实体。", item.ResultHash)).ToArray(),
                Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
                LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
                HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns, HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
            }, [Key]);
            var archived = await archive.SaveAsync(draft); loaded = await new AdviceArchiveStore(path).GetAsync(archived.Id);
            Check(loaded is not null && JsonSerializer.Serialize(loaded.Advice) == JsonSerializer.Serialize(archived.Advice) && loaded.Advice.VisibilityPolicy == context.VisibilityPolicy &&
                loaded.Advice.KnowledgeAsOf == saved.Point.RecordedAt && loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) &&
                loaded.Advice.Evidence.Select(item => item.ResultHash).SequenceEqual(tools.Select(item => item.ResultHash)) && !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal),
                "Archive reopen lost exact advice source or actual tool evidence hashes.");
        }
        var tracePath = Path.Combine(saved.Directory, "trace-" + Guid.NewGuid().ToString("N") + ".db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usage.Select(item => new ModelRequestTrace(item.RequestId, item.MatchId, item.StateVersion, item.Model, item.Round, item.InputTokens, item.OutputTokens, item.TotalTokens,
            item.ChargedTokens, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = Build, Task = context.Task, PromptTemplateVersion = item.PromptTemplateVersion!, AnalysisRunId = item.AnalysisRunId, EventSequence = item.EventSequence }),
        tools.Select(item => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), saved.Frame.MatchId, saved.Frame.StateVersion, item.Name, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = item.ResultHash, Model = item.Model, AnalysisRunId = item.AnalysisRunId, RequestId = item.RequestId, ToolCallId = item.ToolCallId, EventSequence = item.EventSequence }));
        var reopened = new AnalysisTraceStore(tracePath); var persisted = await reopened.ListToolsAsync(saved.Frame.MatchId);
        Check(await reopened.GetBudgetTotalsAsync(saved.Frame.MatchId) is { RequestCount: 2, ChargedTokens: 60 } && persisted.Count == tools.Count &&
            persisted.All(item => item.RequestId == usage[0].RequestId && item.AnalysisRunId == usage[0].AnalysisRunId && item.EventSequence == saved.Frame.EventSequence && tools.Any(observed => observed.ResultHash == item.ResultHash)) &&
            !JsonSerializer.Serialize(persisted).Contains(Key, StringComparison.Ordinal), "Paid audit settlement disappeared after final host rejection.");
        var exact = await new SessionStore(saved.Database).GetHistoricalDecisionAsync(saved.Point.Id);
        Check(exact is not null && SnapshotCodec.Serialize(exact.Context.Snapshot) == saved.Original, "Repeat evaluation overwrote the saved observed frame.");
        return new(model.Line!, model.Snapshot!, model.Effect!, model.Search, advice, loaded, tools.AsReadOnly(), model.Direct);
    }
    private sealed class Model(Spec spec, bool search) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Line { get; private set; }
        public JsonNode? Snapshot { get; private set; }
        public JsonNode? Effect { get; private set; }
        public JsonNode? Search { get; private set; }
        public JsonNode? Direct { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); var payload = JsonNode.Parse(text)!;
            Check(!text.Contains(Private, StringComparison.Ordinal) && !text.Contains(Key, StringComparison.Ordinal), "Actual HTTP leaked private source or credentials.");
            var data = new JsonArray(payload["messages"]!.AsArray().Where(entry => entry?["role"]?.ToString() != "system").Select(entry => entry!.DeepClone()).ToArray()).ToJsonString();
            Check(!data.Contains("EDR_100t13e", StringComparison.Ordinal), "A private hand's attached bonus leaked into actual model data.");
            var instructions = payload["messages"]!.AsArray().First(entry => entry?["role"]?.ToString() == "system")!["content"]!.GetValue<string>();
            Check(instructions.Contains("BattlecryExecutions", StringComparison.Ordinal) && instructions.Contains("StartedActivations和CompletedActivations", StringComparison.Ordinal) &&
                instructions.Contains("支付、入场及使用后触发仍各一次", StringComparison.Ordinal) && instructions.Contains("Fizzled不允许另选目标", StringComparison.Ordinal) &&
                instructions.Contains("IsSingleBattlecryActivationPreview", StringComparison.Ordinal) && instructions.Contains("不把双战吼泛化为双亡语", StringComparison.Ordinal),
                "The actual model prompt lost the atomic execution prefix, single-play, fixed-target or direct-preview authority boundaries.");
            object Tool(string id, string name, object args) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
            object message;
            if (Calls == 1)
            {
                var calls = new List<object> { Tool("repeat-snapshot", "get_snapshot", new { }), Tool("repeat-effect", "get_entity_effect", new { entity_id = 30 }),
                    Tool("repeat-line", "evaluate_line", new { actions = Plan(spec) }) };
                if (search) calls.Add(Tool("repeat-search", "find_lethal", new { maximum_depth = 6, maximum_nodes = 5000 }));
                if (DirectTool(spec) is { } direct) calls.Add(Tool("repeat-direct", direct, new { source_entity_id = 30 }));
                message = new { role = "assistant", tool_calls = calls };
            }
            else
            {
                var messages = payload["messages"]!.AsArray();
                JsonNode Read(string id) => JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == id)!["content"]!.GetValue<string>())!;
                Snapshot = Read("repeat-snapshot"); Effect = Read("repeat-effect"); Line = Read("repeat-line"); if (search) Search = Read("repeat-search");
                if (DirectTool(spec) is not null) Direct = Read("repeat-direct");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "核对当前粗暴唤醒的确定战吼。" + Key, reasons = Array.Empty<string>(), alternative = (string?)null,
                    risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions = Search?["VerifiedLethalFound"]?.GetValue<bool>() == true ? Search["Actions"]!.DeepClone() : Plan(spec), verification = "verified", verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
