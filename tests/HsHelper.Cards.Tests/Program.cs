using System.Net;
using System.Text;
using System.Text.Json;
using HsHelper.Cards;

var passed = 0;
var failed = 0;
var taskParent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local", "cards-tests"));
var taskRoot = Path.Combine(taskParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(taskRoot);
try
{
    await Run("atomic import, Chinese search, fixed build and immutable version", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "catalog.db"));
        await catalog.InitializeAsync();
        Equal<CardManifest?>(null, await catalog.GetActiveManifestAsync());
        var initial = await Import(catalog, Data("造成三点伤害"), "100");
        Equal(3, initial.CardCount);
        Equal(64, initial.Sha256.Length);
        var hunter = await catalog.SearchAsync(new("伤害", "HUNTER"));
        Equal(1, hunter.Count);
        Equal("HUNTER_A", hunter[0].Id);
        Equal(CardLegality.Unknown, hunter[0].StandardLegality);
        Equal("TOKEN_A", hunter[0].Entourage.Single());
        Equal("HUNTER_A", (await catalog.GetByDbfIdsAsync([1])).Single().Id);
        Equal(2, (await catalog.SearchAsync(new(CardClass: "HUNTER"))).Count);
        await Import(catalog, Data("造成四点伤害"), "101");
        Equal("101", (await catalog.GetActiveManifestAsync())!.Build);
        Equal("造成三点伤害", (await catalog.GetByIdsAsync(["HUNTER_A"], "100"))[0].Text);
        await Throws<InvalidDataException>(() => Import(catalog, Data("被改写"), "100"));
        Equal("101", (await catalog.GetActiveManifestAsync())!.Build);
        await Throws<InvalidDataException>(() => Import(catalog, "[]", "102"));
        await Throws<InvalidDataException>(() => Import(catalog, "[{\"id\":\"x\"}]", "102"));
        await Throws<InvalidDataException>(() => Import(catalog, "[{\"id\":\"x\",\"dbfId\":10},{\"id\":\"x\",\"dbfId\":11}]", "102"));
        await Throws<System.Text.Json.JsonException>(() => Import(catalog, "{broken", "102"));
        Equal("101", (await catalog.GetActiveManifestAsync())!.Build);
        Equal(0, (await catalog.SearchAsync(new("不存在"))).Count);
        await Import(catalog, Data("造成三点伤害"), "100");
        Equal("100", (await catalog.GetActiveManifestAsync())!.Build);
    });
    await Run("unknown internal fields are preserved and localized catalogs stay independent", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "internal.db"));
        await catalog.InitializeAsync();
        await Import(catalog, "[{\"id\":\"INTERNAL\",\"dbfId\":1,\"futureField\":{\"x\":1}}]", "100");
        var internalCard = (await catalog.GetByIdsAsync(["INTERNAL"])).Single();
        Equal("INTERNAL", internalCard.Name);
        Equal("", internalCard.Type);
        Equal(CardLegality.Unknown, internalCard.StandardLegality);
        if (!internalCard.RawJson.Contains("futureField", StringComparison.Ordinal)) throw new InvalidOperationException("Raw fields were lost.");
        using var english = new MemoryStream(Encoding.UTF8.GetBytes(Data("english")));
        await catalog.ImportAsync(english, "101", "enUS");
        Equal("100", (await catalog.GetActiveManifestAsync("zhCN"))!.Build);
        Equal("101", (await catalog.GetActiveManifestAsync("enUS"))!.Build);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => catalog.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(Data("取消"))), "102", cancellationToken: canceled.Token));
        Equal("100", (await catalog.GetActiveManifestAsync())!.Build);
    });
    await Run("remote failure preserves previous active cache", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "remote.db"));
        await catalog.InitializeAsync();
        await Import(catalog, Data("已有数据"), "200");
        using var client = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Throws<HttpRequestException>(() => catalog.SyncAsync(client));
        Equal("200", (await catalog.GetActiveManifestAsync())!.Build);
        Equal(3, (await catalog.SearchAsync(new())).Count);
    });
    await Run("mismatched remote build and interrupted import preserve active manifest", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "interrupted.db"));
        await catalog.InitializeAsync();
        var original = await Import(catalog, Data("保留的版本"), "300");
        using var redirectedClient = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.hearthstonejson.com/v1/302/zhCN/cards.json"),
            Content = new StringContent(Data("错误版本"))
        }));
        await Throws<InvalidDataException>(() => catalog.SyncAsync(redirectedClient, "301"));
        Equal(original, await catalog.GetActiveManifestAsync());
        using var cancellation = new CancellationTokenSource();
        await using var stream = new InterruptingStream(Encoding.UTF8.GetBytes(Data("取消导入")), cancellation);
        await Throws<OperationCanceledException>(() => catalog.ImportAsync(stream, "301", cancellationToken: cancellation.Token));
        Equal(original, await catalog.GetActiveManifestAsync());
        Equal<CardManifest?>(null, await catalog.GetManifestAsync("301"));
        Equal("保留的版本", (await catalog.GetByIdsAsync(["HUNTER_A"])).Single().Text);
    });
    await Run("unresolved latest is rejected rather than stored as a mutable build", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "latest.db"));
        await catalog.InitializeAsync();
        using var client = new HttpClient(new FakeHttp(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StringContent(Data("测试"))
        }));
        await Throws<InvalidDataException>(() => catalog.SyncAsync(client));
        Equal<CardManifest?>(null, await catalog.GetActiveManifestAsync());
    });
    await Run("latest index resolves build before downloading immutable data", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "resolved.db"));
        await catalog.InitializeAsync();
        var paths = new List<string>();
        using var client = new HttpClient(new FakeHttp(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("latest/", StringComparison.Ordinal)
                    ? "<a href=\"/v1/255790/zhCN/cards.json\">cards</a><a href=\"/v1/255790/\">root</a>"
                    : Data("固定版本"))
            };
        }));
        var result = await catalog.SyncAsync(client);
        Equal("255790", result.Build);
        Equal("/v1/255790/zhCN/cards.json", paths.Last());
    });
    await Run("deckstring accepts published legacy sample and comment wrapper", () =>
    {
        var deck = DeckCode.Decode("### sample\n# class\nAAEBAQcAAAQBAwIDAwMEAw==\n# end");
        Equal(1, deck.Format);
        Equal(7, deck.Heroes.Single());
        Equal(12, deck.Cards.Sum(x => x.Count));
        var roundtrip = DeckCode.Decode(DeckCode.Encode(deck));
        Equal(string.Join(',', deck.Cards), string.Join(',', roundtrip.Cards));
        return Task.CompletedTask;
    });
    await Run("deckstring preserves sideboards including n-copy entries", () =>
    {
        var source = new DeckDefinition(2, [31], [new(42, 2), new(10000, 1)],
            [new(10000, [new(44, 1), new(55, 2), new(66, 3)])]);
        var encoded = DeckCode.Encode(source);
        var result = DeckCode.Decode(encoded);
        Equal(2, result.Format);
        Equal(10000, result.Sideboards.Single().OwnerDbfId);
        Equal("44:1,55:2,66:3", string.Join(',', result.Sideboards[0].Cards.Select(c => $"{c.DbfId}:{c.Count}")));
        Equal(encoded, DeckCode.Encode(result));
        return Task.CompletedTask;
    });
    await Run("deckstring rejects truncated, overflowing and unknown extensions", async () =>
    {
        await Throws<FormatException>(() => Task.FromResult(DeckCode.Decode(Convert.ToBase64String([0, 1, 2, 1, 31]))));
        await Throws<NotSupportedException>(() => Task.FromResult(DeckCode.Decode(Convert.ToBase64String([0, 2, 2]))));
        await Throws<FormatException>(() => Task.FromResult(DeckCode.Decode(Convert.ToBase64String([0, 1, 255, 255, 255, 255, 255, 0]))));
        var valid = Convert.FromBase64String(DeckCode.Encode(new(2, [31], [new(42, 2)], [])));
        await Throws<NotSupportedException>(() => Task.FromResult(DeckCode.Decode(Convert.ToBase64String(valid.Concat(new byte[] { 99 }).ToArray()))));
    });
    await Run("versioned pool rules distinguish membership, complete legality, bans and expiry", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "rules.db"));
        await catalog.InitializeAsync();
        await Import(catalog, Data("规则测试").Replace("\"entourage\"", "\"mechanics\":[\"BATTLECRY\"],\"entourage\""), "500");
        Equal(0, (await catalog.SearchAsync(new(StandardPoolOnly: true))).Count);
        var now = DateTimeOffset.UtcNow;
        var partial = Rules("partial", "500", now.AddDays(-1), now.AddDays(7), false);
        await ImportRules(catalog, partial);
        Equal(1, (await catalog.SearchAsync(new(StandardPoolOnly: true))).Count);
        Equal(CardLegality.Unknown, (await catalog.SearchAsync(new(StandardPoolOnly: true))).Single().StandardLegality);
        Equal(0, (await catalog.SearchAsync(new(StandardOnly: true))).Count);
        var complete = partial with { RuleId = "complete", EffectiveFrom = now.AddHours(-12), IsComplete = true, AllowedCardIds = ["DUAL_A"], BannedCardIds = ["DUAL_A"] };
        await ImportRules(catalog, complete);
        Equal(CardLegality.Legal, (await catalog.GetByIdsAsync(["HUNTER_A"])).Single().StandardLegality);
        Equal(CardLegality.Illegal, (await catalog.GetByIdsAsync(["DUAL_A"])).Single().StandardLegality);
        Equal(1, (await catalog.SearchAsync(new(StandardOnly: true, Keyword: "battlecry"))).Count);
        Equal(0, (await catalog.SearchAsync(new(StandardOnly: true, Keyword: "TAUNT"))).Count);
        var reopened = new CardCatalog(Path.Combine(taskRoot, "rules.db"));
        await reopened.InitializeAsync();
        Equal("complete", (await reopened.GetPoolRulesAsync("500"))!.RuleId);
        await Throws<InvalidDataException>(() => ImportRules(catalog, complete with { AllowedSets = ["OTHER"] }));
        Equal("complete", (await catalog.GetPoolRulesAsync("500"))!.RuleId);
        await ImportRules(catalog, complete with { RuleId = "expired", EffectiveFrom = now.AddHours(-1), ValidUntil = now.AddMinutes(-30) });
        Equal<CardPoolRules?>(null, await catalog.GetPoolRulesAsync("500"));
        Equal(CardLegality.Unknown, (await catalog.GetByIdsAsync(["HUNTER_A"])).Single().StandardLegality);
        Equal("complete", (await catalog.GetPoolRulesAsync("500", at: now.AddHours(-2)))!.RuleId);
        Equal<CardPoolRules?>(null, await catalog.GetPoolRulesAsync("501"));
    });
    await Run("invalid or canceled pool import leaves prior evidence unchanged", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "rule-failure.db"));
        await catalog.InitializeAsync();
        var rules = Rules("good", "600", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true);
        await ImportRules(catalog, rules);
        await Throws<InvalidDataException>(() => ImportRules(catalog, rules with { RuleId = "bad", Source = "" }));
        await Throws<InvalidDataException>(() => ImportRules(catalog, rules with { RuleId = "bad", ValidUntil = rules.EffectiveFrom }));
        await Throws<InvalidDataException>(() => ImportRules(catalog, rules with { RuleId = "bad", AllowedSets = [], AllowedCardIds = [] }));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => catalog.ImportPoolRulesAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(rules with { RuleId = "cancel" })), cancellation.Token));
        Equal("good", (await catalog.GetPoolRulesAsync("600"))!.RuleId);
    });
    await Run("official pool parses explicit sets and verifies fixed build counts without assuming bans", async () =>
    {
        var config = JsonSerializer.Serialize(new { sets = new object[] {
            new { slug = "test", id = 123, collectibleCount = 2 },
            new { slug = "standard", id = "standard", cardSets = new[] { "test" } } }, setGroups = Array.Empty<object>() });
        var html = "<cards config=\"" + WebUtility.HtmlEncode(config) + "\"></cards>";
        var xml = """<CardDefs build="700"><Entity CardID="A"><Tag name="COLLECTIBLE" value="1"/><Tag name="CARD_SET" value="123"/></Entity><Entity CardID="B"><Tag name="COLLECTIBLE" value="1"/><Tag name="CARD_SET" value="123"/></Entity></CardDefs>""";
        var pool = OfficialStandardPool.CreatePartial(html, new MemoryStream(Encoding.UTF8.GetBytes(xml)), "700", DateTimeOffset.UtcNow);
        Equal(2, pool.AllowedCardIds.Count);
        Equal(false, pool.IsComplete);
        await Throws<InvalidDataException>(() => Task.FromResult(OfficialStandardPool.CreatePartial(html, new MemoryStream(Encoding.UTF8.GetBytes(xml)), "701", DateTimeOffset.UtcNow)));
        await Throws<InvalidDataException>(() => Task.FromResult(OfficialStandardPool.CreatePartial(html.Replace("&quot;collectibleCount&quot;:2", "&quot;collectibleCount&quot;:3"), new MemoryStream(Encoding.UTF8.GetBytes(xml)), "700", DateTimeOffset.UtcNow)));
    });
    await Run("deck description maps cards, curves and ordinary construction while retaining uncertainty", async () =>
    {
        var catalog = new CardCatalog(Path.Combine(taskRoot, "deck-description.db"));
        await catalog.InitializeAsync();
        var records = Enumerable.Range(0, 20).Select(i => new Dictionary<string, object> {
            ["id"] = "DECK_" + i, ["dbfId"] = 100 + i, ["name"] = "套牌牌" + i,
            ["type"] = "MINION", ["cardClass"] = "HUNTER", ["set"] = "TEST", ["rarity"] = "COMMON",
            ["cost"] = i % 7, ["collectible"] = true, ["text"] = "从你的套牌中抽一张牌" }).ToList();
        records.Add(new() { ["id"] = "HERO", ["dbfId"] = 31, ["name"] = "猎人", ["type"] = "HERO", ["cardClass"] = "HUNTER" });
        await Import(catalog, JsonSerializer.Serialize(records), "700");
        await ImportRules(catalog, Rules("deck-rules", "700", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true));
        var deck = new DeckDefinition(2, [31], Enumerable.Range(0, 15).Select(i => new DeckCard(100 + i, 2)).ToArray(), []);
        var description = await catalog.DescribeDeckAsync(deck, "700");
        Equal(CardLegality.Legal, description.Legality);
        Equal("HUNTER", description.HeroClass);
        Equal(30, description.ManaCurve.Sum(b => b.Count));
        Equal("套牌牌0", description.Cards[0].Name);
        var withSideboard = await catalog.DescribeDeckAsync(deck with { Sideboards = [new(100, [new(101, 1)])] }, "700");
        Equal(CardLegality.Unknown, withSideboard.Legality);
        Equal("套牌牌0", withSideboard.Sideboards.Single().OwnerName);
        Equal("套牌牌1", withSideboard.Sideboards.Single().Cards.Single().Name);
        Equal(1, withSideboard.Sideboards.Single().ManaCurve.Sum(b => b.Count));
        Equal(CardLegality.Illegal, (await catalog.DescribeDeckAsync(deck with { Sideboards = [new(999, [new(101, 1)])] }, "700")).Legality);
        Equal(CardLegality.Unknown, (await catalog.DescribeDeckAsync(deck with { Cards = [new(999, 2)] }, "700")).Legality);
        records[0]["rarity"] = "LEGENDARY";
        await Import(catalog, JsonSerializer.Serialize(records), "701");
        await ImportRules(catalog, Rules("deck-rules-701", "701", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true));
        Equal(CardLegality.Illegal, (await catalog.DescribeDeckAsync(deck, "701")).Legality);
        records[0]["rarity"] = "COMMON";
        records[0]["cardClass"] = "NEUTRAL";
        records[0]["classes"] = new[] { "DRUID", "MAGE" };
        await Import(catalog, JsonSerializer.Serialize(records), "702");
        await ImportRules(catalog, Rules("deck-rules-702", "702", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true));
        Equal(CardLegality.Illegal, (await catalog.DescribeDeckAsync(deck, "702")).Legality);
        records[0]["cardClass"] = "HUNTER"; records[0].Remove("classes");
        records[0]["text"] = "你的套牌上限和初始生命值改为40。";
        await Import(catalog, JsonSerializer.Serialize(records), "703");
        await ImportRules(catalog, Rules("deck-rules-703", "703", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true));
        Equal(CardLegality.Unknown, (await catalog.DescribeDeckAsync(deck with { Cards = Enumerable.Range(0, 20).Select(i => new DeckCard(100 + i, 2)).ToArray() }, "703")).Legality);
    });
    await ConstructionChecks.RunAsync(taskRoot, Run);
    await MainDeckSizeChecks.RunAsync(taskRoot, Run);
    await PoolRuleTimeChecks.RunAsync(taskRoot, Run);
    await AssembledCardTextChecks.RunAsync(Run);
    await CardBatchChecks.RunAsync(taskRoot, Run);
    await Run("image cache uses verified source and reuses validated PNG offline", async () =>
    {
        var cache = new CardImageCache(Path.Combine(taskRoot, "images"));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jF9kAAAAASUVORK5CYII=");
        var calls = 0;
        using var http = new HttpClient(new FakeHttp(request => { calls++; return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(png) }; }));
        var first = await cache.GetAsync(http, "TEST_CARD");
        Equal(true, File.Exists(first));
        Equal(first, await cache.GetAsync(http, "TEST_CARD"));
        Equal(1, calls);
        await Throws<ArgumentException>(() => cache.GetAsync(http, "../../escape"));
        using var bad = new HttpClient(new FakeHttp(request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("<html>no image</html>") }));
        await Throws<InvalidDataException>(() => cache.GetAsync(bad, "BAD_CARD"));
        Equal(false, File.Exists(Path.Combine(taskRoot, "images", "zhCN-BAD_CARD.png")));
    });
    if (args.Length >= 2)
    {
        await Run("optional real-data import", async () =>
        {
            var catalog = new CardCatalog(Path.Combine(taskRoot, "real.db"));
            await catalog.InitializeAsync();
            await using var file = File.OpenRead(args[0]);
            var manifest = await catalog.ImportAsync(file, args[1], source: "downloaded-verification");
            Console.WriteLine($"Imported build {manifest.Build}: {manifest.CardCount} cards, SHA256 {manifest.Sha256}");
            Equal(manifest.Build, (await catalog.GetActiveManifestAsync())!.Build);
            if ((await catalog.SearchAsync(new(CardClass: "HUNTER"))).Count == 0) throw new InvalidOperationException("Expected hunter cards.");
        });
    }
    if (args.Length >= 4)
    {
        await Run("optional official pool and fixed-build source compatibility", () =>
        {
            using var definitions = File.OpenRead(args[3]);
            var rules = OfficialStandardPool.CreatePartial(File.ReadAllText(args[2]), definitions, args[1], DateTimeOffset.UtcNow);
            Console.WriteLine($"Official standard pool membership: {rules.AllowedCardIds.Count}; complete legality: {rules.IsComplete}");
            if (rules.AllowedCardIds.Count < 100) throw new InvalidDataException("Expected substantial current pool.");
            if (args[1] == "255790")
            {
                if (rules.Construction?.CardGroups.Single().CardIds.Count != 4 ||
                    rules.Construction.Sideboards.Single() is not { OwnerCardId: "JAIL_831", MaximumCards: 3, IgnoresHeroClass: true, AllowedCardsComplete: false })
                    throw new InvalidDataException("Expected reviewed construction tags in the actual fixed-build definitions.");
                Console.WriteLine("Fixed-build construction: four Old Gods, group maximum 1; JAIL_831 capacity 3, subset membership still unknown.");
            }
            return Task.CompletedTask;
        });
    }
    if (args.Length == 5)
    {
        await Run("optional user beast hunter deck preserves full main deck and sideboards", async () =>
        {
            var deck = DeckCode.Decode(File.ReadAllText(args[4]));
            Equal(2, deck.Format);
            Equal(30, deck.Cards.Sum(c => c.Count));
            Equal(3, deck.Sideboards.SelectMany(s => s.Cards).Sum(c => c.Count));
            Equal(DeckCode.Encode(deck), DeckCode.Encode(DeckCode.Decode(DeckCode.Encode(deck))));
            var catalog = new CardCatalog(Path.Combine(taskRoot, "real.db"));
            using var definitions = File.OpenRead(args[3]);
            await ImportRules(catalog, OfficialStandardPool.CreatePartial(File.ReadAllText(args[2]), definitions, args[1], DateTimeOffset.UtcNow));
            var description = await catalog.DescribeDeckAsync(deck, args[1]);
            if (description.Cards.Any(c => c.CardId is null) || description.Sideboards.SelectMany(s => s.Cards).Any(c => c.CardId is null))
                throw new InvalidDataException("Expected all actual user deck cards in the fixed build.");
            Equal(30, description.ManaCurve.Sum(b => b.Count));
            Equal(3, description.Sideboards.SelectMany(s => s.ManaCurve).Sum(b => b.Count));
            Console.WriteLine($"Actual hunter main deck: 30; sideboard: 3; description legality: {description.Legality}");
        });
    }
}
finally
{
    var cleanup = Path.GetFullPath(taskRoot);
    if (!cleanup.StartsWith(taskParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to clean outside the explicit test directory.");
    Directory.Delete(cleanup, recursive: true);
}

Console.WriteLine($"Cards regression: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
}
static async Task Throws<T>(Func<Task> test) where T : Exception
{
    try { await test(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}
static Task<CardManifest> Import(CardCatalog catalog, string json, string build)
    => catalog.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), build);
static Task<CardPoolRules> ImportRules(CardCatalog catalog, CardPoolRules rules)
    => catalog.ImportPoolRulesAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(rules)));
static CardPoolRules Rules(string id, string build, DateTimeOffset from, DateTimeOffset until, bool complete)
    => new(id, build, 2, from, until, "test-explicit-reviewed-evidence", ["TEST"], [], [], [], complete);
static string Data(string text) => $$"""
    [
      {"id":"HUNTER_A","dbfId":1,"name":"测试猎人牌","text":"{{text}}","type":"SPELL","cardClass":"HUNTER","set":"TEST","cost":2,"collectible":true,"entourage":["TOKEN_A"]},
      {"id":"DUAL_A","dbfId":2,"name":"双职业牌","type":"MINION","cardClass":"NEUTRAL","classes":["HUNTER","DRUID"],"cost":1,"collectible":true},
      {"id":"TOKEN_A","dbfId":3,"name":"衍生牌","type":"MINION","cardClass":"NEUTRAL","cost":0}
    ]
    """;

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response(request));
}

sealed class InterruptingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
{
    private bool _read;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_read)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }
        _read = true;
        return base.ReadAsync(buffer[..Math.Min(16, buffer.Length)], cancellationToken);
    }
}
