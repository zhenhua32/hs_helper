using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class SessionPagingChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "paging");
        yield return ("history pages reach every session beyond 100 with stable timestamp ties", () => AllPages(Path.Combine(root, "all.db")));
        yield return ("history pages preserve filters and legacy list limits", () => Filters(Path.Combine(root, "filters.db")));
        yield return ("history pages are read-only bounded and safe for very large offsets", () => Bounds(Path.Combine(root, "bounds.db")));
        yield return ("history browsing cutoff excludes newly inserted sessions until refresh", () => Cutoff(Path.Combine(root, "cutoff.db")));
    }
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task AllPages(string path)
    {
        var store = await Seed(path);
        var ids = new List<string>();
        for (var number = 1; number <= 3; number++)
        {
            var page = await store.ListSessionPageAsync(pageNumber: number);
            Check(page.PageNumber == number && page.PageSize == 100 && page.Sessions.Count == (number == 3 ? 5 : 100) &&
                page.HasMore == (number < 3) && page.HasPrevious == (number > 1), "Page metadata or sentinel-row handling was wrong.");
            ids.AddRange(page.Sessions.Select(session => session.Id));
        }
        Check(ids.SequenceEqual(Enumerable.Range(0, 205).Select(Id)) && ids.Distinct().Count() == 205,
            "Timestamp ties skipped or repeated a saved session across page boundaries.");
        var reopened = new SessionStore(path);
        Check((await reopened.ListSessionPageAsync(pageNumber: 2)).Sessions.Select(session => session.Id).SequenceEqual(ids.Skip(100).Take(100)),
            "The same page changed order across a connection/restart.");
    }

    private static async Task Filters(string path)
    {
        var store = await Seed(path);
        var filter = new SessionFilter(CardBuild: "paging-a", OpponentClass: "mage", WentFirst: true,
            Format: "standard", PlayerClass: "hunter", Region: "cn", GameType: "gt_ranked", Limit: 1);
        var expected = Enumerable.Range(0, 205).Where(i => i < 150 && i % 2 == 0 && i % 4 < 2 && i % 3 != 0).Select(Id).ToArray();
        var actual = new List<string>();
        for (var number = 1; ; number++)
        {
            var page = await store.ListSessionPageAsync(filter, number, 7);
            Check(page.Sessions.Count <= 7, "Legacy filter Limit replaced the requested page bound.");
            actual.AddRange(page.Sessions.Select(session => session.Id));
            if (!page.HasMore) break;
        }
        Check(actual.SequenceEqual(expected) && (await store.ListSessionsAsync(filter)).Count == 1 &&
            (await store.GetStatsAsync(filter)).Games == expected.Length, "Pagination changed existing filters, legacy list behavior or full statistics.");
    }

    private static async Task Bounds(string path)
    {
        var store = await Seed(path);
        foreach (var (number, size) in new[] { (0, 100), (-1, 100), (1, 0), (1, 201) })
            await Throws<ArgumentOutOfRangeException>(() => store.ListSessionPageAsync(pageNumber: number, pageSize: size));
        var last = await store.ListSessionPageAsync(pageNumber: int.MaxValue, pageSize: 200);
        Check(last.Sessions.Count == 0 && !last.HasMore, "The page offset overflowed or wrapped back to actual sessions.");
        var first = await store.ListSessionPageAsync();
        try { ((IList<GameSession>)first.Sessions).Clear(); throw new InvalidOperationException("Mutable page sessions."); }
        catch (NotSupportedException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.ListSessionPageAsync(cancellationToken: cancelled.Token));
    }

    private static async Task Cutoff(string path)
    {
        var store = await Seed(path);
        var scope = new SessionFilter(Until: Stamp);
        var before = await store.ListSessionPageAsync(scope);
        await Insert(path, 205, Stamp.AddMinutes(1));
        var second = await store.ListSessionPageAsync(scope, 2);
        Check(before.Sessions.Select(session => session.Id).Concat(second.Sessions.Select(session => session.Id))
            .SequenceEqual(Enumerable.Range(0, 200).Select(Id)) &&
            (await store.ListSessionPageAsync()).Sessions[0].Id == Id(205),
            "A new latest session shifted the anchored page, or a fresh query could not see it.");
    }

    private static async Task<SessionStore> Seed(string path)
    {
        var store = new SessionStore(path); await store.InitializeAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); using var transaction = connection.BeginTransaction();
        // Deliberately reverse insertion order: same-timestamp pages must follow the explicit ID tiebreaker.
        for (var index = 204; index >= 0; index--)
            await Insert(connection, transaction, index, Stamp);
        transaction.Commit(); return store;
    }
    private static async Task Insert(string path, int index, DateTimeOffset stamp)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(); await Insert(connection, null, index, stamp);
    }
    private static async Task Insert(SqliteConnection connection, SqliteTransaction? transaction, int index, DateTimeOffset stamp)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_sessions(id,binding_hash,card_build,local_player_id,format,region,started_at,updated_at,ended_at,
                turn_count,outcome,went_first,player_class,opponent_class,is_complete,last_event_seq,game_type)
            VALUES($id,'synthetic-paging',$build,1,'STANDARD','CN',$stamp,$stamp,$stamp,1,'Won',$first,'HUNTER',$class,1,1,$mode)
            """;
        command.Parameters.AddWithValue("$id", Id(index)); command.Parameters.AddWithValue("$build", index < 150 ? "paging-a" : "paging-b");
        command.Parameters.AddWithValue("$stamp", stamp.ToString("O")); command.Parameters.AddWithValue("$first", index % 4 < 2);
        command.Parameters.AddWithValue("$class", index % 2 == 0 ? "MAGE" : "WARRIOR");
        command.Parameters.AddWithValue("$mode", index % 3 == 0 ? "GT_CASUAL" : "GT_RANKED");
        await command.ExecuteNonQueryAsync();
    }
    private static string Id(int value) => "paging-" + value.ToString("D4");
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
