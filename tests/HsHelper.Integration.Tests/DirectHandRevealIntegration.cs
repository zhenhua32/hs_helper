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

internal static class DirectHandRevealIntegration
{
    private const string Build = "255790", Card = "CS2_172", Actor = "EDR_852", Gift = "EDR_100t13e";
    private const string Key = "fixture-direct-hand-reveal-key", Complete = "PowerProcessor.EndCurrentTaskList() - complete";
    private const string Header = "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1";
    private const string Show = "SHOW_ENTITY - Updating Entity=41 CardID=" + Card;

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var definitions = new List<JsonElement>();
        foreach (var name in new[] { "native-definitions", "dark-gift-static", "lethal-search", "deathrattle-draw" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build && fixture.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The direct reveal integration lost its complete fixed primary definitions.");
            definitions.AddRange(fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var directory = Path.Combine(root, "direct-hand-reveal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(definitions.GroupBy(card => card.GetProperty("id").GetString()).Select(group => group.First()))))
            await catalog.ImportAsync(input, Build, source: "fixture://fixed-direct-hand-history");
        Saved? earlyPoint = null, finalPoint = null; Replay? first = null;

        foreach (var metaFirst in new[] { false, true })
        {
            var replay = await Replay.CreateAsync(directory, catalog); await replay.FeedAsync(Block());
            var pending = new List<Saved>();
            if (metaFirst)
            {
                await replay.FeedAsync(Header); pending.Add(await replay.SaveAsync()); AssertUnpublished(replay.Frame);
                await replay.FeedAsync("Info[0] = 41"); pending.Add(await replay.SaveAsync()); AssertUnpublished(replay.Frame);
                await replay.FeedAsync(Show, "tag=ATK value=99", "tag=COST value=0");
            }
            else
            {
                await replay.FeedAsync(Show, "tag=ATK value=99", "tag=COST value=0"); pending.Add(await replay.SaveAsync()); AssertUnpublished(replay.Frame);
                await replay.FeedAsync(Header); pending.Add(await replay.SaveAsync()); AssertUnpublished(replay.Frame);
                await replay.FeedAsync("Info[0] = [entityName=Anonymous id=41 zone=HAND cardId=" + Card + " player=2]");
            }
            pending.Add(await replay.SaveAsync()); AssertUnpublished(replay.Frame);
            foreach (var point in pending) await PipelineAsync(point, catalog, 0, verified: false, forbidCard: true);
            await replay.FeedAsync("BLOCK_END"); var confirmation = replay.Frame.EventSequence;
            Check(Entries(replay.Frame).Count == 0, "A closed block was treated as a settled task before the explicit boundary.");
            await replay.FeedAsync(Complete); var saved = await replay.SaveAsync(); AssertDirect(saved.Frame, 41, confirmation, 2, "TRIGGER");
            await PipelineAsync(saved, catalog, 1);
            foreach (var point in pending)
            {
                var restored = await new SessionStore(point.Database).GetHistoricalDecisionAsync(point.Point.Id);
                Check(restored is not null && SnapshotCodec.Serialize(restored.Context.Snapshot) == point.Original &&
                    !point.Original.Contains(Card, StringComparison.Ordinal), "Completing a later root block backfilled a pending exact snapshot.");
            }
            if (!metaFirst) { earlyPoint = pending[0]; finalPoint = saved; first = replay; }
        }
        pass("both actual SHOW and HISTORY_TARGET orders stay private through incomplete metadata and open blocks then commit only a frozen same-source direct history proof after root closure and task settlement");

        foreach (var lateParent in new[] { false, true })
        {
            var replay = await Replay.CreateAsync(directory, catalog);
            await replay.FeedAsync(Block("POWER"), Block(), Show, Header, "Info[0] = 41"); AssertUnpublished(replay.Frame);
            if (lateParent) { await replay.FeedAsync("BLOCK_END"); AssertUnpublished(replay.Frame); }
            await PipelineAsync(await replay.SaveAsync(), catalog, 0, verified: false, forbidCard: true);
            await replay.FeedAsync("META_DATA - Meta=OVERRIDE_HISTORY Data=0 InfoCount=0", "BLOCK_END");
            if (!lateParent) await replay.FeedAsync("BLOCK_END");
            await replay.FeedAsync(Complete); AssertUnpublished(replay.Frame);
            await PipelineAsync(await replay.SaveAsync(), catalog, 0, forbidCard: true);
        }
        pass("a late OVERRIDE_HISTORY in either matched child or ancestor discards staged identities without exposing a partially closed child through real tools HTTP or saved snapshots");

        var played = await Replay.CreateAsync(directory, catalog, sourceHand: true);
        await played.FeedAsync(Block("PLAY", owner: 1, zone: "HAND"), "TAG_CHANGE Entity=70 tag=ZONE value=PLAY", "TAG_CHANGE Entity=2 tag=RESOURCES_USED value=2",
            Show, Header, "Info[0] = 41", "SHOW_ENTITY - Updating Entity=43 CardID=" + Card, Header, "Info[0] = 43", "BLOCK_END");
        var playedAt = played.Frame.EventSequence; await played.FeedAsync(Complete);
        AssertDirect(played.Frame, 41, playedAt, 1, "PLAY"); AssertDirect(played.Frame, 43, playedAt, 1, "PLAY");
        Check(Entries(played.Frame).All(entry => entry.DirectReveal!.SourcePublicAtSequence > entry.DirectReveal.BlockStartedAtSequence),
            "A source still in private hand at block start was labeled public before its actual PLAY.");
        await played.FeedAsync("FULL_ENTITY - Creating ID=42 CardID=" + Card, "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION",
            "tag=ATK value=3", "tag=HEALTH value=2", "tag=DAMAGE value=0", "HIDE_ENTITY - Entity=42 tag=ZONE value=HAND", Complete);
        var sameIds = Entries(played.Frame);
        Check(sameIds.Count == 3 && sameIds.Select(entry => entry.EntityId).Order().SequenceEqual([41, 42, 43]) &&
            sameIds.All(entry => entry.CardId == Card) && sameIds.Single(entry => entry.EntityId == 42) is { FromZone: "PLAY", EnteredHandAtSequence: not null, DirectReveal: null },
            "Same-card entities collapsed or an ordinary public return was relabeled as a direct reveal.");
        await PipelineAsync(await played.SaveAsync(), catalog, 3);
        pass("a source actually played from hand becomes public within its real block and two direct same-card entities remain separate from an ordinary public-return entity");

        foreach (var variant in new[] { "bare-show", "revealed", "forge", "history-only", "wrong-entity", "TIME_875t", "joust", "start", "unknown-scope", "unknown-build" })
        {
            var replay = await Replay.CreateAsync(directory, catalog, build: variant == "unknown-build" ? "255789" : Build);
            if (variant == "start") await replay.FeedAsync("TAG_CHANGE Entity=1 tag=STEP value=BEGIN_MULLIGAN");
            if (variant is "bare-show" or "revealed" or "forge")
            {
                await replay.FeedAsync(Show);
                if (variant != "bare-show") await replay.FeedAsync("TAG_CHANGE Entity=41 tag=" + (variant == "revealed" ? "410" : "3070") + " value=1");
            }
            else
            {
                await replay.FeedAsync(Block(variant == "joust" ? "JOUST" : variant == "start" ? "START_OF_GAME" : variant == "unknown-scope" ? "UNKNOWN" : "TRIGGER"));
                if (variant != "history-only") await replay.FeedAsync(variant == "TIME_875t" ? "SHOW_ENTITY - Updating Entity=41 CardID=TIME_875t" : Show);
                await replay.FeedAsync(Header, "Info[0] = " + (variant == "wrong-entity" ? 43 : 41), "BLOCK_END");
            }
            await replay.FeedAsync(Complete); AssertUnpublished(replay.Frame, variant == "TIME_875t" ? "TIME_875t" : Card);
            await ProbeAsync(replay.Frame, catalog, variant == "TIME_875t" ? "TIME_875t" : Card);
        }
        pass("private SHOW raw reveal or forge flags metadata without the matching entity TIME_875t jousts start-of-game unknown scopes and unsupported builds never authorize direct hand identity");

        await first!.FeedAsync("FULL_ENTITY - Creating ID=50 CardID=" + Gift, "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT",
            "tag=ATTACHED value=41", "tag=WINDFURY value=1", "tag=DIVINE_SHIELD value=1", "tag=TAG_SCRIPT_DATA_NUM_6 value=120225", Complete);
        Check(!first.Frame.Entities.ContainsKey(50), "Direct base identity made a private attached bonus public.");
        var gift = await PipelineAsync(await first.SaveAsync(), catalog, 1);
        Check(gift.Payloads.All(text => !DataPayload(text).Contains(Gift, StringComparison.Ordinal) && !DataPayload(text).Contains("120225", StringComparison.Ordinal)),
            "Actual HTTP disclosed a private attached gift through direct reveal memory.");
        var sourceProof = Entries(first.Frame).Single().DirectReveal;
        await first.FeedAsync("TAG_CHANGE Entity=70 tag=ZONE value=GRAVEYARD", Complete);
        Check(Entries(first.Frame).Single().DirectReveal == sourceProof, "Later source movement rewrote a previously confirmed history fact.");
        await PipelineAsync(await first.SaveAsync(), catalog, 1);
        pass("confirmed direct identity remains only a base definition after source movement and never grants private attached keywords current cost position or entity-effect access");

        foreach (var variant in new[] { "target-change", "target-recreate", "target-owner", "source-change", "source-recreate", "source-owner", "source-never-public", "spectator", "gap", "meta-count", "meta-duplicate", "meta-malformed" })
        {
            var replay = await Replay.CreateAsync(directory, catalog, sourceHand: variant == "source-never-public");
            await replay.FeedAsync(variant == "source-never-public" ? Block(owner: 1, zone: "HAND") : Block(), Show,
                variant == "meta-count" ? "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=2" : Header,
                variant == "meta-malformed" ? "Info[0] = unknown" : "Info[0] = 41");
            if (variant == "meta-duplicate") await replay.FeedAsync("Info[0] = 41");
            if (variant == "target-change") await replay.FeedAsync("CHANGE_ENTITY - Updating Entity=41 CardID=CS2_171", "tag=CARDTYPE value=MINION");
            if (variant == "target-recreate") await replay.FeedAsync("FULL_ENTITY - Creating ID=41 CardID=" + Card, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION");
            if (variant == "target-owner") await replay.FeedAsync("TAG_CHANGE Entity=41 tag=CONTROLLER value=1", "TAG_CHANGE Entity=41 tag=CONTROLLER value=2");
            if (variant == "source-change") await replay.FeedAsync("CHANGE_ENTITY - Updating Entity=70 CardID=CS2_171", "tag=CARDTYPE value=MINION");
            if (variant == "source-recreate") await replay.FeedAsync("FULL_ENTITY - Creating ID=70 CardID=" + Actor, "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION");
            if (variant == "source-owner") await replay.FeedAsync("TAG_CHANGE Entity=70 tag=CONTROLLER value=1");
            if (variant == "spectator") await replay.FeedAsync("Begin Spectating");
            if (variant == "gap") { replay.Engine.MarkDataGap("synthetic direct-reveal gap"); await replay.RefreshAsync(); }
            await replay.FeedAsync("BLOCK_END", Complete);
            Check(Entries(replay.Frame).Count == 0, "A pending source target observer or metadata contradiction survived to confirmation: " + variant);
            if (variant == "target-owner")
                Check(replay.Frame.RecentEvents.Any(observed => observed.EntityId == 41 && observed.CardId == Card &&
                    observed.IdentityVisibility == EventIdentityVisibility.PlayerOne), "The ownership control did not actually create its legitimate own-view historical fact.");
            await ProbeAsync(replay.Frame, catalog, Card, allowPastOwnIdentity: variant == "target-owner", variant);
        }
        pass("pending target and source changes recreations ownership loss privacy spectator or input gaps and incomplete duplicate malformed metadata discard proof rather than retroactively confirming it");

        var old = await new SessionStore(earlyPoint!.Database).GetHistoricalDecisionAsync(earlyPoint.Point.Id);
        Check(old is not null && SnapshotCodec.Serialize(old.Context.Snapshot) == earlyPoint.Original && !earlyPoint.Original.Contains(Card, StringComparison.Ordinal),
            "Later direct confirmation filled an earlier exact snapshot.");
        await PipelineAsync(earlyPoint with { Frame = old!.Context.Snapshot }, catalog, 0, verified: false, forbidCard: true, task: AdviceTaskKind.Review);
        await PipelineAsync(finalPoint!, catalog, 1, task: AdviceTaskKind.Review);
        pass("exact partial and confirmed decision points retain different immutable histories and historical HTTP at the old state cannot learn a later closed-block identity");

        var entry = Entries(finalPoint!.Frame).Single();
        foreach (var bad in new[] { entry with { EnteredHandAtSequence = entry.PublicAtSequence }, entry with { FromZone = "PLAY" },
            entry with { DirectReveal = entry.DirectReveal! with { ConfirmedAtSequence = finalPoint.Frame.EventSequence + 1 } },
            entry with { DirectReveal = entry.DirectReveal! with { ShowAtSequence = 0 } },
            entry with { DirectReveal = entry.DirectReveal! with { SourcePublicAtSequence = 0 } } })
        {
            var memory = finalPoint.Frame.PublicHandMemory!;
            var altered = finalPoint.Frame with { PublicHandMemory = new(memory.MatchId, memory.StateVersion, memory.EventSequence, memory.LocalPlayerId,
                memory.PerspectiveSource, [bad], memory.IsComplete, memory.Warnings, memory.IsSettled, memory.ContinuityPolicy) };
            Check(PublicHandMemoryVisibility.Confirmed(altered) is null, "An impossible direct evidence chronology or fabricated hand-entry date was accepted.");
        }
        var rejected = await PipelineAsync(finalPoint, catalog, 1, hardReject: true);
        Check(rejected.Advice is null && rejected.Archive is null && rejected.Line["Verification"]?.ToString() == "Invalid" && rejected.Line["AppliedActions"]?.GetValue<int>() == 0,
            "Direct identity became a legal hidden attack target or an archived executable proposal.");
        pass("malformed direct provenance is rejected and a model cannot turn confirmed hidden identity into current effect or a legal target while paid requests and evidence traces are retained");
    }

    private static string Block(string type = "TRIGGER", int owner = 2, string zone = "PLAY") =>
        $"BLOCK_START BlockType={type} Entity=[entityName=Anonymous id=70 zone={zone} zonePos=1 cardId={Actor} player={owner}] EffectCardId= EffectIndex=0 Target=0 SubOption=-1";
    private static IReadOnlyList<PublicHandMemoryEntry> Entries(GameSnapshot frame) => PublicHandMemoryVisibility.Confirmed(frame)?.Entries ?? [];
    private static void AssertUnpublished(GameSnapshot frame, string card = Card) => Check(Entries(frame).Count == 0 &&
        !SnapshotCodec.Serialize(frame).Contains(card, StringComparison.Ordinal), "An unconfirmed history candidate leaked into a snapshot or event.");
    private static void AssertDirect(GameSnapshot frame, int id, long confirmed, int sourceOwner, string type)
    {
        var entry = Entries(frame).Single(entry => entry.EntityId == id); var proof = entry.DirectReveal;
        Check(entry is { FromZone: "HAND", EnteredHandAtSequence: null } && entry.CardId == Card && entry.ControllerId == 2 &&
            proof is not null && entry.PublicAtSequence == confirmed && proof.ConfirmedAtSequence == confirmed &&
            proof.ClientBuild == Build && proof.SourceEntityId == 70 && proof.SourceCardId == Actor && proof.SourceControllerId == sourceOwner &&
            proof.SourceCardType == "MINION" && proof.SourceZone == "PLAY" && proof.BlockType == type &&
            proof.BlockStartedAtSequence <= proof.SourcePublicAtSequence && proof.SourcePublicAtSequence < confirmed &&
            proof.BlockStartedAtSequence < proof.ShowAtSequence && proof.HistoryTargetAtSequence >= proof.BlockStartedAtSequence &&
            Math.Max(proof.ShowAtSequence, proof.HistoryTargetAtSequence) < confirmed,
            "Direct memory lost its actual same-source block evidence or invented a public-zone return.");
        Check(frame.Entities[id].CardId is null && frame.Entities[id].Tags.Keys.All(key => key is "CONTROLLER" or "ZONE" or "ENTITY_ID" or "ZONE_POSITION"),
            "Direct evidence restored a hidden entity or private current attributes.");
    }
    private sealed record Saved(string Database, GameSnapshot Frame, DecisionPoint Point, string Original);
    private sealed record Result(StrategyAdvice? Advice, ArchivedAdviceRecord? Archive, JsonNode Line, IReadOnlyList<string> Payloads);

    private static async Task ProbeAsync(GameSnapshot frame, CardCatalog catalog, string forbidden, bool allowPastOwnIdentity = false, string? variant = null)
    {
        var build = frame.ClientBuild;
        var context = CompactAdviceContextBuilder.Build(frame, build);
        var coach = new SnapshotCoach(frame, context, catalog, build);
        var snapshot = await coach.ExecuteAsync("get_snapshot", new(), default);
        Check((snapshot["public_hand_memory"]?["entries"]?.AsArray().Count ?? 0) == 0 &&
            (allowPastOwnIdentity || !snapshot.ToJsonString().Contains(forbidden, StringComparison.Ordinal)) &&
            !snapshot["entities"]!.AsArray().Any(entity => entity?["id"]?.GetValue<int>() == 41),
            "A rejected scope reached real get_snapshot identity data: " + variant);
        var effect = await coach.ExecuteAsync("get_entity_effect", new() { ["entity_id"] = 41 }, default);
        Check(effect["error"] is not null, "A rejected direct candidate gained current effect access.");
    }

    private static async Task<Result> PipelineAsync(Saved point, CardCatalog catalog, int count, bool verified = true,
        bool forbidCard = false, bool hardReject = false, AdviceTaskKind task = AdviceTaskKind.Turn)
    {
        var path = Path.Combine(Path.GetDirectoryName(point.Database)!, "model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var known = point.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>().Concat(PublicHandMemoryVisibility.KnownCardIds(point.Frame));
        var cards = await catalog.GetByIdsBatchedAsync(known, Build);
        var context = CompactAdviceContextBuilder.Create(point.Frame, Build, task, cards: cards) with { KnowledgeAsOf = point.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        Check(context.TemplateVersion == (task == AdviceTaskKind.Review ? "hunter-v7/review" : "hunter-v22/turn") &&
            (context.Snapshot["public_hand_memory"]?["entries"]?.AsArray().Count ?? 0) == count, "The fixed current template or confirmed memory count changed.");
        foreach (var raw in context.Snapshot["public_hand_memory"]?["entries"]?.AsArray() ?? [])
        {
            var core = Entries(point.Frame).Single(entry => entry.EntityId == raw!["entity_id"]!.GetValue<int>());
            Check(raw!["kind"]?.ToString() == (core.DirectReveal is null ? "public_return" : "history_target") &&
                (core.DirectReveal is null ? raw["direct_reveal"] is null : raw["from"]?.ToString() == "HAND" && raw["entered_hand_at_sequence"] is null &&
                    raw["direct_reveal"]?["confirmed_at_sequence"]?.GetValue<long>() == core.PublicAtSequence && raw["direct_reveal"]?["source_entity_id"]?.GetValue<int>() == 70),
                "Compact data mixed direct reveal with a return or omitted its closed-block provenance.");
        }
        var tools = new List<CoachToolObservation>(); var usage = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(point.Frame, context.Snapshot, catalog, Build, toolCompleted: tools.Add);
        using var model = new Model(context.Snapshot["cards"]!.AsArray().Select(card => card!["id"]!.GetValue<string>()).ToArray(), hardReject);
        using var http = new HttpClient(model); StrategyAdvice? advice = null; var denied = false;
        try { advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://direct-hand-fixture.invalid/v1/chat/completions"), "direct-hand-fixture", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usage.Add)); }
        catch (InvalidDataException) when (hardReject) { denied = true; }
        Check(model.Calls == 2 && denied == hardReject && tools.Count == 4 && tools.All(tool => tool.Success && tool.ResultHash == Hash(tool.ResultJson!) &&
            tool.AnalysisRunId == usage[0].AnalysisRunId && tool.RequestId == usage[0].RequestId && tool.EventSequence == point.Frame.EventSequence) &&
            usage.Count == 2 && usage.All(item => item.Success && item.ChargedTokens == 30 && item.PromptTemplateVersion == context.TemplateVersion) &&
            budget.Snapshot(point.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            (model.Snapshot!["public_hand_memory"]?["entries"]?.AsArray().Count ?? 0) == count && model.Effect!["error"] is not null &&
            (!forbidCard || model.Payloads.All(text => !DataPayload(text).Contains(Card, StringComparison.Ordinal))) && SnapshotCodec.Serialize(point.Frame) == point.Original,
            "Actual HTTP tool validation leaked a pending identity or changed paid source-bound immutable evidence.");
        if (count > 0)
        {
            var printed = model.Cards!["cards"]!.AsArray().Single(card => card?["Id"]?.ToString() == Card)!;
            Check(printed["Cost"]?.GetValue<int>() == cards.Single(card => card.Id == Card).Cost && printed["Cost"]?.GetValue<int>() != 0 &&
                !printed.ToJsonString().Contains("ATTACHED", StringComparison.Ordinal), "Definition lookup returned hidden current cost or attached script data.");
        }
        ArchivedAdviceRecord? loaded = null; var archivePath = Path.Combine(path, "advice.db");
        if (hardReject) Check(advice is null && !File.Exists(archivePath), "Invalid hidden targeting produced advice or an archive.");
        else
        {
            Check(advice is not null && !advice.VerifiedLethal && (verified ? advice.Verification == "verified" && advice.Actions.Count == 1 : advice.Verification != "verified"),
                "The final real host promoted a model claim or changed the expected legal prefix boundary.");
            var draft = AdviceArchiveStore.Prepare(new(point.Frame.MatchId, point.Frame.StateVersion, point.Frame.EventSequence, Build, advice!.TemplateVersion!, advice.Summary)
            {
                Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
                Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
                Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
                Evidence = tools.Select(tool => new AdviceToolEvidence(tool.Name, "闭块历史揭示仅为基础身份事实，不是当前属性或可执行目标。", tool.ResultHash)).ToArray(),
                Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
                LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
                HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
                HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
            }, [Key]);
            var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync(); var saved = await archive.SaveAsync(draft);
            loaded = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing source-bound direct reveal advice.");
            Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) && loaded.Advice.KnowledgeAsOf == point.Point.RecordedAt &&
                loaded.Advice.VisibilityPolicy == context.VisibilityPolicy && loaded.Advice.RequestIds!.SequenceEqual(usage.Select(item => item.RequestId)) &&
                loaded.Advice.Evidence.Select(item => item.ResultHash).SequenceEqual(tools.Select(item => item.ResultHash)) && !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal),
                "Reopened advice lost its immutable source policy cutoff paid lineage or redacted evidence.");
        }
        var tracePath = Path.Combine(path, "trace.db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usage.Select(item => new ModelRequestTrace(item.RequestId, item.MatchId, item.StateVersion, item.Model, item.Round,
            item.InputTokens, item.OutputTokens, item.TotalTokens, item.ChargedTokens, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = Build, Task = context.Task, PromptTemplateVersion = item.PromptTemplateVersion!, AnalysisRunId = item.AnalysisRunId, EventSequence = item.EventSequence }),
        tools.Select(item => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), point.Frame.MatchId, point.Frame.StateVersion, item.Name, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = item.ResultHash, Model = item.Model, AnalysisRunId = item.AnalysisRunId, RequestId = item.RequestId, ToolCallId = item.ToolCallId, EventSequence = item.EventSequence }));
        var reopened = new AnalysisTraceStore(tracePath); var totals = await reopened.GetBudgetTotalsAsync(point.Frame.MatchId); var loadedTools = await reopened.ListToolsAsync(point.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && loadedTools.Count == 4 && loadedTools.All(item => item.AnalysisRunId == usage[0].AnalysisRunId &&
            item.RequestId == usage[0].RequestId && item.EventSequence == point.Frame.EventSequence), "Paid requests and tool lineage disappeared after rejection or reopening.");
        var exact = await new SessionStore(point.Database).GetHistoricalDecisionAsync(point.Point.Id);
        Check(exact is not null && SnapshotCodec.Serialize(exact.Context.Snapshot) == point.Original, "A later tool or model write changed the recorded exact snapshot.");
        return new(advice, loaded, model.Line!, model.Payloads.AsReadOnly());
    }

    private sealed class Replay(DeckTrackingEnricher enricher, SessionStore store, string database)
    {
        public PowerLogStateEngine Engine { get; } = new(1, "direct-hand-" + Guid.NewGuid().ToString("N"));
        public GameSnapshot Frame { get; private set; } = null!;
        private SessionRecorder? _recorder;
        private long _line;
        public static async Task<Replay> CreateAsync(string directory, CardCatalog catalog, bool sourceHand = false, string build = Build)
        {
            var database = Path.Combine(directory, "session-" + Guid.NewGuid().ToString("N") + ".db");
            var store = new SessionStore(database); await store.InitializeAsync();
            var replay = new Replay(await DeckTrackingEnricher.CreateAsync(catalog, null, build), store, database);
            await replay.FeedAsync("GameState.DebugPrintGame() - BuildNumber=" + build + " FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME",
                "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", "tag=STEP value=MAIN_ACTION",
                "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=5", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
                "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
                "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=ATK value=0", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0",
                "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=ATK value=0", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=ARMOR value=0",
                "FULL_ENTITY - Creating ID=30 CardID=CS2_171", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=1", "tag=HEALTH value=1", "tag=DAMAGE value=0", "tag=COST value=1", "tag=CARDRACE value=BEAST", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1",
                "FULL_ENTITY - Creating ID=70 CardID=" + Actor, "tag=CONTROLLER value=" + (sourceHand ? 1 : 2), "tag=ZONE value=" + (sourceHand ? "HAND" : "PLAY"), "tag=CARDTYPE value=MINION", "tag=ATK value=2", "tag=HEALTH value=4", "tag=DAMAGE value=0", "tag=COST value=2",
                "FULL_ENTITY - Creating ID=41 CardID=", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=9", "tag=COST value=0",
                "FULL_ENTITY - Creating ID=43 CardID=", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", Complete);
            replay._recorder = new SessionRecorder(store, new(null, build, 1)); await replay.RecordAsync(); return replay;
        }
        public async Task FeedAsync(params string[] lines)
        { foreach (var line in lines) { _line++; Engine.ProcessLine(line); await RefreshAsync(); } }
        public async Task RefreshAsync()
        {
            var original = Engine.Snapshot(); Frame = enricher.Observe(original);
            Check(JsonSerializer.Serialize(original.PublicHandMemory) == JsonSerializer.Serialize(Frame.PublicHandMemory), "Enrichment fabricated or rebuilt raw-engine reveal evidence.");
            if (_recorder is not null && Frame.LocalPlayerId == 1 && !Frame.IsSpectating) await RecordAsync();
        }
        private Task RecordAsync() { _recorder!.Observe(Frame, _line); return _recorder.FlushAsync(); }
        public async Task<Saved> SaveAsync()
        {
            if (Frame.LocalPlayerId == 1 && !Frame.IsSpectating)
            {
                await _recorder!.CompleteAsync();
                var point = (await store.ListDecisionPointsAsync(Frame.MatchId, true)).Single(point => point.StateVersion == Frame.StateVersion && point.EventSequence == Frame.EventSequence);
                var exact = await new SessionStore(database).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact staged reveal point.");
                Check(SnapshotCodec.Serialize(exact.Context.Snapshot) == SnapshotCodec.Serialize(Frame), "Per-line recording changed or merged an exact stage.");
                return new(database, exact.Context.Snapshot, point, SnapshotCodec.Serialize(Frame));
            }
            throw new InvalidOperationException("An unconfirmed negative view cannot borrow a previous recording's observer binding.");
        }
    }
    private static JsonArray Actions(bool invalid) => new(new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = invalid ? 41 : 21 });
    private sealed class Model(string[] ids, bool invalid) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonNode? Snapshot { get; private set; }
        public JsonNode? Cards { get; private set; }
        public JsonNode? Effect { get; private set; }
        public JsonNode? Line { get; private set; }
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); Payloads.Add(text); var payload = JsonNode.Parse(text)!; object message;
            object Tool(string id, string name, object arguments) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } };
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[] { Tool("direct-snapshot", "get_snapshot", new { }),
                Tool("direct-cards", "get_cards", new { card_ids = ids }), Tool("direct-effect", "get_entity_effect", new { entity_id = 41 }),
                Tool("direct-line", "evaluate_line", new { actions = Actions(invalid) }) } };
            else
            {
                var messages = payload["messages"]!.AsArray();
                JsonNode Read(string id) => JsonNode.Parse(messages.Single(message => message?["tool_call_id"]?.ToString() == id)!["content"]!.GetValue<string>())!;
                Snapshot = Read("direct-snapshot"); Cards = Read("direct-cards"); Effect = Read("direct-effect"); Line = Read("direct-line");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new { summary = "闭块身份不代表当前隐藏属性。" + Key,
                    reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(),
                    unsupported_mechanics = Array.Empty<string>(), actions = Actions(invalid), verified_lethal = true, verification = "verified" }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    private static string DataPayload(string text) => new JsonArray(JsonNode.Parse(text)!["messages"]!.AsArray().Where(message => message?["role"]?.ToString() != "system").Select(message => message!.DeepClone()).ToArray()).ToJsonString();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
