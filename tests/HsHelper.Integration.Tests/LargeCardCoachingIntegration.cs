using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

internal static class LargeCardCoachingIntegration
{
    private const string Build = "255790", FutureBuild = "255791", Native = "CORE_CS2_189";
    private const int HistoryCount = 1203, DonorCount = 601, DonorBase = 2000000;
    private const string Probe = "PRIVATE_BULK_DONOR_PROBE";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "large-card-coaching", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Rules.Tests", "Fixtures", "beast-hunter-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Native fixture lost fixed build.");
        var rows = new JsonArray(fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => JsonNode.Parse(card.GetRawText())).ToArray());
        for (var i = 1; i <= HistoryCount; i++) rows.Add(new JsonObject
        { ["id"] = HistoryId(i), ["dbfId"] = 1000000 + i, ["name"] = "Synthetic public history " + i, ["text"] = "", ["type"] = "MINION", ["cardClass"] = "NEUTRAL", ["collectible"] = false });
        for (var i = 1; i <= DonorCount; i++) rows.Add(new JsonObject
        { ["id"] = DonorId(i), ["dbfId"] = DonorBase + i, ["name"] = "Synthetic donor " + i, ["text"] = $"synthetic trigger {i} & synthetic effect {i}", ["type"] = "MINION", ["collectible"] = false });
        rows.Add(new JsonObject { ["id"] = Probe, ["dbfId"] = DonorBase + 9999, ["name"] = Probe, ["text"] = Probe + " & " + Probe, ["type"] = "MINION" });
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(rows.ToJsonString()))) await catalog.ImportAsync(input, Build, source: "fixture://large-card-coaching");
        var future = (JsonArray)rows.DeepClone();
        foreach (var row in future) { var card = row!.AsObject(); card["name"] = "FUTURE_ONLY_NAME"; card["text"] = "FUTURE_ONLY_TEXT"; }
        future.Add(new JsonObject { ["id"] = "MISSING_CURRENT_SOURCE", ["dbfId"] = 9999999, ["name"] = "Future only", ["text"] = "", ["type"] = "MINION" });
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(future.ToJsonString()))) await catalog.ImportAsync(input, FutureBuild, source: "fixture://future-adversarial-build");

        var engine = Start();
        for (var i = 1; i <= HistoryCount; i++) Feed(engine, [$"FULL_ENTITY - Creating ID={1000 + i} CardID={HistoryId(i)}",
            "tag=CONTROLLER value=1", "tag=ZONE value=GRAVEYARD", "tag=CARDTYPE value=MINION"]);
        Feed(engine, ["PowerProcessor.EndCurrentTaskList() - complete"]);
        // Pick a late actual dictionary position, rather than relying on FrozenDictionary iteration order.
        var sourceId = engine.Snapshot().Entities.Values.Where(entity => entity.Kind == EntityKind.Card).Skip(750).First().Id;
        var native = fixture.RootElement.GetProperty("cards").EnumerateArray().Single(card => card.GetProperty("id").GetString() == Native);
        Feed(engine, [$"CHANGE_ENTITY - Updating Entity={sourceId} CardID={Native}", "tag=CARDTYPE value=MINION",
            $"tag=COST value={native.GetProperty("cost").GetInt32()}", $"tag=ATK value={native.GetProperty("attack").GetInt32()}",
            $"tag=HEALTH value={native.GetProperty("health").GetInt32()}", "tag=DAMAGE value=0", "tag=BATTLECRY value=1",
            $"TAG_CHANGE Entity={sourceId} tag=ZONE value=HAND", "PowerProcessor.EndCurrentTaskList() - complete"]);
        var frame = engine.Snapshot(); var before = SnapshotCodec.Serialize(frame);
        var position = Array.IndexOf(frame.Entities.Values.Select(entity => entity.CardId).OfType<string>().Distinct().ToArray(), Native);
        Check(frame.Quality is { IsComplete: true, IsStable: true, HasLocalPerspective: true } && position >= 500 && frame.Entities.Count > 1000,
            "Parsed fixture did not produce a complete large state with the native source in a late card batch.");
        var observations = new List<CoachToolObservation>(); var coach = Coach(frame, catalog, observations.Add);
        var line = await coach.ExecuteAsync("evaluate_line", Actions(sourceId), CancellationToken.None);
        Check(line["Verification"]?.ToString() == "Verified" && line["AppliedActions"]?.GetValue<int>() == 2 && line["Result"]?["TurnEnded"]?.GetValue<bool>() == true &&
            line["Result"]?["AvailableMana"]?.GetValue<int>() == 9 && Entity(line, 21)["RemainingHealth"]?.GetValue<int>() == 29 &&
            Entity(line, sourceId)["Zone"]?.ToString() == "PLAY" && observations is [{ Success: true, ResultHash.Length: 64 }] &&
            before == SnapshotCodec.Serialize(frame) && !line.ToJsonString().Contains("FUTURE_ONLY", StringComparison.Ordinal),
            "Large host lookup truncated a late native definition, used another build, changed the frame or lost normal play/end-turn results: " + line.ToJsonString());
        pass("parsed 1203-history state loads a late fixed-build native source and preserves exact play damage mana end-turn and trace hash despite a newer active catalog");

        var missingEntities = frame.Entities.ToDictionary(pair => pair.Key, pair => pair.Value);
        missingEntities[sourceId] = missingEntities[sourceId] with { CardId = "MISSING_CURRENT_SOURCE" };
        var missing = frame with { Entities = missingEntities.ToFrozenDictionary() };
        var unavailable = await Coach(missing, catalog).ExecuteAsync("evaluate_line", Actions(sourceId), CancellationToken.None);
        Check(unavailable["AppliedActions"]?.GetValue<int>() == 0 && unavailable["Verification"]?.ToString() is "Unsupported" or "Conditional" &&
            unavailable["IsLethal"]?.GetValue<bool>() == false && unavailable["UnsupportedMechanics"]!.AsArray().Count > 0 &&
            Entity(unavailable, sourceId)["Zone"]?.ToString() == "HAND", "Missing current-build metadata borrowed future data or became a generic batch exception.");
        try { _ = new SnapshotCoach(frame, new JsonObject(), catalog, FutureBuild); throw new Exception("Wrong build accepted."); }
        catch (ArgumentException) { }
        await Throws<ArgumentException>(() => coach.ExecuteAsync("get_cards", new JsonObject
        { ["card_ids"] = new JsonArray(Enumerable.Range(1, 31).Select(i => (JsonNode?)JsonValue.Create(HistoryId(i))).ToArray()) }, CancellationToken.None));
        pass("large host keeps missing source metadata unsupported rejects a foreign build and preserves the explicit 30-card model tool limit");

        var metadata = await catalog.GetByIdsAsync(["FIR_953"], Build);
        var donorEntities = frame.Entities.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (var i = 1; i <= DonorCount; i++) donorEntities[20000 + i] = EffectEntity(20000 + i, DonorBase + i, "PLAY", 1);
        donorEntities[90000] = EffectEntity(90000, DonorBase + 9999, "HAND", 2);
        var donorFrame = frame with { Entities = donorEntities.ToFrozenDictionary() };
        var donorsBefore = SnapshotCodec.Serialize(donorFrame);
        var loaded = await SnapshotCardEffects.LoadDonorsAsync(donorFrame, Build, catalog, metadata);
        var resolved = SnapshotCardEffects.Resolve(donorFrame, Build, donorFrame.Entities[20000 + DonorCount], loaded);
        Check(SnapshotCardEffects.VisibleDonorDbfIds(donorFrame, Build).Count == DonorCount && loaded.ContainsKey(DonorId(DonorCount)) &&
            !loaded.ContainsKey(Probe) && resolved.IsResolved && resolved.EffectCardId == DonorId(DonorCount) &&
            resolved.EffectiveText!.Contains("synthetic effect 601", StringComparison.Ordinal) && !resolved.EffectiveText!.Contains("FUTURE_ONLY", StringComparison.Ordinal) &&
            donorsBefore == SnapshotCodec.Serialize(donorFrame), "Aggregated donor metadata silently stopped at 500, crossed build or exposed a hidden opponent reference.");
        pass("601 visible donor references load the final effect half without truncation future substitution or opponent-hand donor disclosure");

        var unknown = EffectEntity(99000, DonorBase + 8888, "PLAY", 1);
        var unknownResult = SnapshotCardEffects.Resolve(donorFrame, Build, unknown, loaded);
        Check(!unknownResult.IsResolved && unknownResult.EffectiveText is null && unknownResult.Unknowns.Count > 0,
            "A missing donor fell back to base native text.");
        var foreign = donorFrame with { ClientBuild = FutureBuild };
        Check((await SnapshotCardEffects.LoadDonorsAsync(foreign, Build, catalog, metadata)).Count == metadata.Count,
            "Mismatched snapshot build loaded donor evidence anyway.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => SnapshotCardEffects.LoadDonorsAsync(donorFrame, Build, catalog, metadata, cancellation.Token));
        await Throws<OperationCanceledException>(() => coach.ExecuteAsync("evaluate_line", Actions(sourceId), cancellation.Token));
        Check(before == SnapshotCodec.Serialize(frame) && donorsBefore == SnapshotCodec.Serialize(donorFrame), "Cancelled metadata loading mutated an input frame.");
        pass("missing and cross-build donors remain unknown and cancelled large metadata or rule requests return no partial projection");
    }
    private static string HistoryId(int i) => $"BULK_HISTORY_{i:D4}";
    private static string DonorId(int i) => $"BULK_DONOR_{i:D4}";
    private static SnapshotCoach Coach(GameSnapshot frame, CardCatalog catalog, Action<CoachToolObservation>? observe = null)
        => new(frame, CompactAdviceContextBuilder.Build(frame, Build), catalog, Build, toolCompleted: observe);
    private static JsonObject Actions(int source) => new() { ["actions"] = new JsonArray(
        new JsonObject { ["kind"] = "play", ["source_entity_id"] = source, ["target_entity_id"] = 21 },
        new JsonObject { ["kind"] = "end_turn", ["source_entity_id"] = 2 }) };
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]!.GetValue<int>() == id)!;
    private static EntitySnapshot EffectEntity(int id, int donor, string zone, int owner) => new(id, "FIR_953", EntityKind.Card,
        new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = "ENCHANTMENT",
            ["CRAFTABLE_FIRST_HALF_DBID"] = "0", ["CRAFTABLE_SECOND_HALF_DBID"] = donor.ToString() }.ToFrozenDictionary());
    private static PowerLogStateEngine Start()
    {
        var engine = new PowerLogStateEngine(1, "large-card-coaching");
        Feed(engine, [$"GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1",
            "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0", "tag=NUM_CARDS_IN_HAND value=1",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            .. Hero(11, "HERO_05", 1), .. Hero(21, "HERO_08", 2), "TAG_CHANGE Entity=1 tag=TURN value=1",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION"]);
        return engine;
    }
    private static string[] Hero(int id, string card, int owner) => [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}",
        "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=ATK value=0", "tag=IMMUNE value=0"];
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
