using System.Text;

namespace HsHelper.Cards;

public sealed partial class CardCatalog
{
    /// <summary>Reads every distinct ID at one build, locale and legality-evidence snapshot without truncating later batches.</summary>
    public Task<IReadOnlyList<CardRecord>> GetByIdsBatchedAsync(IEnumerable<string> cardIds, string? build = null,
        string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cardIds);
        return GetByKeysBatchedAsync("card_id", cardIds.Cast<object>(), build, locale, cancellationToken);
    }

    public Task<IReadOnlyList<CardRecord>> GetByDbfIdsBatchedAsync(IEnumerable<int> dbfIds, string? build = null,
        string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbfIds);
        return GetByKeysBatchedAsync("dbf_id", dbfIds.Cast<object>(), build, locale, cancellationToken);
    }

    private async Task<IReadOnlyList<CardRecord>> GetByKeysBatchedAsync(string column, IEnumerable<object> ids,
        string? build, string locale, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var iterator = ids.GetEnumerator();
        if (!iterator.MoveNext()) return Array.Empty<CardRecord>();
        build ??= (await GetActiveManifestAsync(locale, cancellationToken))?.Build;
        if (build is null) return Array.Empty<CardRecord>();
        var poolRules = await GetPoolRulesAsync(build, 2, cancellationToken: cancellationToken);
        var seen = new HashSet<object>();
        var batch = new List<object>(500);
        var result = new List<CardRecord>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = iterator.Current;
            if (seen.Add(key)) batch.Add(key);
            if (batch.Count == 500)
            {
                result.AddRange(await GetByPinnedKeysAsync(column, batch.ToArray(), build, locale, poolRules, cancellationToken));
                batch.Clear();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!iterator.MoveNext()) break;
        }
        if (batch.Count > 0)
            result.AddRange(await GetByPinnedKeysAsync(column, batch.ToArray(), build, locale, poolRules, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        // Match SQLite's default BINARY collation, including imported non-ASCII IDs.
        return Array.AsReadOnly(result.Select(card => (Card: card, Key: Encoding.UTF8.GetBytes(card.Id)))
            .OrderBy(item => item.Key, Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right)))
            .Select(item => item.Card).ToArray());
    }
}
