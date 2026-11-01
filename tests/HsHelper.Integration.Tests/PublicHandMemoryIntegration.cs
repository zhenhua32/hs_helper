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

internal static class PublicHandMemoryIntegration
{
    private const string Build = "255790", RememberedCard = "EDR_852", Key = "fixture-public-hand-memory-key";
    private const string PrivateCard = "PRIVATE_MEMORY_UNSEEN_CARD", Gift = "EDR_100t13e";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var definitions = new List<JsonElement>();
        foreach (var name in new[] { "native-definitions", "dark-gift-static", "lethal-search" })
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(fixture.RootElement.GetProperty("build").GetString() == Build && fixture.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The public memory integration lost its fixed primary definitions.");
            definitions.AddRange(fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var cards = definitions.GroupBy(card => card.GetProperty("id").GetString(), StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var directory = Path.Combine(root, "public-hand-memory", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards))) await catalog.ImportAsync(input, Build, source: "fixture://fixed-public-hand-memory");

        var played = await Replay.CreateAsync(directory, catalog);
        AssertHidden(played.Frame, 41); Check(Entries(played.Frame).Count == 0, "An initially private card acquired a public memory.");
        var privatePoint = await played.SaveAsync(); var privateOriginal = SnapshotCodec.Serialize(privatePoint.Frame);
        played.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete);
        var exposed = played.Frame;
        Check(exposed.Entities[41].CardId == RememberedCard, "The positive card was never actually public.");
        played.Feed("TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
        var remembered = Entries(played.Frame).Single(); AssertEntry(played.Frame, remembered, "PLAY");
        Check(remembered.PublicAtSequence <= exposed.EventSequence && remembered.EnteredHandAtSequence > exposed.EventSequence,
            "Memory identity or entry timing was inferred from the later private frame.");
        var returned = await played.SaveAsync(); var positive = await PipelineAsync(returned, catalog, expectedEntries: 1);
        Check(positive.Advice?.Verification == "verified" && !positive.Advice.VerifiedLethal && positive.Advice.Actions.Count == 1,
            "Remembered opponent information upgraded the ordinary attack into a false lethal or disabled normal host validation.");
        var beforeHide = JsonSerializer.Serialize(Entries(played.Frame));
        played.Feed("HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", Complete);
        Check(JsonSerializer.Serialize(Entries(played.Frame)) == beforeHide, "Repeated HIDE created a second remembered card or changed its original entry sequence.");
        await PipelineAsync(await played.SaveAsync(), catalog, expectedEntries: 1);
        pass("real public PLAY to HAND facts survive enrichment exact recording repeated HIDE tool HTTP host validation and source-bound archive without filling the hidden entity identity");

        var grave = await Replay.CreateAsync(directory, catalog);
        grave.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", "TAG_CHANGE Entity=41 tag=ZONE value=GRAVEYARD", Complete);
        grave.Feed("TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
        AssertEntry(grave.Frame, Entries(grave.Frame).Single(), "GRAVEYARD");
        await PipelineAsync(await grave.SaveAsync(), catalog, expectedEntries: 1);
        var secret = await Replay.CreateAsync(directory, catalog);
        secret.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete);
        secret.Feed("TAG_CHANGE Entity=41 tag=ZONE value=SECRET", Complete);
        Check(Entries(secret.Frame).Count == 0, "A hidden secret slot was called a current hand card.");
        secret.Feed("TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
        AssertEntry(secret.Frame, Entries(secret.Frame).Single(), "PLAY");
        await PipelineAsync(await secret.SaveAsync(), catalog, expectedEntries: 1);
        pass("same-entity graveyard returns and uninterrupted public to secret to hand transitions retain only their last genuinely public identity and origin");

        var privateGift = await Replay.CreateAsync(directory, catalog);
        privateGift.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete, "TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
        privateGift.Feed("FULL_ENTITY - Creating ID=50 CardID=" + Gift, "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=41",
            "tag=WINDFURY value=1", "tag=DIVINE_SHIELD value=1", "tag=TAG_SCRIPT_DATA_NUM_6 value=120225", Complete);
        AssertHidden(privateGift.Frame, 41); AssertEntry(privateGift.Frame, Entries(privateGift.Frame).Single(), "PLAY");
        Check(!privateGift.Frame.Entities.ContainsKey(50), "The current private attachment remained public beside an otherwise legitimate memory.");
        var gifted = await PipelineAsync(await privateGift.SaveAsync(), catalog, expectedEntries: 1, expectVerified: null);
        Check(gifted.Payloads.All(payload => !DataPayload(payload).Contains(Gift, StringComparison.Ordinal) && !DataPayload(payload).Contains("120225", StringComparison.Ordinal)),
            "A remembered base identity re-exposed private bonus identity or parameters in actual HTTP data.");
        pass("a remembered base card does not reveal a newly private attached gift bonus dynamic cost stats or current entity effect through tools HTTP or archival evidence");

        foreach (var mutation in new[] { "change", "full", "copy", "deck", "leave-hand" })
        {
            var changed = await Replay.CreateAsync(directory, catalog);
            changed.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete, "TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
            if (mutation == "change") changed.Feed("CHANGE_ENTITY - Updating Entity=41 CardID=CS2_172", "tag=CARDTYPE value=MINION");
            if (mutation == "full") changed.Feed("FULL_ENTITY - Updating Entity=41 CardID=" + RememberedCard, "tag=CARDTYPE value=MINION");
            if (mutation == "deck") changed.Feed("TAG_CHANGE Entity=41 tag=ZONE value=DECK", "TAG_CHANGE Entity=41 tag=ZONE value=HAND");
            if (mutation == "leave-hand") changed.Feed("TAG_CHANGE Entity=41 tag=ZONE value=SETASIDE", "TAG_CHANGE Entity=41 tag=ZONE value=HAND");
            if (mutation == "copy") changed.Feed("FULL_ENTITY - Creating ID=42 CardID=" + RememberedCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION",
                "tag=CREATOR value=41", "tag=COPIED_FROM_ENTITY_ID value=41");
            changed.Feed(Complete);
            Check(mutation == "copy" ? Entries(changed.Frame).Count == 1 && Entries(changed.Frame)[0].EntityId == 41 && changed.Frame.Entities[42].CardId is null : Entries(changed.Frame).Count == 0,
                "A replacement hidden transition or different copied entity inherited a previous public identity: " + mutation);
            await PipelineAsync(await changed.SaveAsync(), catalog, mutation == "copy" ? 1 : 0);
        }
        pass("hidden CHANGE and FULL replacement deck or setaside transitions clear the association and neither same CardId CREATOR nor copy tags transfer memory to a new entity");

        foreach (var mutation in new[] { "controller", "view", "spectator", "gap", "reset" })
        {
            var changed = await Replay.CreateAsync(directory, catalog);
            changed.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete, "TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
            if (mutation == "controller") changed.Feed("TAG_CHANGE Entity=41 tag=CONTROLLER value=1", "TAG_CHANGE Entity=41 tag=CONTROLLER value=2", Complete);
            if (mutation == "view") { changed.Engine.SetLocalPlayerId(2); changed.Refresh(); changed.Engine.SetLocalPlayerId(1); changed.Refresh(); }
            if (mutation == "spectator") changed.Feed("Begin Spectating");
            if (mutation == "gap") { changed.Engine.MarkDataGap("synthetic public-hand integration gap"); changed.Refresh(); }
            if (mutation == "reset") { changed.Engine.ResetSession(); changed.Refresh(); }
            Check(Entries(changed.Frame).Count == 0, "Old memory survived a binding perspective data-gap or session reset: " + mutation);
        }
        var gap = await Replay.CreateAsync(directory, catalog);
        gap.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete, "TAG_CHANGE Entity=41 tag=ZONE value=HAND", "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", Complete);
        gap.Engine.MarkDataGap("synthetic gap before fresh proof"); gap.Refresh(); Check(Entries(gap.Frame).Count == 0, "A gap retained an older hand inference.");
        gap.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", "SHOW_ENTITY - Updating Entity=41 CardID=" + RememberedCard,
            "tag=CARDTYPE value=MINION", Complete, "TAG_CHANGE Entity=41 tag=ZONE value=HAND", Complete);
        Check(Entries(gap.Frame).Count == 1 && PublicHandMemoryVisibility.Confirmed(gap.Frame)?.IsComplete == false,
            "A genuine new public proof after a gap was confused with an old inference or labeled complete.");
        await PipelineAsync(await gap.SaveAsync(), catalog, expectedEntries: 1, expectVerified: false);

