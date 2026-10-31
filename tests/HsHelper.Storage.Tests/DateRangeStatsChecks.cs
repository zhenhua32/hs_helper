using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class DateRangeStatsChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "date-range-stats");
        yield return ("date-filtered statistics and all pages retain 180 of 205 real cross-month sessions", () => CrossMonth205(Path.Combine(root, "cross-month.db")));
        yield return ("date boundaries include the last UTC tick but exclude the next day", () => TickBoundaries(Path.Combine(root, "ticks.db")));
        yield return ("equivalent date offsets produce the same UTC session and statistics scope", () => OffsetEquivalence(Path.Combine(root, "offsets.db")));
        yield return ("date range combines with every existing session binding filter before limits", () => CombinedFilters(Path.Combine(root, "filters.db")));
        yield return ("class groups include unknown ties and concessions with the correct win-rate denominator", () => ClassGroups(Path.Combine(root, "groups.db")));
        yield return ("empty dates and samples without wins or losses retain unknown win rates", () => EmptyAndNoDenominator(Path.Combine(root, "empty.db")));
        yield return ("fixed first-page date bounds exclude new sessions until an explicit refresh", () => FrozenCutoff(Path.Combine(root, "cutoff.db")));
        yield return ("date-bound timestamp ties keep ordinal session order through page boundaries", () => StableTies(Path.Combine(root, "ties.db")));
        yield return ("date statistics use initial observation time rather than later completion time", () => ObservationTime(Path.Combine(root, "observation.db")));
        yield return ("date browsing and cancellation do not change database bytes schema or results", () => ReadOnlyAndCancelled(Path.Combine(root, "read-only.db")));
    }

    private static readonly DateTimeOffset Start = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private static async Task CrossMonth205(string path)
    {
        var store = await Open(path);
        var observations = Enumerable.Range(0, 205).Select(i => new Observation("cross-" + i.ToString("D3"), Start.AddHours(4 * i),
            (i % 5) switch { 0 => "WON", 1 => "LOST", 2 => "TIED", 3 => "CONCEDED", _ => "PLAYING" },
            (i % 4) switch { 0 => "MAGE", 1 => "WARRIOR", 2 => "ROGUE", _ => null }, First: i % 2 == 0)).ToArray();
        await Record(store, path, observations);
        var filter = Default(new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero).AddTicks(-1)) with { Limit = 1 };
        var page1 = await store.ListSessionPageAsync(filter); var page2 = await store.ListSessionPageAsync(filter, 2);
        var stats = await store.GetStatsAsync(filter);
        var expected = Enumerable.Range(12, 180).Reverse().Select(i => "cross-" + i.ToString("D3")).ToArray();
        Check(page1.Sessions.Count == 100 && page1.HasMore && page2.Sessions.Count == 80 && !page2.HasMore &&
            page1.Sessions.Concat(page2.Sessions).Select(row => row.Id).SequenceEqual(expected), "Cross-month dates skipped or repeated stored sessions.");
        Check(stats is { Games: 180, Wins: 36, Losses: 72, Ties: 36, Unknown: 36 } && Near(stats.WinRate, 1d / 3) &&
            stats.Matchups.Count == 4 && stats.Matchups.All(group => group.Games == 45), "Statistics used only the visible hundred rows or counted outcomes incorrectly.");
        Check((await store.ListSessionsAsync(filter)).Count == 1 && (await store.ListSessionPageAsync(Default(Start, Start.AddDays(35)), pageSize: 200)).HasMore,
            "The sample did not retain all 205 real sessions or legacy Limit leaked into full statistics/pages.");
        SumGroups(stats);
    }

    private static async Task TickBoundaries(string path)
    {
        var store = await Open(path); var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero); var end = day.AddDays(1).AddTicks(-1);
        await Record(store, path, [new("before", day.AddTicks(-1)), new("start", day), new("last-tick", end), new("next-day", day.AddDays(1))]);
        var filter = Default(day, end); var rows = (await store.ListSessionPageAsync(filter)).Sessions;
        Check(rows.Select(row => row.Id).SequenceEqual(["last-tick", "start"]) && (await store.GetStatsAsync(filter)) is { Games: 2, Wins: 2 },
            "The inclusive UTC range lost its first/last tick or included the next day.");
        Check((await store.ListSessionPageAsync(filter with { Since = end, Until = end })).Sessions.Single().Id == "last-tick" &&
            (await store.GetStatsAsync(filter with { Since = day.AddDays(1), Until = day.AddDays(1) })).Games == 1,
            "An exact timestamp boundary was rounded to a date or truncated precision.");
    }

    private static async Task OffsetEquivalence(string path)
    {
        var store = await Open(path); var first = new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero); var last = first.AddDays(1).AddTicks(-1);
        await Record(store, path, [new("outside-before", first.AddTicks(-1)), new("offset-start", first), new("offset-end", last), new("outside-after", last.AddTicks(1))]);
        var utc = Default(first, last); var cn = utc with { Since = first.ToOffset(TimeSpan.FromHours(8)), Until = last.ToOffset(TimeSpan.FromHours(8)) };
        var west = utc with { Since = first.ToOffset(TimeSpan.FromHours(-5)), Until = last.ToOffset(TimeSpan.FromHours(-5)) };
        foreach (var filter in new[] { utc, cn, west })
            Check((await store.ListSessionPageAsync(filter)).Sessions.Select(row => row.Id).SequenceEqual(["offset-end", "offset-start"]) &&
                (await store.GetStatsAsync(filter)) is { Games: 2, Wins: 2 }, "A DateTimeOffset query compared local clock text rather than UTC instants.");
    }

    private static async Task CombinedFilters(string path)
    {
        var store = await Open(path);
        var deckA = await store.SaveDeckVersionAsync("日期套牌A", new(2, [31], [new(100, 1)], []), "255790");
        var deckB = await store.SaveDeckVersionAsync("日期套牌B", new(2, [31], [new(101, 1)], []), "255790", deckA.Id);
        var good = new Observation("good-first", Start.AddDays(1), "WON", "MAGE", true, deckA.Id);
        Observation Other(string id) => good with { Id = id, At = Start.AddDays(2) };
        await Record(store, path,
        [
            good, good with { Id = "good-second", At = Start.AddDays(1).AddHours(1), Outcome = "LOST" },
            Other("wrong-date-before") with { At = Start.AddTicks(-1) }, Other("wrong-date-after") with { At = Start.AddDays(4) },
            Other("wrong-deck") with { DeckId = deckB.Id }, Other("no-deck") with { DeckId = null }, Other("wrong-build") with { Build = "255791" },
            Other("wrong-opponent") with { Opponent = "WARRIOR" }, Other("unknown-opponent") with { Opponent = null },
            Other("wrong-first") with { First = false }, Other("unknown-first") with { First = null }, Other("wrong-format") with { Format = "WILD" },
            Other("wrong-local-class") with { PlayerClass = "MAGE" }, Other("wrong-region") with { Region = "EU" }, Other("wrong-mode") with { GameType = "GT_CASUAL" }
        ]);
        var filter = Default(Start, Start.AddDays(3)) with { DeckVersionId = deckA.Id, OpponentClass = "mage", WentFirst = true, Limit = 1 };
        var firstPage = await store.ListSessionPageAsync(filter, pageSize: 1); var secondPage = await store.ListSessionPageAsync(filter, 2, 1);
        Check(firstPage.Sessions.Single().Id == "good-second" && firstPage.HasMore && secondPage.Sessions.Single().Id == "good-first" && !secondPage.HasMore &&
            (await store.GetStatsAsync(filter)) is { Games: 2, Wins: 1, Losses: 1, Ties: 0, Unknown: 0 },
            "Dates or existing bindings were applied after LIMIT or differed between pages and statistics.");
        Check((await store.GetStatsAsync(filter with { GameType = null })).Games == 3 &&
            (await store.ListSessionPageAsync(filter with { CardBuild = null })).Sessions.Count == 3 &&
            (await store.GetStatsAsync(filter with { DeckVersionId = null })).Games == 4, "Removing one filter unexpectedly retained or removed other constraints.");
    }

    private static async Task ClassGroups(string path)
    {
        var store = await Open(path);
        await Record(store, path,
        [
            new("mage-win", Start, "WON", "MAGE"), new("mage-loss", Start.AddHours(1), "LOST", "MAGE"),
            new("mage-tie", Start.AddHours(2), "TIED", "MAGE"), new("mage-concede", Start.AddHours(3), "CONCEDED", "MAGE"),
            new("unknown-opponent", Start.AddHours(4), "PLAYING", null), new("warrior-tie", Start.AddHours(5), "TIED", "WARRIOR"),
            new("warrior-disconnect", Start.AddHours(6), "DISCONNECTED", "WARRIOR"),
            new("mage-mixed-title", Start.AddHours(7), "WON", "Mage"), new("mage-mixed-lower", Start.AddHours(8), "LOST", "mage")
        ]);
        var stats = await store.GetStatsAsync(Default(Start, Start.AddDays(1))); SumGroups(stats);
        Check(stats is { Games: 9, Wins: 2, Losses: 3, Ties: 2, Unknown: 2 } && Near(stats.WinRate, 2d / 5),
            "Ties/unknowns entered the win-rate denominator or concessions stopped counting as losses.");
        Check(stats.Matchups.Count(group => group.OpponentClass.Equals("MAGE", StringComparison.OrdinalIgnoreCase)) == 1,
            "Real CLASS values Mage/mage/MAGE split one opponent class into multiple groups.");
        var mage = stats.Matchups.Single(group => group.OpponentClass.Equals("MAGE", StringComparison.OrdinalIgnoreCase));
        var warrior = stats.Matchups.Single(group => group.OpponentClass == "WARRIOR");
        var unknown = stats.Matchups.Single(group => group.OpponentClass == "Unknown");
        Check(mage is { Games: 6, Wins: 2, Losses: 3, Ties: 1, Unknown: 0 } && Near(mage.WinRate, 2d / 5) &&
            warrior is { Games: 2, Wins: 0, Losses: 0, Ties: 1, Unknown: 1, WinRate: null } && unknown is { Games: 1, Unknown: 1, WinRate: null },
            "Class grouping invented a known opponent, omitted unknown samples, or calculated a different denominator.");
        var filtered = Default(Start, Start.AddDays(1)) with { OpponentClass = "mAgE" };
        Check((await store.GetStatsAsync(filtered)) is { Games: 6, Matchups.Count: 1 } &&
            (await store.ListSessionPageAsync(filtered)).Sessions.Count == 6, "The case-insensitive class filter and grouped totals disagreed.");
        var asOf = DateTimeOffset.UtcNow.AddMinutes(1);
        var historical = await store.GetStatsAsOfAsync(asOf, Default(Start, Start.AddDays(1))); SumGroups(historical);
        Check(historical is { Games: 8, Wins: 2, Losses: 3, Ties: 2, Unknown: 1, Matchups.Count: 2 } &&
            historical.Matchups.Count(group => group.OpponentClass.Equals("MAGE", StringComparison.OrdinalIgnoreCase)) == 1 &&
            (await store.GetStatsAsOfAsync(asOf, filtered)) is { Games: 6, Matchups.Count: 1 },
            "Historical complete-game statistics split the same class spelling variants or changed their scope.");
    }

    private static async Task EmptyAndNoDenominator(string path)
    {
        var store = await Open(path);
        await Record(store, path, [new("tie-only", Start, "TIED"), new("unfinished-only", Start.AddHours(1), "PLAYING"), new("disconnected-only", Start.AddHours(2), "DISCONNECTED")]);
        var noResults = await store.GetStatsAsync(Default(Start, Start.AddDays(1)));
        Check(noResults is { Games: 3, Wins: 0, Losses: 0, Ties: 1, Unknown: 2, WinRate: null } && noResults.Matchups.Single().WinRate is null,
            "No decisive games became zero-percent or one-hundred-percent win rate.");
        var emptyScope = Default(Start.AddMonths(2), Start.AddMonths(3)); var empty = await store.GetStatsAsync(emptyScope);
        Check(empty is { Games: 0, Wins: 0, Losses: 0, Ties: 0, Unknown: 0, WinRate: null, Matchups.Count: 0 } &&
            (await store.ListSessionPageAsync(emptyScope)) is { Sessions.Count: 0, HasMore: false }, "Empty date ranges invented statistics or page rows.");
    }

    private static async Task FrozenCutoff(string path)
    {
        var store = await Open(path); var cutoff = Start.AddDays(1);
        await Record(store, path, Enumerable.Range(0, 105).Select(i => new Observation("frozen-" + i.ToString("D3"), Start.AddMinutes(i))).ToArray());
        var filter = Default(Start, cutoff); var first = await store.ListSessionPageAsync(filter);
        await Record(store, path, [new("new-after-cutoff", cutoff.AddTicks(1), "LOST")]);
        var second = await store.ListSessionPageAsync(filter, 2);
        Check(first.Sessions.Concat(second.Sessions).Select(row => row.Id).SequenceEqual(Enumerable.Range(0, 105).Reverse().Select(i => "frozen-" + i.ToString("D3"))) &&
            (await store.GetStatsAsync(filter)) is { Games: 105, Wins: 105 }, "A later observation shifted fixed-cutoff pages or statistics.");
        var refreshed = filter with { Until = cutoff.AddDays(1) };
        Check((await store.ListSessionPageAsync(refreshed)).Sessions[0].Id == "new-after-cutoff" &&
            (await store.GetStatsAsync(refreshed)) is { Games: 106, Wins: 105, Losses: 1 }, "An explicit later refresh did not expose the new observation.");
    }

    private static async Task StableTies(string path)
    {
        var store = await Open(path);
        await Record(store, path, Enumerable.Range(0, 12).Reverse().Select(i => new Observation("tie-" + i.ToString("D2"), Start)).ToArray());
        var filter = Default(Start, Start); var ids = new List<string>();
        for (var number = 1; number <= 3; number++) ids.AddRange((await store.ListSessionPageAsync(filter, number, 5)).Sessions.Select(row => row.Id));
        Check(ids.SequenceEqual(Enumerable.Range(0, 12).Select(i => "tie-" + i.ToString("D2"))) && (await store.GetStatsAsync(filter)).Games == 12,
            "A shared date-boundary timestamp fell back to insertion order or skipped/repeated tied IDs.");
        Check((await new SessionStore(path).ListSessionPageAsync(filter, 2, 5)).Sessions.Select(row => row.Id).SequenceEqual(ids.Skip(5).Take(5)),
            "Reopening changed same-time date-filtered page order.");
    }

    private static async Task ObservationTime(string path)
    {
        var store = await Open(path); await Record(store, path, [new("old-observation", Start, "WON")]);
        var session = (await store.GetSessionAsync("old-observation"))!; var completion = session.EndedAt!.Value;
        var observed = completion.AddDays(-40); await SetStartedAt(path, [new("old-observation", observed)]);
        Check((await store.GetStatsAsync(Default(completion.AddDays(-1), completion.AddDays(1)))).Games == 0 &&
            (await store.ListSessionPageAsync(Default(completion.AddDays(-1), completion.AddDays(1)))).Sessions.Count == 0,
            "A later completion moved an older observation into the selected recent-date scope.");
        Check((await store.GetStatsAsync(Default(observed, observed))) is { Games: 1, Wins: 1 } &&
            (await store.GetSessionAsync("old-observation"))!.EndedAt == completion,
            "The date query changed completion data or excluded an original observation-time boundary.");
    }

    private static async Task ReadOnlyAndCancelled(string path)
    {
        var store = await Open(path); await Record(store, path, [new("read-win", Start), new("read-loss", Start.AddHours(1), "LOST", "WARRIOR")]);
        var before = await Fingerprint(path); var filter = Default(Start, Start.AddDays(1)); var stats = await store.GetStatsAsync(filter);
        var page = await store.ListSessionPageAsync(filter, pageSize: 1);
        await Throws<NotSupportedException>(() => { ((IList<ClassMatchupStats>)stats.Matchups).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<GameSession>)page.Sessions).Clear(); return Task.CompletedTask; });
        await store.ListSessionPageAsync(filter, 2, 1); await new SessionStore(path).GetStatsAsync(filter);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.GetStatsAsync(filter, cancelled.Token));
        await Throws<OperationCanceledException>(() => store.ListSessionPageAsync(filter, cancellationToken: cancelled.Token));
        Check(await Fingerprint(path) == before, "Date queries, read-only outputs or cancellation modified database bytes or schema.");
    }

    private static SessionFilter Default(DateTimeOffset since, DateTimeOffset until) => new(CardBuild: "255790", Format: "STANDARD", Since: since, Until: until,
        PlayerClass: "HUNTER", Region: "CN", GameType: "GT_RANKED");
    private sealed record Observation(string Id, DateTimeOffset At, string Outcome = "WON", string? Opponent = "MAGE", bool? First = true,
        string? DeckId = null, string Build = "255790", string Format = "STANDARD", string PlayerClass = "HUNTER", string Region = "CN", string GameType = "GT_RANKED");
    private static async Task Record(SessionStore store, string path, IReadOnlyList<Observation> observations)
    {
        foreach (var group in observations.GroupBy(row => (row.DeckId, row.Build, row.Format, row.Region, row.PlayerClass)))
        {
            var binding = group.Key; var recorder = new SessionRecorder(store, new(binding.DeckId, binding.Build, 1, binding.Format, binding.Region, binding.PlayerClass));
            long line = 0;
            foreach (var row in group) recorder.Observe(Frame(row), ++line);
            await recorder.CompleteAsync();
        }
        await SetStartedAt(path, observations);
    }
    private static GameSnapshot Frame(Observation row)
    {
        var local = new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["HERO_ENTITY"] = "11", ["CURRENT_PLAYER"] = "1", ["PLAYSTATE"] = row.Outcome };
        var opponent = new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["HERO_ENTITY"] = "21" };
        if (row.First is { } first) (first ? local : opponent)["FIRST_PLAYER"] = "1";
        var ownHero = new Dictionary<string, string> { ["ZONE"] = "PLAY", ["CONTROLLER"] = "1", ["CLASS"] = row.PlayerClass };
        var enemyHero = new Dictionary<string, string> { ["ZONE"] = "PLAY", ["CONTROLLER"] = "2" };
        if (row.Opponent is not null) enemyHero["CLASS"] = row.Opponent;
        return new(row.Id, 1, 1, 1, 1, 1, row.Outcome == "PLAYING" ? GamePhase.Main : GamePhase.Complete,
            new[] { new EntitySnapshot(2, null, EntityKind.Player, local.ToFrozenDictionary()), new EntitySnapshot(3, null, EntityKind.Player, opponent.ToFrozenDictionary()),
                new EntitySnapshot(11, "HERO_FIXTURE_LOCAL", EntityKind.Card, ownHero.ToFrozenDictionary()),
                new EntitySnapshot(21, "HERO_FIXTURE_OPPONENT", EntityKind.Card, enemyHero.ToFrozenDictionary()) }.ToFrozenDictionary(entity => entity.Id),
            new(true, true, true, true, 0, [])) { ClientBuild = row.Build, Format = row.Format, GameType = row.GameType };
    }
    private static async Task SetStartedAt(string path, IReadOnlyList<Observation> observations)
    {
        await using var connection = await Connect(path); using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        // Only synthetic local fixtures are retimed. Snapshots/events/results still come from SessionRecorder.
        command.CommandText = "UPDATE game_sessions SET started_at=$at WHERE id=$id";
        var at = command.Parameters.AddWithValue("$at", ""); var id = command.Parameters.AddWithValue("$id", "");
        foreach (var row in observations)
        { at.Value = row.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); id.Value = row.Id; Check(await command.ExecuteNonQueryAsync() == 1, "Synthetic observation was not recorded."); }
        transaction.Commit();
    }
    private static void SumGroups(PersonalStats stats)
    {
        Check(stats.Matchups.Sum(group => group.Games) == stats.Games && stats.Matchups.Sum(group => group.Wins) == stats.Wins &&
            stats.Matchups.Sum(group => group.Losses) == stats.Losses && stats.Matchups.Sum(group => group.Ties) == stats.Ties &&
            stats.Matchups.Sum(group => group.Unknown) == stats.Unknown, "Class groups did not sum to the full selected scope.");
    }
    private static async Task<string> Fingerprint(string path)
    {
        string schema;
        await using (var connection = await Connect(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type||':'||name||':'||COALESCE(sql,'') FROM sqlite_master ORDER BY type,name";
            var rows = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0)); schema = string.Join("\n", rows);
        }
        return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) + "\n" + schema;
    }
    private static async Task<SessionStore> Open(string path) { var store = new SessionStore(path); await store.InitializeAsync(); return store; }
    private static async Task<SqliteConnection> Connect(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync(); return connection; }
    private static bool Near(double? value, double expected) => value.HasValue && Math.Abs(value.Value - expected) < 1e-12;
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
