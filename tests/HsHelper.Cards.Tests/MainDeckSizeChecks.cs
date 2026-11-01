using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HsHelper.Cards;
using Microsoft.Data.Sqlite;

internal static class MainDeckSizeChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
    private static readonly Lazy<JsonObject> Fixture = new(() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "deck-size-255790.json")))!.AsObject());
    private static readonly Lazy<JsonObject[]> Records = new(() => Fixture.Value["cards"]!.AsArray().Select(card => card!.AsObject()).ToArray());
    private static int _serial;
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> run)
    {
        await run("fixed primary CardDefs creates only reviewed twenty and forty card rules without completing the pool", () => Factory(root));
        await run("reviewed main-deck sizes accept twenty and forty and reject thirty before any gameplay", () => Counts(root));
        await run("known construction size remains distinct from partial bans class rules and ordinary copies", () => OtherRules(root));
        await run("size evidence remains bound to original build format and effective interval", () => Scope(root));
        await run("conflicting size modifiers and unreviewed replacement rules never choose a convenient size", () => Conflicts(root));
        await run("sideboards neither activate main-deck rules nor exempt a new main-deck size restriction", () => Sideboards(root));
        await run("complete fixed card identity rejects coherent metadata and record mismatches", () => Metadata(root));
        await run("missing main-deck definitions and evidence retain explicit unknown instead of false ordinary legality", () => Missing(root));
        await run("size-only evidence is immutable normalized and rejects null duplicate or conflicting nested rules", () => Evidence(root));
        await run("schema three migration preserves old manifests rules JSON and hash while admitting new size evidence", () => Migration(root));
        await run("size outcomes freeze evidence collections and do not mutate caller decks", () => Immutable(root));
    }

    private static async Task Factory(string root)
    {
        Check(Fixture.Value["sourceSha256"]!.GetValue<string>() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            Fixture.Value["cardDefsSha256"]!.GetValue<string>() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8", "Fixed source provenance changed.");
        var xml = XDocument.Parse(Fixture.Value["cardDefsXml"]!.GetValue<string>());
        var partial = Partial(xml);
        Check(!partial.IsComplete && partial.Construction!.MainDeckSizes!.Select(rule => (rule.OwnerCardId, rule.RequiredCards, rule.KeepsOrdinaryCopyLimits))
            .OrderBy(rule => rule.OwnerCardId).SequenceEqual(new[] { ("JAIL_430", 20, true), ("REV_018", 40, true) }), "Full fixed definitions did not establish precisely the two reviewed rules.");
        var catalog = await Catalog(root);
        await Import(catalog, partial with { AllowedCardIds = Records.Value.Select(card => card["id"]!.GetValue<string>()).ToArray() });
        var result = await catalog.DescribeDeckAsync(Deck(20, "JAIL_430"), "255790", Now);
        Check(result.Legality == CardLegality.Unknown && result.MainDeckSize is { IsKnown: true, RequiredCards: 20, IsSatisfied: true }, "Partial pool erased known construction size or became complete legality.");
        foreach (var change in new Action<XElement>[]
        {
            entity => entity.SetAttributeValue("ID", "126056"),
            entity => entity.Elements("Tag").Single(tag => tag.Attribute("name")?.Value == "DECK_RULE_MOD_DECK_SIZE").SetAttributeValue("value", "30"),
            entity => entity.Elements("Tag").Single(tag => tag.Attribute("name")?.Value == "CARDTEXT").Element("zhCN")!.Value = "未知构筑条件",
            entity => entity.Elements("Tag").Single(tag => tag.Attribute("name")?.Value == "CLASS").SetAttributeValue("value", "3"),
            entity => entity.Add(new XElement("Tag", new XAttribute("name", "EXTRA_RULE"), new XAttribute("enumID", "99999"), new XAttribute("value", "1")))
        })
        {
            var changed = XDocument.Parse(xml.ToString()); var jail = changed.Root!.Elements().Single(entity => entity.Attribute("CardID")?.Value == "JAIL_430"); change(jail);
            Check(Partial(changed).Construction!.MainDeckSizes!.Single().OwnerCardId == "REV_018", "An altered complete CardDefs entity yielded a reviewed twenty-card rule.");
        }
        // Attribute, tag and locale order have no gameplay meaning.
        foreach (var entity in xml.Root!.Elements())
        {
            var tags = entity.Elements().Reverse().Select(tag => new XElement(tag.Name, tag.Attributes().Reverse(), tag.HasElements ? tag.Elements().Reverse() : tag.Value)).ToArray();
            entity.ReplaceNodes(tags);
        }
        Check(Partial(xml).Construction!.MainDeckSizes!.Count == 2, "Canonical definition verification depended on XML ordering.");
        var wrongBuild = XDocument.Parse(xml.ToString()); wrongBuild.Root!.SetAttributeValue("build", "255791");
        Check(Partial(wrongBuild, "255791").Construction is null, "Another build borrowed fixed construction rules.");
    }

    private static async Task Counts(string root)
    {
        var catalog = await Catalog(root); await Import(catalog, Rules());
        foreach (var (owner, required) in new[] { ("JAIL_430", 20), ("REV_018", 40) })
        foreach (var count in new[] { required - 1, required, required + 1, 30 })
        {
            var deck = Deck(count, owner); var code = DeckCode.Encode(deck); var decoded = DeckCode.Decode(code);
            var result = await catalog.DescribeDeckAsync(decoded, "255790", Now);
            Check(result.MainDeckSize is { IsKnown: true } size && size.ActualCards == count && size.RequiredCards == required && size.IsSatisfied == (count == required) &&
                result.Legality == (count == required ? CardLegality.Legal : CardLegality.Illegal) && size.Sources.Single().OwnerCardId == owner &&
                size.Sources[0].EvidenceId == "sizes" && size.Sources[0].EvidenceSource == "synthetic://reviewed-fixed-construction-not-live-pool",
                "Main-deck construction was confused with start-of-game forty cards: " + JsonSerializer.Serialize(result));
        }
    }

    private static async Task OtherRules(string root)
    {
        var catalog = await Catalog(root); await Import(catalog, Rules() with { IsComplete = false });
        Check((await catalog.DescribeDeckAsync(Deck(20, "JAIL_430"), "255790", Now)).MainDeckSize is { IsKnown: true, IsSatisfied: true }, "Partial legality erased an audited requirement.");
        await Import(catalog, Rules("banned") with { EffectiveFrom = Now.AddMinutes(-2), BannedCardIds = ["JAIL_430"],
            Construction = Rules().Construction! with { RuleId = "banned-sizes", EffectiveFrom = Now.AddMinutes(-2) } });
        var banned = await catalog.DescribeDeckAsync(Deck(20, "JAIL_430"), "255790", Now);
        Check(banned.Legality == CardLegality.Illegal && banned.MainDeckSize is { IsSatisfied: true }, "Correct size overrode a pool ban.");
        var ordinary = await Catalog(root); await Import(ordinary, Rules());
        var wrongClass = Deck(20, "JAIL_430") with { Heroes = [31] };
        Check((await ordinary.DescribeDeckAsync(wrongClass, "255790", Now)).Legality == CardLegality.Illegal, "Size rule waived Priest class identity.");
        var copies = Deck(20, "JAIL_430"); var cards = copies.Cards.ToArray(); cards[1] = cards[1] with { Count = 3 }; cards[2] = cards[2] with { Count = cards[2].Count - 1 };
        var tooMany = await ordinary.DescribeDeckAsync(copies with { Cards = cards }, "255790", Now);
        Check(tooMany.Legality == CardLegality.Illegal && tooMany.MainDeckSize is { IsKnown: true, IsSatisfied: true } && tooMany.Issues.Any(issue => issue.Contains("同名牌数量")), "Reviewed size skipped ordinary copy limits.");
        var twoOwners = Deck(20, "JAIL_430"); var duplicates = twoOwners.Cards.ToArray(); duplicates[0] = duplicates[0] with { Count = 2 }; duplicates[1] = duplicates[1] with { Count = duplicates[1].Count - 1 };
        Check((await ordinary.DescribeDeckAsync(twoOwners with { Cards = duplicates }, "255790", Now)).Legality == CardLegality.Illegal, "Reviewed size waived legendary copy limits.");
    }

    private static async Task Scope(string root)
    {
        var catalog = await Catalog(root); await Import(catalog, Rules());
        foreach (var at in new[] { Now.AddDays(-2), Now.AddHours(2) })
            Unknown(await catalog.DescribeDeckAsync(Deck(30, "JAIL_430"), "255790", at));
        await ImportCards(catalog, Records.Value, "255791");
        await Import(catalog, Rules("other-build") with { Build = "255791", Construction = Rules().Construction! with { RuleId = "other-build-sizes", Build = "255791",
            MainDeckSizes = [new("JAIL_430", 21, true)] } });
        Unknown(await catalog.DescribeDeckAsync(Deck(30, "JAIL_430"), "255791", Now));
        Unknown(await catalog.DescribeDeckAsync(Deck(30, "JAIL_430") with { Format = 1 }, "255790", Now));
        var expired = Rules("expired") with { EffectiveFrom = Now.AddMinutes(-30), ValidUntil = Now.AddMinutes(-10),
            Construction = Rules().Construction! with { RuleId = "expired-size", EffectiveFrom = Now.AddMinutes(-30), ValidUntil = Now.AddMinutes(-10) } };
        await Import(catalog, expired); Unknown(await catalog.DescribeDeckAsync(Deck(30, "JAIL_430"), "255790", Now));
    }

    private static async Task Conflicts(string root)
    {
        var catalog = await Catalog(root); await Import(catalog, Rules());
        foreach (var count in new[] { 20, 30, 40 })
        {
            var result = await catalog.DescribeDeckAsync(Deck(count, "JAIL_430", "REV_018"), "255790", Now); Unknown(result);
            Check(result.MainDeckSize!.Sources.Count == 2 && result.MainDeckSize.Unknowns.Any(text => text.Contains("优先级")), "Conflicting known sources were discarded or guessed.");
        }
        foreach (var unknown in new[] { "BOT_914", "DAL_800", "TOY_700", "TIME_005" })
        {
            // Warlock ownership is deliberately retained: unknown size cannot cancel other illegal facts.
            var result = await catalog.DescribeDeckAsync(Deck(40, "REV_018", unknown), "255790", Now);
            Check(result.Legality != CardLegality.Legal && result.MainDeckSize is { IsKnown: false, RequiredCards: null, IsSatisfied: null } && result.MainDeckSize.Sources.Count == 1,
                "An unreviewed replacement/composition rule was treated as only a size modifier.");
        }
    }

    private static async Task Sideboards(string root)
    {
        var catalog = await Catalog(root); await Import(catalog, Rules());
        var side = Deck(30) with { Sideboards = [new(126055, [new(79767, 1)])] };
        Check((await catalog.DescribeDeckAsync(side, "255790", Now)).MainDeckSize is null, "A sideboard owner or card activated a main-deck requirement.");
        var altered = Records.Value.Select(card => card["id"]!.GetValue<string>() == "JAIL_831" ? Change(card, raw => raw["text"] = raw["text"]!.GetValue<string>() + "你的套牌容量为40。") : card).ToArray();
        var changed = await Catalog(root, altered);
        var rules = Rules() with { Construction = Rules().Construction! with { Sideboards = [new("JAIL_831", 3, [], false, 0, 1, true, true)] } };
        await Import(changed, rules);
        var main = Deck(30, "JAIL_831") with { Heroes = [31] };
        Unknown(await changed.DescribeDeckAsync(main, "255790", Now));
        var rawRule = Records.Value.Select(card => card["id"]!.GetValue<string>() == "JAIL_831" ?
            Change(card, raw => raw["deckBuilding"] = new JsonObject { ["requiredMainDeckSize"] = 40 }) : card).ToArray();
        var extra = await Catalog(root, rawRule); await Import(extra, rules);
        Unknown(await extra.DescribeDeckAsync(main, "255790", Now));
    }

    private static async Task Metadata(string root)
    {
        foreach (var owner in new[] { "JAIL_430", "REV_018" })
        foreach (var mutate in new Action<JsonObject>[]
        {
            card => card["dbfId"] = card["dbfId"]!.GetValue<int>() + 1,
            card => card["id"] = "ALTERED_ID",
            card => { card["id"] = "ALTERED_ID"; card["text"] = ""; },
            card => card["text"] = "没有任何已审核的构筑规则。",
            card => card["type"] = "SPELL",
            card => card["cost"] = 0,
            card => card["mechanics"] = new JsonArray("TRIGGER_VISUAL"),
            card => card["newRule"] = 1
        })
        {
            var original = Records.Value.Single(card => card["id"]!.GetValue<string>() == owner);
            var bad = Change(original, mutate);
            var all = Records.Value.Select(card => card == original ? bad : card).ToArray();
            var catalog = await Catalog(root, all); await Import(catalog, Rules() with { AllowedCardIds = all.Select(card => card["id"]!.GetValue<string>()).ToArray() });
            var deck = Deck(30, owner);
            if (bad["dbfId"]!.GetValue<int>() != original["dbfId"]!.GetValue<int>()) deck = deck with { Cards = deck.Cards.Select(card => card.DbfId == original["dbfId"]!.GetValue<int>() ? card with { DbfId = bad["dbfId"]!.GetValue<int>() } : card).ToArray() };
            Unknown(await catalog.DescribeDeckAsync(deck, "255790", Now));
        }
        // A new serialization order remains precisely the same complete definition.
        var ordered = Records.Value.Select(card => { var next = new JsonObject(); foreach (var property in card.Reverse()) next.Add(property.Key, property.Value?.DeepClone()); return next; }).ToArray();
        var good = await Catalog(root, ordered); await Import(good, Rules());
        Check((await good.DescribeDeckAsync(Deck(20, "JAIL_430"), "255790", Now)).Legality == CardLegality.Legal, "Definition trust depended on JSON property order.");
    }

    private static async Task Missing(string root)
    {
        var catalog = await Catalog(root); await Import(catalog, Rules() with { Construction = null });
        Unknown(await catalog.DescribeDeckAsync(Deck(30, "JAIL_430"), "255790", Now));
        var missing = await Catalog(root, Records.Value.Where(card => card["id"]!.GetValue<string>() != "JAIL_430").ToArray());
        await Import(missing, Rules()); Unknown(await missing.DescribeDeckAsync(Deck(30, "JAIL_430"), "255790", Now));
        var ordinaryMissing = Deck(30, "JAIL_430");
        ordinaryMissing = ordinaryMissing with { Cards = ordinaryMissing.Cards.Select((card, index) => index == 1 ? new DeckCard(999999, card.Count) : card).ToArray() };
        var withSource = await catalog.DescribeDeckAsync(ordinaryMissing, "255790", Now);
        Unknown(withSource); // The catalog above intentionally has no child size evidence.
        var withEvidence = await Catalog(root); await Import(withEvidence, Rules());
        var knownAndMissing = await withEvidence.DescribeDeckAsync(ordinaryMissing, "255790", Now);
        Unknown(knownAndMissing); Check(knownAndMissing.MainDeckSize!.Sources.Single().OwnerCardId == "JAIL_430",
            "Missing ordinary-looking identity either erased proven individual evidence or proved absence of another modifier.");
        // Existing valid sideboard evidence must not impersonate size evidence for its same owner.
        var unrelated = await Catalog(root); await Import(unrelated, Rules() with { Construction = Rules().Construction! with { MainDeckSizes = null,
            Sideboards = [new("JAIL_430", 3, [], false, 0, 1, true)] } });
        Unknown(await unrelated.DescribeDeckAsync(Deck(30, "JAIL_430"), "255790", Now));
    }

    private static async Task Evidence(string root)
    {
        var catalog = await Catalog(root);
        var normal = Rules();
        foreach (var sizes in new IReadOnlyList<DeckMainDeckSizeRule>[]
        {
            [null!], [new("", 20)], [new("JAIL_430", 20), new("JAIL_430", 20)], [new("JAIL_430", 40)], [new("REV_018", 0)], [new("UNKNOWN", 1001)]
        }) await Reject(() => Import(catalog, normal with { Construction = normal.Construction! with { MainDeckSizes = sizes } }));
        await Reject(() => Import(catalog, normal with { Construction = normal.Construction! with { Build = "255791" } }));
        await Reject(() => Import(catalog, normal with { Construction = normal.Construction! with { Format = 1 } }));
        await Reject(() => Import(catalog, normal with { Construction = normal.Construction! with { EffectiveFrom = normal.EffectiveFrom.AddTicks(-1) } }));
        await Reject(() => Import(catalog, normal with { Construction = normal.Construction! with { ValidUntil = normal.ValidUntil.AddTicks(1) } }));
        Check(await catalog.GetPoolRulesAsync("255790", at: Now) is null, "Rejected nested evidence partially committed.");
        await Import(catalog, normal);
        await Reject(() => Import(catalog, normal with { Construction = normal.Construction! with { MainDeckSizes = [new("JAIL_430", 20, false)] } }));
        await Reject(() => Import(catalog, normal with { RuleId = "new-parent", Construction = normal.Construction! with { MainDeckSizes = [new("JAIL_430", 20, false)] } }));
        Check((await catalog.GetPoolRulesAsync("255790", at: Now))!.Construction!.MainDeckSizes!.Count == 2, "Failed conflicting import replaced saved evidence.");
    }

    private static async Task Migration(string root)
    {
        var database = Path.Combine(root, "size-schema3-" + Guid.NewGuid().ToString("N") + ".db");
        var catalog = new CardCatalog(database); await catalog.InitializeAsync(); await ImportCards(catalog, Records.Value);
        var legacy = Rules("legacy-hash") with { Construction = Rules().Construction! with { MainDeckSizes = null,
            Sideboards = [new("JAIL_831", 3, [], false, 0, 1, true)], RuleId = "legacy-construction" } };
        await Import(catalog, legacy); var manifest = await catalog.GetActiveManifestAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); await connection.OpenAsync();
        async Task<object?> Sql(string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
        var oldJson = (string)(await Sql("SELECT json FROM card_pool_rules"))!; var oldHash = await Sql("SELECT sha256 FROM card_pool_rules");
        Check(!oldJson.Contains("MainDeckSizes", StringComparison.Ordinal), "Missing old field was silently serialized as a new empty list.");
        await Sql("PRAGMA user_version=3");
        await Reject(() => Import(catalog, Rules()));
        Check(Convert.ToInt64(await Sql("PRAGMA user_version")) == 3, "New semantics were written into old schema without migration.");
        var reopened = new CardCatalog(database); await reopened.InitializeAsync();
        Check(Convert.ToInt64(await Sql("PRAGMA user_version")) == 4 && Equals(oldJson, await Sql("SELECT json FROM card_pool_rules")) &&
            Equals(oldHash, await Sql("SELECT sha256 FROM card_pool_rules")) && await reopened.GetActiveManifestAsync() == manifest &&
            (await reopened.GetByIdsAsync(["JAIL_430"], "255790")).Single().DbfId == 126055, "Schema migration rewrote old data or source identity.");
        await Import(reopened, legacy); // Immutable historical identity must remain idempotent.
        await Import(reopened, Rules());
        Check((await reopened.DescribeDeckAsync(Deck(20, "JAIL_430"), "255790", Now)).Legality == CardLegality.Legal, "Migrated catalog could not use size-only evidence.");
        var count = await Sql("SELECT COUNT(*) FROM cards"); await Sql("PRAGMA user_version=5");
        await Reject(() => new CardCatalog(database).InitializeAsync());
        Check(Convert.ToInt64(await Sql("PRAGMA user_version")) == 5 && Equals(count, await Sql("SELECT COUNT(*) FROM cards")), "Future schema was changed while rejecting it.");
    }

    private static async Task Immutable(string root)
    {
        var catalog = await Catalog(root); var list = new List<DeckMainDeckSizeRule> { new("JAIL_430", 20, true) };
        var imported = await Import(catalog, Rules() with { Construction = Rules().Construction! with { MainDeckSizes = list } }); list.Clear();
        Check(imported.Construction!.MainDeckSizes!.Single().RequiredCards == 20, "Caller mutated normalized stored size rules.");
        var deck = Deck(20, "JAIL_430"); var before = JsonSerializer.Serialize(deck);
        var outcome = (await catalog.DescribeDeckAsync(deck, "255790", Now)).MainDeckSize!;
        Check(JsonSerializer.Serialize(deck) == before && outcome.IsSatisfied == true, "Size validation mutated the source deck.");
        try { ((IList<DeckMainDeckSizeSource>)outcome.Sources).Clear(); throw new Exception("Mutable outcome source list."); } catch (NotSupportedException) { }
        try { ((IList<string>)outcome.Unknowns).Clear(); throw new Exception("Mutable outcome unknown list."); } catch (NotSupportedException) { }
        try { ((IList<DeckMainDeckSizeRule>)imported.Construction.MainDeckSizes!).Clear(); throw new Exception("Mutable evidence list."); } catch (NotSupportedException) { }
    }

    private static CardPoolRules Partial(XDocument xml, string build = "255790")
    {
        var sets = xml.Root!.Elements().Select(entity => entity.Elements("Tag").Single(tag => tag.Attribute("name")?.Value == "CARD_SET").Attribute("value")!.Value)
            .GroupBy(id => id).Select(group => new { slug = "set-" + group.Key, id = int.Parse(group.Key), collectibleCount = group.Count() }).ToArray();
        var config = JsonSerializer.Serialize(new { sets = sets.Cast<object>().Append(new { slug = "standard", id = "standard", cardSets = sets.Select(set => set.slug).ToArray() }).ToArray(), setGroups = Array.Empty<object>() });
        var html = "<cards config=\"" + WebUtility.HtmlEncode(config) + "\"></cards>";
        return OfficialStandardPool.CreatePartial(html, Stream(xml.ToString()), build, Now);
    }
    private static CardPoolRules Rules(string id = "pool") => new(id, "255790", 2, Now.AddDays(-1), Now.AddDays(1), "synthetic://complete-only-to-isolate-structure",
        [], Records.Value.Select(card => card["id"]!.GetValue<string>()).ToArray(), [], [], true)
    { Construction = new("sizes", "255790", 2, Now.AddHours(-1), Now.AddHours(1), "synthetic://reviewed-fixed-construction-not-live-pool", [], [])
        { MainDeckSizes = [new("JAIL_430", 20, true), new("REV_018", 40, true)] } };
    private static async Task<CardCatalog> Catalog(string root, IReadOnlyList<JsonObject>? records = null)
    { var catalog = new CardCatalog(Path.Combine(root, "main-sizes-" + Interlocked.Increment(ref _serial) + ".db")); await catalog.InitializeAsync(); await ImportCards(catalog, records ?? Records.Value); return catalog; }
    private static Task<CardManifest> ImportCards(CardCatalog catalog, IReadOnlyList<JsonObject> records, string build = "255790") => catalog.ImportAsync(Stream(JsonSerializer.Serialize(records)), build);
    private static Task<CardPoolRules> Import(CardCatalog catalog, CardPoolRules rules) => catalog.ImportPoolRulesAsync(Stream(JsonSerializer.Serialize(rules)));
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
    private static JsonObject Change(JsonObject card, Action<JsonObject> mutate) { var copy = card.DeepClone().AsObject(); mutate(copy); return copy; }
    private static DeckDefinition Deck(int count, params string[] owners)
    {
        var cards = owners.Select(owner => new DeckCard(Records.Value.Single(card => card["id"]!.GetValue<string>() == owner)["dbfId"]!.GetValue<int>(), 1)).ToList();
        var remaining = count - owners.Length;
        foreach (var filler in Records.Value.Where(card => card["rarity"]?.GetValue<string>() == "COMMON" && card["cardClass"]?.GetValue<string>() == "NEUTRAL"))
        { if (remaining <= 0) break; var copies = Math.Min(2, remaining); cards.Add(new(filler["dbfId"]!.GetValue<int>(), copies)); remaining -= copies; }
        Check(remaining == 0, "Not enough original filler records for the requested deck size.");
        return new(2, [813], cards, []);
    }
    private static void Unknown(DeckDescription result) => Check(result.Legality != CardLegality.Legal && result.MainDeckSize is { IsKnown: false, RequiredCards: null, IsSatisfied: null },
        "Unreviewed construction became ordinary legality or a known size: " + JsonSerializer.Serialize(result));
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid construction evidence was accepted."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