        var underflow = await Replay.CreateAsync(directory, catalog);
        underflow.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", Complete);
        var beforeUnderflow = await underflow.SaveAsync(); var beforeUnderflowJson = SnapshotCodec.Serialize(beforeUnderflow.Frame);
        var oldEntry = Entries(beforeUnderflow.Frame).Single();
        underflow.Feed("BLOCK_END", Complete);
        var clearedAt = underflow.Frame.EventSequence;
        Check(!underflow.Frame.Quality.IsComplete && underflow.Frame.PublicHandMemory is { IsComplete: false, Entries.Count: 0 } &&
            Entries(underflow.Frame).Count == 0, "An unmatched BLOCK_END marked quality incomplete but retained previously confirmed hand memory.");
        await PipelineAsync(await underflow.SaveAsync(), catalog, expectedEntries: 0, expectVerified: false);
        underflow.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", "SHOW_ENTITY - Updating Entity=41 CardID=" + RememberedCard,
            "tag=CARDTYPE value=MINION", Complete, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", Complete);
        var freshEntry = Entries(underflow.Frame).Single();
        Check(freshEntry.PublicAtSequence > clearedAt && freshEntry.PublicAtSequence > oldEntry.PublicAtSequence &&
            PublicHandMemoryVisibility.Confirmed(underflow.Frame)?.IsComplete == false,
            "Post-underflow recovery reused an old public identity or claimed a complete history.");
        await PipelineAsync(await underflow.SaveAsync(), catalog, expectedEntries: 1, expectVerified: false);
        var oldExact = await new SessionStore(beforeUnderflow.Database).GetHistoricalDecisionAsync(beforeUnderflow.Point.Id);
        Check(oldExact is not null && SnapshotCodec.Serialize(oldExact.Context.Snapshot) == beforeUnderflowJson &&
            Entries(oldExact.Context.Snapshot).Single() == oldEntry && SnapshotCodec.Serialize(beforeUnderflow.Frame) == beforeUnderflowJson,
            "Clearing unreliable current memory rewrote the earlier exact saved observation.");
        await PipelineAsync(beforeUnderflow, catalog, expectedEntries: 1);

