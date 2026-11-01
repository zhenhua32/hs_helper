using System.Collections.Frozen;
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

internal static class EnchantmentPrivacyIntegration
{
    private const string Build = "255790", Key = "fixture-enchantment-privacy-key";
    private static readonly string[] BonusTags = ["ATTACHED", "40", "ATK", "47", "HEALTH", "45", "DAMAGE", "44", "COST", "48",
        "CHARGE", "197", "ELUSIVE", "1211", "WINDFURY", "189", "DIVINE_SHIELD", "194", "IS_NIGHTMARE_BONUS", "3567", "TAG_SCRIPT_DATA_NUM_6", "2921",
        "CRAFTABLE_FIRST_HALF_DBID", "4572", "CRAFTABLE_SECOND_HALF_DBID", "4573"];

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        var definitions = new List<JsonElement>();
        foreach (var name in new[] { "beast-hunter", "dark-gift-static", "lethal-search", "native-definitions" })
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
                "tests", "HsHelper.Rules.Tests", "Fixtures", name + "-255790.json")));
            Check(document.RootElement.GetProperty("build").GetString() == Build && document.RootElement.GetProperty("sourceSha256").GetString()?.Length == 64,
                "The integration lost its fixed primary card definitions.");
            definitions.AddRange(document.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()));
        }
        var cards = definitions.GroupBy(card => card.GetProperty("id").GetString(), StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var directory = Path.Combine(root, "enchantment-privacy", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var catalog = await CatalogAsync(directory, cards);

        foreach (var gift in new[] { "EDR_100t13e", "EDR_100t1e" })
        foreach (var zone in new[] { "HAND", "DECK", "SETASIDE" })
        foreach (var enchantmentZone in new[] { "PLAY", "GRAVEYARD" })
        {
            var spec = new Spec(gift, zone) { EnchantmentZone = enchantmentZone };
            var frame = Engine(spec).Snapshot(); AssertPrivate(frame, gift, historicalIdentityAllowed: false);
            await PipelineAsync(directory, catalog, frame, gift, expectedVisible: false);
        }
        pass("private hand deck and setaside targets mask both fixed gift identities and bonuses even when the enchantment itself is PLAY or GRAVEYARD through real tools HTTP history and archival storage");

        var pending = Engine(new("EDR_100t13e", "HAND") { IncludeGift = false });
        Feed(pending, "FULL_ENTITY - Creating ID=50 CardID=EDR_100t13e", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY");
        AssertPrivate(pending.Snapshot(), "EDR_100t13e", false);
        Feed(pending, "tag=CARDTYPE value=ENCHANTMENT"); AssertPrivate(pending.Snapshot(), "EDR_100t13e", false);
        Feed(pending, "tag=ATTACHED value=61", "PowerProcessor.EndCurrentTaskList() - complete"); AssertPrivate(pending.Snapshot(), "EDR_100t13e", false);
        Feed(pending, "FULL_ENTITY - Creating ID=61 CardID=DINO_421", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION",
            "PowerProcessor.EndCurrentTaskList() - complete"); AssertPrivate(pending.Snapshot(), "EDR_100t13e", false);
        await PipelineAsync(directory, catalog, pending.Snapshot(), "EDR_100t13e", expectedVisible: false);
        pass("fresh creation before type or attachment and an as-yet unknown target never freeze a private gift identity into the later event or saved model view");

        var lifecycle = Engine(new("EDR_100t13e", "PLAY")); AssertVisible(lifecycle.Snapshot(), "EDR_100t13e");
        Feed(lifecycle, "TAG_CHANGE Entity=60 tag=ZONE value=HAND", "PowerProcessor.EndCurrentTaskList() - complete");
        AssertPrivate(lifecycle.Snapshot(), "EDR_100t13e", historicalIdentityAllowed: true);
        var ownerChange = Engine(new("EDR_100t13e", "HAND") { OwnTarget = true }); AssertVisible(ownerChange.Snapshot(), "EDR_100t13e");
        Feed(ownerChange, "TAG_CHANGE Entity=60 tag=CONTROLLER value=2", "PowerProcessor.EndCurrentTaskList() - complete");
        AssertPrivate(ownerChange.Snapshot(), "EDR_100t13e", true);
        var attachedChange = Engine(new("EDR_100t13e", "PLAY")); AssertVisible(attachedChange.Snapshot(), "EDR_100t13e");
        Feed(attachedChange, "FULL_ENTITY - Creating ID=61 CardID=DINO_421", "tag=CONTROLLER value=2", "tag=ZONE value=DECK", "tag=CARDTYPE value=MINION",
            "TAG_CHANGE Entity=50 tag=ATTACHED value=61", "PowerProcessor.EndCurrentTaskList() - complete");
        AssertPrivate(attachedChange.Snapshot(), "EDR_100t13e", true);
        // Older stored/external snapshots may still carry the raw row. Projection must not
        // rely on the current parser already having removed it or rewrite the source payload.
        var legacy = Engine(new("EDR_100t13e", "HAND") { IncludeGift = false }).Snapshot();
        var legacyRows = legacy.Entities.ToDictionary();
        legacyRows[50] = new(50, "EDR_100t13e", EntityKind.Card, new Dictionary<string, string>
        {
            ["CONTROLLER"] = "2", ["ZONE"] = "PLAY", ["CARDTYPE"] = "ENCHANTMENT", ["ATTACHED"] = "60",
            ["WINDFURY"] = "1", ["DIVINE_SHIELD"] = "1", ["IS_NIGHTMARE_BONUS"] = "1", ["TAG_SCRIPT_DATA_NUM_6"] = "120225"
        }.ToFrozenDictionary(StringComparer.Ordinal));
        legacy = legacy with { Entities = legacyRows.ToFrozenDictionary(), RecentEvents = Array.AsReadOnly(legacy.RecentEvents.Append(
            new ObservedGameEvent(legacy.EventSequence, "ZoneChanged", 50, 2, "EDR_100t13e", null, "PLAY", "旧版合成附魔事件，未记录可见性证明")).ToArray()) };
        var legacyBytes = SnapshotCodec.Serialize(legacy);
        Check(legacyBytes.Contains("EDR_100t13e", StringComparison.Ordinal), "The legacy input did not actually contain an unsafe original row.");
        await PipelineAsync(directory, catalog, legacy, "EDR_100t13e", expectedVisible: false, analyzeOriginal: true);
        Check(SnapshotCodec.Serialize(legacy) == legacyBytes, "Deriving a safe model view rewrote the historical source payload.");
        pass("current attachment privacy is recomputed after target hiding controller changes and retargeting without reusing a cached public identity or current bonus association");

        var unknown = Engine(new("EDR_100t13e", "HAND")); unknown.SetLocalPlayerId(null);
        AssertPrivate(unknown.Snapshot(), "EDR_100t13e", false);
        var spectator = Engine(new("EDR_100t1e", "HAND")); Feed(spectator, "Begin Spectating");
        AssertPrivate(spectator.Snapshot(), "EDR_100t1e", false);
        var spectatorOwn = Engine(new("EDR_100t13e", "HAND") { OwnTarget = true }); Feed(spectatorOwn, "Begin Spectating");
        AssertPrivate(spectatorOwn.Snapshot(), "EDR_100t13e", historicalIdentityAllowed: true);
        pass("unconfirmed and spectator views cannot borrow a previous local controller or a public enchantment zone to expose private attached bonuses");

        var own = Engine(new("EDR_100t13e", "HAND") { OwnTarget = true }).Snapshot(); AssertVisible(own, "EDR_100t13e");
        await PipelineAsync(directory, catalog, own, "EDR_100t13e", expectedVisible: true);
        var optionEngine = Engine(new("EDR_100t1e", "SETASIDE") { OwnTarget = true });
        Feed(optionEngine, "FULL_ENTITY - Creating ID=31 CardID=CORE_DS1_184", "tag=CONTROLLER value=1", "tag=ZONE value=GRAVEYARD", "tag=CARDTYPE value=SPELL",
            "GameState.DebugPrintEntityChoices() - id=8 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1",
            "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=31 cardId=CORE_DS1_184 player=1]",
            "GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=60 zone=SETASIDE cardId=DINO_421 player=1]", "PowerProcessor.EndCurrentTaskList() - complete");
        var option = optionEngine.Snapshot();
        Check(ChoiceVisibility.ConfirmedOwn(option) is { IsReady: true } && option.Entities[60].CardId == "DINO_421", "The positive option must be an actual ready parser choice.");
        AssertVisible(option, "EDR_100t1e"); await PipelineAsync(directory, catalog, option, "EDR_100t1e", expectedVisible: true, choice: true);
        pass("confirmed own hand and actual ready current-choice targets preserve legitimate attached identity in production tool requests and source-bound saved advice");

        var aura = Engine(new("EDR_100t13e", "HAND") { IncludeGift = false, Aura = true }).Snapshot();
        var auraResult = await PipelineAsync(directory, catalog, aura, "EDR_100t13e", expectedVisible: false);
        Check(auraResult.ToolLine["Verification"]?.ToString() == "Verified" && auraResult.ToolLine["IsLethal"]?.GetValue<bool>() == true &&
            auraResult.Advice.Verification == "verified" && auraResult.Advice.VerifiedLethal && auraResult.Archive.Advice.VerifiedLethal,
            "The exact native aura positive did not double an observed three damage once.");
        pass("the exact native Goldrinn context doubles an observed Beast attack from three to six through the real tool final host and reopened archive");

        foreach (var corruption in new[] { "dbf", "text", "raw-mechanics" })
        {
            var altered = cards.Select(card => JsonNode.Parse(card.GetRawText())!.AsObject()).ToArray();
            var native = altered.Single(card => card["id"]?.ToString() == "EDR_480");
            if (corruption == "dbf") native["dbfId"] = 777777;
            if (corruption == "text") native["text"] = "在其他友方随从攻击时，使其死亡。";
            if (corruption == "raw-mechanics") native["mechanics"] = new JsonArray("AURA", "DEATHRATTLE");
            var brokenCatalog = await CatalogAsync(directory, altered.Select(card => JsonSerializer.SerializeToElement(card)).ToArray());
            var broken = await PipelineAsync(directory, brokenCatalog, aura, "EDR_100t13e", expectedVisible: false);
            Check(broken.ToolLine["Verification"]?.ToString() != "Verified" && broken.ToolLine["IsLethal"]?.GetValue<bool>() == false &&
                broken.ToolLine["AppliedActions"]?.GetValue<int>() == 0 && broken.Advice.Actions.Count == 0 && !broken.Advice.VerifiedLethal &&
                broken.Archive.Advice.Actions.Count == 0 && !broken.Archive.Advice.VerifiedLethal &&
                broken.Advice.HostUnsupportedMechanics?.Contains("UNAUDITED_NATIVE_DEFINITION") == true,
                "An actual corrupted same-ID native aura became verified through the tool or final host: " + corruption);
        }
        pass("real imported native aura DBF text and raw-mechanics corruption is rejected before applying actions rather than trusting a forged model verdict or a bare card ID");

        await HistoricalEventProofsAsync(directory);
        var approved = new List<ArchivedAdviceRecord>(); ArchivedAdviceRecord? legacyRecord = null;
        AdviceArchiveStore? conversationStore = null;
        var continuationFrame = Engine(new("EDR_100t13e", "HAND") { IncludeGift = false }).Snapshot();
        var continuation = await PipelineAsync(directory, catalog, continuationFrame, "EDR_100t13e", expectedVisible: false, attach: async context =>
        {
            Check(context.VisibilityPolicy == "attachments-v1", "The production context did not stamp its visibility policy.");
            var clock = new ConversationClock(context.KnowledgeAsOf!.Value);
            conversationStore = new AdviceArchiveStore(Path.Combine(directory, "policy-history.db"), clock); await conversationStore.InitializeAsync();
            AdviceRecordDraft Draft(string summary, string? policy) => new(context.MatchId, context.StateVersion, context.EventSequence!.Value, Build, context.TemplateVersion, summary)
            { Task = context.Task, KnowledgeAsOf = context.KnowledgeAsOf, VisibilityPolicy = policy };
            for (var index = 0; index < 6; index++)
            {
                clock.Now = context.KnowledgeAsOf.Value.AddMinutes(index + 1);
                approved.Add(await conversationStore.SaveAsync(Draft("NEW_POLICY_" + index, context.VisibilityPolicy)));
            }
            for (var index = 0; index < 20; index++)
            {
                clock.Now = context.KnowledgeAsOf.Value.AddMinutes(7).AddSeconds(index);
                var record = await conversationStore.SaveAsync(Draft("OLD_POLICY_PRIVATE_EDR_100t13e_" + index, null)); legacyRecord ??= record;
            }
            clock.Now = context.KnowledgeAsOf.Value.AddMinutes(8);
            await conversationStore.SaveAsync(Draft("WRONG_POLICY_PRIVATE_EDR_100t13e", "attachments-v0"));
            var attached = await ConversationHistoryBuilder.AttachAsync(context, conversationStore, null, context.KnowledgeAsOf.Value.AddMinutes(9));
            Check(attached.ConversationTruncated && attached.Conversation.Select(turn => turn.ArchiveId).SequenceEqual(approved.Skip(2).Select(record => record.Id)) &&
                attached.Conversation.Select(turn => turn.Answer).SequenceEqual(Enumerable.Range(2, 4).Select(index => "NEW_POLICY_" + index)) &&
                attached.Conversation.Select(turn => turn.RecordedAt).SequenceEqual(attached.Conversation.Select(turn => turn.RecordedAt).Order()),
                "Policy filtering happened after LIMIT or changed the eligible history order/truncation.");
            return attached;
        });
        Check(continuation.Advice.ConversationArchiveIds.SequenceEqual(approved.Skip(2).Select(record => record.Id)) &&
            continuation.Payloads.All(payload => !payload.Contains("OLD_POLICY_PRIVATE_", StringComparison.Ordinal) && !payload.Contains("WRONG_POLICY_PRIVATE_", StringComparison.Ordinal)) &&
            Enumerable.Range(2, 4).All(index => continuation.Payloads[0].Contains("NEW_POLICY_" + index, StringComparison.Ordinal)) &&
            !continuation.Payloads[0].Contains("NEW_POLICY_0", StringComparison.Ordinal) && !continuation.Payloads[0].Contains("NEW_POLICY_1", StringComparison.Ordinal) &&
            continuation.Archive.Advice.VisibilityPolicy == "attachments-v1",
            "Old unsafe model opinions reached actual HTTP or the eligible current history lost provenance.");
        var oldRead = await conversationStore!.GetAsync(legacyRecord!.Id) ?? throw new InvalidOperationException("The old original archive was removed.");
        Check(oldRead.Advice.VisibilityPolicy is null && oldRead.Advice.Summary == legacyRecord.Advice.Summary &&
            (await conversationStore.SaveAsync(oldRead.Advice, oldRead.Id)).Id == legacyRecord.Id,
            "Filtering a conversation rewrote or made the immutable old archive unreadable.");
        pass("recorded event visibility and current archive policy protect real follow-up HTTP while preserving old original records and eligible chronological four-turn truncation before SQL LIMIT");
    }

    private sealed record Spec(string Gift, string TargetZone)
    {
        public bool IncludeGift { get; init; } = true;
        public bool OwnTarget { get; init; }
        public bool Aura { get; init; }
        public string EnchantmentZone { get; init; } = "PLAY";
    }
    private sealed record Completed(JsonNode ToolLine, StrategyAdvice Advice, ArchivedAdviceRecord Archive, IReadOnlyList<string> Payloads);
    private sealed class ConversationClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task HistoricalEventProofsAsync(string directory)
    {
        var engine = Engine(new("EDR_100t13e", "PLAY") { OwnTarget = true });
        var publicFrame = engine.Snapshot(); var path = Path.Combine(directory, "event-proofs.db");
        var store = new SessionStore(path); await store.InitializeAsync(); var recorder = new SessionRecorder(store, new(null, Build, 1));
        recorder.Observe(publicFrame, 1); await recorder.FlushAsync();
        var first = (await store.ListDecisionPointsAsync(publicFrame.MatchId, true)).Single();
        var publicContext = (await store.GetHistoricalDecisionAsync(first.Id))!.Context;
        var publicEvent = publicContext.RecentEvents.Single(entry => entry.EntityId == 50 && entry.Kind == "EntityObserved");
        Feed(engine, "TAG_CHANGE Entity=60 tag=ZONE value=HAND", "TAG_CHANGE Entity=50 tag=ZONE_POSITION value=1", "PowerProcessor.EndCurrentTaskList() - complete");
        var ownFrame = engine.Snapshot(); recorder.Observe(ownFrame, 2); await recorder.CompleteAsync();
        var latest = (await store.ListDecisionPointsAsync(publicFrame.MatchId, true)).OrderBy(point => point.StateVersion).Last();
        var ownContext = (await store.GetHistoricalDecisionAsync(latest.Id))!.Context;
        var ownEvent = ownContext.RecentEvents.Single(entry => entry.EntityId == 50 && entry.Kind == "EntityChanged");
        Check(publicEvent.DataJson.Contains("Public", StringComparison.Ordinal) && ownEvent.DataJson.Contains("PlayerOne", StringComparison.Ordinal),
            "Actual Recorder attachment diffs did not freeze their observation audience.");
        var visible = HistoricalEventVisibility.Project(ownFrame, [publicEvent, ownEvent]);
        Check(visible.All(entry => entry.CardId == "EDR_100t13e"), "Recorded public or current-own historical identities were erased.");
        engine.SetLocalPlayerId(2); var opposite = engine.Snapshot();
        var switched = HistoricalEventVisibility.Project(opposite, [publicEvent, ownEvent]);
        Check(switched[0].CardId == "EDR_100t13e" && switched[1].CardId is null && switched[1].DataJson == "{}",
            "Switching perspective borrowed the original owner's private event proof.");
        SessionEvent Legacy(SessionEvent entry)
        { var data = JsonNode.Parse(entry.DataJson)!.AsObject(); data.Remove("IdentityVisibility"); return entry with { DataJson = data.ToJsonString() }; }
        var oldRows = new[] { Legacy(publicEvent), Legacy(ownEvent) }; var original = JsonSerializer.Serialize(oldRows);
        var delta = JsonNode.Parse(oldRows[1].DataJson)!["ChangedTags"]!.AsObject();
        Check(!opposite.Entities.ContainsKey(50) && !delta.ContainsKey("CARDTYPE") && !delta.ContainsKey("202") &&
            !delta.ContainsKey("ATTACHED") && !delta.ContainsKey("40"),
            "The legacy delta must rely on its earlier batch type evidence, not a still-visible source or repeated attachment tags.");
        var masked = HistoricalEventVisibility.Project(opposite, oldRows);
        if (masked.Any(entry => entry.CardId is not null || entry.DataJson != "{}"))
            await File.WriteAllTextAsync(Path.Combine(directory, "legacy-attachment-event-proof.json"), JsonSerializer.Serialize(new
            {
                opposite.LocalPlayerId, CurrentSourcePresent = opposite.Entities.ContainsKey(50), Original = oldRows, Derived = masked,
                SourceUnchanged = original == JsonSerializer.Serialize(oldRows)
            }));
        Check(masked.All(entry => entry.CardId is null && entry.DataJson == "{}") && JsonSerializer.Serialize(oldRows) == original,
            "Legacy attachment DataJson restored an unproven identity or was rewritten in place.");
        var truncated = HistoricalEventVisibility.Project(opposite, [oldRows[1]]);
        Check(truncated.Single().CardId is null && truncated.Single().DataJson == "{}",
            "A truncated legacy batch without any type evidence treated an omitted source as an ordinary public card.");
        var legacyDelta = new ObservedGameEvent(opposite.EventSequence, "AttributeChanged", 50, 1, "EDR_100t13e", null, null, "旧版附魔变化");
        var derived = AttachmentVisibility.Project(opposite with { RecentEvents = [legacyDelta] });
        Check(derived.RecentEvents.Single().CardId is null && legacyDelta.CardId == "EDR_100t13e",
            "Core legacy event projection borrowed an unknown actor's identity or rewrote the original event.");
        var ordinary = oldRows[1] with { EntityId = 30, CardId = "CS2_171" };
        Check(HistoricalEventVisibility.Project(opposite, [ordinary]).Single().CardId == "CS2_171",
            "Legacy deltas for a currently known ordinary public minion were unnecessarily erased.");
    }
    private static async Task<CardCatalog> CatalogAsync(string directory, JsonElement[] cards)
    {
        var catalog = new CardCatalog(Path.Combine(directory, "cards-" + Guid.NewGuid().ToString("N") + ".db")); await catalog.InitializeAsync();
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cards));
        await catalog.ImportAsync(stream, Build, source: "fixture://attached-privacy-native-audit"); return catalog;
    }

    private static void AssertPrivate(GameSnapshot frame, string gift, bool historicalIdentityAllowed)
    {
        if (frame.Entities.TryGetValue(50, out var source))
            Check(source.CardId is null && !source.Tags.Keys.Any(key => BonusTags.Contains(key, StringComparer.Ordinal)),
                "A hidden target exposed its attached identity or bonus association.");
        var view = CompactAdviceContextBuilder.Build(frame, Build);
        var row = view["entities"]!.AsArray().SingleOrDefault(entity => entity?["id"]?.GetValue<int>() == 50);
        Check(row?["card_id"] is null && (row?["tags"] is not JsonObject tags || !tags.Any(pair => BonusTags.Contains(pair.Key, StringComparer.Ordinal))),
            "Compact context restored an attached private identity or bonus.");
        if (!historicalIdentityAllowed)
            Check(!SnapshotCodec.Serialize(frame).Contains(gift, StringComparison.Ordinal) && !view.ToJsonString().Contains(gift, StringComparison.Ordinal),
                "A gift that was never visible was frozen into a snapshot or event history.");
    }
    private static void AssertVisible(GameSnapshot frame, string gift) => Check(frame.Entities.TryGetValue(50, out var source) && source.CardId == gift &&
        source.GetIntTag("ATTACHED") == 60 && CompactAdviceContextBuilder.Build(frame, Build)["entities"]!.AsArray().Any(entity =>
            entity?["id"]?.GetValue<int>() == 50 && entity["card_id"]?.ToString() == gift), "A legitimately visible attachment was suppressed.");

    private static async Task<Completed> PipelineAsync(string directory, CardCatalog catalog, GameSnapshot frame, string gift, bool expectedVisible, bool choice = false,
        Func<AdviceContext, Task<AdviceContext>>? attach = null, bool analyzeOriginal = false)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var original = SnapshotCodec.Serialize(frame);
        var sessionsPath = Path.Combine(path, "history.db"); var sessions = new SessionStore(sessionsPath); await sessions.InitializeAsync();
        var recorder = new SessionRecorder(sessions, new(null, Build, 1)); recorder.Observe(frame, 1); await recorder.CompleteAsync();
        var point = (await sessions.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        var historical = await new SessionStore(sessionsPath).GetHistoricalDecisionAsync(point.Id) ?? throw new InvalidOperationException("Missing exact recorded privacy frame.");
        Check(SnapshotCodec.Serialize(historical.Context.Snapshot) == SnapshotCodec.Serialize(AttachmentVisibility.Project(frame)) && !historical.HasUnattributedEvents,
            "New recording did not retain the safe exact observer view or its source boundary.");
        var reopened = analyzeOriginal ? frame : historical.Context.Snapshot;
        var cards = await catalog.GetByIdsBatchedAsync(reopened.Entities.Values.Select(entity => entity.CardId).OfType<string>(), Build);
        var context = CompactAdviceContextBuilder.Create(reopened, Build, choice ? AdviceTaskKind.Choice : AdviceTaskKind.Turn, cards: cards)
            with { KnowledgeAsOf = point.RecordedAt, AsOf = DateTimeOffset.UtcNow };
        if (attach is not null) context = await attach(context);
        var ids = context.Snapshot["cards"]!.AsArray().Select(card => card!["id"]!.GetValue<string>()).ToArray();
        var observations = new List<CoachToolObservation>(); var usages = new List<ModelUsage>(); var budget = new AnalysisBudget();
        var coach = new SnapshotCoach(reopened, context.Snapshot, catalog, Build, toolCompleted: observations.Add);
        using var model = new Model(ids, choice); using var http = new HttpClient(model);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://attached-privacy-fixture.invalid/v1/chat/completions"), "attached-privacy-fixture", 1),
            Key, context, coach.ExecuteAsync, () => true, capabilities: coach.Capabilities(budget, usages.Add));
        Check(model.Calls == 2 && observations.Count == 4 && observations.All(item => item.Success && item.ResultHash?.Length == 64 && item.RequestId == usages[0].RequestId &&
            item.AnalysisRunId == advice.AnalysisRunId && item.EventSequence == frame.EventSequence) &&
            usages.Count == 2 && usages.All(usage => usage.Success && usage.ChargedTokens == 30 && usage.CardBuild == Build && usage.PromptTemplateVersion == context.TemplateVersion) &&
            budget.Snapshot(frame.MatchId) is { RequestsStarted: 2, CompletedRequests: 2, ChargedTokens: 60, InFlightRequests: 0 } &&
            advice.RequestIds.SequenceEqual(usages.Select(usage => usage.RequestId)) && advice.KnowledgeAsOf == point.RecordedAt && original == SnapshotCodec.Serialize(frame),
            "Actual HTTP tool source hashes paid usage history cutoff or immutable frame changed.");
        foreach (var item in observations)
            Check(item.ResultHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.ResultJson!))), "A tool result hash no longer matches its actual sanitized response.");
        // Static system instructions legitimately name the public supported card IDs.
        // Privacy assertions concern current data, tool arguments/results and conversation.
        var sentData = model.Payloads.Select(DataPayload).ToArray();
        Check(sentData.Any(payload => payload.Contains(gift, StringComparison.Ordinal)) == expectedVisible &&
            observations.Any(item => item.ResultJson!.Contains(gift, StringComparison.Ordinal)) == expectedVisible &&
            !sentData.Any(payload => payload.Contains("IdentityVisibility", StringComparison.Ordinal)),
            "The actual HTTP payload or tool result leaked a private attachment or dropped a legitimate visible identity.");
        if (!expectedVisible)
        {
            Check(!sentData.Any(payload => payload.Contains("120225", StringComparison.Ordinal) || payload.Contains("113797", StringComparison.Ordinal)),
                "A private gift button escaped without its card ID.");
            var entities = model.Line?["Result"]?["Entities"]?.AsArray();
            var row = entities?.SingleOrDefault(entity => entity?["EntityId"]?.GetValue<int>() == 50);
            Check(row?["CardId"] is null && (row?["Flags"] is not JsonObject flags || !flags.Any(pair => BonusTags.Contains(pair.Key, StringComparer.Ordinal))),
                "Rule-result projection restored an attached private bonus.");
        }
        var draft = AdviceArchiveStore.Prepare(new(frame.MatchId, frame.StateVersion, frame.EventSequence, Build, advice.TemplateVersion!, advice.Summary)
        {
            Task = context.Task, VisibilityPolicy = context.VisibilityPolicy, ConversationArchiveIds = advice.ConversationArchiveIds,
            Reasons = advice.Reasons, Alternative = advice.Alternative, Risks = advice.Risks, Verification = advice.Verification, VerifiedLethal = advice.VerifiedLethal,
            Actions = advice.Actions.Select(action => new ArchivedAdviceAction(action.Kind, action.SourceEntityId, action.TargetEntityId)).ToArray(),
            Evidence = observations.Select(item => new AdviceToolEvidence(item.Name, "实际已过滤工具结果；当前事实以主机为准。", item.ResultHash)).ToArray(),
            Model = advice.Model, AnalysisRunId = advice.AnalysisRunId, RequestIds = advice.RequestIds, KnowledgeAsOf = advice.KnowledgeAsOf,
            LimitationsVersion = advice.LimitationsVersion, Assumptions = advice.Assumptions, Unknowns = advice.Unknowns, UnsupportedMechanics = advice.UnsupportedMechanics,
            HostRisks = advice.HostRisks, HostAssumptions = advice.HostAssumptions, HostUnknowns = advice.HostUnknowns, HostUnsupportedMechanics = advice.HostUnsupportedMechanics,
            HostLimitationsTruncated = advice.HostLimitationsTruncated
        }, [Key]);
        var archivePath = Path.Combine(path, "advice.db"); var archive = new AdviceArchiveStore(archivePath); await archive.InitializeAsync(); var saved = await archive.SaveAsync(draft);
        var loaded = await new AdviceArchiveStore(archivePath).GetAsync(saved.Id) ?? throw new InvalidOperationException("Missing attachment-safe advice.");
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(loaded.Advice) &&
            JsonSerializer.Serialize(loaded).Contains(gift, StringComparison.Ordinal) == expectedVisible && !JsonSerializer.Serialize(loaded).Contains(Key, StringComparison.Ordinal) &&
            loaded.Advice.AnalysisRunId == advice.AnalysisRunId && loaded.Advice.RequestIds!.SequenceEqual(advice.RequestIds) && loaded.Advice.KnowledgeAsOf == point.RecordedAt &&
            loaded.Advice.Evidence.Select(item => item.ResultHash).SequenceEqual(observations.Select(item => item.ResultHash)),
            "Archival preparation restored private evidence or lost host result provenance.");
        var tracePath = Path.Combine(path, "trace.db"); var trace = new AnalysisTraceStore(tracePath); await trace.InitializeAsync();
        await trace.AppendBatchAsync(usages.Select(usage => new ModelRequestTrace(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model, usage.Round,
            usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { CardBuild = usage.CardBuild, Task = context.Task, PromptTemplateVersion = usage.PromptTemplateVersion!, AnalysisRunId = usage.AnalysisRunId, EventSequence = usage.EventSequence }),
        observations.Select(item => new ToolExecutionTrace(Guid.NewGuid().ToString("N"), frame.MatchId, frame.StateVersion, item.Name, item.ElapsedMilliseconds, AnalysisTraceStatus.Completed)
        { ResultHash = item.ResultHash, Model = item.Model, AnalysisRunId = item.AnalysisRunId, RequestId = item.RequestId, ToolCallId = item.ToolCallId, EventSequence = item.EventSequence }));
        var reopenedTrace = new AnalysisTraceStore(tracePath); var totals = await reopenedTrace.GetBudgetTotalsAsync(frame.MatchId); var tools = await reopenedTrace.ListToolsAsync(frame.MatchId);
        Check(totals is { RequestCount: 2, ChargedTokens: 60 } && tools.Count == 4 && !JsonSerializer.Serialize(tools).Contains(gift, StringComparison.Ordinal) &&
            tools.All(tool => tool.AnalysisRunId == advice.AnalysisRunId && tool.RequestId == usages[0].RequestId && tool.EventSequence == frame.EventSequence),
            "Trace storage disclosed raw gift data or lost paid request/tool source linkage.");
        return new(model.Line!, advice, loaded, model.Payloads.AsReadOnly());
    }

    private static void Feed(PowerLogStateEngine engine, params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static PowerLogStateEngine Engine(Spec spec)
    {
        var engine = new PowerLogStateEngine(1, "attachment-privacy-" + Guid.NewGuid().ToString("N"));
        Feed(engine, "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=5", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=ATK value=0", "tag=HEALTH value=30", "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0",
            "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", "tag=ATK value=0", "tag=HEALTH value=" + (spec.Aura ? 6 : 10), "tag=DAMAGE value=0", "tag=ARMOR value=0", "tag=IMMUNE value=0",
            "FULL_ENTITY - Creating ID=30 CardID=CS2_171", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=" + (spec.Aura ? 3 : 1), "tag=HEALTH value=1", "tag=DAMAGE value=0", "tag=COST value=1", "tag=CARDRACE value=BEAST", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1",
            "FULL_ENTITY - Creating ID=60 CardID=DINO_421", "tag=CONTROLLER value=" + (spec.OwnTarget ? 1 : 2), "tag=ZONE value=" + spec.TargetZone, "tag=CARDTYPE value=MINION",
            "tag=ATK value=8", "tag=HEALTH value=8", "tag=DAMAGE value=0", "tag=COST value=8");
        if (spec.Gift == "EDR_100t13e") Feed(engine, "tag=WINDFURY value=1", "tag=DIVINE_SHIELD value=1");
        else Feed(engine, "tag=ELUSIVE value=1");
        if (spec.IncludeGift) Feed(engine, "FULL_ENTITY - Creating ID=50 CardID=" + spec.Gift, "tag=CONTROLLER value=" + (spec.OwnTarget ? 1 : 2), "tag=ZONE value=" + spec.EnchantmentZone,
            "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=60", "tag=IS_NIGHTMARE_BONUS value=1", "tag=TAG_SCRIPT_DATA_NUM_6 value=" + (spec.Gift == "EDR_100t13e" ? 120225 : 113797));
        if (spec.Aura) Feed(engine, "FULL_ENTITY - Creating ID=40 CardID=EDR_480", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION",
            "tag=ATK value=0", "tag=HEALTH value=5", "tag=DAMAGE value=0", "tag=CARDRACE value=BEAST", "tag=AURA value=1");
        Feed(engine, "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string DataPayload(string payload) => new JsonArray(JsonNode.Parse(payload)!["messages"]!.AsArray()
        .Where(message => message?["role"]?.ToString() != "system").Select(message => message!.DeepClone()).ToArray()).ToJsonString();
    private sealed class Model(string[] ids, bool choice) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Payloads { get; } = [];
        public JsonNode? Line { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var text = await request.Content!.ReadAsStringAsync(token); Payloads.Add(text); var payload = JsonNode.Parse(text)!; object message;
            var actions = choice ? new JsonArray(new JsonObject { ["kind"] = "choose", ["source_entity_id"] = 60 }) :
                new JsonArray(new JsonObject { ["kind"] = "attack", ["source_entity_id"] = 30, ["target_entity_id"] = 21 });
            object Tool(string id, string name, object args) => new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
            if (Calls == 1) message = new { role = "assistant", tool_calls = new[]
            {
                Tool("attached-snapshot", "get_snapshot", new { }), Tool("attached-cards", "get_cards", new { card_ids = ids }),
                Tool("attached-events", "get_recent_events", new { }),
                choice ? Tool("attached-line", "validate_choice", new { choice_id = 8, selected_entity_ids = new[] { 60 } }) : Tool("attached-line", "evaluate_line", new { actions })
            } };
            else
            {
                var messages = payload["messages"]!.AsArray();
                var snapshot = JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == "attached-snapshot")!["content"]!.GetValue<string>())!;
                var known = snapshot["entities"]!.AsArray().SingleOrDefault(entity => entity?["id"]?.GetValue<int>() == 50)?["card_id"]?.ToString();
                Line = JsonNode.Parse(messages.Single(item => item?["tool_call_id"]?.ToString() == "attached-line")!["content"]!.GetValue<string>())!;
                message = new { role = "assistant", content = JsonSerializer.Serialize(new
                {
                    summary = (known is null ? "No known attached identity." : "Observed attached gift: " + known) + " " + Key,
                    reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                    actions, verified_lethal = true, verification = "verified", HostUnsupportedMechanics = Array.Empty<string>()
                }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = Calls == 1 ? "tool_calls" : "stop", message } }, usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
}
