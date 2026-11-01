using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class DeckConstructionContextChecks
{
    private const string Build = "255790";
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("pre-game HTTP receives host-reviewed twenty-card construction without promoting partial pool legality", PlanningRequest);
        yield return ("pre-game construction distinguishes illegal thirty cards, legal forty and conflicting size sources", SizeOutcomes);
        yield return ("pre-game construction cannot borrow another deck build task or time and preserves the original context", Bindings);
        yield return ("pre-game missing expired or unknown-build size evidence never becomes ordinary legal thirty", MissingEvidence);
        yield return ("pre-game construction limits retain the entire large reference deck while marking the check unknown", OversizedReference);
    }

    private static async Task PlanningRequest()
    {
        var catalog = await Catalog(complete: false);
        var deck = Deck(20, 126055);
        var original = Context(deck);
        var before = original.Snapshot.ToJsonString();
        var context = await DeckConstructionContext.AttachAsync(original, catalog, deck);
        var construction = context.Snapshot["deck_construction"]!;
        Check(context.TemplateVersion == "hunter-v8/deckplan" && context.DeckVersionId == "saved-plan-deck" &&
            construction["legality"]!.GetValue<string>() == "Unknown" &&
            construction["special_main_deck_size"]!["required_cards"]!.GetValue<int>() == 20 &&
            construction["special_main_deck_size"]!["satisfied"]!.GetValue<bool>() &&
            original.Snapshot.ToJsonString() == before, "Reviewed size was absent, confused with complete legality or mutated the input context.");
        var handler = new PlanHandler();
        using var http = new HttpClient(handler);
        var advice = await new AdviceService(http).AnalyzeAsync(new(new Uri("https://construction.invalid/v1/chat/completions"), "fake-construction-model"),
            "fake-construction-key", context, (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => true);
        var request = JsonNode.Parse(handler.Request!)!;
        var messages = request["messages"]!.AsArray();
        var system = messages[0]!["content"]!.GetValue<string>();
        var body = string.Join("\n", messages.Skip(1).Select(message => message!["content"]!.GetValue<string>()));
        Check(handler.Calls == 1 && body.Contains("deck_construction", StringComparison.Ordinal) && body.Contains("126055", StringComparison.Ordinal) &&
            body.Contains("JAIL_430", StringComparison.Ordinal) && body.Contains("reviewed-plan-construction", StringComparison.Ordinal) &&
            system.Contains("不是阿扎莉娜开局复制后的牌库数量", StringComparison.Ordinal) &&
            system.Contains("不能把符合20或40张要求当作整个标准卡池合法", StringComparison.Ordinal) &&
            advice.TemplateVersion == "hunter-v8/deckplan" && advice.Actions.Count == 0 && !advice.VerifiedLethal &&
            !handler.Request!.Contains("fake-construction-key", StringComparison.Ordinal), "Production plan request lost host construction evidence or its scope.");
    }

    private static async Task SizeOutcomes()
    {
        var catalog = await Catalog();
        foreach (var (total, owner, legality, required, known) in new[]
        {
            (30, 126055, "Illegal", 20, true), (40, 79767, "Legal", 40, true)
        })
        {
            var deck = Deck(total, owner);
            var context = await DeckConstructionContext.AttachAsync(Context(deck), catalog, deck);
            var facts = context.Snapshot["deck_construction"]!;
            var size = facts["special_main_deck_size"]!;
            Check(facts["legality"]!.GetValue<string>() == legality && size["known"]!.GetValue<bool>() == known &&
                size["required_cards"]!.GetValue<int>() == required && size["satisfied"]!.GetValue<bool>() == (total == required),
                "Plan construction changed the host twenty/forty-card result.");
        }
        var conflict = Deck(30, 126055, 79767);
        var projected = (await DeckConstructionContext.AttachAsync(Context(conflict), catalog, conflict)).Snapshot["deck_construction"]!;
        Check(projected["legality"]!.GetValue<string>() != "Legal" && projected["special_main_deck_size"]!["known"]!.GetValue<bool>() == false &&
            projected["special_main_deck_size"]!["required_cards"] is null && projected["special_main_deck_size"]!["satisfied"] is null &&
            projected["special_main_deck_size"]!["sources"]!.AsArray().Count == 2, "Conflicting sources were flattened into ordinary thirty or a chosen favorable rule.");
    }

    private static async Task Bindings()
    {
        var catalog = await Catalog();
        var deck = Deck(20, 126055);
        var context = Context(deck);
        await Reject(() => DeckConstructionContext.AttachAsync(context, catalog, Deck(30, 126055)));
        await Reject(() => DeckConstructionContext.AttachAsync(context with { CardBuild = "255791" }, catalog, deck));
        await Reject(() => DeckConstructionContext.AttachAsync(context with { TaskKind = AdviceTaskKind.Review }, catalog, deck));
        await Reject(() => DeckConstructionContext.AttachAsync(context with { AsOf = null, KnowledgeAsOf = null }, catalog, deck));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await DeckConstructionContext.AttachAsync(context, catalog, deck, canceled.Token); throw new Exception("Canceled construction ran."); }
        catch (OperationCanceledException) { }
        var attached = await DeckConstructionContext.AttachAsync(context, catalog, deck);
        attached.Snapshot["deck_construction"]!["legality"] = "TAMPERED_COPY";
        Check(context.Snapshot["deck_construction"] is null && context.DeckVersionId == "saved-plan-deck" &&
            context.KnowledgeAsOf == At && attached.Snapshot["imported_deck"]!.ToJsonString() == context.Snapshot["imported_deck"]!.ToJsonString(),
            "Attaching a plan changed producer data, saved identity or the original imported deck.");
    }

    private static async Task MissingEvidence()
    {
        foreach (var expired in new[] { false, true })
        {
            var catalog = await Catalog(includeRules: expired, expired: expired);
            var deck = Deck(30, 126055);
            var result = (await DeckConstructionContext.AttachAsync(Context(deck), catalog, deck)).Snapshot["deck_construction"]!;
            Check(result["legality"]!.GetValue<string>() == "Unknown" &&
                result["special_main_deck_size"]?["satisfied"]?.GetValue<bool>() != true,
                "Missing or expired size evidence restored a false ordinary thirty-card legal claim.");
        }
        var unknownDeck = Deck(20, 126055);
        var unopened = new CardCatalog(Path.Combine(Path.GetTempPath(), "hs-helper-construction-plan", Guid.NewGuid().ToString("N"), "unopened.db"));
        var context = Context(unknownDeck, "unknown");
        var unknown = await DeckConstructionContext.AttachAsync(context, unopened, unknownDeck);
        Check(unknown.Snapshot["deck_construction"]!["legality"]!.GetValue<string>() == "Unknown" &&
            unknown.Snapshot["deck_construction"]!["special_main_deck_size"] is null,
            "Unknown build acquired another build's size evidence or required opening a catalog.");
    }

    private static AdviceContext Context(DeckDefinition deck, string build = Build)
    {
        var frame = new GameSnapshot("deck-saved-plan-deck", 0, 0, null, null, null, GamePhase.Unknown,
            FrozenDictionary<int, EntitySnapshot>.Empty, new(false, false, true, false, 0, ["Synthetic pre-game plan."]));
        return CompactAdviceContextBuilder.Create(frame, build, AdviceTaskKind.DeckPlan, deck)
            with { DeckVersionId = "saved-plan-deck", KnowledgeAsOf = At, AsOf = At };
    }
    private static async Task OversizedReference()
    {
        var source = Deck(20, 126055);
        var large = source with { Sideboards = [new(126055, Enumerable.Range(0, 1001).Select(index => new DeckCard(900000 + index, 1)).ToArray())] };
        var context = Context(large);
        var catalog = new CardCatalog(Path.Combine(Path.GetTempPath(), "hs-helper-construction-plan", Guid.NewGuid().ToString("N"), "unopened.db"));
        var result = await DeckConstructionContext.AttachAsync(context, catalog, large);
        var construction = result.Snapshot["deck_construction"]!;
        Check(construction["legality"]!.GetValue<string>() == "Unknown" && construction["input_limit_reached"]!.GetValue<bool>() &&
            construction["maximum_checked_entries"]!.GetValue<int>() == 500 && construction["special_main_deck_size"] is null &&
            construction["issues"]!.AsArray().Single()!.GetValue<string>().Contains("检查上限", StringComparison.Ordinal) &&
            result.Snapshot["imported_deck"]!["sideboards"]![0]!["cards"]!.AsArray().Count == 1001 &&
            context.Snapshot["deck_construction"] is null,
            "A large reference deck was truncated, promoted, or forced into a bounded catalog check.");
    }
    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory?.FullName ?? throw new Exception("Missing test root."),
            "tests", "HsHelper.Cards.Tests", "Fixtures", "deck-size-255790.json")));
    }
    private static DeckDefinition Deck(int total, params int[] owners)
    {
        using var fixture = Fixture();
        var fillers = fixture.RootElement.GetProperty("cards").EnumerateArray().Where(card =>
            card.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean() && card.GetProperty("cardClass").GetString() == "NEUTRAL" &&
            card.GetProperty("type").GetString() == "MINION" && card.GetProperty("rarity").GetString() == "COMMON")
            .Select(card => card.GetProperty("dbfId").GetInt32()).Order().ToArray();
        var entries = owners.Select(owner => new DeckCard(owner, 1)).ToList();
        var left = total - owners.Length;
        foreach (var filler in fillers) { if (left <= 0) break; var count = Math.Min(left, 2); entries.Add(new(filler, count)); left -= count; }
        Check(left == 0, "Not enough independent common fillers.");
        return new(2, [owners.Contains(126055) ? 813 : 31], entries.AsReadOnly(), []);
    }
    private static async Task<CardCatalog> Catalog(bool complete = true, bool includeRules = true, bool expired = false)
    {
        var catalog = new CardCatalog(Path.Combine(Path.GetTempPath(), "hs-helper-construction-plan", Guid.NewGuid().ToString("N"), "cards.db"));
        await catalog.InitializeAsync();
        using var fixture = Fixture();
        using var cards = new MemoryStream(Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("cards").GetRawText()));
        await catalog.ImportAsync(cards, Build, source: "Fixed public fixture; no live card-pool claim.");
        if (includeRules)
        {
            var from = At.AddDays(-2); var until = expired ? At.AddDays(-1) : At.AddDays(1);
            var pool = new CardPoolRules("isolated-plan-pool", Build, 2, from, until, "Synthetic complete/partial pool isolates construction only.", [],
                fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.GetProperty("id").GetString()!).ToArray(), [], [], complete)
            {
                Construction = new("reviewed-plan-construction", Build, 2, from, until, "Fixed 255790 CardDefs 997; test rule scope only.", [], [])
                { MainDeckSizes = [new("JAIL_430", 20, true), new("REV_018", 40, true)] }
            };
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(pool));
            await catalog.ImportPoolRulesAsync(input);
        }
        return catalog;
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Mismatched construction context was accepted.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class PlanHandler : HttpMessageHandler
    {
        public string? Request;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"summary\":\"构筑条件已说明\",\"reasons\":[],\"risks\":[]}"}}]}""") };
        }
    }
}
