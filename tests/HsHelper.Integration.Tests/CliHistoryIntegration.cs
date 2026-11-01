using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;

/// <summary>Runs the production CLI, an isolated SQLite library, and a bounded loopback-only model.</summary>
internal static partial class CliHistoryIntegration
{
    private const string Build = "255790";
    private const string Model = "cli-history-fixture";
    private const string FakeKey = "CLI_HISTORY_SYNTHETIC_KEY_ONLY_2026";
    private const string ToolId = "cli-history-plan-call";

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var cli = typeof(CliHistoryIntegration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CliTestTargetPath").Value!;
        Check(Path.IsPathFullyQualified(cli) && File.Exists(cli) && File.Exists(Path.ChangeExtension(cli, ".deps.json")) &&
            File.Exists(Path.ChangeExtension(cli, ".runtimeconfig.json")), "Production CLI build output was not supplied by MSBuild.");
        var directory = Path.Combine(root, "cli-history", Guid.NewGuid().ToString("N"));
        var fixture = await Fixture.Create(Path.Combine(directory, "matched"));
        var valid = await SeedHistory(fixture);
        var first = await Invoke(cli, fixture, fixture.Frame, "FIRST_CLI_QUESTION", [Reply.Tool(), Reply.Final("FIRST_CLI_ANSWER")]);
        RequireSuccess(first);
        var firstContext = Context(first.Requests[0]);
        Check(firstContext["Snapshot"]?["personal_library_available"]?.GetValue<bool>() == true &&
            firstContext["KnowledgeAsOf"]?.GetValue<DateTimeOffset>() == fixture.Point.RecordedAt &&
            firstContext["AsOf"]?.GetValue<DateTimeOffset>() >= first.StartedAt && firstContext["AsOf"]?.GetValue<DateTimeOffset>() <= first.EndedAt &&
            firstContext["DeckVersionId"]?.GetValue<string>() == fixture.Deck.Id,
            "The exact saved point did not use its real session/deck/knowledge cutoff and this invocation's query time.");
        var plan = ToolResult(first.Requests[1], ToolId);
        Check(plan["as_of"]?.GetValue<DateTimeOffset>() == fixture.Point.RecordedAt && plan["entries"] is JsonArray { Count: 2 } entries &&
            entries.Any(entry => entry?["EntryId"]?.GetValue<string>() == fixture.Plan.EntryId && entry["Version"]?.GetValue<int>() == 1) &&
            entries.Any(entry => entry?["EntryId"]?.GetValue<string>() == fixture.Note.EntryId),
            "The actual CLI tool did not return the pre-decision plan and note versions.");
        foreach (var excluded in new[] { "LATE_REVISION", "LATE_NOTE", "FUTURE_EVENT_NOTE", "OTHER_DECK_PLAN" })
            Check(!plan.ToJsonString().Contains(excluded, StringComparison.Ordinal), "CLI knowledge crossed its real time/event/deck boundary: " + excluded);
        var firstArchived = (await fixture.Archive.ListAsync()).Single(record => record.Advice.Summary == "FIRST_CLI_ANSWER");
        AssertHistory(first.Requests[0], valid.TakeLast(4).ToArray(), firstArchived);
        Check(firstArchived.Advice.KnowledgeAsOf == fixture.Point.RecordedAt && firstArchived.Advice.DeckVersionId == fixture.Deck.Id,
            "CLI archive lost the real saved-point knowledge/deck binding.");
        await AssertTrace(fixture, firstArchived, expectedRequests: 2, ToolId);
        pass("production CLI exact saved point queries historical knowledge and preserves tool, request, run, event and deck provenance");

        var second = await Invoke(cli, fixture, fixture.Frame, "SECOND_CLI_QUESTION", [Reply.Final("SECOND_CLI_ANSWER")]);
        RequireSuccess(second);
        var secondArchived = (await fixture.Archive.ListAsync()).Single(record => record.Advice.Summary == "SECOND_CLI_ANSWER");
        var expected = valid.TakeLast(3).Append(firstArchived).ToArray();
        AssertHistory(second.Requests.Single(), expected, secondArchived);
        Check(Context(second.Requests[0])["ConversationTruncated"]?.GetValue<bool>() == true &&
            secondArchived.Advice.KnowledgeAsOf == firstArchived.Advice.KnowledgeAsOf && secondArchived.Advice.AnalysisRunId != firstArchived.Advice.AnalysisRunId,
            "Repeated CLI analysis did not retain the old knowledge cutoff while accepting a later completed conversation.");
        await AssertTrace(fixture, secondArchived, expectedRequests: 1);
        pass("second real CLI invocation carries only the latest four eligible opinions in chronological order with exact archive IDs and fixed knowledge cutoff");

        var alteredEntities = fixture.Frame.Entities.ToDictionary();
        var alteredTags = alteredEntities[11].Tags.ToDictionary(); alteredTags["ARMOR"] = "1";
        alteredEntities[11] = alteredEntities[11] with { Tags = alteredTags.ToFrozenDictionary() };
        var altered = await Invoke(cli, fixture, fixture.Frame with { Entities = alteredEntities.ToFrozenDictionary() },
            "ALTERED_EXTERNAL_SNAPSHOT", [Reply.Final("ALTERED_EXTERNAL_ANSWER")]);
        RequireSuccess(altered);
        AssertNoLibrary(altered, (await fixture.Archive.ListAsync()).Single(record => record.Advice.Summary == "ALTERED_EXTERNAL_ANSWER"));
        pass("CLI same sequence/version with altered external snapshot cannot attach a saved-point library, knowledge cutoff or conversation");

        var unsavedFixture = await Fixture.Create(Path.Combine(directory, "unsaved"));
        await SeedHistory(unsavedFixture);
        foreach (var frame in new[]
        {
            unsavedFixture.Frame with { StateVersion = unsavedFixture.Frame.StateVersion + 1 },
            unsavedFixture.Frame with { StateVersion = unsavedFixture.Frame.StateVersion + 1, EventSequence = unsavedFixture.Frame.EventSequence + 1 }
        })
        {
            var unsaved = await Invoke(cli, unsavedFixture, frame, "UNSAVED_POINT_QUESTION", [Reply.Final("UNSAVED_ANSWER_" + frame.EventSequence)]);
            RequireSuccess(unsaved);
            AssertNoLibrary(unsaved, (await unsavedFixture.Archive.ListAsync()).Single(record => record.Advice.Summary == "UNSAVED_ANSWER_" + frame.EventSequence));
        }
        pass("CLI absent exact saved point, including a different version at the same event, never invents personal-library or historical context");

        var missingBuild = await Fixture.Create(Path.Combine(directory, "saved-source-build"), omitBuild: true);
        var recovered = await Invoke(cli, missingBuild, missingBuild.Frame, "ORIGINAL_SESSION_BUILD", [Reply.Tool(), Reply.Final("SAVED_BUILD_ANSWER")]);
        RequireSuccess(recovered);
        var recoveredContext = Context(recovered.Requests[0]);
        var recoveredArchive = (await missingBuild.Archive.ListAsync()).Single();
        Check(recoveredContext["CardBuild"]?.GetValue<string>() == Build && recoveredContext["DeckVersionId"]?.GetValue<string>() == missingBuild.Deck.Id &&
            recoveredContext["KnowledgeAsOf"]?.GetValue<DateTimeOffset>() == missingBuild.Point.RecordedAt &&
            ToolResult(recovered.Requests[1], ToolId)["entries"] is JsonArray { Count: 2 } && recoveredArchive.Advice.CardBuild == Build &&
            recoveredArchive.Advice.DeckVersionId == missingBuild.Deck.Id, "Saved session did not supply its real missing build/deck source.");
        var conflict = await Fixture.Create(Path.Combine(directory, "tracking-conflict"), conflictingTracking: true);
        var rejected = await Invoke(cli, conflict, conflict.Frame, "CONFLICTING_TRACKING", [Reply.Final("MUST_NOT_BE_REQUESTED")]);
        Check(rejected.ExitCode != 0 && rejected.Requests.Count == 0 && !File.Exists(rejected.OutputPath) &&
            (await conflict.Archive.ListAsync()).Count == 0 && (await conflict.Traces.GetBudgetTotalsAsync(conflict.Frame.MatchId)).RequestCount == 0,
            "Conflicting saved tracking borrowed another deck or reached model/output/archive before preflight rejection.");
        pass("CLI uses original saved session build/deck when snapshot metadata is absent and rejects conflicting tracking before any HTTP or archive");

        await Failure(cli, Path.Combine(directory, "http-failure"), timeout: false);
        pass("CLI second-round HTTP failure retains completed usage and exact tool provenance without exporting or archiving an unfinished answer");
        await Failure(cli, Path.Combine(directory, "timeout"), timeout: true);
        pass("CLI second-round timeout flushes spend and tool audit through finally without output, archive or leaked provider credentials");

        foreach (var (id, index) in new[] { (FakeKey, 0), ("https://cli-tool.invalid/private", 1) })
        {
            var malicious = await Fixture.Create(Path.Combine(directory, "invalid-tool-id-" + index));
            var rejectedTool = await Invoke(cli, malicious, malicious.Frame, "INVALID_TOOL_ID", [Reply.Tool(id, prependValid: index == 1)]);
            var requests = await malicious.Traces.ListRequestsAsync(malicious.Frame.MatchId);
            var tools = await malicious.Traces.ListToolsAsync(malicious.Frame.MatchId);
            var totals = await malicious.Traces.GetBudgetTotalsAsync(malicious.Frame.MatchId);
            Check(rejectedTool.ExitCode != 0 && rejectedTool.Requests.Count == 1 && !File.Exists(rejectedTool.OutputPath) &&
                (await malicious.Archive.ListAsync()).Count == 0 && tools.Count == 0 && requests is [{ Status: AnalysisTraceStatus.Completed, ChargedTokens: 70 }] &&
                totals.RequestCount == 1 && totals.ChargedTokens == 70,
                "Provider-controlled invalid tool IDs executed a partial batch, broke the spend audit or produced a completed answer.");
            var durable = JsonSerializer.Serialize(requests) + JsonSerializer.Serialize(tools) + rejectedTool.Stdout + rejectedTool.Stderr +
                Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(malicious.Directory, "analysis-traces.db")));
            AssertNoSecrets(durable);
            Check(!durable.Contains("https://cli-tool.invalid/private", StringComparison.Ordinal), "An invalid provider URL entered CLI audit/output.");
        }
        pass("CLI rejects credential and URL tool IDs before executing any tool while retaining the actual successful HTTP usage and clean audit");

        var review = await Fixture.Create(Path.Combine(directory, "complete-review"), complete: true);
        var reviewHistory = await SeedHistory(review);
        Check(review.Point.Phase == GamePhase.Complete && review.Frame.LocalOutcome == "Won", "Review fixture is not a real saved completed game.");
        var reviewed = await Invoke(cli, review, review.Frame, "DEFAULT_REVIEW_QUESTION", [Reply.Tool(), Reply.Final("REVIEW_CLI_ANSWER")], task: null);
        RequireSuccess(reviewed);
        var reviewContext = Context(reviewed.Requests[0]);
        var reviewArchive = (await review.Archive.ListAsync()).Single(record => record.Advice.Summary == "REVIEW_CLI_ANSWER");
        Check(reviewContext["Task"]?.GetValue<string>() == AdviceTaskKind.Review.ToString() &&
            reviewContext["Snapshot"]?["personal_library_available"]?.GetValue<bool>() == true &&
            reviewContext["KnowledgeAsOf"]?.GetValue<DateTimeOffset>() == review.Point.RecordedAt &&
            reviewContext["DeckVersionId"]?.GetValue<string>() == review.Deck.Id &&
            ToolResult(reviewed.Requests[1], ToolId)["entries"] is JsonArray { Count: 2 } &&
            reviewArchive.Advice.Task == AdviceTaskKind.Review.ToString() && reviewArchive.Advice.KnowledgeAsOf == review.Point.RecordedAt &&
            reviewArchive.Advice.Actions.Count == 0 && !reviewArchive.Advice.VerifiedLethal && reviewArchive.Advice.Verification == "strategy",
            "Default CLI completed-game review lost its actual saved knowledge/deck source or acquired executable actions.");
        AssertHistory(reviewed.Requests[0], reviewHistory.TakeLast(4).ToArray(), reviewArchive);
        await AssertTrace(review, reviewArchive, expectedRequests: 2, ToolId);
        pass("CLI defaults a real saved Complete point to Review with fixed knowledge, original deck, eligible history and non-executable advice");

        var publicSecret = await Fixture.Create(Path.Combine(directory, "public-secret"));
        foreach (var (raw, expectedFlag) in new[] { ("0", "0"), ("1", "1"), ("not_a_boolean", "unknown") })
        {
            var secret = MaskedSecret(raw, expectedFlag);
            var frame = WithSecret(publicSecret.Frame, secret);
            var accepted = await Invoke(cli, publicSecret, frame, "CORE_PUBLIC_SECRET_" + expectedFlag, [Reply.Final("PUBLIC_SECRET_" + expectedFlag)]);
            RequireSuccess(accepted);
            var archive = (await publicSecret.Archive.ListAsync()).Single(record => record.Advice.Summary == "PUBLIC_SECRET_" + expectedFlag);
            AssertNoLibrary(accepted, archive);
            var sent = accepted.Requests.Single().ToJsonString();
            foreach (var privateValue in new[] { "PRIVATE_CLI_SECRET_ID", "FAKE_PRIVATE_TAG", "PRIVATE_CLI_SECRET_VALUE", "919191919" })
                Check(!sent.Contains(privateValue, StringComparison.Ordinal), "Core-to-CLI model request exposed a hidden secret field: " + privateValue);
        }
        var rejectedSecret = await Fixture.Create(Path.Combine(directory, "rejected-secret"));
        var masked = MaskedSecret("1", "1");
        var classified = WithSecret(rejectedSecret.Frame, masked);
        var rejections = new List<GameSnapshot>
        {
            WithSecret(rejectedSecret.Frame, masked with { CardId = "FORGED_PRIVATE_CLI_SECRET_ID" }),
            WithSecret(rejectedSecret.Frame, Tag(masked, "SECRET", "PRIVATE_CLI_SECRET_VALUE")),
            WithSecret(rejectedSecret.Frame, Tag(masked, "CARDTYPE", "SPELL")),
            WithSecret(rejectedSecret.Frame, Tag(masked, "COST", "919191919")),
            WithSecret(rejectedSecret.Frame, Tag(masked, "CONTROLLER", "unknown")),
            classified with { Entities = classified.Entities.Where(pair => pair.Key != 2).ToFrozenDictionary() },
            classified with { Quality = classified.Quality with { HasLocalPerspective = false } },
            classified with { IsSpectating = true }
        };
        rejections.AddRange(new[] { "HAND", "DECK", "SETASIDE" }.Select(zone => WithSecret(rejectedSecret.Frame, Tag(masked, "ZONE", zone))));
        foreach (var frame in rejections)
        {
            var rejectedPrivate = await Invoke(cli, rejectedSecret, frame, "REJECT_PRIVATE_SECRET_INPUT", [Reply.Final("MUST_NOT_BE_REQUESTED")]);
            var error = JsonNode.Parse(rejectedPrivate.Stderr);
            Check(rejectedPrivate.ExitCode != 0 && rejectedPrivate.Requests.Count == 0 && !File.Exists(rejectedPrivate.OutputPath) &&
                error?["Stage"]?.GetValue<string>() == "preflight" && error["Kind"]?.GetValue<string>() == nameof(InvalidDataException) &&
                (await rejectedSecret.Archive.ListAsync()).Count == 0 && (await rejectedSecret.Traces.GetBudgetTotalsAsync(frame.MatchId)).RequestCount == 0 &&
                !rejectedPrivate.Stderr.Contains("PRIVATE_CLI_SECRET", StringComparison.Ordinal),
                "CLI hidden-secret allowlist accepted private identity, raw attributes, non-secret slots or an unconfirmed perspective.");
        }
        pass("real Core masked enemy SECRET classifications reach CLI without private data while forged identity tags zones owners and perspectives stop before HTTP or archive");

        var starting = await StartingDeckIntegration.CreateSavedCaseAsync(Path.Combine(directory, "original-sideboard"));
        var originalSideboard = await Invoke(cli, starting.Directory, starting.Frame, "ORIGINAL_SESSION_SIDEBOARD",
            [Reply.Choice(), Reply.Final("ORIGINAL_SIDEBOARD_ANSWER")], task: "choice");
        RequireSuccess(originalSideboard);
        StartingDeckIntegration.AssertReference(ToolResult(originalSideboard.Requests[1], "cli-starting-deck"), starting.A);
        var originalArchive = (await starting.Archive.ListAsync()).Single();
        var originalTraces = new AnalysisTraceStore(Path.Combine(starting.Directory, "analysis-traces.db"));
        var originalTools = await originalTraces.ListToolsAsync(starting.Frame.MatchId);
        var originalRequests = await originalTraces.ListRequestsAsync(starting.Frame.MatchId);
        Check(originalArchive.Advice.DeckVersionId == starting.A.Id && originalArchive.Advice.KnowledgeAsOf == starting.Point.RecordedAt &&
            Context(originalSideboard.Requests[0])["DeckVersionId"]?.GetValue<string>() == starting.A.Id &&
            originalTools is [{ ToolName: "evaluate_choice", ToolCallId: "cli-starting-deck", ResultHash.Length: 64 }] &&
            originalTools[0].ResultHash == originalArchive.Advice.Evidence.Single().ResultHash &&
            originalArchive.Advice.RequestIds!.SequenceEqual(originalRequests.Select(request => request.RequestId)) &&
            originalRequests.All(request => request.DeckVersionId == starting.A.Id && request.AnalysisRunId == originalArchive.Advice.AnalysisRunId),
            "Actual CLI did not preserve original session A sideboard source and real request/tool hash when later B exists.");
        var unbound = starting.Frame with { StateVersion = starting.Frame.StateVersion + 1, EventSequence = starting.Frame.EventSequence + 1,
            DeckTracking = new(starting.Frame.MatchId, starting.Frame.StateVersion + 1, starting.B.Id, "255790", null,
                FrozenDictionary<string, int>.Empty, 0, false, []) };
        var background = await Invoke(cli, starting.Directory, unbound, "BACKGROUND_SIDEBOARD_NOT_SESSION_BOUND",
            [Reply.Choice(), Reply.Final("BACKGROUND_SIDEBOARD_ANSWER")], task: "choice");
        RequireSuccess(background);
        Check(Context(background.Requests[0])["DeckVersionId"]?.GetValue<string>() == starting.B.Id &&
            ToolResult(background.Requests[1], "cli-starting-deck")["SideboardEvidence"]?["InitialReference"] is null,
            "Unmatched CLI tracking background was promoted to an original-session starting reference.");
        AssertNoLibrary(background, (await starting.Archive.ListAsync()).Single(record => record.Advice.Summary == "BACKGROUND_SIDEBOARD_ANSWER"));
        var unrelatedEntities = starting.Frame.Entities.ToDictionary();
        unrelatedEntities[500] = new(500, "PRIVATE_CLI_CHOICE_PROBE", EntityKind.Card,
            new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "SETASIDE", ["CARDTYPE"] = "MINION" }.ToFrozenDictionary());
        var extraTagEntities = starting.Frame.Entities.ToDictionary();
        extraTagEntities[81] = Tag(extraTagEntities[81], "PRIVATE_CHOICE_TAG", "PRIVATE_CLI_CHOICE_PROBE");
        var invalidChoices = new[]
        {
            starting.Frame with { Choice = starting.Frame.Choice! with { IsReady = false } },
            starting.Frame with { Choice = starting.Frame.Choice! with { PlayerId = 2 } },
            starting.Frame with { Choice = starting.Frame.Choice! with { Options = starting.Frame.Choice.Options.Select(option => option.EntityId == 81 ? option with { CardId = "EDR_489" } : option).ToArray() } },
            starting.Frame with { Entities = unrelatedEntities.ToFrozenDictionary() },
            starting.Frame with { Entities = extraTagEntities.ToFrozenDictionary() }
        };
        var archiveCount = (await starting.Archive.ListAsync()).Count;
        var requestCount = (await originalTraces.GetBudgetTotalsAsync(starting.Frame.MatchId)).RequestCount;
        foreach (var invalidChoice in invalidChoices)
        {
            var denied = await Invoke(cli, starting.Directory, invalidChoice, "REJECT_PRIVATE_CHOICE_INPUT", [Reply.Final("MUST_NOT_BE_REQUESTED")], task: "choice");
            var error = JsonNode.Parse(denied.Stderr);
            Check(denied.ExitCode != 0 && denied.Requests.Count == 0 && !File.Exists(denied.OutputPath) &&
                error?["Stage"]?.GetValue<string>() == "preflight" && error["Kind"]?.GetValue<string>() == nameof(InvalidDataException) &&
                (await starting.Archive.ListAsync()).Count == archiveCount &&
                (await originalTraces.GetBudgetTotalsAsync(starting.Frame.MatchId)).RequestCount == requestCount &&
                !denied.Stderr.Contains("PRIVATE_CLI_CHOICE_PROBE", StringComparison.Ordinal),
                "CLI current-choice exception allowed a wrong scope identity unrelated hidden entity or private extra tag to reach HTTP/output/archive.");
        }
        pass("actual CLI original session sideboard A survives later deck B and archives exact evidence while unsaved tracking B stays unbound background");
    }

    private static GameSnapshot WithSecret(GameSnapshot frame, EntitySnapshot secret) => frame with
    { Entities = frame.Entities.Values.Append(secret).ToFrozenDictionary(entity => entity.Id) };

    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value)
    {
        var tags = entity.Tags.ToDictionary(); tags[key] = value;
        return entity with { Tags = tags.ToFrozenDictionary() };
    }

    private static EntitySnapshot MaskedSecret(string rawFlag, string expectedFlag)
    {
        var engine = new PowerLogStateEngine(1, "cli-public-secret-" + Guid.NewGuid().ToString("N"));
        foreach (var line in new[]
        {
            "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CARDTYPE value=HERO", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CLASS value=HUNTER", "tag=HEALTH value=30", "tag=DAMAGE value=0",
            "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CARDTYPE value=HERO", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CLASS value=MAGE", "tag=HEALTH value=30", "tag=DAMAGE value=0",
            "FULL_ENTITY - Creating ID=60 CardID=PRIVATE_CLI_SECRET_ID", "tag=CARDTYPE value=SPELL", "tag=CONTROLLER value=2", "tag=ZONE value=SECRET",
            "tag=SECRET value=" + rawFlag, "tag=QUEST value=0", "tag=SIDE_QUEST value=0", "tag=COST value=919191919", "tag=FAKE_PRIVATE_TAG value=PRIVATE_CLI_SECRET_VALUE",
            "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"
        }) engine.ProcessLine(line);
        var snapshot = engine.Snapshot(); var secret = snapshot.Entities[60];
        Check(ChoiceVisibility.ConfirmedLocalPlayer(snapshot) == 1 && secret.CardId is null && secret.ControllerId == 2 && secret.Zone == "SECRET" &&
            secret.Tags.GetValueOrDefault("SECRET") == expectedFlag && secret.Tags.GetValueOrDefault("QUEST") == "0" && secret.Tags.GetValueOrDefault("SIDE_QUEST") == "0" &&
            secret.Tags.Keys.All(key => key is "ZONE" or "CONTROLLER" or "ZONE_POSITION" or "ENTITY_ID" or "SECRET" or "QUEST" or "SIDE_QUEST"),
            "Real Core parser did not produce a correctly classified masked opponent secret.");
        return secret;
    }

    private static async Task Failure(string cli, string directory, bool timeout)
    {
        var fixture = await Fixture.Create(directory);
        var result = await Invoke(cli, fixture, fixture.Frame, "FAILED_QUESTION", [Reply.Tool(), timeout ? Reply.Timeout() : Reply.Error()], timeoutSeconds: timeout ? 2 : 10);
        Check(result.ExitCode != 0 && !File.Exists(result.OutputPath) && !Directory.EnumerateFiles(directory, "*.tmp").Any() &&
            (await fixture.Archive.ListAsync()).Count == 0 && result.Requests.Count == 2,
            "An unfinished CLI analysis exported/archived an answer or failed to run both real HTTP rounds.");
        var requests = await fixture.Traces.ListRequestsAsync(fixture.Frame.MatchId);
        var tools = await fixture.Traces.ListToolsAsync(fixture.Frame.MatchId);
        var totals = await fixture.Traces.GetBudgetTotalsAsync(fixture.Frame.MatchId);
        Check(requests.Count == 2 && requests[0] is { Round: 1, Status: AnalysisTraceStatus.Completed, ChargedTokens: 70 } &&
            requests[1] is { Round: 2, Status: AnalysisTraceStatus.Failed, ChargedTokens: 0 } &&
            requests[0].AnalysisRunId is { Length: > 0 } run && requests[1].AnalysisRunId == run &&
            requests.All(request => request.Model == Model && request.StateVersion == fixture.Frame.StateVersion &&
                request.EventSequence == fixture.Frame.EventSequence && request.CardBuild == Build && request.DeckVersionId == fixture.Deck.Id &&
                request.Task == AdviceTaskKind.Turn.ToString() && request.PromptTemplateVersion == AdviceTaskTemplates.Get(AdviceTaskKind.Turn).Version) &&
            tools is [{ Status: AnalysisTraceStatus.Completed, ToolCallId: ToolId, ResultHash.Length: 64 }] &&
            tools[0].RequestId == requests[0].RequestId && tools[0].AnalysisRunId == run && tools[0].Model == Model &&
            tools[0].EventSequence == fixture.Frame.EventSequence && totals.RequestCount == 2 && totals.ChargedTokens == 70,
            "CLI failure lost prior usage, charged a failed HTTP request, or detached tool provenance from its actual request/run.");
        Check((timeout ? result.ExitCode == 130 && string.IsNullOrWhiteSpace(result.Stderr) : result.Stderr.Contains("advice_failed", StringComparison.Ordinal)) &&
            !result.Stderr.Contains("PROVIDER_PRIVATE_BODY", StringComparison.Ordinal),
            "CLI failure diagnostics were not bounded and sanitized.");
        AssertNoSecrets(JsonSerializer.Serialize(requests) + JsonSerializer.Serialize(tools) + result.Stdout + result.Stderr);
    }

    private static async Task AssertTrace(Fixture fixture, ArchivedAdviceRecord record, int expectedRequests, string? toolId = null)
    {
        var draft = record.Advice;
        var requests = (await fixture.Traces.ListRequestsAsync(fixture.Frame.MatchId)).Where(request => request.AnalysisRunId == draft.AnalysisRunId).ToArray();
        var tools = (await fixture.Traces.ListToolsAsync(fixture.Frame.MatchId)).Where(tool => tool.AnalysisRunId == draft.AnalysisRunId).ToArray();
        Check(draft.Model == Model && draft.AnalysisRunId is { Length: > 0 } && requests.Length == expectedRequests &&
            draft.RequestIds!.SequenceEqual(requests.Select(request => request.RequestId)) &&
            requests.Select(request => request.Round).SequenceEqual(Enumerable.Range(1, expectedRequests)) &&
            requests.All(request => request is { Status: AnalysisTraceStatus.Completed, InputTokens: 40, OutputTokens: 30, TotalTokens: 70, ChargedTokens: 70, ConservativeCharge: false } &&
                request.Model == draft.Model && request.MatchId == draft.MatchId && request.StateVersion == draft.StateVersion &&
                request.CardBuild == draft.CardBuild && request.DeckVersionId == draft.DeckVersionId && request.EventSequence == draft.EventSequence &&
                request.Task == draft.Task && request.PromptTemplateVersion == draft.PromptTemplateVersion),
            "Actual CLI usage/request metadata differs from its completed archive source.");
        Check(toolId is null ? tools.Length == 0 && draft.Evidence.Count == 0 :
            tools is [{ Status: AnalysisTraceStatus.Completed, ToolName: "get_matchup_plan", ResultHash.Length: 64 }] &&
            tools[0].ToolCallId == toolId && tools[0].RequestId == requests[0].RequestId && tools[0].Model == draft.Model &&
            tools[0].EventSequence == draft.EventSequence && draft.Evidence is [{ ToolName: "get_matchup_plan" }] &&
            tools[0].ResultHash == draft.Evidence[0].ResultHash,
            "Actual CLI tool invocation/result evidence does not link to the exact successful request and archive.");
        AssertNoSecrets(JsonSerializer.Serialize(requests) + JsonSerializer.Serialize(tools) + JsonSerializer.Serialize(record));
    }

    private static void AssertHistory(JsonObject request, IReadOnlyList<ArchivedAdviceRecord> expected, ArchivedAdviceRecord actual)
    {
        var previous = request["messages"]!.AsArray().OfType<JsonObject>()
            .Where(message => message["role"]?.GetValue<string>() is "user" or "assistant")
            .Select(message => ParseContent(message)).Where(content => content?["kind"]?.GetValue<string>() is "previous_user_question" or "previous_assistant_opinion").ToArray();
        Check(previous.Length == expected.Count * 2 && actual.Advice.ConversationArchiveIds!.SequenceEqual(expected.Select(record => record.Id)),
            "CLI sent an unbounded/wrong conversation or archived different source IDs.");
        for (var index = 0; index < expected.Count; index++)
        {
            var record = expected[index]; var question = previous[index * 2]!; var answer = previous[index * 2 + 1]!;
            Check(question["kind"]?.GetValue<string>() == "previous_user_question" && question["question"]?.GetValue<string>() == record.Advice.UserQuestion &&
                answer["kind"]?.GetValue<string>() == "previous_assistant_opinion" && answer["is_current_fact"]?.GetValue<bool>() == false &&
                answer["answer"]?.GetValue<string>() == record.Advice.Summary && JsonNode.DeepEquals(question["source"], answer["source"]),
                "CLI history changed chronological roles or presented an older opinion as current fact.");
            var source = question["source"]!;
            Check(source["ArchiveId"]?.GetValue<string>() == record.Id && source["MatchId"]?.GetValue<string>() == record.Advice.MatchId &&
                source["StateVersion"]?.GetValue<long>() == record.Advice.StateVersion && source["EventSequence"]?.GetValue<long>() == record.Advice.EventSequence &&
                source["CardBuild"]?.GetValue<string>() == record.Advice.CardBuild && source["DeckVersionId"]?.GetValue<string>() == record.Advice.DeckVersionId &&
                source["RecordedAt"]?.GetValue<DateTimeOffset>() == record.RecordedAt && source["KnowledgeAsOf"]?.GetValue<DateTimeOffset>() == record.Advice.KnowledgeAsOf &&
                source["Model"]?.GetValue<string>() == record.Advice.Model && source["AnalysisRunId"]?.GetValue<string>() == record.Advice.AnalysisRunId &&
                source["RequestIds"]!.AsArray().Select(id => id!.GetValue<string>()).SequenceEqual(record.Advice.RequestIds!),
                "CLI conversation provenance was invented, dropped or taken from another scope.");
        }
    }

    private static void AssertNoLibrary(CliResult result, ArchivedAdviceRecord archived)
    {
        var context = Context(result.Requests[0]);
        Check(context["Snapshot"]?["personal_library_available"]?.GetValue<bool>() == false, "An unmatched external CLI snapshot inherited a personal library.");
        Check(context["KnowledgeAsOf"] is null && archived.Advice.KnowledgeAsOf is null, "An unmatched external CLI snapshot inherited a knowledge cutoff.");
        Check(context["Conversation"] is JsonArray { Count: 0 } && archived.Advice.ConversationArchiveIds is null or { Count: 0 }, "An unmatched external CLI snapshot archived conversation sources.");
        Check(!result.Requests[0]["messages"]!.AsArray().OfType<JsonObject>().Any(message => ParseContent(message)?["kind"]?.GetValue<string>() is "previous_user_question" or "previous_assistant_opinion"),
            "An unmatched external CLI snapshot transmitted earlier opinions.");
        Check(!result.Requests[0]["tools"]!.AsArray().Any(tool => tool?["function"]?["name"]?.GetValue<string>() is "get_matchup_plan" or "get_personal_stats"),
            "An unmatched external CLI snapshot registered personal tools.");
    }

    private static async Task<IReadOnlyList<ArchivedAdviceRecord>> SeedHistory(Fixture fixture)
    {
        var clock = new Clock(DateTimeOffset.UtcNow.AddMinutes(-20));
        var archive = new AdviceArchiveStore(Path.Combine(fixture.Directory, "advice-archive.db"), clock);
        var valid = new List<ArchivedAdviceRecord>();
        AdviceRecordDraft Draft(string summary) => new(fixture.Frame.MatchId, fixture.Frame.StateVersion, fixture.Frame.EventSequence, Build,
            AdviceTaskTemplates.Get(AdviceTaskKind.Turn).Version, summary)
        {
            DeckVersionId = fixture.Deck.Id, VisibilityPolicy = HsHelper.Coaching.CompactAdviceContextBuilder.VisibilityPolicy,
            Task = "turn", UserQuestion = "QUESTION_" + summary,
            KnowledgeAsOf = clock.Now.AddMinutes(-1), Model = "prior-cli-model", AnalysisRunId = "run-" + summary, RequestIds = ["request-" + summary]
        };
        for (var index = 0; index < 6; index++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            valid.Add(await archive.SaveAsync(Draft("OLD_OPINION_" + index)));
        }
        foreach (var excluded in new[]
        {
            Draft("WRONG_MATCH") with { MatchId = "other-match" }, Draft("WRONG_DECK") with { DeckVersionId = fixture.OtherDeck.Id },
            Draft("WRONG_BUILD") with { CardBuild = "255791" }, Draft("FUTURE_STATE") with { StateVersion = fixture.Frame.StateVersion + 1 },
            Draft("FUTURE_EVENT") with { EventSequence = fixture.Frame.EventSequence + 1 }, Draft("UNKNOWN_KNOWLEDGE") with { KnowledgeAsOf = null }
        })
        {
            clock.Now = clock.Now.AddSeconds(1); await archive.SaveAsync(excluded);
        }
        clock.Now = DateTimeOffset.UtcNow;
        await archive.SaveAsync(Draft("KNOWN_FUTURE_KNOWLEDGE") with { KnowledgeAsOf = fixture.Point.RecordedAt.AddTicks(1) });
        return valid.AsReadOnly();
    }

    private sealed record Fixture(string Directory, GameSnapshot Frame, DecisionPoint Point, DeckVersion Deck, DeckVersion OtherDeck,
        CoachKnowledgeVersion Plan, CoachKnowledgeVersion Note, AdviceArchiveStore Archive, AnalysisTraceStore Traces)
    {
        public static async Task<Fixture> Create(string directory, bool omitBuild = false, bool conflictingTracking = false, bool complete = false)
        {
            System.IO.Directory.CreateDirectory(directory);
            var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
            const string json = """[{"id":"CLI_HISTORY_HERO","dbfId":900101,"name":"synthetic hunter","type":"HERO","cardClass":"HUNTER","text":""},{"id":"CLI_HISTORY_OPPONENT","dbfId":900102,"name":"synthetic mage","type":"HERO","cardClass":"MAGE","text":""},{"id":"CLI_HISTORY_CARD","dbfId":900103,"name":"synthetic body","type":"MINION","cardClass":"HUNTER","cost":1,"attack":1,"health":1,"text":"","collectible":true,"set":"SYNTHETIC","mechanics":[]}]""";
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(json))) await catalog.ImportAsync(input, Build, source: "synthetic://cli-history-tests-only");
            var sessions = new SessionStore(Path.Combine(directory, "sessions.db")); await sessions.InitializeAsync();
            var definition = new DeckDefinition(2, [900101], [new(900103, 2)], []);
            var deck = await sessions.SaveDeckVersionAsync("CLI history synthetic deck", definition, Build);
            var otherDeck = await sessions.SaveDeckVersionAsync("Other CLI synthetic deck", definition, Build);
            var frame = FrameOf("cli-history-" + Guid.NewGuid().ToString("N"));
            if (complete)
            {
                var entities = frame.Entities.ToDictionary();
                foreach (var (id, outcome) in new[] { (2, "WON"), (3, "LOST") })
                {
                    var tags = entities[id].Tags.ToDictionary(); tags["PLAYSTATE"] = outcome;
                    entities[id] = entities[id] with { Tags = tags.ToFrozenDictionary() };
                }
                frame = frame with { Phase = GamePhase.Complete, Entities = entities.ToFrozenDictionary() };
            }
            if (omitBuild) frame = frame with { ClientBuild = null };
            if (conflictingTracking) frame = frame with { DeckTracking = new(frame.MatchId, frame.StateVersion, otherDeck.Id, Build, 2,
                new Dictionary<string, int> { ["CLI_HISTORY_CARD"] = 2 }, 0, true, []) };
            var clock = new Clock(DateTimeOffset.UtcNow.AddHours(-1));
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db"), clock); await knowledge.InitializeAsync();
            var draft = new CoachKnowledgeDraft(deck.Id, CoachKnowledgeKind.DeckPlan, "saved plan", "OLD_PLAN_VISIBLE", "MAGE", true);
            var plan = await knowledge.SaveAsync(draft);
            var note = await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "saved note", "OLD_NOTE_VISIBLE", "MAGE", true, frame.MatchId)
                { EventSequence = frame.EventSequence - 1 });
            await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "future anchor", "FUTURE_EVENT_NOTE", "MAGE", true, frame.MatchId)
                { EventSequence = frame.EventSequence + 1 });
            await knowledge.SaveAsync(new(otherDeck.Id, CoachKnowledgeKind.DeckPlan, "other deck", "OTHER_DECK_PLAN"));
            var recorder = new SessionRecorder(sessions, new(deck.Id, Build, 1, PlayerClass: "HUNTER"));
            recorder.Observe(frame, 1); await recorder.CompleteAsync();
            var point = (await sessions.ListDecisionPointsAsync(frame.MatchId, includeUnstable: true)).Single();
            Check(point.IsDecisionPoint && point.IsStable && point.IsComplete, "Fixture did not save a real complete stable checkpoint.");
            clock.Now = DateTimeOffset.UtcNow;
            await knowledge.SaveAsync(draft with { Content = "LATE_REVISION" }, plan.EntryId, plan.Version);
            await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "late note", "LATE_NOTE", "MAGE", true, frame.MatchId)
                { EventSequence = frame.EventSequence });
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice-archive.db")); await archive.InitializeAsync();
            var traces = new AnalysisTraceStore(Path.Combine(directory, "analysis-traces.db")); await traces.InitializeAsync();
            return new(directory, frame, point, deck, otherDeck, plan, note, archive, traces);
        }
    }

    private static GameSnapshot FrameOf(string match)
    {
        EntitySnapshot Entity(int id, EntityKind kind, string? card, params (string Key, string Value)[] tags) =>
            new(id, card, kind, tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value));
        var entities = new[]
        {
            Entity(2, EntityKind.Player, null, ("PLAYER_ID", "1"), ("CONTROLLER", "1"), ("HERO_ENTITY", "11"), ("FIRST_PLAYER", "1"),
                ("CURRENT_PLAYER", "1"), ("RESOURCES", "10"), ("RESOURCES_USED", "0"), ("TEMP_RESOURCES", "0")),
            Entity(3, EntityKind.Player, null, ("PLAYER_ID", "2"), ("CONTROLLER", "2"), ("HERO_ENTITY", "21"), ("CURRENT_PLAYER", "0")),
            Entity(11, EntityKind.Card, "CLI_HISTORY_HERO", ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CONTROLLER", "1"), ("CLASS", "HUNTER"),
                ("ATK", "0"), ("HEALTH", "30"), ("DAMAGE", "0"), ("ARMOR", "0")),
            Entity(21, EntityKind.Card, "CLI_HISTORY_OPPONENT", ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CONTROLLER", "2"), ("CLASS", "MAGE"),
                ("ATK", "0"), ("HEALTH", "30"), ("DAMAGE", "0"), ("ARMOR", "0"))
        };
        // Deck ownership comes from the real saved session; tracking is deliberately absent.
        return new(match, 8, 15, 1, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        { ClientBuild = Build, Format = "STANDARD", GameType = "GT_RANKED" };
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr, string OutputPath, IReadOnlyList<JsonObject> Requests,
        DateTimeOffset StartedAt, DateTimeOffset EndedAt);

    private static Task<CliResult> Invoke(string cli, Fixture fixture, GameSnapshot snapshot, string question, IReadOnlyList<Reply> replies, int timeoutSeconds = 10, string? task = "turn") =>
        Invoke(cli, fixture.Directory, snapshot, question, replies, timeoutSeconds, task);

    private static async Task<CliResult> Invoke(string cli, string directory, GameSnapshot snapshot, string question, IReadOnlyList<Reply> replies,
        int timeoutSeconds = 10, string? task = "turn", string capabilityEnvironment = "", bool modelCheck = false)
    {
        await using var server = new FakeModel(replies);
        var prefix = Path.Combine(directory, "invocation-" + Guid.NewGuid().ToString("N"));
        var snapshotPath = prefix + ".snapshot.json"; var outputPath = prefix + ".advice.json"; var envPath = prefix + ".env";
        if (!modelCheck) await File.WriteAllTextAsync(snapshotPath, SnapshotCodec.Serialize(snapshot));
        await File.WriteAllTextAsync(envPath, $"api_url={server.Endpoint}\nmodel_name={Model}\napi_key={FakeKey}\nMODEL_MAX_TOOL_ROUNDS=1\nMODEL_TIMEOUT_SECONDS={timeoutSeconds}\n" + capabilityEnvironment);
        var processPath = Environment.ProcessPath;
        var host = processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath : Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..")), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in new[] { cli, modelCheck ? "model-check" : "advise", "--env", envPath, "--data-dir", directory }) start.ArgumentList.Add(argument);
        if (!modelCheck)
        {
            foreach (var argument in new[] { "--snapshot", snapshotPath, "--out", outputPath, "--question", question }) start.ArgumentList.Add(argument);
            if (task is not null) { start.ArgumentList.Add("--task"); start.ArgumentList.Add(task); }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var began = DateTimeOffset.UtcNow;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Production CLI process failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = new CliResult(process.ExitCode, await stdout, await stderr, outputPath, server.Requests.ToArray(), began, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(prefix + ".stdout.log", result.Stdout); await File.WriteAllTextAsync(prefix + ".stderr.log", result.Stderr);
            AssertNoSecrets(result.Stdout + result.Stderr);
            if (File.Exists(outputPath)) AssertNoSecrets(await File.ReadAllTextAsync(outputPath));
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed record Reply(string? Body, int Status = 200, bool Stall = false)
    {
        public static Reply Tool(string id = ToolId, bool prependValid = false)
        {
            object Call(string callId) => new { id = callId, type = "function", function = new { name = "get_matchup_plan", arguments = "{}" } };
            return Json(new { role = "assistant", tool_calls = prependValid ? new[] { Call(ToolId), Call(id) } : new[] { Call(id) } }, "tool_calls");
        }
        public static Reply Choice() => Json(new { role = "assistant", tool_calls = new[] { new { id = "cli-starting-deck", type = "function",
            function = new { name = "evaluate_choice", arguments = "{\"choice_id\":9,\"selected_entity_ids\":[81]}" } } } }, "tool_calls");
        public static Reply Final(string summary) => Json(new { role = "assistant", content = JsonSerializer.Serialize(new
        { summary, reasons = new[] { "synthetic CLI evidence" }, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(),
            unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(), actions = Array.Empty<object>() }) }, "stop");
        public static Reply Error() => new("PROVIDER_PRIVATE_BODY " + FakeKey, 503);
        public static Reply Timeout() => new(null, Stall: true);
        private static Reply Json(object message, string finish) => new(JsonSerializer.Serialize(new
        { model = Model, choices = new[] { new { finish_reason = finish, message } }, usage = new { prompt_tokens = 40, completion_tokens = 30, total_tokens = 70 } }));
    }

    private sealed class FakeModel : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        public List<JsonObject> Requests { get; } = [];
        public Uri Endpoint { get; }
        public FakeModel(IReadOnlyList<Reply> replies)
        {
            _listener.Start(); Endpoint = new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/v1/chat/completions");
            _run = Run(replies);
        }
        private async Task Run(IReadOnlyList<Reply> replies)
        {
            foreach (var reply in replies)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var request = await ReadRequest(stream, _stop.Token);
                Check(request["model"]?.GetValue<string>() == Model, "CLI sent a different model to the loopback fixture.");
                AssertNoSecrets(request.ToJsonString()); Requests.Add(request);
                if (reply.Stall) { await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token); return; }
                var body = Encoding.UTF8.GetBytes(reply.Body!);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} {(reply.Status == 200 ? "OK" : "Service Unavailable")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, _stop.Token); await stream.WriteAsync(body, _stop.Token); await stream.FlushAsync(_stop.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _run; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _stop.Dispose(); }
        }
        private static async Task<JsonObject> ReadRequest(NetworkStream stream, CancellationToken token)
        {
            using var bytes = new MemoryStream(); var buffer = new byte[8192]; var boundary = -1; var length = -1;
            while (boundary < 0 || bytes.Length < boundary + 4L + length)
            {
                var count = await stream.ReadAsync(buffer, token); Check(count > 0, "Local CLI HTTP request ended before its declared content length.");
                bytes.Write(buffer, 0, count); Check(bytes.Length <= 1024 * 1024, "Local CLI HTTP request exceeded the test bound.");
                var data = bytes.ToArray(); boundary = data.AsSpan().IndexOf("\r\n\r\n"u8);
                if (boundary >= 0 && length < 0)
                {
                    var header = Encoding.ASCII.GetString(data, 0, boundary);
                    Check(Regex.IsMatch(header, "(?im)^Authorization: Bearer " + Regex.Escape(FakeKey) + "\\r?$"), "CLI fake credential was not confined to the authorization header.");
                    var size = Regex.Match(header, @"(?im)^Content-Length:\s*(\d+)\s*$");
                    Check(size.Success && int.TryParse(size.Groups[1].Value, out length) && length is >= 0 and <= 1024 * 1024, "Expected bounded CLI Content-Length.");
                }
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray(), boundary + 4, length))!.AsObject();
        }
    }

    private static JsonObject Context(JsonObject request) => request["messages"]!.AsArray().OfType<JsonObject>()
        .Where(message => message["role"]?.GetValue<string>() == "user").Select(ParseContent).Last(content => content?["Snapshot"] is JsonObject)!;
    private static JsonObject? ParseContent(JsonObject message)
    {
        var text = message["content"]?.GetValue<string>();
        if (text is null) return null;
        try { return JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; }
    }
    private static JsonObject ToolResult(JsonObject request, string toolId) => ParseContent(request["messages"]!.AsArray().OfType<JsonObject>()
        .Single(message => message["role"]?.GetValue<string>() == "tool" && message["tool_call_id"]?.GetValue<string>() == toolId))!;
    private static void RequireSuccess(CliResult result) => Check(result.ExitCode == 0 && File.Exists(result.OutputPath), "Actual CLI failed: " + result.Stderr);
    private static void AssertNoSecrets(string text) => Check(!text.Contains(FakeKey, StringComparison.Ordinal) && !text.Contains("http://127.0.0.1:", StringComparison.Ordinal),
        "CLI durable output/audit or model request body contains a credential or configured endpoint.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
}