        var stalePublic = await Replay.CreateAsync(directory, catalog);
        stalePublic.Feed("TAG_CHANGE Entity=41 tag=ZONE value=PLAY", Complete);
        stalePublic.Feed("BLOCK_END", Complete, "TAG_CHANGE Entity=41 tag=ATK value=10", "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", Complete);
        Check(!stalePublic.Frame.Quality.IsComplete && stalePublic.Frame.PublicHandMemory is { Entries.Count: 0 } && Entries(stalePublic.Frame).Count == 0,
            "An ordinary attribute update after block underflow revived a public source that had never re-established its identity.");
        await PipelineAsync(await stalePublic.SaveAsync(), catalog, expectedEntries: 0, expectVerified: false);
        pass("control view spectator reset explicit gaps and block underflow clear old associations while only fresh settled post-gap public proof can recover incomplete memory without rewriting earlier exact observations");

        var old = await new SessionStore(privatePoint.Database).GetHistoricalDecisionAsync(privatePoint.Point.Id) ?? throw new InvalidOperationException("Missing earlier exact point.");
        Check(SnapshotCodec.Serialize(old.Context.Snapshot) == privateOriginal && Entries(old.Context.Snapshot).Count == 0,
            "Later observation backfilled the earlier source snapshot or memory.");
        var oldSaved = privatePoint with { Frame = old.Context.Snapshot };
        var early = await PipelineAsync(oldSaved, catalog, expectedEntries: 0, task: AdviceTaskKind.Review);
        Check(early.Payloads.All(payload => !DataPayload(payload).Contains(RememberedCard, StringComparison.Ordinal)),
            "The actual historical model request learned a later public card identity.");
        var later = await PipelineAsync(returned, catalog, expectedEntries: 1, task: AdviceTaskKind.Review);
        Check(early.Archive!.Advice.PromptTemplateVersion == "hunter-v7/review" && later.Archive!.Advice.PromptTemplateVersion == "hunter-v7/review" &&
            old.Point.EventSequence < returned.Point.EventSequence, "Review lost its exact event or current base template boundary.");
        pass("the same session reopened before and after the return retains separate immutable as-of memory and real historical HTTP never receives the later identity");

