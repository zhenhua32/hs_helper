using System.Text.Json;
using HsHelper.Cards;

internal static class CardBatchChecks
{
    private const int Count = 1203;
    private const string Old = "810001", New = "810002";
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> run)
    {
        await run("batched card and DBF lookup reaches later definitions with global ordering and deduplication", async () =>
        {
            var catalog = await Create(root, "many");
            var ids = Enumerable.Range(1, Count).Reverse().Select(Id).Concat([Id(1), "MISSING"]);
            var cards = await catalog.GetByIdsBatchedAsync(ids, Old);
            Check(cards.Count == Count && cards.Select(card => card.Id).SequenceEqual(Enumerable.Range(1, Count).Select(Id)) &&
                cards[^1].Text == "original 1203", "ID batching lost or reordered late records.");
            var numbers = Enumerable.Range(1, Count).Reverse().Select(Number).Concat([Number(1), int.MaxValue]);
            var byNumber = await catalog.GetByDbfIdsBatchedAsync(numbers, Old);
            Check(byNumber.Select(card => card.Id).SequenceEqual(cards.Select(card => card.Id)), "DBF batching did not preserve the same complete catalog results.");
        });
        await run("legacy single-batch guards remain at 500 while duplicate IDs are not counted twice", async () =>
        {
            var catalog = await Create(root, "guard");
            await Throws<ArgumentOutOfRangeException>(() => catalog.GetByIdsAsync(Enumerable.Range(1, 501).Select(Id), Old));
            await Throws<ArgumentOutOfRangeException>(() => catalog.GetByDbfIdsAsync(Enumerable.Range(1, 501).Select(Number), Old));
            Check((await catalog.GetByIdsAsync(Enumerable.Repeat(Id(1), 1500), Old)).Count == 1 &&
                (await catalog.GetByDbfIdsBatchedAsync(Enumerable.Repeat(Number(1), 1500), Old)).Count == 1,
                "Deduplication consumed multiple rows or changed the old API guard.");
        });
        await run("batched default build remains fixed when real active catalog changes during enumeration", async () =>
        {
            var catalog = await Create(root, "pin"); await Import(catalog, New, "changed"); await Import(catalog, Old, "original");
            var switched = false;
            IEnumerable<string> Input()
            {
                for (var i = 1; i <= Count; i++)
                {
                    yield return Id(i);
                    if (i == 500) { Import(catalog, New, "changed").GetAwaiter().GetResult(); switched = true; }
                }
            }
            var cards = await catalog.GetByIdsBatchedAsync(Input());
            Check(switched && (await catalog.GetActiveManifestAsync())!.Build == New && cards.Count == Count && cards.All(card => card.Text.StartsWith("original ", StringComparison.Ordinal)),
                "A later active version supplied later batches or changed the captured default build.");
        });
        await run("batched DBF default build and locale remain independent across a real English active switch", async () =>
        {
            var catalog = await Create(root, "locale"); await Import(catalog, New, "english-new", "enUS"); await Import(catalog, Old, "english-old", "enUS");
            IEnumerable<int> Input()
            {
                for (var i = 1; i <= Count; i++)
                {
                    yield return Number(i);
                    if (i == 500) Import(catalog, New, "english-new", "enUS").GetAwaiter().GetResult();
                }
            }
            var cards = await catalog.GetByDbfIdsBatchedAsync(Input(), locale: "enUS");
            Check(cards.Count == Count && cards.All(card => card.Text.StartsWith("english-old ", StringComparison.Ordinal)) &&
                (await catalog.GetActiveManifestAsync("enUS"))!.Build == New && (await catalog.GetActiveManifestAsync())!.Build == Old,
                "Batching mixed locale scopes or reread a later active build.");
        });
        await run("batched legality uses one pool rule even when a newer same-build rule arrives between batches", async () =>
        {
            var catalog = await Create(root, "pool"); var now = DateTimeOffset.UtcNow;
            var first = new CardPoolRules("batch-pool-first", Old, 2, now.AddHours(-2), now.AddDays(1), "synthetic://batch-rule-test",
                ["BATCH_TEST"], [], [], [], true);
            var next = first with { RuleId = "batch-pool-next", EffectiveFrom = now.AddHours(-1), BannedCardIds = Enumerable.Range(1, Count).Select(Id).ToArray() };
            async Task ImportRule(CardPoolRules rules)
            { using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(rules)); await catalog.ImportPoolRulesAsync(input); }
            await ImportRule(first);
            IEnumerable<string> Input()
            {
                for (var i = 1; i <= Count; i++)
                { yield return Id(i); if (i == 500) ImportRule(next).GetAwaiter().GetResult(); }
            }
            var cards = await catalog.GetByIdsBatchedAsync(Input(), Old);
            Check(cards.Count == Count && cards.All(card => card.StandardLegality == CardLegality.Legal) &&
                (await catalog.GetPoolRulesAsync(Old))!.RuleId == next.RuleId &&
                (await catalog.GetByIdsAsync([Id(Count)], Old)).Single().StandardLegality == CardLegality.Illegal,
                "One bulk result mixed legality snapshots, or the rule-switch fixture did not change actual current evidence.");
            var absent = await Create(root, "pool-absent");
            IEnumerable<int> WithoutInitialRule()
            {
                for (var i = 1; i <= Count; i++)
                {
                    yield return Number(i);
                    if (i == 500)
                    {
                        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(first));
                        absent.ImportPoolRulesAsync(input).GetAwaiter().GetResult();
                    }
                }
            }
            var unknown = await absent.GetByDbfIdsBatchedAsync(WithoutInitialRule(), Old);
            Check(unknown.Count == Count && unknown.All(card => card.StandardLegality == CardLegality.Unknown) &&
                (await absent.GetByDbfIdsAsync([Number(Count)], Old)).Single().StandardLegality == CardLegality.Legal,
                "Captured missing evidence was reread or later batches gained legality from a new rule.");
        });
        await run("empty batch lookup requires no catalog and already cancelled lookup publishes nothing", async () =>
        {
            var catalog = new CardCatalog(Path.Combine(root, "batch-empty-uninitialized.db"));
            Check((await catalog.GetByIdsBatchedAsync([])).Count == 0 && (await catalog.GetByDbfIdsBatchedAsync([])).Count == 0,
                "Empty lookup unnecessarily required an initialized or active catalog.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => catalog.GetByIdsBatchedAsync([Id(1)], cancellationToken: cancellation.Token));
            await Throws<OperationCanceledException>(() => catalog.GetByDbfIdsBatchedAsync([], cancellationToken: cancellation.Token));
        });
        await run("cancellation after a completed batch prevents returning partial records", async () =>
        {
            var catalog = await Create(root, "cancel"); using var cancellation = new CancellationTokenSource(); var crossed = false;
            IEnumerable<string> Input()
            {
                for (var i = 1; i <= Count; i++)
                {
                    yield return Id(i);
                    if (i == 500) { crossed = true; cancellation.Cancel(); }
                }
            }
            IReadOnlyList<CardRecord>? partial = null;
            await Throws<OperationCanceledException>(async () => partial = await catalog.GetByIdsBatchedAsync(Input(), Old, cancellationToken: cancellation.Token));
            Check(crossed && partial is null && (await catalog.GetByIdsBatchedAsync(Enumerable.Range(1, Count).Select(Id), Old)).Count == Count,
                "Cancelled lookup published partial results or damaged the fixed catalog.");
        });
        await run("late input failure cannot publish an earlier successful batch", async () =>
        {
            var catalog = await Create(root, "failure");
            IEnumerable<int> Input()
            {
                for (var i = 1; i <= 500; i++) yield return Number(i);
                throw new InvalidOperationException("synthetic late input failure");
            }
            IReadOnlyList<CardRecord>? partial = null;
            await Throws<InvalidOperationException>(async () => partial = await catalog.GetByDbfIdsBatchedAsync(Input(), Old));
            Check(partial is null, "A failed aggregate read exposed partial data.");
        });
        await run("batched results and metadata remain read-only and missing fixed builds never use active", async () =>
        {
            var catalog = await Create(root, "readonly");
            var cards = await catalog.GetByIdsBatchedAsync([Id(Count), "MISSING", Id(1)], Old);
            Check(cards.Count == 2 && (await catalog.GetByIdsBatchedAsync([Id(1)], "810099")).Count == 0,
                "Missing definitions or builds were borrowed from another scope.");
            ThrowsReadOnly(() => ((IList<CardRecord>)cards).Clear());
            ThrowsReadOnly(() => ((IList<string>)cards[0].Classes).Clear());
            ThrowsReadOnly(() => ((IList<string>)cards[0].Entourage).Clear());
            Check(cards[0].Id == Id(1) && cards[^1].Id == Id(Count), "Consumer mutation altered immutable batch results.");
            string[] unicodeIds = ["B_\uE000", "B_\U00010000", "B_A"];
            using var unicode = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(unicodeIds.Select((id, i) => new { id, dbfId = i + 1 })));
            await catalog.ImportAsync(unicode, "810003");
            var single = await catalog.GetByIdsAsync(unicodeIds, "810003");
            Check((await catalog.GetByIdsBatchedAsync(unicodeIds, "810003")).Select(card => card.Id).SequenceEqual(single.Select(card => card.Id)),
                "Global ordering changed SQLite's CardId collation for imported Unicode identifiers.");
        });
    }
    private static string Id(int i) => $"BATCH_{i:D4}";
    private static int Number(int i) => 90000 + i;
    private static async Task<CardCatalog> Create(string root, string name)
    {
        var catalog = new CardCatalog(Path.Combine(root, "batch-" + name + ".db")); await catalog.InitializeAsync();
        await Import(catalog, Old, "original"); return catalog;
    }
    private static Task<CardManifest> Import(CardCatalog catalog, string build, string text, string locale = "zhCN")
        => catalog.ImportAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, Count).Select(i => new
        { id = Id(i), dbfId = Number(i), name = "Batch " + i, text = text + " " + i, cardClass = "HUNTER", set = "BATCH_TEST", type = "MINION", collectible = true, classes = new[] { "HUNTER" }, entourage = new[] { "MISSING" } }))), build, locale);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void ThrowsReadOnly(Action action)
    { try { action(); } catch (NotSupportedException) { return; } throw new InvalidOperationException("Expected read-only collection."); }
}
