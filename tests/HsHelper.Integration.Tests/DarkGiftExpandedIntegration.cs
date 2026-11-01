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

internal static class DarkGiftExpandedIntegration
{
    private const string Build = "255790", Template = "hunter-v20/turn";
    private const string Key = "fixture-expanded-gift-key", PrivateCard = "PRIVATE_EXPANDED_GIFT_ID";
    private static readonly IReadOnlyDictionary<string, string> Coverage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["EDR_100t1e"] = "elusive", ["EDR_100t2e"] = "short-claws", ["EDR_100t3e"] = "taunt",
        ["EDR_100t8e1"] = "sweet-dreams", ["EDR_100t13e"] = "shield-windfury", ["EDR_100t8e"] = "shuffled-stats",
        ["EDR_100t5e2"] = "copy-stats", ["EDR_100t5e5"] = "copy-stats"
    };

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var fixtures = Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures");
        var definitions = new List<JsonElement>();
        foreach (var name in new[] { "beast-hunter", "dark-gift-static", "lethal-search", "quest-progress" })
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, name + "-255790.json")));
            Check(document.RootElement.GetProperty("build").GetString() == Build && document.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The expanded integration lost its pinned public source definitions.");
            definitions.AddRange(document.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var cards = definitions.GroupBy(card => card.GetProperty("id").GetString(), StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        Check(Coverage.Keys.All(id => cards.Any(card => card.GetProperty("id").GetString() == id)), "An audited gift is missing its complete public fixture record.");
        var directory = Path.Combine(root, "dark-gift-expanded", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);

        var elusive = Board("EDR_100t1e", 7, 4, 1, "1211=1");
        var elusiveItem = await CreateAsync(directory, cards, elusive);
        Check(elusiveItem.Frame.Entities[30].GetIntTag("ELUSIVE") == 1, "The real parser did not normalize actual ELUSIVE tag 1211.");
        var elusiveResult = await CompleteAsync(elusiveItem, elusive);
        AssertPositive(elusiveResult, elusive, lethal: true);
        await AssertControlAsync(directory, cards, elusive, elusiveResult);
        var spellOnElusive = elusive with { Search = false, Actions = Actions(("play", 31, 30)), Arcane = true };
        await CompleteAsync(await CreateAsync(directory, cards, spellOnElusive), spellOnElusive, hardReject: true);
        foreach (var replacement in new[] { "311=1", "332=1" })
        {
            var wrongKeyword = elusive with { Flags = [replacement], Search = false };
            AssertUnverified(await CompleteAsync(await CreateAsync(directory, cards, wrongKeyword), wrongKeyword), "A narrower targeting restriction substituted for Elusive.");
        }
        pass("actual parser ELUSIVE 1211 survives recording search host verification and archive while spell targeting is rejected and 311 or 332 never stand in for the gift keyword");

        var claws = new Spec("EDR_100t2e", 3, 2, 0, [], Actions(("play", 30, null), ("attack", 30, 40))) { InHand = true };
        var clawsResult = await CompleteAsync(await CreateAsync(directory, cards, claws), claws);
        AssertPositive(clawsResult, claws, lethal: false);
        Check(Entity(clawsResult.Line!, 30)["Attack"]?.GetValue<int>() == 3 && Entity(clawsResult.Line!, 30)["Cost"]?.GetValue<int>() == 0 &&
            Entity(clawsResult.Line!, 40)["RemainingHealth"]?.GetValue<int>() == 17 && clawsResult.Line!["Result"]?["AvailableMana"]?.GetValue<int>() == 5,
            "Short Claws reapplied its printed attack or cost reduction instead of using current observed values.");
        await AssertControlAsync(directory, cards, claws, clawsResult);
        pass("current zero cost and attack three Short Claws plays then attacks through the real model host archive chain without charging mana or subtracting either value again");

        var taunt = new Spec("EDR_100t3e", 5, 6, 1, ["190=1"], Actions(("attack", 30, 40))) { GiftOnEnemy = true };
        var tauntResult = await CompleteAsync(await CreateAsync(directory, cards, taunt), taunt);
        AssertPositive(tauntResult, taunt, lethal: false);
        Check(Entity(tauntResult.Line!, 40)["MaximumHealth"]?.GetValue<int>() == 6 && Entity(tauntResult.Line!, 40)["RemainingHealth"]?.GetValue<int>() == 1 &&
            Entity(tauntResult.Line!, 30)["RemainingHealth"]?.GetValue<int>() == 15, "Taunt reapplied health or changed ordinary retaliation.");
        await AssertControlAsync(directory, cards, taunt, tauntResult);
        var skipTaunt = taunt with { Actions = Actions(("attack", 30, 21)) };
        await CompleteAsync(await CreateAsync(directory, cards, skipTaunt), skipTaunt, hardReject: true);
        var tauntDeath = new Spec("EDR_100t3e", 1, 5, 1, ["TAUNT=1"], Actions(("attack", 30, 40), ("attack", 32, 21)))
        { GiftOnEnemy = true, DeathFollowup = true, EnemyHealth = 3 };
        var deathResult = await CompleteAsync(await CreateAsync(directory, cards, tauntDeath), tauntDeath);
        AssertPositive(deathResult, tauntDeath, lethal: true);
        Check(Entity(deathResult.Line!, 30)["RemainingHealth"]?.GetValue<int>() == 1 &&
            Entity(deathResult.Line!, 32)["AttacksUsed"]?.GetValue<int>() == 1 &&
            !deathResult.Line!["Result"]!["Entities"]!.AsArray().Any(entity => entity?["EntityId"]?.GetValue<int>() == 40),
            "A previously audited static source outlived its covered target death and downgraded the second attack.");
        await AssertControlAsync(directory, cards, tauntDeath, deathResult);
        var ongoingOnDeadTarget = tauntDeath with { ExtraGift = "EDR_100t4e" };
        var ongoingDeath = await CompleteAsync(await CreateAsync(directory, cards, ongoingOnDeadTarget), ongoingOnDeadTarget);
        AssertUnverified(ongoingDeath, "An unknown ongoing attachment was retired with an audited static source.");
        Check(Entity(ongoingDeath.Line!, 51)["Zone"]?.ToString() == "PLAY" && Entity(ongoingDeath.Line!, 51)["IsAlive"]?.GetValue<bool>() == true &&
            ongoingDeath.Archive!.Advice.HostUnsupportedMechanics?.Contains("UNMAPPED_ATTACHED_ENCHANTMENT") == true,
            "Target death erased the still-uncovered attachment or its archived host boundary.");
        var initiallyDead = tauntDeath with { TargetInitiallyDead = true, Actions = Actions(("attack", 32, 21)) };
        AssertUnverified(await CompleteAsync(await CreateAsync(directory, cards, initiallyDead), initiallyDead),
            "A stale source attached to an initially dead target was accepted without an earlier in-runtime audit.");
        pass("actual enemy Taunt constrains targeting and its audited static attachment expires only after a covered death before the next attacker while ongoing or initially stale sources retain their boundary");

        foreach (var spec in new[]
        {
            Board("EDR_100t8e1", 9, 7, 1), Board("EDR_100t8e", 4, 8, 3),
            Board("EDR_100t5e2", 1, 1, 1), Board("EDR_100t5e5", 2, 2, 1)
        })
        {
            var result = await CompleteAsync(await CreateAsync(directory, cards, spec), spec);
            AssertPositive(result, spec, lethal: true);
            Check(Entity(result.Line!, 30)["Attack"]?.GetValue<int>() == spec.Attack && Entity(result.Line!, 30)["MaximumHealth"]?.GetValue<int>() == spec.Health &&
                Entity(result.Line!, 30)["Cost"]?.GetValue<int>() == spec.Cost &&
                result.Line!["Result"]!["Entities"]!.AsArray().Where(entity => entity?["Type"]?.ToString() == "MINION").All(entity => entity!["EntityId"]!.GetValue<int>() == 30),
                "An observed stat-only attachment rerolled, added stats, or manufactured a copy entity.");
            await AssertControlAsync(directory, cards, spec, result);
        }
        pass("all four exact settled stat attachments reach actual find_lethal and final persisted host validation using only the existing entity and observed values without shuffling copying or reapplying bonuses");

        var shieldWind = new Spec("EDR_100t13e", 5, 2, 1, ["189=1", "194=1"], Actions(("attack", 30, 40), ("attack", 30, 21))) { EnemyHealth = 5 };
        var shieldItem = await CreateAsync(directory, cards, shieldWind);
        var first = await EvaluateAsync(shieldItem, shieldWind with { Actions = Actions(("attack", 30, 40)) });
        Check(first["Verification"]?.ToString() == "Verified" && Entity(first, 30)["Flags"]?["DIVINE_SHIELD"]?.GetValue<int>() == 0 &&
            Entity(first, 30)["RemainingHealth"]?.GetValue<int>() == 2 && Entity(first, 30)["AttacksUsed"]?.GetValue<int>() == 1,
            "The first hit did not consume the observed shield once while preserving the current body.");
        var shieldResult = await CompleteAsync(shieldItem, shieldWind);
        AssertPositive(shieldResult, shieldWind, lethal: true);
        Check(Entity(shieldResult.Line!, 30)["Flags"]?["DIVINE_SHIELD"]?.GetValue<int>() == 0 && Entity(shieldResult.Line!, 30)["Flags"]?["WINDFURY"]?.GetValue<int>() == 1 &&
            Entity(shieldResult.Line!, 30)["RemainingHealth"]?.GetValue<int>() == 2 && Entity(shieldResult.Line!, 30)["AttacksUsed"]?.GetValue<int>() == 2,
            "The second attack regranted the spent shield or lost the original Windfury audit.");
        await AssertControlAsync(directory, cards, shieldWind, shieldResult);
        var spent = shieldWind with { Flags = ["WINDFURY=1", "DIVINE_SHIELD=0"] };
        var spentResult = await CompleteAsync(await CreateAsync(directory, cards, spent), spent);
        AssertPositive(spentResult, spent, lethal: true);
        Check(Entity(spentResult.Line!, 30)["RemainingHealth"]?.GetValue<int>() == 1 && Entity(spentResult.Line!, 30)["Flags"]?["DIVINE_SHIELD"]?.GetValue<int>() == 0,
            "An originally spent shield was regranted from the gift's printed description.");
        var multipleStatic = shieldWind with { Attack = 9, Health = 7, EnemyHealth = 9, ExtraGift = "EDR_100t8e1" };
        var multipleResult = await CompleteAsync(await CreateAsync(directory, cards, multipleStatic), multipleStatic);
        AssertPositive(multipleResult, multipleStatic, lethal: true);
        Check(HasCoverage(multipleResult.Line!, "EDR_100t8e1") && Entity(multipleResult.Line!, 30)["Attack"]?.GetValue<int>() == 9 &&
            Entity(multipleResult.Line!, 30)["MaximumHealth"]?.GetValue<int>() == 7 && Entity(multipleResult.Line!, 30)["RemainingHealth"]?.GetValue<int>() == 7,
            "Multiple fully audited static sources duplicated their aggregated current values or became unknown without cause.");
        pass("Shield Windfury survives shield consumption across two real attacks including original shield zero and two audited same-target gifts without regranting shield or adding aggregated stats twice");

        foreach (var gift in new[] { "EDR_100t4e", "EDR_100t5e", "EDR_100t7e", "EDR_100t7e1", "EDR_100t9e", "EDR_100t10" })
        {
            var ongoing = Board(gift, 5, 2, 1) with { Search = false };
            AssertUnverified(await CompleteAsync(await CreateAsync(directory, cards, ongoing), ongoing), "Ongoing gift was accepted as a static observation: " + gift);
        }
        var mixed = elusive with { ExtraGift = "EDR_100t5e", Search = false };
        var mixedResult = await CompleteAsync(await CreateAsync(directory, cards, mixed), mixed);
        AssertUnverified(mixedResult, "An unknown same-target source was erased by a known static gift.");
        Check(mixedResult.Archive!.Advice.HostUnsupportedMechanics?.Contains("UNMAPPED_ATTACHED_ENCHANTMENT") == true,
            "The partial line's unsupported attachment disappeared from the reopened archive.");
        pass("ongoing deathrattle copy repeat battlecry reborn and three-hit effects plus mixed unknown attachments remain host limits rather than fabricated executable or lethal advice");

        foreach (var variant in new[] { "gift-dbf", "gift-text", "gift-raw", "target-dbf", "source-owner", "attached", "source-type", "missing-attack", "missing-health", "missing-damage", "missing-elusive", "malformed-source-flag" })
        {
            var modified = cards.Select(card => JsonNode.Parse(card.GetRawText())!.AsObject()).ToArray();
            var spec = elusive with { Search = false };
            if (variant == "gift-dbf") modified.Single(card => card["id"]?.ToString() == spec.Gift)["dbfId"] = 113805;
            if (variant == "gift-text") modified.Single(card => card["id"]?.ToString() == spec.Gift)["text"] = "未审计的额外动作。";
            if (variant == "gift-raw") modified.Single(card => card["id"]?.ToString() == spec.Gift)["mechanics"] = new JsonArray("TRIGGER_VISUAL");
            if (variant == "target-dbf") modified.Single(card => card["id"]?.ToString() == "EDR_227")["dbfId"] = 114021;
            if (variant == "source-owner") spec = spec with { Changes = ["TAG_CHANGE Entity=50 tag=CONTROLLER value=2"] };
            if (variant == "attached") spec = spec with { UnresolvedAttachment = true, Changes = ["TAG_CHANGE Entity=50 tag=ATTACHED value=999"] };
            if (variant == "source-type") spec = spec with { Changes = ["TAG_CHANGE Entity=50 tag=CARDTYPE value=SPELL"] };
            if (variant == "missing-attack") spec = spec with { Omit = ["ATK"] };
            if (variant == "missing-health") spec = spec with { Omit = ["HEALTH"] };
            if (variant == "missing-damage") spec = spec with { Omit = ["DAMAGE"] };
            if (variant == "missing-elusive") spec = spec with { Flags = [] };
            if (variant == "malformed-source-flag") spec = spec with { Changes = ["TAG_CHANGE Entity=50 tag=DEATHRATTLE value=bad"] };
            var imported = modified.Select(card => JsonSerializer.SerializeToElement(card)).ToArray();
            AssertUnverified(await CompleteAsync(await CreateAsync(directory, imported, spec), spec), "A source definition binding or current observation gap was promoted: " + variant);
        }
        pass("real catalog metadata corruption attachment ownership type conflicts and missing current observations cannot upgrade a numerical prefix or forged model verdict into a verified archive");
    }

    private sealed record Spec(string Gift, int Attack, int Health, int Cost, string[] Flags, JsonArray Actions)
    {
        public bool InHand { get; init; }
        public bool Search { get; init; }
        public bool GiftOnEnemy { get; init; }
        public bool Arcane { get; init; }
        public bool DeathFollowup { get; init; }
        public bool TargetInitiallyDead { get; init; }
        public bool IncludeGift { get; init; } = true;
        public bool UnresolvedAttachment { get; init; }
        public int EnemyHealth { get; init; } = 30;
        public string? ExtraGift { get; init; }
        public string[] Omit { get; init; } = [];
        public string[] Changes { get; init; } = [];
    }
    private static Spec Board(string gift, int attack, int health, int cost, params string[] flags) =>
        new(gift, attack, health, cost, flags, Actions(("attack", 30, 21))) { EnemyHealth = attack, Search = true };
    private sealed record Saved(string Directory, CardCatalog Catalog, GameSnapshot Frame, DecisionPoint Point);
    private sealed record Completed(JsonNode Tool, JsonNode? Line, StrategyAdvice? Advice, ArchivedAdviceRecord? Archive);

    private static async Task<Saved> CreateAsync(string directory, JsonElement[] cards, Spec spec)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var catalog = new CardCatalog(Path.Combine(path, "cards.db")); await catalog.InitializeAsync();
        using (var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards))) await catalog.ImportAsync(stream, Build, source: "fixture://fixed-expanded-static-gifts");
        var frame = Engine(spec).Snapshot();
        Check(frame.Quality.HasLocalPerspective && frame.Quality.IsStable == !spec.UnresolvedAttachment && frame.Quality.IsComplete == !spec.UnresolvedAttachment &&
            (!spec.UnresolvedAttachment || !frame.Entities.ContainsKey(50) && frame.Quality.Warnings.Any(warning => warning.Contains("关联目标未确认", StringComparison.Ordinal))) && frame.ClientBuild == Build && frame.Entities[60].CardId is null &&
            !SnapshotCodec.Serialize(frame).Contains(PrivateCard, StringComparison.Ordinal), "The real parser fixture lost its stable privacy boundary.");
        var database = Path.Combine(path, "history.db"); var store = new SessionStore(database); await store.InitializeAsync();
        var recorder = new SessionRecorder(store, new(null, Build, 1)); recorder.Observe(frame, 1); await recorder.CompleteAsync();
        var point = (await store.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        var reopened = new SessionStore(database); await reopened.InitializeAsync();
        var exact = await reopened.GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing recorded gift frame.");
        Check(exact.Point == point && exact.Context.CardBuild == Build && !exact.HasUnattributedEvents && SnapshotCodec.Serialize(exact.Context.Snapshot) == SnapshotCodec.Serialize(frame),
            "Reopening changed the exact current static effect or decision boundary.");
        return new(path, catalog, exact.Context.Snapshot, point);
    }

    private static async Task<JsonNode> EvaluateAsync(Saved item, Spec spec) => await new SnapshotCoach(item.Frame,
        CompactAdviceContextBuilder.Build(item.Frame, Build), item.Catalog, Build).ExecuteAsync("evaluate_line", new() { ["actions"] = spec.Actions.DeepClone() }, default);

    private static async Task AssertControlAsync(string directory, JsonElement[] cards, Spec spec, Completed result)
    {
        var controlItem = await CreateAsync(directory, cards, spec with { IncludeGift = false, ExtraGift = null });
        var control = await EvaluateAsync(controlItem, spec);
        var observed = result.Line!["Result"]!.DeepClone(); var entities = observed["Entities"]!.AsArray();
        foreach (var entity in entities.Where(entity => entity?["EntityId"]?.GetValue<int>() is 50 or 51).ToArray()) entities.Remove(entity);
        Check(control["Verification"]?.ToString() == "Verified" && JsonNode.DeepEquals(observed, control["Result"]) && !HasCoverage(control, spec.Gift),
            "An audited attachment changed current direct values relative to the same observed-state control: " + spec.Gift);
    }

    private static async Task<Completed> CompleteAsync(Saved item, Spec spec, bool hardReject = false)
    {
        var before = SnapshotCodec.Serialize(item.Frame);
        var definitions = await item.Catalog.GetByIdsBatchedAsync(item.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(item.Frame, Build, AdviceTaskKind.Turn, cards: definitions)
            with { KnowledgeAsOf = item.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(item.Frame, context.Snapshot, item.Catalog, Build, toolCompleted: observations.Add);
        using var model = new Model(spec); using var http = new HttpClient(model);
        StrategyAdvice? advice = null; var denied = false;
        try
        {
            advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://expanded-gift-fixture.invalid/v1/chat/completions"), "expanded-gift-fixture", 1),
                Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        }
        catch (InvalidDataException) when (hardReject) { denied = true; }
        var tool = observations.Single();
        Check(model.Calls == 2 && model.Result is not null && denied == hardReject &&
            tool is { Success: true, ResultHash.Length: 64, ToolCallId: "expanded-gift-tool", RequestId: not null } && tool.Name == (spec.Search ? "find_lethal" : "evaluate_line") &&
            tool.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.Result!.ToJsonString()))) &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30 && usage.PromptTemplateVersion == Template && usage.CardBuild == Build) &&
            tool.AnalysisRunId == usages[0].AnalysisRunId && tool.RequestId == usages[0].RequestId && tool.EventSequence == item.Frame.EventSequence &&
            budget.Snapshot(item.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            before == SnapshotCodec.Serialize(item.Frame) && !model.Payloads.Any(payload => payload.Contains(PrivateCard, StringComparison.Ordinal)),
            "The actual HTTP tool invocation lost immutable source evidence, privacy, or charged usage: " + spec.Gift);
        var archivePath = Path.Combine(item.Directory, "advice.db"); ArchivedAdviceRecord? archived = null;
        if (hardReject) Check(advice is null && !File.Exists(archivePath), "A hard-invalid proposal produced final or archived advice.");
        else
        {
            Check(advice is not null && advice.TemplateVersion == Template && advice.RequestIds.SequenceEqual(usages.Select(usage => usage.RequestId)) &&
                advice.AnalysisRunId == tool.AnalysisRunId, "The final answer lost actual request lineage.");
            var draft = AdviceArchiveStore.Prepare(new(item.Frame.MatchId, item.Frame.StateVersion, item.Frame.EventSequence, Build, advice!.TemplateVersion!, advice.Summary)
            {
                Task = context.Task, Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
                Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
                Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
                Evidence = [new(tool.Name, "已观察静态赠福；以固定快照的主机验证为准，不代表重新授予或创建。", tool.ResultHash)],
                Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
                LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
                HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
                HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
            }, [Key]);
            var store = new AdviceArchiveStore(archivePath); await store.InitializeAsync(); var saved = await store.SaveAsync(draft);
            archived = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing expanded gift advice.");
            Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(archived.Advice) && archived.Advice.Verification == advice.Verification &&
                archived.Advice.VerifiedLethal == advice.VerifiedLethal && archived.Advice.KnowledgeAsOf == item.Point.RecordedAt &&
                archived.Advice.RequestIds!.SequenceEqual(advice.RequestIds) && archived.Advice.Evidence.Single().ResultHash == tool.ResultHash &&
                archived.Advice.MatchId == item.Frame.MatchId && archived.Advice.StateVersion == item.Frame.StateVersion && archived.Advice.EventSequence == item.Frame.EventSequence &&
                archived.Advice.CardBuild == Build && archived.Advice.AnalysisRunId == advice.AnalysisRunId && !JsonSerializer.Serialize(archived).Contains(Key, StringComparison.Ordinal),
                "Preparation or reopening changed source-bound host verification, limits, redaction, or tool evidence.");
        }
        var tracePath = Path.Combine(item.Directory, "trace.db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usages.Select(usage => new ModelRequestTrace(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model,
            usage.Round, usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = Build, Task = context.Task, PromptTemplateVersion = usage.PromptTemplateVersion!, AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence }),
        [new(Guid.NewGuid().ToString("N"), item.Frame.MatchId, item.Frame.StateVersion, tool.Name, tool.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = tool.ResultHash, Model = tool.Model, AnalysisRunId = tool.AnalysisRunId, RequestId = tool.RequestId, ToolCallId = tool.ToolCallId, EventSequence = tool.EventSequence }]);
        var reopenedTrace = new AnalysisTraceStore(tracePath); var totals = await reopenedTrace.GetBudgetTotalsAsync(item.Frame.MatchId);
        var requests = await reopenedTrace.ListRequestsAsync(item.Frame.MatchId); var tools = await reopenedTrace.ListToolsAsync(item.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && requests.Count == 2 && requests.All(request => request.AnalysisRunId == tool.AnalysisRunId &&
            request.CardBuild == Build && request.EventSequence == item.Frame.EventSequence && request.PromptTemplateVersion == Template) &&
            tools.Single().ResultHash == tool.ResultHash && tools.Single().RequestId == usages[0].RequestId && tools.Single().ToolCallId == tool.ToolCallId &&
            tools.Single().AnalysisRunId == tool.AnalysisRunId, "Durable audit discarded paid rounds or exact tool result lineage after host validation.");
        return new(model.Result!, spec.Search ? model.Result!["Evaluation"] : model.Result, advice, archived);
    }

    private static void AssertPositive(Completed result, Spec spec, bool lethal)
    {
        Check(result.Line?["Verification"]?.ToString() == "Verified" && result.Line["IsLegal"]?.GetValue<bool>() == true &&
            result.Line["IsLethal"]?.GetValue<bool>() == lethal && HasCoverage(result.Line, spec.Gift) &&
            result.Advice is { Verification: "verified" } && result.Advice.VerifiedLethal == lethal && result.Advice.Actions.Count == spec.Actions.Count &&
            result.Archive!.Advice.Verification == "verified" && result.Archive.Advice.VerifiedLethal == lethal && result.Archive.Advice.Actions.Count == spec.Actions.Count &&
            (!spec.Search || result.Tool["VerifiedLethalFound"]?.GetValue<bool>() == true && result.Tool["IsExhaustive"]?.GetValue<bool>() == false),
            "Observed static gift failed its complete production line or manufactured a value: " + spec.Gift + " " + result.Tool.ToJsonString());
    }
    private static void AssertUnverified(Completed result, string message) => Check(result.Line?["Verification"]?.ToString() != "Verified" &&
        result.Line?["IsLethal"]?.GetValue<bool>() != true && result.Advice?.Verification is "conditional" or "unsupported" &&
        result.Advice.Actions.Count == 0 && !result.Advice.VerifiedLethal && result.Archive!.Advice.Actions.Count == 0 && !result.Archive.Advice.VerifiedLethal, message);
    private static bool HasCoverage(JsonNode line, string id) => Coverage.TryGetValue(id, out var kind) &&
        line["RuleCoverage"]!.AsArray().Any(value => value?.ToString() == "dark-gift-observed-" + kind + ":" + id);
    private static JsonNode Entity(JsonNode line, int id) => line["Result"]!["Entities"]!.AsArray().Single(entity => entity?["EntityId"]?.GetValue<int>() == id)!;
    private static JsonArray Actions(params (string Kind, int Source, int? Target)[] actions) => new(actions.Select(action => (JsonNode)new JsonObject
    { ["kind"] = action.Kind, ["source_entity_id"] = action.Source, ["target_entity_id"] = action.Target }).ToArray());

    private static PowerLogStateEngine Engine(Spec spec)
    {
        var engine = new PowerLogStateEngine(1, "expanded-gift-" + Guid.NewGuid().ToString("N"));
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        void TargetTag(string name, string value) { if (!spec.Omit.Contains(name, StringComparer.Ordinal)) Feed("tag=" + name + " value=" + value); }
        void Active(int id, int owner, int attack, int health, bool inHand, bool applyGiftFlags, string card = "EDR_227")
        {
            Feed("FULL_ENTITY - Creating ID=" + id + " CardID=" + card, "tag=CONTROLLER value=" + owner, "tag=ZONE value=" + (inHand ? "HAND" : "PLAY"), "tag=CARDTYPE value=MINION");
            if (applyGiftFlags)
            {
                TargetTag("ATK", attack.ToString()); TargetTag("HEALTH", health.ToString()); TargetTag("DAMAGE", "0"); TargetTag("COST", spec.Cost.ToString());
                foreach (var flag in spec.Flags) { var split = flag.Split('='); TargetTag(split[0], split[1]); }
            }
            else Feed("tag=ATK value=" + attack, "tag=HEALTH value=" + health, "tag=DAMAGE value=0", "tag=COST value=1");
            if (card == "EDR_227") Feed("tag=RUSH value=1", "tag=DEATHRATTLE value=1");
            if (!inHand) Feed("tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=NUM_ATTACKS_THIS_TURN value=0");
        }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=5", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            "tag=NUM_CARDS_IN_HAND value=" + ((spec.InHand ? 1 : 0) + (spec.Arcane ? 1 : 0)), "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21");
        Feed("FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=HUNTER",
            "tag=ATK value=0", "tag=HEALTH value=30", "tag=DAMAGE value=10", "tag=ARMOR value=0", "tag=IMMUNE value=0");
        Feed("FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=CLASS value=MAGE",
            "tag=ATK value=0", "tag=HEALTH value=" + spec.EnemyHealth, "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0");
        Active(30, 1, spec.GiftOnEnemy ? 5 : spec.Attack, spec.DeathFollowup ? 2 : spec.GiftOnEnemy ? 20 : spec.Health,
            spec.InHand, !spec.GiftOnEnemy, spec.DeathFollowup ? "CS2_171" : "EDR_227");
        if (spec.DeathFollowup) Active(32, 1, 3, 1, false, false, "CS2_171");
        if (spec.GiftOnEnemy) Active(40, 2, spec.Attack, spec.Health, false, true, spec.DeathFollowup ? "CS2_171" : "EDR_227");
        else if (spec.Actions.Any(action => action?["target_entity_id"]?.GetValue<int>() == 40))
            Feed("FULL_ENTITY - Creating ID=40 CardID=CS2_171", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION",
                "tag=ATK value=" + (spec.Gift == "EDR_100t13e" ? 1 : 0), "tag=HEALTH value=" + (spec.Gift == "EDR_100t13e" ? 1 : 20), "tag=DAMAGE value=0");
        if (spec.Arcane) Feed("FULL_ENTITY - Creating ID=31 CardID=CORE_DS1_185", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=SPELL", "tag=COST value=1");
        if (spec.IncludeGift) Feed("FULL_ENTITY - Creating ID=50 CardID=" + spec.Gift, "tag=CONTROLLER value=" + (spec.GiftOnEnemy ? 2 : 1), "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=" + (spec.GiftOnEnemy ? 40 : 30));
        if (spec.ExtraGift is not null) Feed("FULL_ENTITY - Creating ID=51 CardID=" + spec.ExtraGift, "tag=CONTROLLER value=" + (spec.GiftOnEnemy ? 2 : 1), "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=" + (spec.GiftOnEnemy ? 40 : 30));
        Feed("FULL_ENTITY - Creating ID=60 CardID=" + PrivateCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION");
        if (spec.TargetInitiallyDead) Feed("TAG_CHANGE Entity=40 tag=ZONE value=GRAVEYARD", "TAG_CHANGE Entity=40 tag=DAMAGE value=" + spec.Health);
        foreach (var change in spec.Changes) Feed(change);
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Model(Spec spec) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Result { get; private set; }
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); Payloads.Add(text); var payload = JsonNode.Parse(text)!;
            object message;
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[] { new { id = "expanded-gift-tool", type = "function",
                function = new { name = spec.Search ? "find_lethal" : "evaluate_line", arguments = spec.Search ? "{\"maximum_depth\":4,\"maximum_nodes\":500}" : new JsonObject { ["actions"] = spec.Actions.DeepClone() }.ToJsonString() } } } };
            else
            {
                var tool = payload["messages"]!.AsArray().Single(node => node?["role"]?.ToString() == "tool")!;
                Result = JsonNode.Parse(tool["content"]!.GetValue<string>())!;
                var actions = spec.Search && Result["VerifiedLethalFound"]?.GetValue<bool>() == true
                    ? new JsonArray(Result["Actions"]!.AsArray().Select(action => (JsonNode)new JsonObject
                    { ["kind"] = action!["Kind"]!.ToString() switch { "Attack" => "attack", "PlayCard" => "play", "HeroPower" => "hero_power", _ => throw new InvalidOperationException("Unexpected search action.") },
                        ["source_entity_id"] = action["SourceEntityId"]!.DeepClone(), ["target_entity_id"] = action["TargetEntityId"]?.DeepClone() }).ToArray())
                    : (JsonArray)spec.Actions.DeepClone();
                message = new { role = "assistant", content = JsonSerializer.Serialize(new
                {
                    summary = "EXPANDED_GIFT_MODEL_OPINION " + Key, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                    assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions, verified_lethal = true, verification = "verified", HostUnsupportedMechanics = Array.Empty<string>()
                }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
}