        var ledger = returned.Frame.PublicHandMemory!;
        foreach (var invalid in new[]
        {
            new PublicHandMemorySnapshot("other-match", ledger.StateVersion, ledger.EventSequence, ledger.LocalPlayerId, ledger.PerspectiveSource, ledger.Entries, ledger.IsComplete, ledger.Warnings),
            new PublicHandMemorySnapshot(ledger.MatchId, ledger.StateVersion + 1, ledger.EventSequence, ledger.LocalPlayerId, ledger.PerspectiveSource, ledger.Entries, ledger.IsComplete, ledger.Warnings),
            new PublicHandMemorySnapshot(ledger.MatchId, ledger.StateVersion, ledger.EventSequence + 1, ledger.LocalPlayerId, ledger.PerspectiveSource, ledger.Entries, ledger.IsComplete, ledger.Warnings)
        })
        {
            var forged = returned.Frame with { PublicHandMemory = invalid };
            Check(PublicHandMemoryVisibility.Confirmed(forged) is null && CompactAdviceContextBuilder.Build(forged, Build)["public_hand_memory"] is null,
                "A ledger from another source or future boundary became a current hand fact.");
        }
        var copied = SnapshotCodec.Deserialize(SnapshotCodec.Serialize(returned.Frame));
        Check(JsonSerializer.Serialize(copied.PublicHandMemory) == JsonSerializer.Serialize(ledger) &&
            copied.PublicHandMemory!.Entries is IList<PublicHandMemoryEntry> { IsReadOnly: true } && copied.PublicHandMemory.Warnings is IList<string> { IsReadOnly: true },
            "The ledger codec discarded get-only fields or returned mutable collection aliases.");
        var rememberedDefinition = await catalog.GetByIdsAsync([RememberedCard], Build);
        var staleContext = CompactAdviceContextBuilder.Build(returned.Frame, Build, cards: rememberedDefinition,
            recentEvents: returned.Frame.RecentEvents);
        Check(staleContext["cards"]!.AsArray().Any(card => card?["id"]?.ToString() == RememberedCard) &&
            staleContext["recent_events"]!.AsArray().Any(observed => observed?["sequence"]?.GetValue<long>() > privatePoint.Frame.EventSequence &&
                observed?["card_id"]?.ToString() == RememberedCard), "The stale context did not actually carry later card definitions and public events.");
        var staleRejected = false;
        try { _ = new SnapshotCoach(privatePoint.Frame, staleContext, catalog, Build); }
        catch (ArgumentException) { staleRejected = true; }
        Check(staleRejected, "A later caller-supplied context retained future cards or events after only its memory field was removed.");
        pass("current memory binding rejects another match state or future sequence and the exact storage codec retains the deeply frozen evidence fields");

