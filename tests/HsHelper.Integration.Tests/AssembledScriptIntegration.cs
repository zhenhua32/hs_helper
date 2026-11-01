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

internal static class AssembledScriptIntegration
{
    private const string Build = "255790", Template = "hunter-v25/turn", Key = "assembled-script-fake-key";
    private const string Private = "PRIVATE_ASSEMBLED_SCRIPT_PROBE", Complete = "PowerProcessor.EndCurrentTaskList() - complete";
    private const string Script = "Battlecry.HandMinionBuff.1.1.v1";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in new[] { "assembled-scripts", "native-definitions", "lethal-search", "dark-gift-static", "terror-end-turn" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build && fixture.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The actual assembled-script test lost its fixed complete primary definitions.");
            foreach (var card in fixture.RootElement.GetProperty("cards").EnumerateArray()) records[card.GetProperty("id").GetString()!] = card.Clone();
        }
        var directory = Path.Combine(root, "assembled-scripts", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var catalog = await CatalogAsync(directory, records.Values); var replay = await Replay.CreateAsync(directory, catalog, new());
        var saved = await replay.SaveAsync(); var normal = await PipelineAsync(saved, catalog, new(), search: true);
        AssertVerified(normal, lethal: true); AssertEffect(normal.Line, saved, 30, "EDR_102", 0, 121051, "EDR_102", "TIME_100", 1, true);
        Check(normal.Effect["effect"]?["is_resolved"]?.GetValue<bool>() == true && normal.Effect["effect"]?["effect_card_id"]?.ToString() == "TIME_100" &&
            normal.Effect["effect"]?["text"]?.ToString().Contains("使你手牌中的所有随从牌获得+1/+1", StringComparison.Ordinal) == true &&
            normal.Effect["effect"]?["text"]?.ToString().Contains("发现", StringComparison.Ordinal) == false &&
            saved.Frame.Entities[30].GetIntTag("CRAFTABLE_SECOND_HALF_DBID") == 121051 && saved.Frame.Entities.Values.All(entity => entity.CardId != "TIME_100"),
            "The actual lazy entity-effect lookup used native Discover text or required a donor entity to exist in the snapshot.");
        Check(normal.Search?["VerifiedLethalFound"]?.GetValue<bool>() == true && normal.Search["ExploredNodes"]?.GetValue<int>() > 0 &&
            normal.Search["Evaluation"]?["IsLethal"]?.GetValue<bool>() == true && normal.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 0 &&
            Entity(normal.Line, 30)["Attack"]?.GetValue<int>() == 5 && Entity(normal.Line, 30)["MaximumHealth"]?.GetValue<int>() == 4 &&
            Entity(normal.Line, 31)["Attack"]?.GetValue<int>() == 2 && Entity(normal.Line, 31)["MaximumHealth"]?.GetValue<int>() == 2 &&
            Entity(normal.Line, 31)["Cost"]?.GetValue<int>() == 1 && normal.Line["Result"]?["DiscoveryBoundaryReached"]?.GetValue<bool>() == false,
            "The normal actual recipe paid twice, buffed itself, rediscovered a Dark Gift, or was unavailable to the real search.");
        pass("a real parsed four-cost assembled Battlecry buffs only the other hand minion once and lazy donor loading search final host archive and paid traces all verify the resulting charge lethal");

        foreach (var donor in new[] { (131236, "BE_207"), (40567, "CFM_753"), (102196, "CORE_CFM_753"), (66858, "CS3_025"), (121051, "TIME_100") })
        {
            var spec = new Spec { Second = donor.Item1.ToString() }; var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync();
            var result = await PipelineAsync(item, catalog, spec); AssertVerified(result, true);
            AssertEffect(result.Line, item, 30, "EDR_102", 0, donor.Item1, "EDR_102", donor.Item2, 1, true);
        }
        foreach (var spec in new[]
        {
            new Spec { Base = "TIME_100", First = "123623", Second = "0" },
            new Spec { Base = "EDR_942", First = "69637", Second = "66858" }
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var result = await PipelineAsync(item, catalog, spec);
            AssertVerified(result, true);
            AssertEffect(result.Line, item, 30, spec.Base, int.Parse(spec.First!), int.Parse(spec.Second!),
                spec.First == "123623" ? "MEND_803" : "CORE_EX1_323", spec.Second == "0" ? "TIME_100" : "CS3_025", 1, true);
            Check(Entity(result.Line, 30)["Type"]?.ToString() == "MINION" && Entity(result.Line, 30)["Attack"]?.GetValue<int>() == 5 &&
                result.Line["Result"]?["HeroPowerChanges"]!.AsArray().Count == 0 && Entity(result.Line, 30)["Flags"]?["DIVINE_SHIELD"]?.GetValue<int>() != 1,
                "A weapon or hero trigger donor replaced the actual base body or restored the removed base keyword/native hero effect.");
        }
        pass("all five fixed identical effect donors plus retained-effect and positive weapon or hero Battlecry donors use the same audited operation without claiming a generation pool or changing body identity");

        var pairSpec = new Spec { SecondSource = true, Mana = 9, EnemyHealth = 3 };
        var pair = await (await Replay.CreateAsync(directory, catalog, pairSpec)).SaveAsync(); var paired = await PipelineAsync(pair, catalog, pairSpec);
        AssertVerified(paired, true); AssertEffect(paired.Line, pair, 30, "EDR_102", 0, 121051, "EDR_102", "TIME_100", 1, true);
        AssertEffect(paired.Line, pair, 32, "EDR_102", 0, 66858, "EDR_102", "CS3_025", 1, true);
        Check(Effects(paired.Line).Count == 2 && Entity(paired.Line, 30)["Attack"]?.GetValue<int>() == 5 && Entity(paired.Line, 32)["Attack"]?.GetValue<int>() == 6 &&
            Entity(paired.Line, 31)["Attack"]?.GetValue<int>() == 3 && Entity(paired.Line, 31)["Cost"]?.GetValue<int>() == 1 &&
            Effects(paired.Line)[0]!["Targets"]!.AsArray().Select(target => target!["EntityId"]!.GetValue<int>()).Order().SequenceEqual([31, 32]) &&
            Effects(paired.Line)[1]!["Targets"]!.AsArray().Single()!["EntityId"]?.GetValue<int>() == 31,
            "Two same-ID entities shared a card-level script or were buffed after leaving the hand.");
        pass("two same-base entities keep separate donor identities and sequential target before-after values while sharing only actual current hand state");

        var observedSpec = new Spec { SourceCost = 2, SourceAttack = 9, SourceHealth = 12, BoarCost = 3, BoarAttack = 4, BoarHealth = 8, BoarDamage = 2, PlayOnly = true };
        var observed = await (await Replay.CreateAsync(directory, catalog, observedSpec)).SaveAsync(); var stats = await PipelineAsync(observed, catalog, observedSpec);
        AssertVerified(stats, false); var target = Effects(stats.Line).Single()!["Targets"]!.AsArray().Single()!;
        Check(Entity(stats.Line, 30)["Attack"]?.GetValue<int>() == 9 && Entity(stats.Line, 30)["MaximumHealth"]?.GetValue<int>() == 12 &&
            stats.Line["Result"]?["AvailableMana"]?.GetValue<int>() == 3 && Entity(stats.Line, 31)["Cost"]?.GetValue<int>() == 3 &&
            target["AttackBefore"]?.GetValue<int>() == 4 && target["AttackAfter"]?.GetValue<int>() == 5 && target["MaximumHealthBefore"]?.GetValue<int>() == 8 &&
            target["MaximumHealthAfter"]?.GetValue<int>() == 9 && target["RemainingHealthBefore"]?.GetValue<int>() == 6 && target["RemainingHealthAfter"]?.GetValue<int>() == 7,
            "The script replaced observed cost stats or existing damage with printed base or donor values.");
        foreach (var flag in new[] { "BATTLECRY", "SILENCED" })
        {
            var suppressed = new Spec { Changes = ["TAG_CHANGE Entity=30 tag=" + flag + " value=" + (flag == "BATTLECRY" ? "0" : "1")] };
            var item = await (await Replay.CreateAsync(directory, catalog, suppressed)).SaveAsync(); var result = await PipelineAsync(item, catalog, suppressed);
            AssertVerified(result, false); AssertEffect(result.Line, item, 30, "EDR_102", 0, 121051, "EDR_102", "TIME_100", 0, false);
            Check(Entity(result.Line, 31)["Attack"]?.GetValue<int>() == 1, "Suppressed Battlecry was restored during summon or applied its buff anyway.");
        }
        var noExtraMarker = await (await Replay.CreateAsync(directory, catalog, new() { OmitBattlecry = true })).SaveAsync();
        var ordinaryOnce = await PipelineAsync(noExtraMarker, catalog, new() { OmitBattlecry = true }); AssertVerified(ordinaryOnce, true);
        AssertEffect(ordinaryOnce.Line, noExtraMarker, 30, "EDR_102", 0, 121051, "EDR_102", "TIME_100", 1, true);
        var repeatedSpec = new Spec { RepeatGift = "EDR_100t7e" };
        var repeated = await (await Replay.CreateAsync(directory, catalog, repeatedSpec)).SaveAsync();
        var observedTwice = await PipelineAsync(repeated, catalog, repeatedSpec); AssertVerified(observedTwice, true);
        AssertEffect(observedTwice.Line, repeated, 30, "EDR_102", 0, 121051, "EDR_102", "TIME_100", 2, true);
        Check(Entity(observedTwice.Line, 31)["Attack"]?.GetValue<int>() == 3 && Entity(observedTwice.Line, 31)["Cost"]?.GetValue<int>() == 1,
            "The newly audited single repeat attachment failed its actual two buffs or duplicated payment.");
        pass("current cost attack maximum health and damage remain authoritative and reliable Battlecry suppression or silence yields zero executions without restoring the original script");

        foreach (var variant in new[] { "base-dbf", "base-text", "donor-dbf", "donor-text", "donor-mechanics", "missing-donor" })
        {
            var altered = records.ToDictionary(pair => pair.Key, pair => JsonNode.Parse(pair.Value.GetRawText())!.AsObject());
            if (variant == "base-dbf") altered["EDR_102"]["dbfId"] = 113823;
            if (variant == "base-text") altered["EDR_102"]["text"] = "<b>战吼：</b>&未审核的脚本。";
            if (variant == "donor-dbf") altered["TIME_100"]["dbfId"] = 121052;
            if (variant == "donor-text") altered["TIME_100"]["text"] = "在你的回合结束时，&使你手牌中的所有随从牌获得+9/+9。";
            if (variant == "donor-mechanics") altered["TIME_100"]["mechanics"] = new JsonArray("AURA");
            if (variant == "missing-donor") altered.Remove("TIME_100");
            var mutated = await CatalogAsync(directory, altered.Values.Select(value => JsonSerializer.SerializeToElement(value)));
            var item = await (await Replay.CreateAsync(directory, mutated, new())).SaveAsync(); var result = await PipelineAsync(item, mutated, new());
            AssertUnverified(result); Check(Effects(result.Line).Count == 0 && result.Line["AppliedActions"]?.GetValue<int>() == 0,
                "Bad or missing whole fixed definitions acquired an executable assembled recipe: " + variant);
        }
        foreach (var spec in new[]
        {
            new Spec { First = null }, new Spec { Second = null }, new Spec { First = "1" }, new Spec { Second = "not-a-number" },
            new Spec { First = "130871" }, new Spec { Changes = ["TAG_CHANGE Entity=30 tag=48 value=unknown"] },
            new Spec { Changes = ["TAG_CHANGE Entity=31 tag=45 value=unknown"] }
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); AssertUnverified(await PipelineAsync(item, catalog, spec));
        }
        pass("missing marker malformed or unsupported halves and coherent-looking altered base or donor metadata cannot authorize a fallback native effect or fabricated final lethal");

        foreach (var baseId in new[] { "FIR_953", "CORE_TTN_866", "BE_207" })
        {
            var spec = new Spec { Base = baseId, First = "113822", Second = "121051", NativeProbe = true, SourceHealth = baseId == "BE_207" ? 1 : 4 };
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var result = await PipelineAsync(item, catalog, spec);
            AssertVerified(result, false); AssertEffect(result.Line, item, 30, baseId, 113822, 121051, "EDR_102", "TIME_100", 1, true);
            Check(result.Line["Result"]?["MagmaSplitBoundaryReached"]?.GetValue<bool>() != true && result.Line["Result"]?["MagmaSplitPreviews"]!.AsArray().Count == 0 &&
                result.Line["Result"]?["PendingForcedAttacks"]!.AsArray().Count == 0 && result.Line["Result"]?["TerrorEndTurnPreviews"]!.AsArray().Count == 0 &&
                result.Line["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() != true,
                "An original after-attack end-turn death or Reborn handler was selected solely by the assembled body's old CardId.");
        }
        pass("audited assembled bodies with original Magma Terror or Reborn identities execute only their observed replacement and cannot revive removed native handlers later in the same line");

        foreach (var spec in new[]
        {
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=EXTRA_BATTLECRIES_BASE value=2"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=188 value=unknown"] },
            new Spec { Raptor = true }, new Spec { RepeatGift = "EDR_100t7e1" }, new Spec { OpposingSecret = true }
        })
        {
            var item = await (await Replay.CreateAsync(directory, catalog, spec)).SaveAsync(); var result = await PipelineAsync(item, catalog, spec);
            AssertUnverified(result);
            Check(result.Advice is null || result.Advice.HostRisks?.Count > 0, "The real host's unresolved condition disappeared from its final restrictions.");
        }
        pass("unresolved repeat counts malformed suppression Raptor randomness unknown attachments and opposing secrets retain their actual host boundaries rather than gaining verified lethal from the new script family");

        var history = await Replay.CreateAsync(directory, catalog, new() { Second = "1" }); var early = await history.SaveAsync();
        await history.FeedAsync("TAG_CHANGE Entity=30 tag=4573 value=121051", Complete); var late = await history.SaveAsync();
        var old = await new SessionStore(early.Database).GetHistoricalDecisionAsync(early.Point.Id);
        Check(old is not null && SnapshotCodec.Serialize(old.Context.Snapshot) == early.Original && old.Context.Snapshot.Entities[30].GetIntTag("CRAFTABLE_SECOND_HALF_DBID") == 1 &&
            late.Frame.Entities[30].GetIntTag("CRAFTABLE_SECOND_HALF_DBID") == 121051, "A later observed donor replaced the old exact half marker.");
        AssertUnverified(await PipelineAsync(early with { Frame = old!.Context.Snapshot }, catalog, new() { Second = "1" }));
        AssertVerified(await PipelineAsync(late, catalog, new()), true);
        var hidden = await (await Replay.CreateAsync(directory, catalog, new() { PrivateSource = true })).SaveAsync();
        var privateResult = await PipelineAsync(hidden, catalog, new() { PrivateSource = true }); AssertUnverified(privateResult);
        Check(hidden.Frame.Entities[30].CardId is null && privateResult.Effect["error"] is not null && Effects(privateResult.Line).Count == 0 &&
            !privateResult.Line.ToJsonString().Contains(Private, StringComparison.Ordinal), "Hidden body identity or private half metadata reached a recipe or entity-effect result.");
        var wrongBuildRejected = false;
        try { _ = new SnapshotCoach(saved.Frame, CompactAdviceContextBuilder.Build(saved.Frame, "255791"), catalog, "255791"); }
        catch (ArgumentException) { wrongBuildRejected = true; }
        Check(wrongBuildRejected, "A different card build accepted a source-bound observed recipe.");
        pass("earlier exact SQLite decisions never borrow a later finished half and real model tools keep hidden opponent bodies their gifts and current entity effects private");
    }

