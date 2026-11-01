using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Storage;

internal static partial class CliHistoryIntegration
{
    public static async Task RunDeckPlanConstructionAsync(string root, Action<string> pass)
    {
        var cli = typeof(CliHistoryIntegration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CliTestTargetPath").Value!;
        Check(Path.IsPathFullyQualified(cli) && File.Exists(cli), "The construction check needs the actual production CLI output.");
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "HsHelper.slnx"))) workspace = workspace.Parent;
        Check(workspace is not null, "Cannot find the fixed primary fixture workspace.");
        using var primary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,
            "tests", "HsHelper.Cards.Tests", "Fixtures", "deck-size-255790.json")));
        Check(primary.RootElement.GetProperty("build").GetString() == Build &&
            primary.RootElement.GetProperty("sourceSha256").GetString() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            primary.RootElement.GetProperty("cardDefsSha256").GetString() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8",
            "CLI planning no longer uses the fixed complete public card records.");
        var records = primary.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()).ToArray();
        int Dbf(string id) => records.Single(card => card.GetProperty("id").GetString() == id).GetProperty("dbfId").GetInt32();
        DeckDefinition Deck(int total, string owner)
        {
            var entries = new List<DeckCard> { new(Dbf(owner), 1) };
            var remaining = total - 1;
            foreach (var card in records.Where(card => card.GetProperty("cardClass").GetString() == "NEUTRAL" &&
                card.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean() &&
                card.GetProperty("rarity").GetString() != "LEGENDARY"))
            {
                if (remaining == 0) break;
                var count = Math.Min(2, remaining); entries.Add(new(card.GetProperty("dbfId").GetInt32(), count)); remaining -= count;
            }
            Check(remaining == 0, "The unmodified real neutral fillers cannot fill this construction fixture.");
            return new(2, [Dbf("HERO_09")], entries, []);
        }

        foreach (var complete in new[] { true, false })
        {
            var directory = Path.Combine(root, "cli-deck-construction", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records))))
                await catalog.ImportAsync(input, Build, source: "fixture://unchanged-public-primary-255790");
            var ownerRaw = (await catalog.GetByIdsAsync(["JAIL_430"], Build)).Single().RawJson;
            var from = DateTimeOffset.UtcNow.AddDays(-3); var until = from.AddDays(6);
            var name = complete ? "cli-structural-complete" : "cli-structural-partial";
            // Membership is explicitly fictional so this test isolates construction; no real Standard-pool claim.
            var rules = new CardPoolRules(name, Build, 2, from, until,
                "fixture://fictional-membership-not-live-standard", [],
                records.Where(card => card.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean())
                    .Select(card => card.GetProperty("id").GetString()!).ToArray(), [], [], complete,
                "FICTIONAL pool membership only; every underlying card record is the unchanged fixed public primary record.")
            {
                Construction = new(name + "-evidence", Build, 2, from, until,
                    "fixture://fixed-255790-primary-deck-size-evidence", [], [])
                { MainDeckSizes = [new("JAIL_430", 20, true), new("REV_018", 40, true)] }
            };
            async Task Import(CardPoolRules value)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
                await catalog.ImportPoolRulesAsync(input);
            }
            await Import(rules);
            var sessions = new SessionStore(Path.Combine(directory, "sessions.db")); await sessions.InitializeAsync();
            var original = Deck(20, "JAIL_430"); var originalCode = DeckCode.Encode(original);
            var deck = await sessions.SaveDeckVersionAsync("Original real twenty-card plan", original, Build);
            var decoy = await sessions.SaveDeckVersionAsync("Later unrelated real forty-card deck", Deck(40, "REV_018"), Build);
            var frame = FrameOf("cli-deck-construction-" + Guid.NewGuid().ToString("N"));
            var entities = frame.Entities.ToDictionary();
            var hero = entities[11]; var heroTags = hero.Tags.ToDictionary(); heroTags["CLASS"] = "PRIEST";
            entities[11] = hero with { CardId = "HERO_09", Tags = heroTags.ToFrozenDictionary() };
            var opponent = entities[21]; var opponentTags = opponent.Tags.ToDictionary(); opponentTags["CLASS"] = "HUNTER";
            entities[21] = opponent with { CardId = "HERO_05", Tags = opponentTags.ToFrozenDictionary() };
            frame = frame with { Entities = entities.ToFrozenDictionary() };
            var before = SnapshotCodec.Serialize(frame);
            var recorder = new SessionRecorder(sessions, new(deck.Id, Build, 1, PlayerClass: "PRIEST"));
            recorder.Observe(frame, 1); await recorder.CompleteAsync();
            var point = (await sessions.ListDecisionPointsAsync(frame.MatchId, includeUnstable: true)).Single();
            Check(point.IsDecisionPoint && point.IsStable && point.IsComplete, "No real stable saved point binds the planning deck.");
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); await knowledge.InitializeAsync();
            var plan = await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.DeckPlan, "original primary deck", "SAVED_TWENTY_PLAN"));
            var note = await knowledge.SaveAsync(new(deck.Id, CoachKnowledgeKind.MatchNote, "original match", "SAVED_TWENTY_NOTE", MatchId: frame.MatchId));
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice-archive.db")); await archive.InitializeAsync();
            var traces = new AnalysisTraceStore(Path.Combine(directory, "analysis-traces.db")); await traces.InitializeAsync();
            var fixture = new Fixture(directory, frame, point, deck, decoy, plan, note, archive, traces);

            {
                // JSON preserves ticks even though the SQL index uses milliseconds. The exact
                // saved-point query must exclude a genuinely later revision within that millisecond.
                var later = rules with { RuleId = name + "-future", EffectiveFrom = point.RecordedAt.AddTicks(1), IsComplete = !complete,
                    Construction = rules.Construction! with { RuleId = name + "-future-evidence", EffectiveFrom = point.RecordedAt.AddTicks(1) } };
                await Import(later);
                Check((await catalog.GetPoolRulesAsync(Build, at: DateTimeOffset.UtcNow))?.RuleId == later.RuleId,
                    "The later pool revision did not actually become the current lookup result.");
            }

            var summary = complete ? "CLI_DECKPLAN_COMPLETE_PRIMARY" : "CLI_DECKPLAN_PARTIAL_PRIMARY";
            var result = await Invoke(cli, fixture, frame, "PLAN_ORIGINAL_TWENTY", [Reply.Final(summary)], task: "deckplan");
            RequireSuccess(result);
            var context = Context(result.Requests.Single()); var snapshot = context["Snapshot"]!;
            var construction = snapshot["deck_construction"]!; var size = construction["special_main_deck_size"]!;
            Check(context["DeckVersionId"]?.GetValue<string>() == deck.Id && context["CardBuild"]?.GetValue<string>() == Build &&
                context["KnowledgeAsOf"]?.GetValue<DateTimeOffset>() == point.RecordedAt &&
                construction["checked_at"]?.GetValue<DateTimeOffset>() == point.RecordedAt &&
                construction["card_build"]?.GetValue<string>() == Build && construction["pool_rule_id"]?.GetValue<string>() == name &&
                construction["legality"]?.GetValue<string>() == (complete ? "Legal" : "Unknown") &&
                construction["actual_main_cards"]?.GetValue<int>() == 20 && construction["ordinary_main_reference"]?.GetValue<int>() == 30 &&
                size["actual_cards"]?.GetValue<int>() == 20 && size["required_cards"]?.GetValue<int>() == 20 &&
                size["known"]?.GetValue<bool>() == true && size["satisfied"]?.GetValue<bool>() == true &&
                size["sources"] is JsonArray { Count: 1 } sources && sources[0]?["owner_card_id"]?.GetValue<string>() == "JAIL_430" &&
                sources[0]?["evidence_id"]?.GetValue<string>() == name + "-evidence" &&
                sources[0]?["evidence_source"]?.GetValue<string>() == rules.Construction!.Source,
                "The real CLI lost the saved original deck/build/cutoff, borrowed later evidence, padded twenty to thirty, or upgraded partial membership to Legal: " + construction.ToJsonString());
            Check(snapshot["imported_deck"]?["cards"]!.AsArray().Sum(card => card!["count"]!.GetValue<int>()) == 20 &&
                context["TemplateVersion"]?.GetValue<string>() == "hunter-v8/deckplan" &&
                result.Requests[0]["messages"]!.AsArray().Any(message => message?["role"]?.GetValue<string>() == "system" &&
                    message["content"]!.GetValue<string>().Contains("deck_construction", StringComparison.Ordinal)),
                "The actual request omitted the original definition or the v8 host construction policy.");
            var reopenedArchive = new AdviceArchiveStore(Path.Combine(fixture.Directory, "advice-archive.db")); await reopenedArchive.InitializeAsync();
            var archived = (await reopenedArchive.ListAsync()).Single(record => record.Advice.Summary == summary);
            Check(archived.Advice.PromptTemplateVersion == "hunter-v8/deckplan" && archived.Advice.Task == "DeckPlan" &&
                archived.Advice.DeckVersionId == deck.Id && archived.Advice.DeckVersionId != decoy.Id &&
                archived.Advice.KnowledgeAsOf == point.RecordedAt && archived.Advice.CardBuild == Build &&
                archived.Advice.MatchId == frame.MatchId && archived.Advice.StateVersion == frame.StateVersion &&
                archived.Advice.EventSequence == frame.EventSequence && archived.Advice.Actions.Count == 0 && !archived.Advice.VerifiedLethal,
                "The CLI archive changed the v8 planning task or its exact saved deck/frame/time provenance.");
            await AssertTrace(fixture, archived, expectedRequests: 1);
            var restored = await sessions.GetDeckVersionAsync(deck.Id);
            Check(restored?.Code == originalCode && DeckCode.Encode(restored.Definition) == originalCode &&
                SnapshotCodec.Serialize(frame) == before && (await catalog.GetByIdsAsync(["JAIL_430"], Build)).Single().RawJson == ownerRaw,
                "Planning rewrote the saved original code, source snapshot, or real primary card definition.");

            if (complete)
            {
                var altered = frame.Entities.ToDictionary(); var tags = altered[11].Tags.ToDictionary(); tags["ARMOR"] = "1";
                altered[11] = altered[11] with { Tags = tags.ToFrozenDictionary() };
                var unbound = await Invoke(cli, fixture, frame with { Entities = altered.ToFrozenDictionary() },
                    "UNMATCHED_DECKPLAN", [Reply.Final("CLI_UNBOUND_DECKPLAN")], task: "deckplan");
                RequireSuccess(unbound);
                var unboundContext = Context(unbound.Requests.Single());
                var unboundRecord = (await reopenedArchive.ListAsync()).Single(record => record.Advice.Summary == "CLI_UNBOUND_DECKPLAN");
                Check(unboundContext["DeckVersionId"] is null && unboundContext["Snapshot"]?["deck_construction"] is null &&
                    unboundContext["Snapshot"]?["imported_deck"] is null && unboundRecord.Advice.DeckVersionId is null &&
                    unboundRecord.Advice.KnowledgeAsOf is null && unboundRecord.Advice.PromptTemplateVersion == "hunter-v8/deckplan",
                    "An unmatched external frame borrowed the most recently saved deck or invented construction evidence.");
                await AssertTrace(fixture, unboundRecord, expectedRequests: 1);
            }
        }
        pass("production CLI deckplan v8 sends saved original twenty-card construction at its exact historical cutoff, keeps partial membership Unknown, archives exact deck provenance, and never borrows a deck for an unmatched frame");
    }
}
