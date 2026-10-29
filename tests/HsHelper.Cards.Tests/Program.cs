using System.Net;
using System.Text;
using HsHelper.Cards;

var passed = 0;
var failed = 0;
var taskRoot = Path.Combine(Path.GetTempPath(), "HsHelper.Cards.Tests", Guid.NewGuid().ToString("N"));
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
    if (args.Length == 2)
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
}
finally { Directory.Delete(taskRoot, recursive: true); }

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