    private sealed record Spec
    {
        public string Base { get; init; } = "EDR_102";
        public string? First { get; init; } = "0";
        public string? Second { get; init; } = "121051";
        public int SourceCost { get; init; } = 4;
        public int SourceAttack { get; init; } = 5;
        public int SourceHealth { get; init; } = 4;
        public int Mana { get; init; } = 5;
        public int EnemyHealth { get; init; } = 2;
        public int BoarCost { get; init; } = 1;
        public int BoarAttack { get; init; } = 1;
        public int BoarHealth { get; init; } = 1;
        public int BoarDamage { get; init; }
        public bool SecondSource { get; init; }
        public bool PlayOnly { get; init; }
        public bool NativeProbe { get; init; }
        public bool Raptor { get; init; }
        public string? RepeatGift { get; init; }
        public bool OpposingSecret { get; init; }
        public bool PrivateSource { get; init; }
        public bool OmitBattlecry { get; init; }
        public string[] Changes { get; init; } = [];
    }
    private sealed record Saved(string Directory, string Database, GameSnapshot Frame, DecisionPoint Point, string Original);
    private sealed record Finished(JsonNode Line, JsonNode Effect, JsonNode? Search, StrategyAdvice? Advice, ArchivedAdviceRecord? Archive);
    private static async Task<CardCatalog> CatalogAsync(string root, IEnumerable<JsonElement> records)
    {
        var catalog = new CardCatalog(Path.Combine(root, "cards-" + Guid.NewGuid().ToString("N") + ".db")); await catalog.InitializeAsync();
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(records)); await catalog.ImportAsync(stream, Build, source: "fixture://fixed-observed-assembled-scripts"); return catalog;
    }
    private sealed class Replay(DeckTrackingEnricher enricher, SessionStore store, string path, string database)
    {
        private readonly PowerLogStateEngine _engine = new(1, "assembled-script-" + Guid.NewGuid().ToString("N"));
        private SessionRecorder? _recorder; private long _line;
        public GameSnapshot Frame { get; private set; } = null!;
        public static async Task<Replay> CreateAsync(string root, CardCatalog catalog, Spec spec)
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            var database = Path.Combine(path, "session.db"); var store = new SessionStore(database); await store.InitializeAsync();
            var replay = new Replay(await DeckTrackingEnricher.CreateAsync(catalog, null, Build), store, path, database);
            await replay.FeedAsync("GameState.DebugPrintGame() - BuildNumber=" + Build + " FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
                "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=" + spec.Mana, "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
                "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
            await replay.CharacterAsync(11, "HERO_05", 1, "PLAY", "HERO", 0, 30, 0);
            await replay.CharacterAsync(21, "HERO_08", 2, "PLAY", "HERO", 0, spec.NativeProbe ? 30 : spec.EnemyHealth, 0);
            await replay.CharacterAsync(30, spec.PrivateSource ? Private : spec.Base, spec.PrivateSource ? 2 : 1, "HAND", "MINION", spec.SourceAttack, spec.SourceHealth, spec.SourceCost);
            if (!spec.OmitBattlecry) await replay.FeedAsync("tag=BATTLECRY value=1");
            if (spec.First is not null) await replay.FeedAsync("tag=4572 value=" + spec.First);
            if (spec.Second is not null) await replay.FeedAsync("tag=4573 value=" + spec.Second);
            if (spec.NativeProbe) await replay.FeedAsync("tag=CHARGE value=1");
            await replay.CharacterAsync(31, "CS2_171", 1, "HAND", "MINION", spec.BoarAttack, spec.BoarHealth, spec.BoarCost);
            await replay.FeedAsync("tag=DAMAGE value=" + spec.BoarDamage, "tag=CHARGE value=1", "tag=CARDRACE value=BEAST");
            if (spec.SecondSource)
            {
                await replay.CharacterAsync(32, "EDR_102", 1, "HAND", "MINION", 5, 4, 4);
                await replay.FeedAsync("tag=BATTLECRY value=1", "tag=4572 value=0", "tag=4573 value=66858");
            }
            if (spec.NativeProbe) await replay.CharacterAsync(40, "CORE_CS2_231", 2, "PLAY", "MINION", 1, 20, 0);
            if (spec.Raptor) await replay.CharacterAsync(50, "EDR_849", 1, "PLAY", "MINION", 1, 5, 0);
            if (spec.RepeatGift is not null) await replay.FeedAsync("FULL_ENTITY - Creating ID=51 CardID=" + spec.RepeatGift, "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=30");
            if (spec.OpposingSecret) await replay.FeedAsync("FULL_ENTITY - Creating ID=52 CardID=" + Private, "tag=CONTROLLER value=2", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1");
            await replay.FeedAsync("FULL_ENTITY - Creating ID=90 CardID=" + Private, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=4572 value=113822", "tag=4573 value=121051",
                "FULL_ENTITY - Creating ID=91 CardID=EDR_100t13e", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=90");
            await replay.FeedAsync(spec.Changes); await replay.FeedAsync("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", Complete);
            Check(replay.Frame.Quality.IsComplete && replay.Frame.Quality.IsStable && !SnapshotCodec.Serialize(replay.Frame).Contains(Private, StringComparison.Ordinal), "The parser fixture lost stable perspective or private filtering.");
            replay._recorder = new(store, new(null, Build, 1)); await replay.RecordAsync(); return replay;
        }
        private Task CharacterAsync(int id, string card, int owner, string zone, string type, int attack, int health, int cost) => FeedAsync(
            "FULL_ENTITY - Creating ID=" + id + " CardID=" + card, "tag=CONTROLLER value=" + owner, "tag=ZONE value=" + zone, "tag=CARDTYPE value=" + type,
            "tag=ATK value=" + attack, "tag=HEALTH value=" + health, "tag=DAMAGE value=0", "tag=COST value=" + cost, "tag=ARMOR value=0", "tag=SILENCED value=0", "tag=DORMANT value=0",
            "tag=IMMUNE value=0", "tag=UNTOUCHABLE value=0", "tag=EXHAUSTED value=0", "tag=FROZEN value=0", "tag=NUM_ATTACKS_THIS_TURN value=0", "tag=NUM_TURNS_IN_PLAY value=1",
            "tag=DIVINE_SHIELD value=0", "tag=REBORN value=0", "tag=LIFESTEAL value=0", "tag=DEATHRATTLE value=0");
        public async Task FeedAsync(params string[] lines)
        { foreach (var line in lines) { _line++; _engine.ProcessLine(line); Frame = enricher.Observe(_engine.Snapshot()); if (_recorder is not null) await RecordAsync(); } }
        private Task RecordAsync() { _recorder!.Observe(Frame, _line); return _recorder.FlushAsync(); }
        public async Task<Saved> SaveAsync()
        {
            await _recorder!.CompleteAsync(); var point = (await store.ListDecisionPointsAsync(Frame.MatchId, true)).Single(point => point.StateVersion == Frame.StateVersion && point.EventSequence == Frame.EventSequence);
            var exact = await new SessionStore(database).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact assembled frame.");
            Check(SnapshotCodec.Serialize(exact.Context.Snapshot) == SnapshotCodec.Serialize(Frame) && exact.Context.CardBuild == Build && !exact.HasUnattributedEvents, "The reopened exact frame changed.");
            return new(path, database, exact.Context.Snapshot, point, SnapshotCodec.Serialize(Frame));
        }
    }
    private static JsonArray Plan(Spec spec)
    {
        var actions = new JsonArray(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 });
        if (spec.PlayOnly) return actions;
        if (spec.NativeProbe)
        {
            actions.Add(new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 40 });
            actions.Add(new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }); return actions;
        }
        if (spec.SecondSource) actions.Add(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 32 });
        actions.Add(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 31 });
        actions.Add(new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 31, ["target_entity_id"] = 21 }); return actions;
    }
    private static JsonArray Effects(JsonNode line) => line["Result"]?["AssembledEffects"]?.AsArray() ?? new();
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]?.GetValue<int>() == id)!;
    private static void AssertEffect(JsonNode line, Saved saved, int source, string baseId, int first, int second, string trigger, string effect, int count, bool active)
    {
        var value = Effects(line).Single(effect => effect!["SourceEntityId"]?.GetValue<int>() == source)!;
        Check(value["MatchId"]?.ToString() == saved.Frame.MatchId && value["StateVersion"]?.GetValue<long>() == saved.Frame.StateVersion && value["EventSequence"]?.GetValue<long>() == saved.Frame.EventSequence &&
            value["CardBuild"]?.ToString() == Build && value["BaseCardId"]?.ToString() == baseId && value["FirstHalfDbfId"]?.GetValue<int>() == first && value["SecondHalfDbfId"]?.GetValue<int>() == second &&
            value["TriggerCardId"]?.ToString() == trigger && value["EffectCardId"]?.ToString() == effect && value["ScriptKey"]?.ToString() == Script &&
            value["IsTriggerActive"]?.GetValue<bool>() == active && value["ExecutedCount"]?.GetValue<int>() == count && !string.IsNullOrEmpty(value["Scope"]?.ToString()),
            "The typed operation lost its exact per-entity recipe binding or trigger count: " + value.ToJsonString());
    }
    private static void AssertVerified(Finished value, bool lethal) => Check(value.Line["Verification"]?.ToString() == "Verified" && value.Line["IsLethal"]?.GetValue<bool>() == lethal &&
        value.Advice is { Verification: "verified" } && value.Advice.VerifiedLethal == lethal && value.Advice.Actions.Count > 0 && value.Archive!.Advice.Verification == "verified" && value.Archive.Advice.VerifiedLethal == lethal,
        "The actual final host did not independently verify the deterministic observed recipe: " + value.Line.ToJsonString());
    private static void AssertUnverified(Finished value) => Check(value.Line["Verification"]?.ToString() != "Verified" && value.Line["IsLethal"]?.GetValue<bool>() == false &&
        value.Search?["VerifiedLethalFound"]?.GetValue<bool>() != true && (value.Advice is null || !value.Advice.VerifiedLethal && value.Advice.Actions.Count == 0) &&
        (value.Archive is null || !value.Archive.Advice.VerifiedLethal && value.Archive.Advice.Actions.Count == 0), "An unresolved recipe became verified executable advice.");

    private static async Task<Finished> PipelineAsync(Saved saved, CardCatalog catalog, Spec spec, bool search = false)
    {
        var visibleCards = await catalog.GetByIdsBatchedAsync(saved.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(saved.Frame, Build, AdviceTaskKind.Turn, cards: visibleCards)
            with { KnowledgeAsOf = saved.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        Check(context.TemplateVersion == Template && context.VisibilityPolicy == "attachments-v1", "The actual template or attachment policy changed.");
        var observations = new List<CoachToolObservation>(); var usage = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(saved.Frame, context.Snapshot, catalog, Build, toolCompleted: observations.Add);
        using var model = new Model(spec, search); using var http = new HttpClient(model); StrategyAdvice? advice = null;
        try { advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://assembled-script-fixture.invalid/v1/chat/completions"), "assembled-script-fake", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usage.Add)); }
        catch (InvalidDataException error) when (model.Calls == 2 && model.Line?["Verification"]?.ToString() == "Invalid" &&
            error.Message == "模型提出的动作未通过本地合法性校验。") { }
        Check(model.Calls == 2 && model.Line is not null && model.Effect is not null && observations.Count == (search ? 3 : 2) && observations.All(item => item.Success && item.ResultHash == Hash(item.ResultJson!) &&
            item.RequestId == usage[0].RequestId && item.AnalysisRunId == usage[0].AnalysisRunId && item.EventSequence == saved.Frame.EventSequence) &&
            usage.Count == 2 && usage.All(item => item.Success && item.ChargedTokens == 30 && item.CardBuild == Build && item.PromptTemplateVersion == Template) &&
            budget.Snapshot(saved.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } && SnapshotCodec.Serialize(saved.Frame) == saved.Original,
            "Actual tools HTTP usage provenance or immutable snapshot changed.");
        ArchivedAdviceRecord? loaded = null;
        if (advice is not null)
        {
            var archivePath = Path.Combine(saved.Directory, "archive-" + Guid.NewGuid().ToString("N") + ".db"); var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync();
            var draft = AdviceArchiveStore.Prepare(new(saved.Frame.MatchId, saved.Frame.StateVersion, saved.Frame.EventSequence, Build, advice.TemplateVersion!, advice.Summary)
            {
                Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, Reasons = advice.Reasons, Risks = advice.Risks, Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
                Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
                Evidence = observations.Select(item => new AdviceToolEvidence(item.Name, "已观察拼装脚本及当前目标数值；不证明随机池或新实体。", item.ResultHash)).ToArray(),
                Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
                LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
                HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns, HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
            }, [Key]);
            var archived = await archive.SaveAsync(draft); loaded = await new AdviceArchiveStore(archivePath).GetAsync(archived.Id);
            Check(loaded is not null && JsonSerializer.Serialize(loaded.Advice) == JsonSerializer.Serialize(archived.Advice) && loaded.Advice.VisibilityPolicy == context.VisibilityPolicy &&
                loaded.Advice.KnowledgeAsOf == saved.Point.RecordedAt && loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) &&
                loaded.Advice.Evidence.Select(item => item.ResultHash).SequenceEqual(observations.Select(item => item.ResultHash)) && !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal),
                "Archive reopening lost the exact prepared advice or actual tool hashes.");
        }
        var tracePath = Path.Combine(saved.Directory, "trace-" + Guid.NewGuid().ToString("N") + ".db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usage.Select(item => new ModelRequestTrace(item.RequestId, item.MatchId, item.StateVersion, item.Model, item.Round, item.InputTokens, item.OutputTokens, item.TotalTokens,
            item.ChargedTokens, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = Build, Task = context.Task, PromptTemplateVersion = item.PromptTemplateVersion!, AnalysisRunId = item.AnalysisRunId, EventSequence = item.EventSequence }),
        observations.Select(item => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), saved.Frame.MatchId, saved.Frame.StateVersion, item.Name, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = item.ResultHash, Model = item.Model, AnalysisRunId = item.AnalysisRunId, RequestId = item.RequestId, ToolCallId = item.ToolCallId, EventSequence = item.EventSequence }));
        var reopened = new AnalysisTraceStore(tracePath); var tools = await reopened.ListToolsAsync(saved.Frame.MatchId);
        Check(await reopened.GetBudgetTotalsAsync(saved.Frame.MatchId) is { RequestCount: 2, ChargedTokens: 60 } && tools.Count == observations.Count &&
            tools.All(item => item.RequestId == usage[0].RequestId && item.AnalysisRunId == usage[0].AnalysisRunId && item.EventSequence == saved.Frame.EventSequence && observations.Any(observed => observed.ResultHash == item.ResultHash)) &&
            !JsonSerializer.Serialize(tools).Contains(Key, StringComparison.Ordinal), "Paid trace settlement was lost on rejection or source linkage changed.");
        var exact = await new SessionStore(saved.Database).GetHistoricalDecisionAsync(saved.Point.Id);
        Check(exact is not null && SnapshotCodec.Serialize(exact.Context.Snapshot) == saved.Original, "The new script rewrote the old exact observed frame.");
        return new(model.Line!, model.Effect!, model.Search, advice, loaded);
    }
    private sealed class Model(Spec spec, bool search) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Line { get; private set; }
        public JsonNode? Effect { get; private set; }
        public JsonNode? Search { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); var payload = JsonNode.Parse(text)!;
            Check(!text.Contains(Private, StringComparison.Ordinal) && !text.Contains(Key, StringComparison.Ordinal), "Actual model HTTP leaked a private identity or credential.");
            var data = new JsonArray(payload["messages"]!.AsArray().Where(item => item?["role"]?.ToString() != "system").Select(item => item!.DeepClone()).ToArray()).ToJsonString();
            Check(!data.Contains("EDR_100t13e", StringComparison.Ordinal) && (!spec.PrivateSource || !data.Contains("121051", StringComparison.Ordinal)),
                "A hidden attachment or private source's donor DBF entered actual model data.");
            var instructions = payload["messages"]!.AsArray().First(item => item?["role"]?.ToString() == "system")!["content"]!.GetValue<string>();
            Check(instructions.Contains("AssembledEffects", StringComparison.Ordinal) && instructions.Contains("不是对局中已发生的事实", StringComparison.Ordinal) &&
                instructions.Contains("不证明候选生成池", StringComparison.Ordinal) && instructions.Contains("仅完整序列Verified", StringComparison.Ordinal) &&
                instructions.Contains("不执行基础牌已被替换的效果", StringComparison.Ordinal), "The actual model prompt lost the typed-operation composition and authority boundaries.");
            object Tool(string id, string name, object args) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
            object message;
            if (Calls == 1)
            {
                var calls = new List<object> { Tool("assembled-effect", "get_entity_effect", new { entity_id = 30 }), Tool("assembled-line", "evaluate_line", new { actions = Plan(spec) }) };
                if (search) calls.Add(Tool("assembled-search", "find_lethal", new { maximum_depth = 6, maximum_nodes = 5000 }));
                message = new { role = "assistant", tool_calls = calls };
            }
            else
            {
                var messages = payload["messages"]!.AsArray();
                JsonNode Read(string id) => JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == id)!["content"]!.GetValue<string>())!;
                Line = Read("assembled-line"); Effect = Read("assembled-effect"); if (search) Search = Read("assembled-search");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "按已观察拼装语义计算。" + Key, reasons = Array.Empty<string>(), alternative = (string?)null,
                    risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions = Search?["VerifiedLethalFound"]?.GetValue<bool>() == true ? Search["Actions"]!.DeepClone() : Plan(spec), verification = "verified", verified_lethal = true }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } },
                usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
