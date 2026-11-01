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

internal static class DeathrattleDrawIntegration
{
    private const string Build = "255790", Gift = "EDR_100t4e", Template = "hunter-v23/turn";
    private const string Key = "fixture-deathrattle-draw-key", PrivateCard = "PRIVATE_DEATHRATTLE_DRAW_CARD";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var definitions = new List<JsonElement>();
        foreach (var name in new[] { "beast-hunter", "dark-gift-static", "native-definitions", "deathrattle-draw" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build && fixture.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The deathrattle integration lost its complete fixed primary definitions.");
            definitions.AddRange(fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var cards = definitions.GroupBy(card => card.GetProperty("id").GetString(), StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        Check(cards.Single(card => card.GetProperty("id").GetString() == Gift).GetProperty("dbfId").GetInt32() == 113807,
            "The observed deathrattle gift is not the fixed 113807 definition.");
        var directory = Path.Combine(root, "deathrattle-draw", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);

        var full = await CompleteAsync(await CreateAsync(directory, cards, new()), new());
        AssertDraw(full.Draw, removed: 2, added: 0, burned: 2, empty: 0, damage: 0, hand: 10, deck: 0, fatigue: 2, health: 30, armor: 0);
        Check(full.Draw["SourceEntityId"]?.GetValue<int>() == 30 && full.Draw["SourceCardId"]?.ToString() == "CS2_171" &&
            full.Draw["TriggerEntityId"]?.GetValue<int>() == 30 && full.Draw["EnchantmentEntityId"]?.GetValue<int>() == 50 &&
            full.Draw["EnchantmentCardId"]?.ToString() == Gift, "The direct preview conflated the body with the actual attached trigger.");
        AssertBoundary(full);
        pass("a real recorded PLAY body with ten cards and two deck cards burns both deathrattle draws without inventing a source-leaves-hand vacancy or new identities");

        var oneSpec = new Spec { Hand = 9, Deck = 1, Armor = 2, NumericDeathrattle = true };
        var one = await CompleteAsync(await CreateAsync(directory, cards, oneSpec), oneSpec);
        AssertDraw(one.Draw, 1, 1, 0, 1, 3, 10, 0, 3, 29, 0); AssertBoundary(one);
        pass("nine actual hand cards and one deck card add one then apply fatigue three through armor without subtracting the on-board deathrattle body from hand");

        foreach (var scenario in new[]
        {
            (Spec: new Spec { Deck = 0, Armor = 5 }, Damage: 7L, Attempts: 2L, Fatigue: 4L, Health: 28L, Armor: 0L),
            (Spec: new Spec { Deck = 0, Immune = true }, Damage: 0L, Attempts: 2L, Fatigue: 4L, Health: 30L, Armor: 0L),
            (Spec: new Spec { Deck = 0, Health = 2 }, Damage: 3L, Attempts: 1L, Fatigue: 3L, Health: 0L, Armor: 0L)
        })
        {
            var result = await CompleteAsync(await CreateAsync(directory, cards, scenario.Spec), scenario.Spec);
            AssertDraw(result.Draw, 0, 0, 0, scenario.Attempts, scenario.Damage, 10, 0, scenario.Fatigue, scenario.Health, scenario.Armor);
            Check(result.Draw["MayStopAtHeroDeath"]?.GetValue<bool>() == (scenario.Health == 0), "Fatigue continued after a directly lethal packet or ignored observed immunity.");
            AssertBoundary(result);
        }
        pass("two deathrattle fatigue packets respect current armor immunity and hero death stopping the second packet while the numerical preview remains separate from future actions");

        var uncertain = new Spec { Changes = ["TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_HAND value=bad", "TAG_CHANGE Entity=2 tag=NUM_CARDS_IN_DECK value=bad", "TAG_CHANGE Entity=2 tag=FATIGUE value=bad"] };
        var uncertainResult = await CompleteAsync(await CreateAsync(directory, cards, uncertain), uncertain);
        Check(Exact(uncertainResult.Draw["RequestedDraws"], 2) && !Exact(uncertainResult.Draw["CardsAddedToHand"], 0) &&
            !Exact(uncertainResult.Draw["CardsBurned"], 2) && uncertainResult.Draw["Unknowns"]!.AsArray().Count > 0 &&
            uncertainResult.Draw["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
            "Malformed current counts became the favorable exact scenario or a follow-up hand.");
        AssertBoundary(uncertainResult);
        pass("conflicting or malformed current hand deck and fatigue evidence produces genuine ranges and unknowns rather than default counters or a usable simulated hand");

        var death = await CompleteAsync(await CreateAsync(directory, cards, new Spec { Hand = 9, Deck = 1 }), new Spec { Hand = 9, Deck = 1 });
        AssertBoundary(death);
        var projected = death.Line["Result"]!;
        var inLine = projected["DrawPreviews"]!.AsArray().Single()!;
        Check(inLine["Kind"]?.ToString() == "DeathrattleOrdinary" && Exact(inLine["CardsAddedToHand"], 1) &&
            inLine["EnchantmentEntityId"]?.GetValue<int>() == 50 &&
            Entity(death.Line, 32)["AttacksUsed"]?.GetValue<int>() == 0 && Entity(death.Line, 21)["RemainingHealth"]?.GetValue<int>() == 3 &&
            projected["Entities"]!.AsArray().All(entity => entity!["EntityId"]!.GetValue<int>() < 1000),
            "The actual covered death did not carry its own draw preview or executed the second lethal attack after the boundary.");
        pass("an actual evaluate_line attack kills the attached body and records the same direct draw before stopping the next attacker and final model lethal claim at the observation boundary");

        foreach (var variant in new[] { "dbf", "text", "raw", "build", "attached-alias", "type-alias", "controller-alias" })
        {
            var altered = cards.Select(card => JsonNode.Parse(card.GetRawText())!.AsObject()).ToArray();
            var gift = altered.Single(card => card["id"]?.ToString() == Gift); var spec = new Spec();
            if (variant == "dbf") gift["dbfId"] = 113808;
            if (variant == "text") gift["text"] = "<b>亡语：</b>消灭敌方英雄。";
            if (variant == "raw") gift["mechanics"] = new JsonArray("TRIGGER_VISUAL");
            if (variant == "build") spec = spec with { CardBuild = "255789" };
            if (variant == "attached-alias") spec = spec with { Changes = ["TAG_CHANGE Entity=50 tag=40 value=32"] };
            if (variant == "type-alias") spec = spec with { Changes = ["TAG_CHANGE Entity=50 tag=202 value=5"] };
            if (variant == "controller-alias") spec = spec with { Changes = ["TAG_CHANGE Entity=50 tag=50 value=2"] };
            var result = await CompleteAsync(await CreateAsync(directory, altered.Select(card => JsonSerializer.SerializeToElement(card)).ToArray(), spec), spec);
            Check(result.Draw["ResolvesEffects"]?.GetValue<bool>() != true && !result.Advice.VerifiedLethal && result.Advice.Actions.Count == 0,
                "Bad observed gift metadata build or alias became a native draw or verified action: " + variant);
        }
        pass("same-ID DBF full-text raw-definition version and explicit attachment type or ownership contradictions cannot become verified native draws through the real host");

        foreach (var spec in new[]
        {
            new Spec { BodyZone = "HAND" }, new Spec { BodyOwner = 2 }, new Spec { BodyZone = "DECK" },
            new Spec { ExtraGift = Gift }, new Spec { ExtraGift = "EDR_100t9e" },
            new Spec { Changes = ["TAG_CHANGE Entity=2 tag=371 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=1831 value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=REBORN value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=DEATHRATTLE value=0"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=SILENCED value=1"] },
            new Spec { Changes = ["TAG_CHANGE Entity=30 tag=HEALTH value=bad"] }
        })
        {
            // A private/opposing source is deliberately not proposed as an actionable own attacker.
            var item = await CreateAsync(directory, cards, spec);
            var direct = await new SnapshotCoach(item.Frame, CompactAdviceContextBuilder.Build(item.Frame, spec.CardBuild), item.Catalog, spec.CardBuild)
                .ExecuteAsync("evaluate_deathrattle_draw", new() { ["source_entity_id"] = 30 }, default);
            Check(direct["ResolvesEffects"]?.GetValue<bool>() != true || Exact(direct["RequestedDraws"], 0),
                "An unconfirmed private opposing duplicated reborn suppressed or incomplete body acquired a single native draw: " + JsonSerializer.Serialize(spec));
            Check(!direct.ToJsonString().Contains(PrivateCard, StringComparison.Ordinal), "The rejection response restored a hidden card identity.");
            if (spec.BodyZone == "PLAY" && spec.BodyOwner == 1 && spec.Changes.All(change => !change.Contains("SILENCED", StringComparison.Ordinal) && !change.Contains("DEATHRATTLE value=0", StringComparison.Ordinal)))
            {
                var result = await CompleteAsync(item, spec);
                Check(!result.Advice.VerifiedLethal && result.Advice.Actions.Count == 0, "An unknown death continuation became executable final advice.");
            }
        }
        pass("private or opposing bodies and duplicate deathrattles reborn mixed ongoing attachments suppression and incomplete observations never masquerade as one confirmed draw-two trigger");

        var controlSpec = new Spec { IncludeGift = false };
        var control = await CompleteAsync(await CreateAsync(directory, cards, controlSpec), controlSpec);
        Check(control.Draw["ResolvesEffects"]?.GetValue<bool>() != true && control.Line["Verification"]?.ToString() == "Verified" &&
            control.Line["IsLethal"]?.GetValue<bool>() == true && control.Line["AppliedActions"]?.GetValue<int>() == 2 &&
            control.Line["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == false && control.Line["Result"]?["DrawPreviews"]!.AsArray().Count == 0 &&
            control.Advice.VerifiedLethal && control.Archive.Advice.VerifiedLethal && control.Advice.Actions.Count == 2,
            "The new preview changed an unattached ordinary combat control or inferred a deathrattle from the body's ID.");
        pass("the same recorded board without a deathrattle attachment retains verified ordinary two-attack lethal while schema usage immutable archive policy and trace hashes remain source-bound");
    }

    private sealed record Spec
    {
        public int Hand { get; init; } = 10;
        public int Deck { get; init; } = 2;
        public int Health { get; init; } = 30;
        public int Armor { get; init; }
        public bool Immune { get; init; }
        public bool IncludeGift { get; init; } = true;
        public bool NumericDeathrattle { get; init; }
        public string? ExtraGift { get; init; }
        public string BodyZone { get; init; } = "PLAY";
        public int BodyOwner { get; init; } = 1;
        public string CardBuild { get; init; } = Build;
        public string[] Changes { get; init; } = [];
    }
    private sealed record Saved(string Directory, CardCatalog Catalog, GameSnapshot Frame, DecisionPoint Point, string OriginalHash);
    private sealed record Completed(JsonNode Draw, JsonNode Line, StrategyAdvice Advice, ArchivedAdviceRecord Archive);
    private static JsonArray Actions() => new(new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 40 },
        new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 32, ["target_entity_id"] = 21 });

    private static async Task<Saved> CreateAsync(string directory, JsonElement[] cards, Spec spec)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var catalog = new CardCatalog(Path.Combine(path, "cards.db")); await catalog.InitializeAsync();
        using (var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards)))
            await catalog.ImportAsync(stream, spec.CardBuild, source: "fixture://fixed-deathrattle-draw-integration");
        var frame = Engine(spec).Snapshot(); var original = SnapshotCodec.Serialize(frame);
        Check(frame.Phase == GamePhase.Main && frame.ClientBuild == spec.CardBuild && frame.LocalPlayerId == 1 &&
            !original.Contains(PrivateCard, StringComparison.Ordinal), "The real parser fixture lost its stable perspective or private deck boundary.");
        if (spec.NumericDeathrattle) Check(frame.Entities[30].GetIntTag("DEATHRATTLE") == 1,
            "Actual numeric tag 217 was not normalized into the observed deathrattle evidence.");
        var database = Path.Combine(path, "session.db"); var store = new SessionStore(database); await store.InitializeAsync();
        var recorder = new SessionRecorder(store, new(null, spec.CardBuild, 1)); recorder.Observe(frame, 1); await recorder.CompleteAsync();
        var point = (await store.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        var reopened = await new SessionStore(database).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact recorded deathrattle frame.");
        Check(SnapshotCodec.Serialize(reopened.Context.Snapshot) == original && !reopened.HasUnattributedEvents && reopened.Point == point,
            "Recording or reopening changed the observed attachment or its exact event boundary.");
        return new(path, catalog, reopened.Context.Snapshot, point, Hash(original));
    }

    private static async Task<Completed> CompleteAsync(Saved item, Spec spec)
    {
        var before = SnapshotCodec.Serialize(item.Frame);
        var cards = await item.Catalog.GetByIdsBatchedAsync(item.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), spec.CardBuild);
        var context = CompactAdviceContextBuilder.Create(item.Frame, spec.CardBuild, AdviceTaskKind.Turn, cards: cards)
            with { KnowledgeAsOf = item.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        Check(context.TemplateVersion == Template && context.VisibilityPolicy == "attachments-v1", "The actual template or attachment provenance stamp changed.");
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(item.Frame, context.Snapshot, item.Catalog, spec.CardBuild, toolCompleted: observations.Add);
        var definition = coach.Capabilities().Tools.Single(tool => tool.Name == "evaluate_deathrattle_draw");
        Check(definition.ValidateArguments(new() { ["source_entity_id"] = 30 }) &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 30, ["enchantment_entity_id"] = 50 }) &&
            !definition.ValidateArguments(new() { ["source_entity_id"] = 0 }), "The new tool accepts invented enchantment selectors or an invalid body ID.");
        using var model = new Model(); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://deathrattle-draw-fixture.invalid/v1/chat/completions"), "deathrattle-fixture", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        Check(model.Calls == 2 && observations.Count == 2 && observations.All(tool => tool.Success && tool.ResultHash == Hash(tool.ResultJson!) &&
            tool.RequestId == usages[0].RequestId && tool.AnalysisRunId == advice.AnalysisRunId && tool.EventSequence == item.Frame.EventSequence) &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30 && usage.CardBuild == spec.CardBuild && usage.PromptTemplateVersion == Template) &&
            budget.Snapshot(item.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            advice.RequestIds.SequenceEqual(usages.Select(usage => usage.RequestId)) && advice.KnowledgeAsOf == item.Point.RecordedAt &&
            before == SnapshotCodec.Serialize(item.Frame) && Hash(before) == item.OriginalHash &&
            model.Payloads.All(payload => !payload.Contains(PrivateCard, StringComparison.Ordinal)),
            "The real tool HTTP host chain lost source-bound usage hashes privacy or immutable snapshot content.");
        var draft = AdviceArchiveStore.Prepare(new(item.Frame.MatchId, item.Frame.StateVersion, item.Frame.EventSequence, spec.CardBuild, advice.TemplateVersion!, advice.Summary)
        {
            Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
            Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = observations.Select(tool => new AdviceToolEvidence(tool.Name, "直接亡语数量预览；未生成身份、实体、费用或后续动作。", tool.ResultHash)).ToArray(),
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
            HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
            HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
        }, [Key]);
        var archivePath = Path.Combine(item.Directory, "advice.db"); var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync(); var saved = await archive.SaveAsync(draft);
        var loaded = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing source-bound deathrattle advice.");
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) && loaded.Advice.VisibilityPolicy == "attachments-v1" &&
            loaded.Advice.KnowledgeAsOf == item.Point.RecordedAt && loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) &&
            loaded.Advice.Evidence.Select(tool => tool.ResultHash).SequenceEqual(observations.Select(tool => tool.ResultHash)) &&
            !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal), "Archive preparation changed limits source policy redaction or exact tool hashes.");
        var tracePath = Path.Combine(item.Directory, "trace.db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usages.Select(usage => new ModelRequestTrace(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model, usage.Round,
            usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = spec.CardBuild, Task = context.Task, PromptTemplateVersion = usage.PromptTemplateVersion!, AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence }),
        observations.Select(tool => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), item.Frame.MatchId, item.Frame.StateVersion, tool.Name, tool.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = tool.ResultHash, Model = tool.Model, AnalysisRunId = tool.AnalysisRunId, RequestId = tool.RequestId, ToolCallId = tool.ToolCallId, EventSequence = tool.EventSequence }));
        var reopenedTrace = new AnalysisTraceStore(tracePath); var totals = await reopenedTrace.GetBudgetTotalsAsync(item.Frame.MatchId); var tools = await reopenedTrace.ListToolsAsync(item.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && tools.Count == 2 && tools.All(tool => tool.AnalysisRunId == advice.AnalysisRunId &&
            tool.RequestId == usages[0].RequestId && tool.EventSequence == item.Frame.EventSequence) && !JsonSerializer.Serialize(tools).Contains(Key, StringComparison.Ordinal),
            "Trace reopening lost paid request linkage or stored a credential instead of bounded evidence.");
        var storedAgain = await new SessionStore(Path.Combine(item.Directory, "session.db")).GetHistoricalDecisionAsync(item.Point.Id);
        Check(storedAgain is not null && storedAgain.Point == item.Point && Hash(SnapshotCodec.Serialize(storedAgain.Context.Snapshot)) == item.OriginalHash &&
            model.Line!["Result"]!["Entities"]!.AsArray().All(entity => item.Frame.Entities.ContainsKey(entity!["EntityId"]!.GetValue<int>())),
            "A direct preview rewrote the recorded source or created an unobserved entity.");
        if (model.Line!["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == true)
            Check(Entity(model.Line, 11)["RemainingHealth"]?.GetValue<int>() == spec.Health && Entity(model.Line, 11)["Armor"]?.GetValue<int>() == spec.Armor &&
                model.Line["Result"]!["Entities"]!.AsArray().Count(entity => entity?["ControllerId"]?.GetValue<int>() == 1 && entity?["Zone"]?.ToString() == "HAND") == spec.Hand &&
                !model.Line["Result"]!["Entities"]!.AsArray().Any(entity => entity?["Zone"]?.ToString() == "DECK"),
                "The isolated draw/fatigue ranges were incorrectly applied as an authoritative post-draw entity state.");
        return new(model.Draw!, model.Line!, advice, loaded);
    }

    private static void AssertDraw(JsonNode result, long removed, long added, long burned, long empty, long damage, long hand, long deck, long fatigue, long health, long armor)
    {
        Check(result["Kind"]?.ToString() == "DeathrattleOrdinary" && result["ResolvesEffects"]?.GetValue<bool>() == true &&
            Exact(result["RequestedDraws"], 2) && Exact(result["CardsRemovedFromDeck"], removed) && Exact(result["CardsAddedToHand"], added) &&
            Exact(result["CardsBurned"], burned) && Exact(result["EmptyDeckAttempts"], empty) && Exact(result["FatigueDamage"], damage) &&
            Exact(result["HandCountAfter"], hand) && Exact(result["DeckCountAfter"], deck) && Exact(result["FatigueAfter"], fatigue) &&
            Exact(result["HeroHealthAfter"], health) && Exact(result["HeroArmorAfter"], armor) &&
            result["IsDirectEffectOnly"]?.GetValue<bool>() == true && result["CanEvaluateFollowupActions"]?.GetValue<bool>() == false,
            "The direct deathrattle draw counts differ from actual hand deck and fatigue evidence: " + result.ToJsonString());
    }
    private static void AssertBoundary(Completed result) => Check(result.Line["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == true &&
        result.Line["AppliedActions"]?.GetValue<int>() == 1 && result.Line["Verification"]?.ToString() != "Verified" &&
        result.Line["IsLethal"]?.GetValue<bool>() == false && result.Advice.Actions.Count == 0 && !result.Advice.VerifiedLethal &&
        result.Archive.Advice.Actions.Count == 0 && !result.Archive.Advice.VerifiedLethal && result.Archive.Advice.HostUnsupportedMechanics?.Count > 0,
        "A direct draw preview became an authoritative future hand or verified lethal action sequence.");
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity!["EntityId"]!.GetValue<int>() == id)!;
    private static bool Exact(JsonNode? range, long value) => range?["Minimum"]?.GetValue<long>() == value && range?["Maximum"]?.GetValue<long>() == value;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static PowerLogStateEngine Engine(Spec spec)
    {
        var engine = new PowerLogStateEngine(1, "deathrattle-draw-" + Guid.NewGuid().ToString("N"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        void Entity(int id, string card, int owner, string zone, params string[] tags)
        {
            Feed("FULL_ENTITY - Creating ID=" + id + " CardID=" + card, "tag=CONTROLLER value=" + owner, "tag=ZONE value=" + zone);
            foreach (var tag in tags) Feed("tag=" + tag.Replace("=", " value=", StringComparison.Ordinal));
        }
        Feed("GameState.DebugPrintGame() - BuildNumber=" + spec.CardBuild + " FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=5", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            "tag=NUM_CARDS_IN_HAND value=" + spec.Hand, "tag=NUM_CARDS_IN_DECK value=" + spec.Deck, "tag=MAXHANDSIZE value=10", "tag=FATIGUE value=2",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Entity(11, "HERO_05", 1, "PLAY", "CARDTYPE=HERO", "ATK=0", "HEALTH=30", "DAMAGE=" + (30 - spec.Health), "ARMOR=" + spec.Armor, "IMMUNE=" + (spec.Immune ? 1 : 0));
        Entity(21, "HERO_08", 2, "PLAY", "CARDTYPE=HERO", "ATK=0", "HEALTH=3", "DAMAGE=0", "ARMOR=0", "IMMUNE=0");
        Entity(30, "CS2_171", spec.BodyOwner, spec.BodyZone, "CARDTYPE=MINION", "CARDRACE=BEAST", "ATK=1", "HEALTH=1", "DAMAGE=0", "COST=1",
            (spec.NumericDeathrattle ? "217=" : "DEATHRATTLE=") + (spec.IncludeGift ? 1 : 0), "EXHAUSTED=0", "NUM_TURNS_IN_PLAY=1");
        Entity(32, "CS2_171", 1, "PLAY", "CARDTYPE=MINION", "CARDRACE=BEAST", "ATK=3", "HEALTH=1", "DAMAGE=0", "COST=1", "EXHAUSTED=0", "NUM_TURNS_IN_PLAY=1");
        Entity(40, "CS2_171", 2, "PLAY", "CARDTYPE=MINION", "CARDRACE=BEAST", "ATK=1", "HEALTH=10", "DAMAGE=0", "COST=1", "EXHAUSTED=0", "NUM_TURNS_IN_PLAY=1");
        if (spec.IncludeGift) Entity(50, Gift, spec.BodyOwner, "PLAY", "CARDTYPE=ENCHANTMENT", "ATTACHED=30");
        if (spec.ExtraGift is not null) Entity(51, spec.ExtraGift, spec.BodyOwner, "PLAY", "CARDTYPE=ENCHANTMENT", "ATTACHED=30");
        for (var index = 0; index < spec.Hand; index++) Entity(100 + index, "", 1, "HAND");
        for (var index = 0; index < spec.Deck; index++) Entity(200 + index, PrivateCard, 1, "DECK");
        foreach (var change in spec.Changes) Feed(change);
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }

    private sealed class Model : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Draw { get; private set; }
        public JsonNode? Line { get; private set; }
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); Payloads.Add(text); var payload = JsonNode.Parse(text)!; object message;
            object Tool(string id, string name, JsonObject arguments) => new { id, type = "function", function = new { name, arguments = arguments.ToJsonString() } };
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[]
            {
                Tool("deathrattle-draw", "evaluate_deathrattle_draw", new() { ["source_entity_id"] = 30 }),
                Tool("deathrattle-line", "evaluate_line", new() { ["actions"] = Actions() })
            } };
            else
            {
                var messages = payload["messages"]!.AsArray();
                Draw = JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == "deathrattle-draw")!["content"]!.GetValue<string>())!;
                Line = JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == "deathrattle-line")!["content"]!.GetValue<string>())!;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new
                {
                    summary = "直接亡语风险是条件预览。" + Key, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                    assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions = Actions(), verified_lethal = true, verification = "verified", HostUnsupportedMechanics = Array.Empty<string>()
                }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
}