        var rejected = await PipelineAsync(returned, catalog, expectedEntries: 1, hardReject: true);
        Check(rejected.Advice is null && rejected.Archive is null && rejected.Line["Verification"]?.ToString() == "Invalid" && rejected.Line["AppliedActions"]?.GetValue<int>() == 0,
            "A remembered opponent hand card became a legal attack target or an archived executable model proposal.");
        pass("a model cannot target the remembered hidden entity or obtain its current effect and a rejected final proposal preserves paid request and tool traces without producing archived advice");
    }

    private const string Complete = "PowerProcessor.EndCurrentTaskList() - complete";
    private static IReadOnlyList<PublicHandMemoryEntry> Entries(GameSnapshot frame) => PublicHandMemoryVisibility.Confirmed(frame)?.Entries ?? [];
    private static void AssertEntry(GameSnapshot frame, PublicHandMemoryEntry entry, string from)
    {
        AssertHidden(frame, 41);
        Check(entry.EntityId == 41 && entry.CardId == RememberedCard && entry.ControllerId == 2 && entry.FromZone == from &&
            entry.PublicAtSequence > 0 && entry.PublicAtSequence <= entry.EnteredHandAtSequence && entry.EnteredHandAtSequence <= frame.EventSequence,
            "Memory is missing the exact entity public identity owner origin or causal event boundary.");
    }
    private static void AssertHidden(GameSnapshot frame, int id) => Check(frame.Entities[id].CardId is null &&
        frame.Entities[id].Tags.Keys.All(key => key is "CONTROLLER" or "ZONE" or "ZONE_POSITION" or "ENTITY_ID"),
        "Public identity memory was written back into a hidden entity or its private current attributes.");
    private sealed record Saved(string Database, GameSnapshot Frame, DecisionPoint Point);
    private sealed record Result(StrategyAdvice? Advice, ArchivedAdviceRecord? Archive, JsonNode Line, IReadOnlyList<string> Payloads);

    private static async Task<Result> PipelineAsync(Saved item, CardCatalog catalog, int expectedEntries,
        bool? expectVerified = true, bool hardReject = false, AdviceTaskKind task = AdviceTaskKind.Turn)
    {
        var path = Path.Combine(Path.GetDirectoryName(item.Database)!, "model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var original = SnapshotCodec.Serialize(item.Frame);
        var ids = item.Frame.Entities.Values.Select(entity => entity.CardId).OfType<string>().Concat(Entries(item.Frame).Select(entry => entry.CardId));
        var cards = await catalog.GetByIdsBatchedAsync(ids, Build);
        var context = CompactAdviceContextBuilder.Create(item.Frame, Build, task, cards: cards) with { KnowledgeAsOf = item.Point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        var memory = context.Snapshot["public_hand_memory"];
        Check((memory?["entries"]?.AsArray().Count ?? 0) == expectedEntries && context.TemplateVersion == (task == AdviceTaskKind.Review ? "hunter-v7/review" : "hunter-v21/turn"),
            "The actual compact projection lost its memory or prompt source boundary.");
        if (expectedEntries > 0)
        {
            var entry = memory!["entries"]!.AsArray().Single()!;
            Check(entry["entity_id"]?.GetValue<int>() == 41 && entry["card_id"]?.ToString() == RememberedCard &&
                entry["kind"]?.ToString() == "public_return" && entry["direct_reveal"] is null &&
                entry is JsonObject row && row.All(pair => pair.Key is "entity_id" or "card_id" or "controller" or "from" or "public_at_sequence" or "entered_hand_at_sequence" or "kind"),
                "The compact memory exposed current stats effects or unreviewed raw fields.");
        }
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(item.Frame, context.Snapshot, catalog, Build, toolCompleted: observations.Add);
        using var model = new Model(context.Snapshot["cards"]!.AsArray().Select(card => card!["id"]!.GetValue<string>()).ToArray(), hardReject);
        using var http = new HttpClient(model); StrategyAdvice? advice = null; var denied = false;
        try
        {
            advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://public-memory-fixture.invalid/v1/chat/completions"), "public-memory-fixture", 1),
                Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        }
        catch (InvalidDataException) when (hardReject) { denied = true; }
        Check(model.Calls == 2 && observations.Count == 4 && observations.All(tool => tool.Success && tool.ResultHash == Hash(tool.ResultJson!) &&
            tool.RequestId == usages[0].RequestId && tool.AnalysisRunId == usages[0].AnalysisRunId && tool.EventSequence == item.Frame.EventSequence) &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30 && usage.PromptTemplateVersion == context.TemplateVersion) &&
            budget.Snapshot(item.Frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } && denied == hardReject &&
            SnapshotCodec.Serialize(item.Frame) == original && model.Payloads.All(payload => !DataPayload(payload).Contains(PrivateCard, StringComparison.Ordinal)),
            "Actual model tool evidence usage redaction host rejection or immutable frame source changed.");
        Check(model.Snapshot!["public_hand_memory"]?["entries"]?.AsArray().Count == memory?["entries"]?.AsArray().Count &&
            model.Effect?["error"] is not null && model.Effect.ToJsonString().Contains(RememberedCard, StringComparison.Ordinal) == false,
            "Lazy get_snapshot diverged from initial memory or get_entity_effect treated it as a visible current card.");
        if (expectedEntries > 0)
        {
            var printed = model.Cards!["cards"]!.AsArray().Single(card => card?["Id"]?.ToString() == RememberedCard)!;
            var actual = cards.Single(card => card.Id == RememberedCard);
            Check(printed["Text"]?.ToString() == actual.Text && printed["Cost"]?.GetValue<int>() == actual.Cost &&
                printed["Cost"]?.GetValue<int>() != 0 && !printed.ToJsonString().Contains("ATTACHED", StringComparison.Ordinal),
                "Base card lookup returned remembered private current cost or an attached effect.");
        }
        var archivePath = Path.Combine(path, "advice.db"); ArchivedAdviceRecord? loaded = null;
        if (hardReject) Check(advice is null && !File.Exists(archivePath), "Rejected hidden targeting produced an output or archive.");
        else
        {
            await File.WriteAllTextAsync(Path.Combine(path, "actual-host-line.json"), JsonSerializer.Serialize(new
            {
                advice?.Verification, advice?.VerifiedLethal, advice?.Actions, advice?.HostUnsupportedMechanics,
                advice?.HostAssumptions, Line = model.Line
            }));
            Check(advice is not null && !advice.VerifiedLethal && (expectVerified is null || (expectVerified.Value ? advice.Verification == "verified" && advice.Actions.Count == 1 : advice.Verification != "verified")) &&
                advice.AnalysisRunId == usages[0].AnalysisRunId && advice.RequestIds.SequenceEqual(usages.Select(usage => usage.RequestId)),
                "The actual host trusted a model lethal claim or changed the current facts into an executable future action.");
            var draft = AdviceArchiveStore.Prepare(new(item.Frame.MatchId, item.Frame.StateVersion, item.Frame.EventSequence, Build, advice!.TemplateVersion!, advice.Summary)
            {
                Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
                Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks,
                Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
                Evidence = observations.Select(tool => new AdviceToolEvidence(tool.Name, "公开身份记忆；不是当前动态属性或可执行的隐藏目标。", tool.ResultHash)).ToArray(),
                Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
                LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
                HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns,
                HostUnsupportedMechanics = advice.HostUnsupportedMechanics, HostLimitationsTruncated = advice.HostLimitationsTruncated
            }, [Key]);
            var store = new AdviceArchiveStore(archivePath); await store.InitializeAsync(); var saved = await store.SaveAsync(draft);
            loaded = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing remembered-hand advice.");
            Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) && loaded.Advice.KnowledgeAsOf == item.Point.RecordedAt &&
                loaded.Advice.VisibilityPolicy == context.VisibilityPolicy && loaded.Advice.Evidence.Select(tool => tool.ResultHash).SequenceEqual(observations.Select(tool => tool.ResultHash)) &&
                !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal), "The archive lost immutable source policy evidence limits or redaction.");
        }
        var tracePath = Path.Combine(path, "trace.db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usages.Select(usage => new ModelRequestTrace(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model, usage.Round,
            usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = Build, Task = context.Task, PromptTemplateVersion = usage.PromptTemplateVersion!, AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence }),
        observations.Select(tool => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), item.Frame.MatchId, item.Frame.StateVersion, tool.Name, tool.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = tool.ResultHash, Model = tool.Model, AnalysisRunId = tool.AnalysisRunId, RequestId = tool.RequestId, ToolCallId = tool.ToolCallId, EventSequence = tool.EventSequence }));
        var reopened = new AnalysisTraceStore(tracePath); var totals = await reopened.GetBudgetTotalsAsync(item.Frame.MatchId); var tools = await reopened.ListToolsAsync(item.Frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && tools.Count == 4 && tools.All(tool => tool.RequestId == usages[0].RequestId &&
            tool.AnalysisRunId == usages[0].AnalysisRunId && tool.EventSequence == item.Frame.EventSequence) && !JsonSerializer.Serialize(tools).Contains(Key, StringComparison.Ordinal),
            "Trace reopening lost real paid requests or stored raw secret data instead of evidence hashes.");
        var exactAgain = await new SessionStore(item.Database).GetHistoricalDecisionAsync(item.Point.Id);
        Check(exactAgain is not null && exactAgain.Point == item.Point && Hash(SnapshotCodec.Serialize(exactAgain.Context.Snapshot)) == Hash(original),
            "A tool or later model request changed the saved exact source memory.");
        return new(advice, loaded, model.Line!, model.Payloads.AsReadOnly());
    }

    private sealed class Replay(string directory, DeckTrackingEnricher enricher)
    {
        public PowerLogStateEngine Engine { get; } = new(1, "public-hand-" + Guid.NewGuid().ToString("N"));
        public GameSnapshot Frame { get; private set; } = null!;
        private readonly string _database = Path.Combine(directory, "session-" + Guid.NewGuid().ToString("N") + ".db");
        private SessionRecorder? _recorder;
        private long _line;
        public static async Task<Replay> CreateAsync(string directory, CardCatalog catalog)
        {
            var result = new Replay(directory, await DeckTrackingEnricher.CreateAsync(catalog, null, Build));
            result.Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
                "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=5", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
                "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
                "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=ATK value=0", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0",
                "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=ATK value=0", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=ARMOR value=0",
                "FULL_ENTITY - Creating ID=30 CardID=CS2_171", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=1", "tag=HEALTH value=1", "tag=DAMAGE value=0", "tag=COST value=1", "tag=CARDRACE value=BEAST", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1",
                "FULL_ENTITY - Creating ID=41 CardID=" + RememberedCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=9", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=COST value=0",
                "FULL_ENTITY - Creating ID=46 CardID=" + PrivateCard, "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=ATK value=999", "tag=COST value=0",
                "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", Complete);
            return result;
        }
        public void Feed(params string[] lines) { foreach (var line in lines) { _line++; if (Engine.ProcessLine(line)) Refresh(); } }
        public void Refresh()
        {
            var original = Engine.Snapshot(); Frame = enricher.Observe(original);
            Check(JsonSerializer.Serialize(original.PublicHandMemory) == JsonSerializer.Serialize(Frame.PublicHandMemory),
                "The deck enricher rebuilt or altered raw-engine public identity proof.");
        }
        public async Task<Saved> SaveAsync()
        {
            var store = new SessionStore(_database); await store.InitializeAsync();
            _recorder ??= new SessionRecorder(store, new(null, Build, 1)); _recorder.Observe(Frame, _line); await _recorder.CompleteAsync();
            var point = (await store.ListDecisionPointsAsync(Frame.MatchId, true)).Single(point => point.StateVersion == Frame.StateVersion && point.EventSequence == Frame.EventSequence);
            var exact = await new SessionStore(_database).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing saved memory point.");
            Check(SnapshotCodec.Serialize(Frame) == SnapshotCodec.Serialize(exact.Context.Snapshot) && !exact.HasUnattributedEvents,
                "Recorder or exact reopening changed public memory or its observation boundary.");
            return new(_database, exact.Context.Snapshot, point);
        }
    }
    private static JsonArray Actions(bool hiddenTarget) => new(new JsonObject
        { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = hiddenTarget ? 41 : 21 });
    private sealed class Model(string[] ids, bool hiddenTarget) : HttpMessageHandler
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
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[]
            {
                Tool("memory-snapshot", "get_snapshot", new { }), Tool("memory-cards", "get_cards", new { card_ids = ids }),
                Tool("memory-effect", "get_entity_effect", new { entity_id = 41 }), Tool("memory-line", "evaluate_line", new { actions = Actions(hiddenTarget) })
            } };
            else
            {
                var messages = payload["messages"]!.AsArray();
                JsonNode Read(string id) => JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == id)!["content"]!.GetValue<string>())!;
                Snapshot = Read("memory-snapshot"); Cards = Read("memory-cards"); Effect = Read("memory-effect"); Line = Read("memory-line");
                message = new { role = "assistant", content = JsonSerializer.Serialize(new
                {
                    summary = "公开身份记忆不证明当前隐藏属性。" + Key, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(),
                    assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions = Actions(hiddenTarget), verified_lethal = true, verification = "verified"
                }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    private static string DataPayload(string text) => new JsonArray(JsonNode.Parse(text)!["messages"]!.AsArray().Where(message => message?["role"]?.ToString() != "system").Select(message => message!.DeepClone()).ToArray()).ToJsonString();
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
