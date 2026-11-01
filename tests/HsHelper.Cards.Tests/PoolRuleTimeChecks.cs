using System.Text;
using System.Text.Json;
using HsHelper.Cards;
using Microsoft.Data.Sqlite;

internal static class PoolRuleTimeChecks
{
    private const string Build = "255790";
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1000);
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> run)
    {
        await run("future evidence one tick in the same millisecond cannot promote partial card legality", async () =>
        {
            var (catalog, _) = await Catalog(root);
            var old = Rules("old-partial", At.AddTicks(-2000), false);
            var future = Rules("future-complete", At.AddTicks(1), true);
            Check(old.EffectiveFrom.ToUnixTimeMilliseconds() < At.ToUnixTimeMilliseconds() &&
                future.EffectiveFrom.ToUnixTimeMilliseconds() == At.ToUnixTimeMilliseconds(), "The fixture did not put future evidence within the query millisecond.");
            await Import(catalog, old); await Import(catalog, future);
            Check((await catalog.GetPoolRulesAsync(Build, at: At))?.RuleId == old.RuleId &&
                (await catalog.SearchAsync(new(Collectible: true, At: At), Build)).Single().StandardLegality == CardLegality.Unknown,
                "A future complete rule upgraded an older partial pool to Legal before its exact effective tick.");
            Check((await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(1)))?.RuleId == future.RuleId &&
                (await catalog.SearchAsync(new(Collectible: true, At: At.AddTicks(1)), Build)).Single().StandardLegality == CardLegality.Legal,
                "Evidence did not become effective at its precise inclusive start.");
            // The inverse transition is equally time-bound: later partial evidence cannot
            // downgrade an earlier complete rule before the actual observed query time.
            var (inverse, _) = await Catalog(root);
            await Import(inverse, old with { IsComplete = true }); await Import(inverse, future with { IsComplete = false });
            Check((await inverse.SearchAsync(new(Collectible: true, At: At), Build)).Single().StandardLegality == CardLegality.Legal &&
                (await inverse.SearchAsync(new(Collectible: true, At: At.AddTicks(1)), Build)).Single().StandardLegality == CardLegality.Unknown,
                "A future partial rule replaced complete evidence early.");
            var (none, _) = await Catalog(root); await Import(none, future);
            Check(await none.GetPoolRulesAsync(Build, at: At) is null, "A future-only millisecond group supplied current evidence.");
        });

        await run("same-millisecond rules order by full effective ticks independently of import order and IDs", async () =>
        {
            foreach (var reverse in new[] { false, true })
            {
                var (catalog, _) = await Catalog(root);
                var older = Rules("z-older-import-can-win-millisecond-sort", At.AddTicks(1), false);
                var latest = Rules("a-latest-exact-time", At.AddTicks(2), true);
                foreach (var rule in reverse ? new[] { latest, older } : new[] { older, latest }) await Import(catalog, rule);
                Check((await catalog.GetPoolRulesAsync(Build, at: At)) is null &&
                    (await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(1)))?.RuleId == older.RuleId &&
                    (await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(2)))?.RuleId == latest.RuleId &&
                    (await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(999)))?.RuleId == latest.RuleId,
                    "Truncated SQL time, import order, or RuleId ordering replaced the newest actually effective rule.");
                var offset = At.AddTicks(2).ToOffset(TimeSpan.FromHours(8));
                Check((await catalog.GetPoolRulesAsync(Build, at: offset))?.RuleId == latest.RuleId, "Query offset changed the actual evidence instant.");
            }
        });

        await run("exact supersession expires without reviving old rules even beside future same-millisecond evidence", async () =>
        {
            var (catalog, _) = await Catalog(root);
            var previous = Rules("previous-long-valid", At.AddTicks(-2000), true);
            var superseding = Rules("superseding-short-valid", At.AddTicks(-2), false) with { ValidUntil = At.AddTicks(-1) };
            var future = Rules("future-complete", At.AddTicks(1), true);
            await Import(catalog, superseding); await Import(catalog, future); await Import(catalog, previous);
            Check((await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(-3)))?.RuleId == previous.RuleId &&
                (await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(-2)))?.RuleId == superseding.RuleId,
                "An older import displaced the full-tick superseding boundary.");
            foreach (var instant in new[] { At.AddTicks(-1), At })
                Check(await catalog.GetPoolRulesAsync(Build, at: instant) is null &&
                    (await catalog.SearchAsync(new(Collectible: true, At: instant), Build)).Single().StandardLegality == CardLegality.Unknown,
                    "Expired latest evidence revived an old complete pool or selected a future replacement.");
            Check((await catalog.GetPoolRulesAsync(Build, at: At.AddTicks(1)))?.RuleId == future.RuleId,
                "A later precise effective boundary remained blocked by an expired predecessor.");
        });

        await run("precise reads preserve exact-time tie policy immutable JSON hashes schema and cancellation", async () =>
        {
            var (catalog, database) = await Catalog(root);
            var a = Rules("a-rule", At, false); var z = Rules("z-rule", At, true);
            await Import(catalog, z); await Import(catalog, a);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); await connection.OpenAsync();
            async Task<object?> Sql(string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
            // Pin equal import timestamps to check the pre-existing RuleId tie breaker as
            // distinct from sub-millisecond effective ordering; normal imports remain intact.
            await Sql("UPDATE card_pool_rules SET imported_at=100");
            var beforeJson = await Sql("SELECT group_concat(json,'|') FROM (SELECT json FROM card_pool_rules ORDER BY rule_id)");
            var beforeHash = await Sql("SELECT group_concat(sha256,'|') FROM (SELECT sha256 FROM card_pool_rules ORDER BY rule_id)");
            Check((await catalog.GetPoolRulesAsync(Build, at: At))?.RuleId == "z-rule", "Exact effective/import time ties changed the stable RuleId rule.");
            await Sql("UPDATE card_pool_rules SET imported_at=101 WHERE rule_id='a-rule'");
            Check((await catalog.GetPoolRulesAsync(Build, at: At))?.RuleId == "a-rule", "Exact effective time no longer honors later import priority.");
            Check(await catalog.GetPoolRulesAsync("255791", at: At) is null && await catalog.GetPoolRulesAsync(Build, 1, At) is null,
                "Precise selection crossed build or format scope.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await catalog.GetPoolRulesAsync(Build, at: At, cancellationToken: canceled.Token); throw new Exception("Canceled precise read completed."); }
            catch (OperationCanceledException) { }
            Check(Equals(beforeJson, await Sql("SELECT group_concat(json,'|') FROM (SELECT json FROM card_pool_rules ORDER BY rule_id)")) &&
                Equals(beforeHash, await Sql("SELECT group_concat(sha256,'|') FROM (SELECT sha256 FROM card_pool_rules ORDER BY rule_id)")) &&
                Convert.ToInt64(await Sql("PRAGMA user_version")) == 4, "Precision correction rewrote immutable evidence or schema.");
        });
    }
    private static CardPoolRules Rules(string id, DateTimeOffset from, bool complete) => new(id, Build, 2, from, At.AddHours(1),
        "synthetic://exact-pool-query-time", ["TIME_TEST"], [], [], [], complete);
    private static async Task<(CardCatalog Catalog, string Database)> Catalog(string root)
    {
        var database = Path.Combine(root, "pool-exact-time-" + Guid.NewGuid().ToString("N") + ".db");
        var catalog = new CardCatalog(database); await catalog.InitializeAsync();
        using var cards = new MemoryStream(Encoding.UTF8.GetBytes("""[{"id":"EXACT_TIME_CARD","dbfId":999900,"name":"时点测试","type":"MINION","cardClass":"NEUTRAL","set":"TIME_TEST","rarity":"COMMON","collectible":true,"cost":1}]"""));
        await catalog.ImportAsync(cards, Build); return (catalog, database);
    }
    private static Task<CardPoolRules> Import(CardCatalog catalog, CardPoolRules rules) => catalog.ImportPoolRulesAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(rules)));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
