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

internal static class RepeatDeathrattleIntegration
{
    private const string Build = "255790", Template = "hunter-v25/turn", Key = "repeat-deathrattle-fake-key";
    private const string Gift = "EDR_531e", Private = "PRIVATE_REPEAT_DEATHRATTLE_PROBE";
    private const string Complete = "PowerProcessor.EndCurrentTaskList() - complete";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "repeat-deathrattle", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var records = await RecordsAsync(); var catalog = await CatalogAsync(directory, records.Values);
        await RunChecksAsync(directory, records, catalog, pass);
    }
    private sealed record Spec
    {
        public string Source { get; init; } = "EDR_227";
        public string? GiftId { get; init; } = Gift;
        public string SourceZone { get; init; } = "PLAY";
        public int SourceAttack { get; init; } = 7;
        public int SourceHealth { get; init; } = 2;
        public int SourceDamage { get; init; }
        public int Mana { get; init; } = 3;
        public int EnemyHealth { get; init; } = 5;
        public int TargetAttack { get; init; } = 2;
        public int TargetHealth { get; init; } = 8;
        public int BoarCost { get; init; } = 5;
        public int BoarAttack { get; init; } = 1;
        public int Hand { get; init; } = 1;
        public int Deck { get; init; }
        public bool Pair { get; init; }
        public bool AddKnight { get; init; }
        public bool PrivateSource { get; init; }
        public bool OpponentSource { get; init; }
        public bool OpponentHunter { get; init; }
        public bool Duplicate { get; init; }
        public bool Mixed { get; init; }
        public bool Draw { get; init; }
        public bool Raptor { get; init; }
        public bool Quest { get; init; }
        public bool AllowIncomplete { get; init; }
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
        foreach (var name in new[] { "deathrattle-repeat", "battlecry-repeat", "native-definitions", "lethal-search", "dark-gift-static", "terror-end-turn", "quest-progress", "deathrattle-draw" })
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
        await catalog.ImportAsync(stream, Build, source: "fixture://fixed-observed-double-deathrattle"); return catalog;
    }
    private sealed class Replay(DeckTrackingEnricher enricher, SessionStore store, string directory, string database)
    {
        private readonly PowerLogStateEngine _engine = new(1, "repeat-deathrattle-" + Guid.NewGuid().ToString("N"));
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
                "tag=MAXHANDSIZE value=10", "tag=NUM_CARDS_IN_DECK value=" + spec.Deck, "tag=FATIGUE value=2",
                "tag=NUM_CARDS_IN_HAND value=" + spec.Hand,
                "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
            await replay.CharacterAsync(11, "HERO_05", 1, "PLAY", "HERO", 0, 30, 0); await replay.FeedAsync("tag=CLASS value=HUNTER");
            await replay.CharacterAsync(21, spec.OpponentHunter ? "HERO_05" : "HERO_08", 2, "PLAY", "HERO", 0, spec.EnemyHealth, 0);
            await replay.FeedAsync("tag=CLASS value=" + (spec.OpponentHunter ? "HUNTER" : "MAGE"));
            await replay.FeedAsync("FULL_ENTITY - Creating ID=12 CardID=EDR_850p", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER",
                "tag=COST value=2", "tag=EXHAUSTED value=0", "tag=TAG_SCRIPT_DATA_NUM_1 value=2", "tag=TAG_SCRIPT_DATA_NUM_2 value=2");
            if (spec.OpponentHunter)
                await replay.FeedAsync("TAG_CHANGE Entity=3 tag=HERO_POWER_ENTITY value=13", "TAG_CHANGE Entity=3 tag=3527 value=2",
                    "FULL_ENTITY - Creating ID=13 CardID=EDR_850p", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO_POWER",
                    "tag=COST value=2", "tag=EXHAUSTED value=0", "tag=TAG_SCRIPT_DATA_NUM_1 value=2", "tag=TAG_SCRIPT_DATA_NUM_2 value=2");
            await replay.CharacterAsync(30, spec.PrivateSource ? Private : spec.Source, spec.PrivateSource || spec.OpponentSource ? 2 : 1,
                spec.PrivateSource ? "HAND" : spec.SourceZone, "MINION", spec.SourceAttack, spec.SourceHealth, spec.Source == "DINO_421" ? 8 : 4);
            await replay.FeedAsync("tag=DAMAGE value=" + spec.SourceDamage, "tag=DEATHRATTLE value=1", "tag=CANT_ATTACK_HEROES value=1");
            if (spec.Source is "EDR_227" or "DINO_421") await replay.FeedAsync("tag=CARDRACE value=BEAST");
            if (spec.Source == "EDR_227") await replay.FeedAsync("tag=RUSH value=1");
            if (spec.Source == "DINO_421") await replay.FeedAsync("tag=TAUNT value=1", "tag=ELUSIVE value=1");
            if (spec.Source == "RLK_912") await replay.FeedAsync("tag=CARDRACE value=UNDEAD", "tag=882 value=1");
            if (spec.Source == "CS2_171") await replay.FeedAsync("tag=CARDRACE value=BEAST", "tag=CHARGE value=1");
            if (spec.Hand > 0)
            {
                await replay.CharacterAsync(31, spec.AddKnight ? "EDR_852" : "CS2_171", 1, "HAND", "MINION", spec.AddKnight ? 2 : spec.BoarAttack,
                    spec.AddKnight ? 3 : 1, spec.AddKnight ? 2 : spec.BoarCost);
                await replay.FeedAsync(spec.AddKnight ? "tag=BATTLECRY value=1" : "tag=CHARGE value=1");
                if (!spec.AddKnight) await replay.FeedAsync("tag=CARDRACE value=BEAST");
            }
            for (var i = 1; i < spec.Hand; i++)
                await replay.CharacterAsync(100 + i, "CS2_172", 1, "HAND", "MINION", 3, 2, 2);
            if (spec.OpponentSource)
            {
                await replay.CharacterAsync(32, "CS2_171", 1, "PLAY", "MINION", 2, 1, 1);
                await replay.FeedAsync("tag=CARDRACE value=BEAST", "tag=CHARGE value=1");
            }
            for (var i = 0; i < spec.Deck; i++)
                await replay.FeedAsync("FULL_ENTITY - Creating ID=" + (200 + i) + " CardID=", "tag=CONTROLLER value=1", "tag=ZONE value=DECK");
            await replay.CharacterAsync(40, "CORE_CS2_231", 2, "PLAY", "MINION", spec.TargetAttack, spec.TargetHealth, 0);
            if (spec.Pair)
            {
                await replay.CharacterAsync(32, spec.Source, 1, "PLAY", "MINION", 5, 2, 4);
                await replay.FeedAsync("tag=CARDRACE value=BEAST", "tag=RUSH value=1", "tag=DEATHRATTLE value=1");
                await replay.CharacterAsync(41, "CORE_CS2_231", 2, "PLAY", "MINION", 2, 2, 0);
            }
            if (spec.GiftId is not null) await replay.EnchantmentAsync(50, spec.GiftId, spec.PrivateSource || spec.OpponentSource ? 2 : 1, 30);
            if (spec.Duplicate) await replay.EnchantmentAsync(51, Gift, 1, 30);
            if (spec.Mixed) await replay.EnchantmentAsync(53, "EDR_100t5e", 1, 30);
            if (spec.Raptor) { await replay.CharacterAsync(60, "EDR_849", 1, "PLAY", "MINION", 1, 5, 0); await replay.FeedAsync("tag=CARDRACE value=BEAST", "tag=TRIGGER_VISUAL value=1"); }
            if (spec.Quest) await replay.FeedAsync("FULL_ENTITY - Creating ID=70 CardID=TLC_EVENT_400", "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL",
                "tag=SIDE_QUEST value=1", "tag=QUEST_PROGRESS value=0", "tag=QUEST_PROGRESS_TOTAL value=3");
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

    private static async Task RunChecksAsync(string directory, Dictionary<string, JsonElement> records, CardCatalog catalog, Action<string> pass)
    {
        var bearSpec = new Spec(); var bear = await (await Replay.CreateAsync(directory, catalog, bearSpec)).SaveAsync();
        var imbued = await PipelineAsync(bear, catalog, bearSpec, search: true); AssertVerified(imbued, true); AssertExecution(imbued, bear);
        Check(imbued.Search?["VerifiedLethalFound"]?.GetValue<bool>() == true && imbued.Search["ExploredNodes"]?.GetValue<int>() > 0 &&
            imbued.Line["AppliedActions"]?.GetValue<int>() == 4 && imbued.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 0 &&
            imbued.Line["Result"]?["ImbueCountBefore"]?.GetValue<int>() == 2 && imbued.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 4 &&
            imbued.Line["Result"]!["HeroPowerChanges"]!.AsArray().Count == 2 && Entity(imbued.Line, 12)["Flags"]?["TAG_SCRIPT_DATA_NUM_1"]?.GetValue<int>() == 4 &&
            Entity(imbued.Line, 31)["Attack"]?.GetValue<int>() == 5 && Entity(imbued.Line, 31)["Cost"]?.GetValue<int>() == 1 &&
            !HasEntity(imbued.Line, 30) && Entity(imbued.Line, 31)["AttacksUsed"]?.GetValue<int>() == 1 &&
            bear.Frame.Entities[30].GetIntTag("CANT_ATTACK_HEROES") == 1 && bear.Frame.Entities[30].GetIntTag("ATK") == 7 &&
            Entity(imbued.Line, 40)["RemainingHealth"]?.GetValue<int>() == 1,
            "The four-action actual Umbraclaw double-Imbue chain lost a repeat, added source Attack again or bypassed death by attacking face.");
        pass("parsed observed Siphoned Growth doubles one Umbraclaw death and the exact reopened four-action Wolf-four charge lethal closes real search host archive and paid traces");

        var dinoSpec = Dino(); var dino = await (await Replay.CreateAsync(directory, catalog, dinoSpec)).SaveAsync();
        var buffed = await PipelineAsync(dino, catalog, dinoSpec, search: true); AssertVerified(buffed, true); AssertExecution(buffed, dino);
        Check(buffed.Search?["VerifiedLethalFound"]?.GetValue<bool>() == true && buffed.Line["AppliedActions"]?.GetValue<int>() == 3 &&
            Entity(buffed.Line, 31)["Attack"]?.GetValue<int>() == 7 && Entity(buffed.Line, 31)["MaximumHealth"]?.GetValue<int>() == 7 &&
            Entity(buffed.Line, 31)["RemainingHealth"]?.GetValue<int>() == 7 && Entity(buffed.Line, 31)["Cost"]?.GetValue<int>() == 1 &&
            buffed.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 0 && !HasEntity(buffed.Line, 30) &&
            dino.Frame.Entities[30].GetIntTag("ATK") == 10 && dino.Frame.Entities.Values.All(entity => entity.Zone != "DECK") &&
            Entity(buffed.Line, 40)["RemainingHealth"]?.GetValue<int>() == 1,
            "DINO did not serially buff the existing hand minion twice or fabricated hidden deck state, costs or another death.");
        pass("parsed DINO death repeats two real plus-three stat effects over one current hand and an actually empty deck then completes the three-action charge-seven lethal");

        foreach (var (spec, count, after) in new[]
        {
            (new Spec { GiftId = null, Actions = Actions(("attack", 30, 40)) }, 1, 3),
            (new Spec { Changes = ["TAG_CHANGE Entity=30 tag=1831 value=1"], Actions = Actions(("attack", 30, 40)) }, 0, 2),
            (new Spec { Changes = ["TAG_CHANGE Entity=30 tag=217 value=0"], Actions = Actions(("attack", 30, 40)) }, 0, 2)
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var value = await PipelineAsync(item, catalog, spec);
            AssertVerified(value, false); AssertExecution(value, item, count, enchantment: count == 1 ? null : 50);
            Check(value.Line["AppliedActions"]?.GetValue<int>() == 1 && value.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == after &&
                !HasEntity(value.Line, 30) && (value.Line["Result"]?["BattlecryExecutions"]?.AsArray().Count ?? 0) == 0,
                "Ordinary one or explicit source suppression zero did not remove the body once without inventing Battlecries.");
        }
        var pairSpec = new Spec { Pair = true, Actions = Actions(("attack", 32, 40), ("attack", 30, 41)) };
        var pair = await (await Replay.CreateAsync(directory, catalog, pairSpec)).SaveAsync(); var paired = await PipelineAsync(pair, catalog, pairSpec);
        AssertVerified(paired, false); AssertExecution(paired, pair); AssertExecution(paired, pair, 1, 32, null);
        Check(Executions(paired.Line).Count == 2 && paired.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 5 &&
            !HasEntity(paired.Line, 30) && !HasEntity(paired.Line, 32), "A same-ID sibling inherited another body's attachment or a death was executed twice.");
        pass("ordinary once explicit suppression zero and two same-card bodies retain separately bound native schedules with one actual destruction each");

        var drawSpec = Draw(); var drawItem = await (await Replay.CreateAsync(directory, catalog, drawSpec)).SaveAsync();
        var draw = await PipelineAsync(drawItem, catalog, drawSpec); AssertUnverified(draw);
        var schedule = Execution(draw.Line); var preview = draw.Line["Result"]!["DrawPreviews"]!.AsArray().Single()!;
        Check(schedule["PlannedActivations"]?.GetValue<int>() == 2 && schedule["StartedActivations"]?.GetValue<int>() == 1 &&
            schedule["CompletedActivations"]?.GetValue<int>() == 0 && schedule["IsComplete"]?.GetValue<bool>() == false &&
            schedule["RemainingActivations"]?.GetValue<int>() == 2 && schedule["Activations"]!.AsArray().Count == 1 &&
            schedule["EffectKind"]?.ToString() == "ObservedEnchantmentDraw" && schedule["IsAcquiredEffect"]?.GetValue<bool>() == true &&
            schedule["EffectSourceEntityId"]?.GetValue<int>() == 50 && schedule["EffectSourceCardId"]?.ToString() == "EDR_100t4e" &&
            schedule["EffectEnchantmentEntityId"]?.GetValue<int>() == 50 && schedule["MultiplierSourceEntityIds"]!.AsArray().Single()!.GetValue<int>() == 30 &&
            draw.Line["AppliedActions"]?.GetValue<int>() == 1 && draw.Line["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == true &&
            Exact(preview["RequestedDraws"], 2) && Exact(preview["CardsBurned"], 2) && Exact(preview["CardsAddedToHand"], 0) &&
            Exact(preview["HandCountAfter"], 10) && drawItem.Frame.Entities.Values.Count(entity => entity.Zone == "HAND" && entity.ControllerId == 1) == 10 &&
            draw.Direct?["DeathrattleMultiplicity"]?["Activations"]?.GetValue<int>() == 2 &&
            draw.Direct["IsSingleDeathrattleActivationPreview"]?.GetValue<bool>() == true && Exact(draw.Direct["RequestedDraws"], 2) &&
            draw.Line["Result"]!["Entities"]!.AsArray().All(entity => drawItem.Frame.Entities.ContainsKey(entity!["EntityId"]!.GetValue<int>())),
            "Scourge Troll's acquired-only double Deathrattle skipped the first real draw boundary, created hand slots or completed a fictional four-card draw.");
        var noGranted = drawSpec with { GiftId = null, Draw = false, Changes = ["TAG_CHANGE Entity=30 tag=DEATHRATTLE value=0"], Actions = Actions(("attack", 30, 40)) };
        var noGrantedItem = await (await Replay.CreateAsync(directory, catalog, noGranted)).SaveAsync(); var empty = await PipelineAsync(noGrantedItem, catalog, noGranted);
        AssertVerified(empty, false);
        Check(empty.Line["Result"]!["DrawPreviews"]!.AsArray().Count == 0 && Executions(empty.Line).Count == 0,
            "RLK912's gained-only text became a native Deathrattle even without an actual granted script.");
        pass("real acquired Inner Demons on Scourge Troll schedules two but the full hand burns only the first draw-two preview and stops all later actions without inventing native DR");

        foreach (var spec in new[]
        {
            new Spec { Duplicate = true }, new Spec { Mixed = true }, new Spec { GiftId = "EDR_100t9e" },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=371 value=1"] }, new Spec { Changes = ["TAG_CHANGE Entity=2 tag=882 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=1131 value=1"] }, new Spec { Changes = ["TAG_CHANGE Entity=2 tag=1831 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=1831 value=2"] }, new Spec { Changes = ["TAG_CHANGE Entity=30 tag=217 value=2"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=44 value=unknown"] }, new Spec { Changes = ["TAG_CHANGE Entity=31 tag=48 value=unknown"] },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=40 value=999"], AllowIncomplete = true },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=202 value=SPELL"] }, new Spec { Changes = ["TAG_CHANGE Entity=50 tag=CONTROLLER value=2"] },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=TAG_SCRIPT_DATA_NUM_1 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=50 tag=DEATHRATTLE value=1"] },
            Dino() with { Deck = 1 }, Draw() with { Duplicate = true },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=REBORN value=1"] },
            new Spec { TargetHealth = 7, Changes = ["TAG_CHANGE Entity=40 tag=DEATHRATTLE value=1"] },
            new Spec { Changes = ["FULL_ENTITY - Creating ID=80 CardID=FP1_031", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=1", "tag=HEALTH value=7", "tag=DAMAGE value=0", "tag=AURA value=1"] }
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); AssertUnverified(await PipelineAsync(item, catalog, spec));
        }
        var opponentSpec = new Spec { OpponentSource = true, Hand = 0, Actions = Actions(("attack", 32, 30)) };
        var opponent = await (await Replay.CreateAsync(directory, catalog, opponentSpec)).SaveAsync();
        var opponentResult = await PipelineAsync(opponent, catalog, opponentSpec); AssertUnverified(opponentResult);
        Check(opponentResult.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 2 &&
            Entity(opponentResult.Line, 12)["Flags"]?["TAG_SCRIPT_DATA_NUM_1"]?.GetValue<int>() == 2,
            "An opponent-owned double native Deathrattle upgraded the local player's Hero Power or cumulative Imbue counter.");
        var opponentHunterSpec = opponentSpec with { OpponentHunter = true, Hand = 1 };
        var opponentHunter = await (await Replay.CreateAsync(directory, catalog, opponentHunterSpec)).SaveAsync();
        var opposingImbue = await PipelineAsync(opponentHunter, catalog, opponentHunterSpec); AssertVerified(opposingImbue, false); AssertExecution(opposingImbue, opponentHunter);
        Check(Entity(opposingImbue.Line, 13)["Flags"]?["TAG_SCRIPT_DATA_NUM_1"]?.GetValue<int>() == 4 &&
            Entity(opposingImbue.Line, 12)["Flags"]?["TAG_SCRIPT_DATA_NUM_1"]?.GetValue<int>() == 2 &&
            opposingImbue.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 2 && Entity(opposingImbue.Line, 31)["Attack"]?.GetValue<int>() == 1 &&
            Entity(opposingImbue.Line, 31)["Cost"]?.GetValue<int>() == 5 && opposingImbue.Line["Result"]!["HeroPowerChanges"]!.AsArray().Count == 2 &&
            opposingImbue.Line["Result"]!["HeroPowerChanges"]!.AsArray().All(change => change!["ControllerId"]?.GetValue<int>() == 2 && change["EntityId"]?.GetValue<int>() == 13),
            "Opponent-owned real repeat effects were ignored or upgraded the wrong player's current Wolf power, counter or hand.");
        pass("unknown global modifiers duplicate or mixed sources raw malformed values current missing stats other aura and hidden DINO deck retain their real host boundary");

        foreach (var variant in new[] { "gift-dbf", "gift-text", "gift-mechanics", "body-dbf", "body-text", "missing-gift", "self-dbf" })
        {
            var altered = records.ToDictionary(pair => pair.Key, pair => JsonNode.Parse(pair.Value.GetRawText())!.AsObject());
            if (variant == "gift-dbf") altered[Gift]["dbfId"] = 120094;
            if (variant == "gift-text") altered[Gift]["text"] = "+2攻击力。本随从的亡语会触发三次。";
            if (variant == "gift-mechanics") altered[Gift]["mechanics"] = new JsonArray("DEATHRATTLE");
            if (variant == "body-dbf") altered["EDR_227"]["dbfId"] = 114021;
            if (variant == "body-text") altered["EDR_227"]["text"] = "<b>亡语：</b>消灭敌方英雄。";
            if (variant == "missing-gift") altered.Remove(Gift);
            if (variant == "self-dbf") altered["RLK_912"]["dbfId"] = 84230;
            var mutated = await CatalogAsync(directory, altered.Values.Select(value => JsonSerializer.SerializeToElement(value)));
            var spec = variant == "self-dbf" ? Draw() : new Spec();
            var item = await (await Replay.CreateAsync(directory, mutated, spec)).SaveAsync(); AssertUnverified(await PipelineAsync(item, mutated, spec));
        }
        var rejected = false;
        try { _ = new SnapshotCoach(bear.Frame, CompactAdviceContextBuilder.Build(bear.Frame, "255791"), catalog, "255791"); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Another client build accepted a frozen double-Deathrattle source.");
        pass("real lazy catalog lookups reject changed whole native and attachment DBF text raw mechanics missing definition self-source and wrong build rather than trusting card IDs");

        var historySpec = new Spec { GiftId = null, Actions = Actions(("attack", 30, 40)) };
        var replay = await Replay.CreateAsync(directory, catalog, historySpec); var early = await replay.SaveAsync();
        await replay.FeedAsync("FULL_ENTITY - Creating ID=50 CardID=" + Gift, "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30", Complete);
        var late = await replay.SaveAsync();
        var old = await new SessionStore(early.Database).GetHistoricalDecisionAsync(early.Point.Id);
        Check(old is not null && SnapshotCodec.Serialize(old.Context.Snapshot) == early.Original && !old.Context.Snapshot.Entities.ContainsKey(50),
            "A later gift was written back into an earlier exact decision.");
        var once = await PipelineAsync(early, catalog, historySpec); AssertVerified(once, false); AssertExecution(once, early, 1, enchantment: null);
        var twice = await PipelineAsync(late, catalog, historySpec); AssertVerified(twice, false); AssertExecution(twice, late);
        Check(once.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 3 && twice.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 4,
            "Exact older and later real points did not preserve their separate multiplicity proof.");
        var privateSpec = new Spec { PrivateSource = true }; var hidden = await (await Replay.CreateAsync(directory, catalog, privateSpec)).SaveAsync();
        var privateResult = await PipelineAsync(hidden, catalog, privateSpec); AssertUnverified(privateResult);
        Check(hidden.Frame.Entities[30].CardId is null && !hidden.Frame.Entities.ContainsKey(50) && privateResult.Effect["error"] is not null &&
            Executions(privateResult.Line).Count == 0 && !privateResult.Snapshot.ToJsonString().Contains(Private, StringComparison.Ordinal),
            "An opponent private hand or its current attachment received identity, current effects or usable repeated actions.");
        pass("exact history never borrows a later modifier and hidden bodies or attached bonuses stay out of tools model HTTP paid traces and archives");

        var playSpec = new Spec { AddKnight = true, Mana = 2, Actions = Actions(("play", 31, null)) };
        var playItem = await (await Replay.CreateAsync(directory, catalog, playSpec)).SaveAsync(); var played = await PipelineAsync(playItem, catalog, playSpec);
        AssertVerified(played, false);
        Check(Executions(played.Line).Count == 0 && played.Line["Result"]?["ImbueCountAfter"]?.GetValue<int>() == 3 &&
            played.Line["Result"]!["BattlecryExecutions"]!.AsArray().Single()!["PlannedActivations"]?.GetValue<int>() == 1 &&
            played.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 0 && Entity(played.Line, 30)["Attack"]?.GetValue<int>() == 7,
            "A live attached Deathrattle modifier duplicated another card's Battlecry play payment or source Attack.");
        var raptorSpec = Dino() with { Mana = 1, Quest = true, Raptor = true, Actions = Actions(("attack", 30, 40), ("play", 31, null), ("attack", 31, 21)) };
        var raptorItem = await (await Replay.CreateAsync(directory, catalog, raptorSpec)).SaveAsync(); var raptor = await PipelineAsync(raptorItem, catalog, raptorSpec);
        AssertUnverified(raptor); AssertExecution(raptor, raptorItem);
        Check(raptor.Line["AppliedActions"]?.GetValue<int>() == 2 && raptor.Line["Result"]?["RaptorBoundaryReached"]?.GetValue<bool>() == true &&
            raptor.Line["Result"]!["RaptorPreviews"]!.AsArray().Single()!["TriggerCount"]?["Maximum"]?.GetValue<int>() == 1 &&
            raptor.Line["Result"]!["QuestProgressPreviews"]!.AsArray().Single()!["Increment"]?.GetValue<int>() == 1,
            "Repeating an earlier Deathrattle duplicated the later play or after-play effect or crossed its random observation boundary.");
        pass("observed double DR never doubles Battlecries payment or play-only quest and Raptor events and a later real random boundary remains mandatory");
    }

    private static void AssertExecution(Finished value, Saved saved, int planned = 2, int source = 30, int? enchantment = 50)
    {
        var execution = Execution(value.Line, source);
        Check(execution["MatchId"]?.ToString() == saved.Frame.MatchId && execution["StateVersion"]?.GetValue<long>() == saved.Frame.StateVersion &&
            execution["EventSequence"]?.GetValue<long>() == saved.Frame.EventSequence && execution["CardBuild"]?.ToString() == Build &&
            execution["SourceCardId"]?.ToString() == saved.Frame.Entities[source].CardId && execution["ActionOrdinal"]?.GetValue<int>() >= 1 &&
            execution["DeathBatchOrdinal"]?.GetValue<int>() >= 1 && execution["DeathBatchEntityIds"]!.AsArray().Count(id => id!.GetValue<int>() == source) == 1 &&
            execution["DeathBatchEntityIds"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual(
                execution["DeathBatchEntityIds"]!.AsArray().Select(id => id!.GetValue<int>()).Distinct().Order()) &&
            execution["IsBatchOrderIndependent"]?.GetValue<bool>() == true &&
            execution["EffectSourceEntityId"]?.GetValue<int>() == source && execution["EffectSourceCardId"]?.ToString() == saved.Frame.Entities[source].CardId &&
            execution["IsAcquiredEffect"]?.GetValue<bool>() == false && execution["EffectKind"]?.ToString() == (saved.Frame.Entities[source].CardId == "DINO_421" ? "NativeHandDeckBuff" : "NativeImbue") &&
            execution["IsMultiplicityResolved"]?.GetValue<bool>() == true && execution["IsEnabled"]?.GetValue<bool>() == (planned > 0) &&
            execution["PlannedActivations"]?.GetValue<int>() == planned && execution["StartedActivations"]?.GetValue<int>() == planned &&
            execution["CompletedActivations"]?.GetValue<int>() == planned && execution["RemainingActivations"]?.GetValue<int>() == 0 &&
            execution["IsComplete"]?.GetValue<bool>() == true && (enchantment is null || execution["EnchantmentEntityIds"]!.AsArray().Any(id => id!.GetValue<int>() == enchantment)) &&
            (planned != 2 || execution["MultiplierSourceEntityIds"]!.AsArray().Single()!.GetValue<int>() == enchantment) &&
            execution["Activations"]!.AsArray().Count == planned && execution["Activations"]!.AsArray().Select(step => step!["ActivationOrdinal"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(1, planned)),
            "The typed repeat lost its exact evidence binding or destroyed the body once per activation: " + execution.ToJsonString());
    }

    private static Spec Dino() => new() { Source = "DINO_421", SourceAttack = 10, SourceHealth = 8, SourceDamage = 7, TargetAttack = 1, TargetHealth = 11,
        Mana = 1, BoarCost = 1, EnemyHealth = 7 };
    private static Spec Draw() => new() { Source = "RLK_912", GiftId = "EDR_100t4e", Draw = true, SourceAttack = 1, SourceHealth = 3, SourceDamage = 2,
        TargetAttack = 1, Hand = 10, Deck = 2, BoarCost = 1, EnemyHealth = 20 };
    private static string? DirectTool(Spec spec) => spec.Draw ? "evaluate_deathrattle_draw" : null;
    private static JsonArray Executions(JsonNode line) => line["Result"]?["DeathrattleExecutions"]?.AsArray() ?? new();
    private static JsonNode Execution(JsonNode line, int id = 30) => Executions(line).SingleOrDefault(value => value!["SourceEntityId"]?.GetValue<int>() == id)
        ?? throw new InvalidOperationException("The expected atomic Deathrattle execution was absent: " + line.ToJsonString());
    private static JsonArray Plan(Spec spec) => spec.Actions is not null ? (JsonArray)spec.Actions.DeepClone() : spec.Source == "DINO_421"
        ? Actions(("attack", 30, 40), ("play", 31, null), ("attack", 31, 21))
        : spec.Draw ? Actions(("attack", 30, 40), ("play", 31, null))
        : Actions(("attack", 30, 40), ("hero_power", 12, null), ("play", 31, null), ("attack", 31, 21));
    private static JsonObject Action(string kind, int source, int? target = null) => new() { ["kind"] = kind, ["source_entity_id"] = source, ["target_entity_id"] = target };
    private static JsonArray Actions(params (string Kind, int Source, int? Target)[] actions) => new(actions.Select(action => (JsonNode)Action(action.Kind, action.Source, action.Target)).ToArray());
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    private static bool HasEntity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Any(entity => entity!["EntityId"]?.GetValue<int>() == id);
    private static bool Exact(JsonNode? range, long value) => range?["Minimum"]?.GetValue<long>() == value && range?["Maximum"]?.GetValue<long>() == value;
    private static void AssertVerified(Finished value, bool lethal) => Check(value.Line["Verification"]?.ToString() == "Verified" && value.Line["IsLethal"]?.GetValue<bool>() == lethal &&
        value.Advice is { Verification: "verified" } && value.Advice.VerifiedLethal == lethal && value.Advice.Actions.Count > 0 &&
        value.Archive!.Advice.Verification == "verified" && value.Archive.Advice.VerifiedLethal == lethal,
        "The final host did not independently verify the actual repeated native script: " + value.Line.ToJsonString());
    private static void AssertUnverified(Finished value) => Check(value.Line["Verification"]?.ToString() != "Verified" && value.Line["IsLethal"]?.GetValue<bool>() == false &&
        value.Search?["VerifiedLethalFound"]?.GetValue<bool>() != true && (value.Advice is null || !value.Advice.VerifiedLethal && value.Advice.Actions.Count == 0) &&
        (value.Archive is null || !value.Archive.Advice.VerifiedLethal && value.Archive.Advice.Actions.Count == 0), "Unknown Deathrattle evidence became executable verified advice.");
    private static async Task<Finished> PipelineAsync(Saved saved, CardCatalog catalog, Spec spec, bool search = false)
    {
        var visible = await catalog.GetByIdsBatchedAsync(saved.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(saved.Frame, Build, AdviceTaskKind.Turn, cards: visible)
            with { KnowledgeAsOf = saved.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        Check(context.TemplateVersion == Template && context.VisibilityPolicy == "attachments-v1", "The real repeat template or privacy stamp changed.");
        var tools = new List<CoachToolObservation>(); var usage = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(saved.Frame, context.Snapshot, catalog, Build, toolCompleted: tools.Add);
        using var model = new Model(spec, search); using var http = new HttpClient(model); StrategyAdvice? advice = null;
        try { advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://repeat-deathrattle-fixture.invalid/v1/chat/completions"), "repeat-deathrattle-fake", 1),
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
                Evidence = tools.Select(item => new AdviceToolEvidence(item.Name, "当前准确附魔与原子亡语计算；不是重复使用牌或新实体。", item.ResultHash)).ToArray(),
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
            Check(instructions.Contains("DeathrattleExecutions", StringComparison.Ordinal) && instructions.Contains("StartedActivations", StringComparison.Ordinal) &&
                instructions.Contains("CompletedActivations", StringComparison.Ordinal) && instructions.Contains("一次模拟死亡", StringComparison.Ordinal) &&
                instructions.Contains("不是触发顺序", StringComparison.Ordinal) && instructions.Contains("不能再次施加", StringComparison.Ordinal) &&
                instructions.Contains("EffectSourceEntityId/EffectEnchantmentEntityId", StringComparison.Ordinal) &&
                instructions.Contains("IsSingleDeathrattleActivationPreview", StringComparison.Ordinal) && instructions.Contains("不能乘次数声称四抽", StringComparison.Ordinal),
                "The actual model prompt lost atomic Deathrattle execution and incomplete-prefix semantics.");
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
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "核对当前虹吸生长的确定亡语。" + Key, reasons = Array.Empty<string>(), alternative = (string?)null,
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
