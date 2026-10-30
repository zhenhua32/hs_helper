using System.Net;
using System.Text;
using System.Text.Json;
using HsHelper.Cards;
using Microsoft.Data.Sqlite;

internal static class ConstructionChecks
{
    private const string Build = "255790";
    private const int King = 125998;
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Gods = ["BE_009", "BE_013", "BE_017", "BE_784"];
    private static readonly int[] GodNumbers = [132547, 132339, 128389, 131851];
    private static readonly string[] Contraband = ["EDR_489", "DINO_421", "CORE_TTN_866"];
    private static readonly int[] ContrabandNumbers = [115203, 118617, 129169];

    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> run)
    {
        await run("construction evidence survives reopening and preserves legacy rule identity", async () =>
        {
            var catalog = await Catalog(root, "evidence");
            var legacy = Rules("legacy") with { Construction = null };
            var originalJson = JsonSerializer.Serialize(legacy);
            Check(!originalJson.Contains("Construction", StringComparison.Ordinal), "Null evidence changed the legacy canonical JSON/hash.");
            await Import(catalog, legacy);
            await Import(catalog, JsonSerializer.Deserialize<CardPoolRules>(originalJson)!);
            var rules = Rules("reviewed");
            await Import(catalog, rules);
            var reopened = new CardCatalog(Path.Combine(root, "construction-evidence.db"));
            var saved = (await reopened.GetPoolRulesAsync(Build, at: Now))!.Construction!;
            Check(saved.Build == Build && saved.Format == 2 && saved.Source == rules.Construction!.Source &&
                saved.CardGroups.Single().CardIds.SequenceEqual(Gods.Order(StringComparer.Ordinal)), "Construction scope or source was lost.");
            try { ((IList<string>)saved.CardGroups.Single().CardIds).Clear(); throw new Exception("Mutable evidence member list."); }
            catch (NotSupportedException) { }
            await Reject(() => Import(catalog, rules with { RuleId = "different-parent", Construction = rules.Construction! with
            { CardGroups = [new("old-gods", Gods, 2)] } }));
            Check((await reopened.GetPoolRulesAsync(Build, at: Now))!.RuleId == "reviewed", "Conflicting evidence changed the active record.");
        });
        await run("construction evidence rejects null duplicate cross-build and conflicting scope", async () =>
        {
            var catalog = await Catalog(root, "invalid");
            var baseline = Rules("base"); await Import(catalog, baseline);
            var c = baseline.Construction!;
            var malformed = new[]
            {
                c with { RuleId = "" }, c with { Source = "" }, c with { Build = "255791" }, c with { Format = 1 },
                c with { EffectiveFrom = baseline.EffectiveFrom.AddTicks(-1) }, c with { ValidUntil = baseline.ValidUntil.AddTicks(1) },
                c with { ValidUntil = c.EffectiveFrom }, c with { CardGroups = null! }, c with { Sideboards = null! },
                c with { CardGroups = [null!] }, c with { Sideboards = [null!] },
                c with { CardGroups = [new("dupe", ["BE_009", "BE_009"], 1)] },
                c with { CardGroups = [new("dupe", Gods, 1), new("dupe", Gods, 2)] },
                c with { CardGroups = [new("zero", Gods, 0)] },
                c with { Sideboards = [SideRule(), SideRule()] },
                c with { Sideboards = [SideRule() with { MaximumCards = 0 }] },
                c with { Sideboards = [SideRule() with { MinimumCards = 4 }] },
                c with { Sideboards = [SideRule() with { MaximumCopiesPerCard = 4 }] },
                c with { Sideboards = [SideRule() with { AllowedCardIds = ["EDR_489", "EDR_489"] }] },
                c with { Sideboards = [SideRule() with { AllowedCardIds = [], AllowedCardsComplete = true }] }
            };
            for (var i = 0; i < malformed.Length; i++)
                await Reject(() => Import(catalog, baseline with { RuleId = "bad-" + i, Construction = malformed[i] }));
            Check((await catalog.GetPoolRulesAsync(Build, at: Now))!.RuleId == "base", "Rejected evidence replaced good rules.");
        });
        await run("reviewed old god group is one total copy and never guessed from a card name", async () =>
        {
            var catalog = await Catalog(root, "gods"); await Import(catalog, Rules("gods"));
            Check((await catalog.DescribeDeckAsync(Deck(new DeckCard(GodNumbers[0], 1)), Build, Now)).Legality == CardLegality.Legal, "One reviewed Old God was rejected.");
            var two = await catalog.DescribeDeckAsync(Deck(new DeckCard(GodNumbers[0], 1), new(GodNumbers[1], 1)), Build, Now);
            Check(two.Legality == CardLegality.Illegal && two.Issues.Any(i => i.Contains("old-gods", StringComparison.Ordinal)), "Two different Old Gods bypassed the group limit.");
            var named = await catalog.DescribeDeckAsync(Deck(new DeckCard(4000, 2)), Build, Now);
            Check(named.Legality == CardLegality.Legal, "A similar name or prefix was inferred to be an Old God.");
            var later = await catalog.DescribeDeckAsync(Deck(new DeckCard(GodNumbers[0], 1)), Build, Now.AddHours(2));
            Check(later.Legality == CardLegality.Unknown, "An expired construction constraint still proved legality.");
        });
        await run("reviewed sideboard construction preserves ordinary copies while unknown copy exceptions stay unknown", async () =>
        {
            var catalog = await Catalog(root, "copies"); await Import(catalog, Rules("copies"));
            var deck = Deck(new DeckCard(King, 1), new(1000, 3));
            var described = await catalog.DescribeDeckAsync(deck, Build, Now);
            Check(described.Legality == CardLegality.Illegal && described.Issues.Any(i => i.Contains("同名牌数量", StringComparison.Ordinal)),
                "A reviewed sideboard-only exception skipped another card's known copy limit.");
            var unknown = await catalog.DescribeDeckAsync(Deck(new DeckCard(4001, 1), new(1000, 3)), Build, Now);
            Check(unknown.Legality == CardLegality.Unknown && unknown.Issues.Any(i => i.Contains("同名牌上限需专用规则", StringComparison.Ordinal)),
                "An unreviewed construction exception was guessed to preserve ordinary copy limits.");
        });
        await run("card schema two migrates without losing legacy manifests cards or pool hashes and future schema is rejected", async () =>
        {
            var catalog = await Catalog(root, "migration");
            var legacy = Rules("migration-legacy") with { Construction = null };
            await Import(catalog, legacy);
            var manifest = await catalog.GetActiveManifestAsync();
            var database = Path.Combine(root, "construction-migration.db");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            await connection.OpenAsync();
            async Task<object?> Scalar(string sql)
            { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
            var previousHash = await Scalar("SELECT sha256 FROM card_pool_rules WHERE rule_id='migration-legacy'");
            var previousJson = await Scalar("SELECT json FROM card_pool_rules WHERE rule_id='migration-legacy'");
            Check(!((string)previousJson!).Contains("Construction", StringComparison.Ordinal), "Legacy fixture contains post-v2 semantics.");
            // v2 and v3 have the same tables; v3 marks that construction evidence must be understood.
            await Scalar("PRAGMA user_version=2");
            var reopened = new CardCatalog(database);
            await Reject(() => Import(reopened, Rules("migration-rejected-before-initialize")));
            Check(Convert.ToInt64(await Scalar("PRAGMA user_version")) == 2, "Import silently wrote new semantics into a v2 database.");
            await reopened.InitializeAsync();
            Check(Convert.ToInt64(await Scalar("PRAGMA user_version")) == 3, "Old card schema was not upgraded.");
            Check(await reopened.GetActiveManifestAsync() == manifest &&
                (await reopened.GetByIdsAsync(["JAIL_831"], Build)).Single().DbfId == King,
                "Migration changed the active manifest or lost legacy card data.");
            Check(Equals(await Scalar("SELECT sha256 FROM card_pool_rules WHERE rule_id='migration-legacy'"), previousHash) &&
                Equals(await Scalar("SELECT json FROM card_pool_rules WHERE rule_id='migration-legacy'"), previousJson),
                "Migration rewrote a legacy pool's immutable hash or JSON.");
            await Import(reopened, legacy);
            await Import(reopened, Rules("migration-reviewed"));
            Check((await reopened.GetPoolRulesAsync(Build, at: Now))!.Construction is not null, "Migrated schema could not persist construction evidence.");
            var countBeforeFuture = await Scalar("SELECT COUNT(*) FROM cards");
            await Scalar("PRAGMA user_version=4");
            await Reject(() => new CardCatalog(database).InitializeAsync());
            Check(Convert.ToInt64(await Scalar("PRAGMA user_version")) == 4 && Equals(await Scalar("SELECT COUNT(*) FROM cards"), countBeforeFuture),
                "An unsupported future schema was changed or erased while rejecting it.");
        });
        await run("explicit sideboard membership capacity copies and class waiver are enforced", async () =>
        {
            var catalog = await Catalog(root, "sideboards"); await Import(catalog, Rules("sideboards"));
            var main = Deck(new DeckCard(King, 1));
            var valid = main with { Sideboards = [new(King, ContrabandNumbers.Select(id => new DeckCard(id, 1)).ToArray())] };
            Check((await catalog.DescribeDeckAsync(valid, Build, Now)).Legality == CardLegality.Legal, "Explicit foreign-class membership was not accepted under its reviewed waiver.");
            var over = valid with { Sideboards = [new(King, [new(115203, 2), new(118617, 1), new(129169, 1)])] };
            Check((await catalog.DescribeDeckAsync(over, Build, Now)).Legality == CardLegality.Illegal, "Sideboard capacity/copies were not checked.");
            var foreign = valid with { Sideboards = [new(King, [new(1000, 1)])] };
            Check((await catalog.DescribeDeckAsync(foreign, Build, Now)).Legality == CardLegality.Illegal, "A nonmember was accepted under a complete explicit member list.");
            var split = valid with { Sideboards = [new(King, [new(115203, 1)]), new(King, [new(118617, 1)])] };
            Check((await catalog.DescribeDeckAsync(split, Build, Now)).Legality == CardLegality.Illegal, "Duplicate sideboard owners bypassed aggregate capacity.");
            var required = Rules("required") with { Construction = Rules("required").Construction! with { Sideboards = [SideRule() with { MinimumCards = 3 }] } };
            await Import(catalog, required);
            Check((await catalog.DescribeDeckAsync(main, Build, Now)).Legality == CardLegality.Illegal, "A required sideboard was silently omitted.");
        });
        await run("partial member evidence never upgrades incomplete pools or guesses missing members", async () =>
        {
            var catalog = await Catalog(root, "partial");
            var partial = Rules("partial") with { IsComplete = false };
            await Import(catalog, partial);
            var deck = Deck(new DeckCard(King, 1)) with { Sideboards = [new(King, ContrabandNumbers.Select(id => new DeckCard(id, 1)).ToArray())] };
            Check((await catalog.DescribeDeckAsync(deck, Build, Now)).Legality == CardLegality.Unknown, "Construction evidence upgraded an incomplete card pool.");
            var incompleteMembers = Rules("partial-members") with { Construction = Rules("partial-members").Construction! with
            { Sideboards = [SideRule() with { AllowedCardIds = ["EDR_489"], AllowedCardsComplete = false }] } };
            await Import(catalog, incompleteMembers);
            var result = await catalog.DescribeDeckAsync(deck, Build, Now);
            Check(result.Legality == CardLegality.Unknown && result.Issues.Any(i => i.Contains("允许边栏子集尚未确认", StringComparison.Ordinal)),
                "Unlisted members of a partial subset were assumed legal or definitely excluded.");
            var noWaiver = Rules("no-waiver") with { Construction = Rules("no-waiver").Construction! with { Sideboards = [SideRule() with { IgnoresHeroClass = false }] } };
            await Import(catalog, noWaiver);
            Check((await catalog.DescribeDeckAsync(deck, Build, Now)).Legality == CardLegality.Illegal, "An absent class waiver was silently granted.");
        });
        await run("fixed-build definitions attach only audited old-god and sideboard facts", () =>
        {
            var config = JsonSerializer.Serialize(new { sets = new object[] { new { slug = "synthetic", id = 999, collectibleCount = 5 },
                new { slug = "standard", id = "standard", cardSets = new[] { "synthetic" } } }, setGroups = Array.Empty<object>() });
            var html = "<cards config=\"" + WebUtility.HtmlEncode(config) + "\"></cards>";
            var entities = string.Join("", Gods.Select(id => $"<Entity CardID=\"{id}\"><Tag name=\"COLLECTIBLE\" value=\"1\"/><Tag name=\"CARD_SET\" value=\"999\"/><Tag name=\"OLD_GOD\" value=\"1\"/></Entity>"));
            entities += "<Entity CardID=\"JAIL_831\"><Tag name=\"COLLECTIBLE\" value=\"1\"/><Tag name=\"CARD_SET\" value=\"999\"/><Tag name=\"MAX_SIDEBOARD_CARDS\" value=\"3\"/><Tag name=\"SIDEBOARD_TYPE\" value=\"4\"/><Tag name=\"ALLOWED_SIDEBOARD_SUBSET\" value=\"19499\"/><Tag name=\"SIDEBOARD_IGNORES_CLASS_RULE\" value=\"1\"/></Entity>";
            var xml = $"<CardDefs build=\"{Build}\">{entities}</CardDefs>";
            var result = OfficialStandardPool.CreatePartial(html, Stream(xml), Build, Now);
            Check(!result.IsComplete && result.Construction?.CardGroups.Single().MaximumCopies == 1, "Audited group was missing or upgraded pool completeness.");
            var side = result.Construction!.Sideboards.Single();
            Check(side.MaximumCards == 3 && side.IgnoresHeroClass && side.AllowedCardIds.Count == 0 && !side.AllowedCardsComplete,
                "A subset identifier was converted into an invented complete member list.");
            var other = OfficialStandardPool.CreatePartial(html, Stream(xml.Replace(Build, "255791", StringComparison.Ordinal)), "255791", Now);
            Check(other.Construction is null, "Audited constraints leaked to an unreviewed build.");
            return Task.CompletedTask;
        });
    }

    private static async Task<CardCatalog> Catalog(string root, string name)
    {
        var catalog = new CardCatalog(Path.Combine(root, "construction-" + name + ".db"));
        await catalog.InitializeAsync();
        var records = Enumerable.Range(0, 30).Select(i => new Dictionary<string, object> { ["id"] = "FILLER_" + i, ["dbfId"] = 1000 + i,
            ["name"] = "合成填充" + i, ["type"] = "MINION", ["cardClass"] = "HUNTER", ["rarity"] = "COMMON", ["set"] = "TEST", ["collectible"] = true, ["cost"] = i % 7 }).ToList();
        records.Add(new() { ["id"] = "HERO", ["dbfId"] = 31, ["name"] = "合成猎人", ["type"] = "HERO", ["cardClass"] = "HUNTER" });
        void Card(string id, int dbf, string cardClass, string rarity, string text) => records.Add(new() { ["id"] = id, ["dbfId"] = dbf,
            ["name"] = "合成" + id, ["type"] = "MINION", ["cardClass"] = cardClass, ["rarity"] = rarity, ["set"] = "TEST", ["collectible"] = true, ["text"] = text, ["cost"] = 5 });
        Card("JAIL_831", King, "HUNTER", "LEGENDARY", "构筑套牌时，选择三张私藏的野兽牌。");
        for (var i = 0; i < Gods.Length; i++) Card(Gods[i], GodNumbers[i], "NEUTRAL", "LEGENDARY", i == 0 ? "" : "<b>古神</b>");
        for (var i = 0; i < Contraband.Length; i++) Card(Contraband[i], ContrabandNumbers[i], "WARLOCK", "LEGENDARY", "");
        Card("BE_LOOKALIKE", 4000, "HUNTER", "COMMON", "名字像古神但不属于已审核组。");
        Card("UNKNOWN_SIZE", 4001, "HUNTER", "LEGENDARY", "你的套牌上限改为40。");
        await catalog.ImportAsync(Stream(JsonSerializer.Serialize(records)), Build);
        return catalog;
    }
    private static CardPoolRules Rules(string name) => new(name, Build, 2, Now.AddDays(-1), Now.AddDays(1), "synthetic://reviewed-pool", ["TEST"], [], [], [], true)
    {
        Construction = new("construction-" + name, Build, 2, Now.AddHours(-1), Now.AddHours(1), "synthetic://reviewed-construction",
            [new("old-gods", Gods, 1)], [SideRule()])
    };
    private static DeckSideboardRule SideRule() => new("JAIL_831", 3, Contraband, true, 0, 1, true, true);
    private static DeckDefinition Deck(params DeckCard[] special)
    {
        var cards = special.ToList();
        var remaining = 30 - special.Sum(card => card.Count);
        for (var i = 1; remaining > 0; i++) { var count = Math.Min(2, remaining); cards.Add(new(1000 + i, count)); remaining -= count; }
        return new(2, [31], cards, []);
    }
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
    private static Task<CardPoolRules> Import(CardCatalog catalog, CardPoolRules rules) => catalog.ImportPoolRulesAsync(Stream(JsonSerializer.Serialize(rules)));
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid or conflicting evidence was accepted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
