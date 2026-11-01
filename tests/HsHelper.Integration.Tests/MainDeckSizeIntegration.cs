using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Storage;

/// <summary>Real fixed card definitions, fictional complete membership only, and the production CLI; no model/game.</summary>
internal static class MainDeckSizeIntegration
{
    private const string Build = "255790";
    private const string Small = "JAIL_430";
    private const string Large = "REV_018";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "main-deck-size", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var workspace = Workspace();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace,
            "tests", "HsHelper.Cards.Tests", "Fixtures", "deck-size-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build &&
            fixture.RootElement.GetProperty("sourceSha256").GetString() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            fixture.RootElement.GetProperty("cardDefsSha256").GetString() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8",
            "Main-deck fixture lost its fixed primary provenance.");
        var records = fixture.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.Clone()).ToArray();
        var xml = fixture.RootElement.GetProperty("cardDefsXml").GetString()!;
        var reviewed = Reviewed(xml);
        Check(!reviewed.IsComplete && reviewed.Construction?.MainDeckSizes is { Count: 2 },
            "The actual public import factory did not extract both finite size constraints, or claimed complete live legality.");
        // This complete membership is deliberately fictional: it isolates structural legality.
        // Every card record and all reviewed construction constraints remain the actual fixed data.
        var complete = Complete("main-size-reviewed", reviewed.Construction!, records);
        var small = Deck(records, 20, Small); var large = Deck(records, 40, Large);
        var basic = await Create(directory, "complete", records, complete);
        var beforeCards = (await basic.Catalog.GetByIdsAsync([Small, Large], Build)).ToDictionary(card => card.Id, card => card.RawJson);
        var twenty = await basic.Catalog.DescribeDeckAsync(small, Build, Now);
        var forty = await basic.Catalog.DescribeDeckAsync(large, Build, Now);
        AssertSize(twenty, 20, Small, CardLegality.Legal);
        AssertSize(forty, 40, Large, CardLegality.Legal);
        var history = new SessionStore(Path.Combine(basic.Directory, "history.db")); await history.InitializeAsync();
        foreach (var (name, deck, expected) in new[] { ("actual twenty", small, 20), ("actual forty", large, 40) })
        {
            var code = DeckCode.Encode(deck); var version = await history.SaveDeckVersionAsync(name, deck, Build);
            var reopenedStore = new SessionStore(Path.Combine(basic.Directory, "history.db")); await reopenedStore.InitializeAsync();
            var restored = (await reopenedStore.ListDeckVersionsAsync()).Single(item => item.Id == version.Id);
            Check(restored.Code == code && DeckCode.Encode(DeckCode.Decode(restored.Code)) == code &&
                restored.Definition.Cards.Sum(card => card.Count) == expected && restored.CardBuild == Build,
                "Saving a non-thirty-card deck padded/truncated its actual code or lost the fixed source.");
        }
        var reopened = new CardCatalog(Path.Combine(basic.Directory, "cards.db")); await reopened.InitializeAsync();
        AssertSize(await reopened.DescribeDeckAsync(DeckCode.Decode(DeckCode.Encode(small)), Build, Now), 20, Small, CardLegality.Legal);
        Check((await reopened.GetByIdsAsync([Small, Large], Build)).All(card => beforeCards[card.Id] == card.RawJson),
            "Construction review modified a real card definition to manufacture a test pool or count.");
        pass("fixed twenty and forty owners pass scoped structural review and exact deck codes and saved versions survive both real SQLite reopens without padding or rewriting card metadata");

        foreach (var (owner, correct) in new[] { (Small, 20), (Large, 40) })
            foreach (var wrong in new[] { correct - 1, correct + 1, 30 })
            {
                var described = await reopened.DescribeDeckAsync(Deck(records, wrong, owner), Build, Now);
                Check(described.Legality == CardLegality.Illegal && described.MainDeckSize is { IsKnown: true, IsSatisfied: false } size &&
                    size.RequiredCards == correct && size.ActualCards == wrong,
                    "A reviewed explicit main count mismatch was merely unknown or reverted to ordinary thirty.");
            }
        var duplicate = small with { Cards = small.Cards.Select((card, index) => index == 1 ? card with { Count = 3 } : index == 2 ? card with { Count = 1 } : card).ToArray() };
        var duplicateResult = await reopened.DescribeDeckAsync(duplicate, Build, Now);
        Check(duplicateResult.Legality == CardLegality.Illegal && duplicateResult.MainDeckSize is { ActualCards: 20, IsSatisfied: true },
            "A size-only modifier waived ordinary per-card copy limits.");
        pass("known twenty forty and ordinary copy constraints are enforced independently instead of accepting a thirty-card modifier deck");

        var missing = await Create(directory, "missing", records, complete with { RuleId = "main-size-no-evidence", Construction = null });
        foreach (var size in new[] { 20, 30, 40 })
        {
            var value = await missing.Catalog.DescribeDeckAsync(Deck(records, size, Small), Build, Now);
            Check(value.Legality != CardLegality.Legal && value.MainDeckSize is { IsKnown: false, RequiredCards: null },
                "Missing construction evidence plus unfamiliar Chinese text silently used ordinary thirty.");
        }
        var unmapped = await missing.Catalog.DescribeDeckAsync(Deck(records, 30, "BOT_914"), Build, Now);
        Check(unmapped.Legality != CardLegality.Legal && unmapped.MainDeckSize is { IsKnown: false },
            "An observed replacement-deck modifier was treated as an ordinary thirty-card deck.");
        pass("real Chinese twenty-card and known unmapped replacement owners never become Legal when scoped main-size evidence is absent");

        var partial = await Create(directory, "partial", records, complete with { RuleId = "main-size-partial", IsComplete = false });
        var partialGood = await partial.Catalog.DescribeDeckAsync(small, Build, Now);
        Check(partialGood.Legality == CardLegality.Unknown && partialGood.MainDeckSize is { RequiredCards: 20, IsKnown: true, IsSatisfied: true },
            "Correct structural size promoted incomplete pool membership into full legality.");
        Check((await partial.Catalog.DescribeDeckAsync(Deck(records, 30, Small), Build, Now)).Legality == CardLegality.Illegal,
            "Incomplete pool membership erased a positively proven count violation.");
        var expiredConstruction = complete with { RuleId = "main-size-expired-child", Construction = complete.Construction! with
            { RuleId = "main-size-expired-child-evidence", EffectiveFrom = Now.AddDays(-1), ValidUntil = Now.AddHours(-1) } };
        var expired = await Create(directory, "expired-child", records, expiredConstruction);
        Check((await expired.Catalog.DescribeDeckAsync(Deck(records, 30, Small), Build, Now)).Legality == CardLegality.Unknown,
            "An expired inner constraint or ordinary fallback proved thirty-card legality.");
        var other = await Create(directory, "other-build", records,
            complete with { RuleId = "main-size-other-build", Build = "255791", Construction = null }, "255791");
        Check((await other.Catalog.DescribeDeckAsync(Deck(records, 30, Small), "255791", Now)).Legality == CardLegality.Unknown,
            "The same fixed ID leaked its reviewed count across an unreviewed build.");
        pass("partial pools preserve known structural mismatches while expired construction and another build cannot revive or borrow count evidence");

        var conflicts = await reopened.DescribeDeckAsync(Deck(records, 30, Small, Large), Build, Now);
        Check(conflicts.Legality == CardLegality.Unknown && conflicts.MainDeckSize is { IsKnown: false, RequiredCards: null, IsSatisfied: null } conflict &&
            conflict.Sources.Select(source => source.RequiredCards).Order().SequenceEqual([20, 40]),
            "Conflicting real twenty/forty owners silently chose one value, summed to sixty, or used ordinary thirty.");
        var ordinary = Deck(records, 30);
        var ordinaryDescription = await reopened.DescribeDeckAsync(ordinary, Build, Now);
        var side = ordinary with { Sideboards = [new(ordinary.Cards[0].DbfId, [new(Id(records, Small), 1)])] };
        var sideOnly = await reopened.DescribeDeckAsync(side, Build, Now);
        Check(ordinaryDescription.Legality == CardLegality.Legal && sideOnly.Legality != CardLegality.Legal &&
            JsonSerializer.Serialize(sideOnly.MainDeckSize) == JsonSerializer.Serialize(ordinaryDescription.MainDeckSize) &&
            sideOnly.Cards.Sum(card => card.Count) == 30,
            "A size modifier appearing only in an unresolved sideboard changed main-deck count or proved full legality.");
        pass("conflicting active size sources stay unknown and a sideboard-only owner does not activate its main-deck exception");

        var superseded = await Create(directory, "superseded", records, complete with
            { RuleId = "main-size-old-active", EffectiveFrom = Now.AddDays(-4), Construction = complete.Construction! with { RuleId = "main-size-old-active-evidence" } });
        var newer = Complete("main-size-new-expired", reviewed.Construction!, records) with
        {
            EffectiveFrom = Now.AddDays(-2), ValidUntil = Now.AddDays(-1), Construction = reviewed.Construction! with
                { RuleId = "main-size-new-expired-evidence", EffectiveFrom = Now.AddDays(-2), ValidUntil = Now.AddDays(-1) }
        };
        await Import(superseded.Catalog, newer);
        Check(await superseded.Catalog.GetPoolRulesAsync(Build, at: Now) is null &&
            (await superseded.Catalog.DescribeDeckAsync(small, Build, Now)).Legality == CardLegality.Unknown,
            "A newer expired parent revived the older superseded evidence.");
        var retained = JsonSerializer.Serialize((await reopened.GetPoolRulesAsync(Build, at: Now))!.Construction);
        await Reject(() => Import(reopened, complete with { RuleId = "main-size-conflicting-reimport", Construction = complete.Construction! with
            { MainDeckSizes = [new(Small, 40, true)] } }));
        Check(JsonSerializer.Serialize((await reopened.GetPoolRulesAsync(Build, at: Now))!.Construction) == retained,
            "A conflicting reimport rewrote the immutable construction identity.");
        try { ((IList<DeckMainDeckSizeSource>)twenty.MainDeckSize!.Sources).Clear(); throw new InvalidOperationException("Mutable evaluated evidence"); }
        catch (NotSupportedException) { }
        pass("superseded expiry and conflicting evidence reimport retain immutable SQLite rule identity and readonly evaluated sources");

        var cli = typeof(MainDeckSizeIntegration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CliTestTargetPath").Value!;
        Check(Path.IsPathFullyQualified(cli) && File.Exists(cli), "No production CLI path supplied by the original build.");
        var cliCase = await Create(directory, "production-cli", records, null);
        var rulePath = Path.Combine(cliCase.Directory, "reviewed-structural-rules.json");
        await File.WriteAllTextAsync(rulePath, JsonSerializer.Serialize(complete));
        var imported = await Cli(cli, cliCase.Directory, "pool-import", "--json", rulePath, "--data-dir", cliCase.Directory);
        Check(imported.GetProperty("Construction").GetProperty("MainDeckSizes").GetArrayLength() == 2,
            "Actual CLI pool import did not preserve the typed size evidence.");
        foreach (var (deck, expected, owner) in new[] { (small, 20, Small), (large, 40, Large), (Deck(records, 30, Small), 20, Small) })
        {
            var codePath = Path.Combine(cliCase.Directory, "deck-" + deck.Cards.Sum(card => card.Count) + ".txt");
            var code = DeckCode.Encode(deck); await File.WriteAllTextAsync(codePath, code);
            var described = await Cli(cli, cliCase.Directory, "deck-describe", "--deck-file", codePath, "--build", Build, "--data-dir", cliCase.Directory);
            var actual = deck.Cards.Sum(card => card.Count); var size = described.GetProperty("MainDeckSize");
            Check(described.GetProperty("Legality").GetString() == (actual == expected ? "Legal" : "Illegal") &&
                size.GetProperty("ActualCards").GetInt64() == actual && size.GetProperty("RequiredCards").GetInt32() == expected &&
                size.GetProperty("Sources").EnumerateArray().Any(source => source.GetProperty("OwnerCardId").GetString() == owner) &&
                await File.ReadAllTextAsync(codePath) == code, "The production CLI changed the code or disagreed with shared structural legality.");
        }
        Check(!File.Exists(Path.Combine(cliCase.Directory, "model-config.json")), "A deck-only CLI flow created model configuration.");
        pass("production no-model CLI imports reviewed evidence and describes exact twenty forty and invalid thirty codes through the same persisted catalog and typed outcome");
    }

    private static CardPoolRules Reviewed(string xml)
    {
        // The fake page lists only the actual fixed XML entities; it is not a real Standard-pool claim.
        var definitions = System.Xml.Linq.XDocument.Parse(xml).Root!.Elements("Entity").ToArray();
        var collectible = definitions.Where(entity => entity.Elements("Tag").Any(tag => tag.Attribute("name")?.Value == "COLLECTIBLE" && tag.Attribute("value")?.Value == "1")).ToArray();
        var xmlSets = collectible.GroupBy(entity => entity.Elements("Tag").Single(tag => tag.Attribute("name")?.Value == "CARD_SET").Attribute("value")!.Value)
            .Select(group => new { slug = "fixed-" + group.Key, id = int.Parse(group.Key), collectibleCount = group.Count() }).ToArray();
        var actualSets = xmlSets.Cast<object>().Append(new { slug = "standard", id = "standard", cardSets = xmlSets.Select(set => set.slug).ToArray() }).ToArray();
        var html = "<cards config=\"" + System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(new { sets = actualSets, setGroups = Array.Empty<object>() })) + "\"></cards>";
        using var input = Utf8(xml); return OfficialStandardPool.CreatePartial(html, input, Build, Now.AddDays(-3));
    }
    private static CardPoolRules Complete(string name, DeckConstructionEvidence construction, JsonElement[] cards) => new(name, Build, 2,
        Now.AddDays(-3), Now.AddDays(3), "fixture://fictional-complete-membership-not-live-standard",
        [], cards.Where(card => card.TryGetProperty("collectible", out var value) && value.GetBoolean()).Select(card => card.GetProperty("id").GetString()!).ToArray(), [], [], true,
        "FICTIONAL complete membership isolates deck structure; all imported cards and construction primary definitions are unchanged.")
    { Construction = construction with { RuleId = name + "-construction", EffectiveFrom = Now.AddDays(-3), ValidUntil = Now.AddDays(3) } };
    private static DeckDefinition Deck(JsonElement[] records, int total, params string[] owners)
    {
        var entries = owners.Select(owner => new DeckCard(Id(records, owner), 1)).ToList();
        var remaining = total - entries.Count;
        foreach (var filler in records.Where(card => card.GetProperty("cardClass").GetString() == "NEUTRAL" &&
            card.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean() &&
            card.GetProperty("rarity").GetString() != "LEGENDARY"))
        { if (remaining <= 0) break; var count = Math.Min(2, remaining); entries.Add(new(filler.GetProperty("dbfId").GetInt32(), count)); remaining -= count; }
        Check(remaining == 0, "Real fixed neutral fillers cannot satisfy this structural case.");
        return new(2, [Id(records, "HERO_09")], entries, []);
    }
    private sealed record Case(string Directory, CardCatalog Catalog);
    private static async Task<Case> Create(string root, string name, JsonElement[] records, CardPoolRules? rules, string build = Build)
    {
        var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        var catalog = new CardCatalog(Path.Combine(directory, "cards.db")); await catalog.InitializeAsync();
        using (var input = Utf8(JsonSerializer.Serialize(records))) await catalog.ImportAsync(input, build, source: "fixture://fixed-255790-records-structural-boundary-only");
        if (rules is not null) await Import(catalog, rules);
        return new(directory, catalog);
    }
    private static void AssertSize(DeckDescription description, int expected, string owner, CardLegality legality) => Check(
        description.Legality == legality && description.MainDeckSize is { IsKnown: true, IsSatisfied: true } size &&
        size.ActualCards == expected && size.RequiredCards == expected && size.Sources.Count == 1 &&
        size.Sources[0].OwnerCardId == owner && !string.IsNullOrWhiteSpace(size.Sources[0].EvidenceId) &&
        !string.IsNullOrWhiteSpace(size.Sources[0].EvidenceSource), "Fixed owner lost its reviewed count/provenance: " + JsonSerializer.Serialize(description));
    private static async Task<JsonElement> Cli(string cli, string directory, params string[] arguments)
    {
        var processPath = Environment.ProcessPath;
        var host = processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath : Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..")), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        start.ArgumentList.Add(cli); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("HSHELPER_DATA_DIR");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Production deck CLI failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var output = await stdout; var error = await stderr;
        var log = Path.Combine(directory, "cli-" + arguments[0] + "-" + process.Id);
        await File.WriteAllTextAsync(log + ".stdout.json", output); await File.WriteAllTextAsync(log + ".stderr.log", error);
        Check(process.ExitCode == 0 && error.Length == 0, "Actual production deck CLI failed: " + error + output);
        using var result = JsonDocument.Parse(output); return result.RootElement.Clone();
    }
    private static string Workspace()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "HsHelper.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Integration workspace not found.");
    }
    private static int Id(JsonElement[] records, string id) => records.Single(card => card.GetProperty("id").GetString() == id).GetProperty("dbfId").GetInt32();
    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));
    private static async Task Import(CardCatalog catalog, CardPoolRules rule) { using var input = Utf8(JsonSerializer.Serialize(rule)); await catalog.ImportPoolRulesAsync(input); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Conflicting evidence was accepted."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
