using System.Text;
using System.Text.Json;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

static class AdviceArchiveChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "advice");
        yield return ("completed advice retains fixed binding and deep immutable evidence", () => Durable(Path.Combine(root, "durable.db")));
        yield return ("advice archives reject overwrite and deduplicate concurrent saves", () => Idempotent(Path.Combine(root, "idempotent.db")));
        yield return ("archived advice time and sequence cutoffs exclude later analyses", () => AsOf(Path.Combine(root, "asof.db")));
        yield return ("advice archival redacts provided credentials common secrets and addresses", () => Redaction(Path.Combine(root, "redaction.db")));
        yield return ("raw HTTP bodies and source log lines never enter advice archives", () => RawBodies(Path.Combine(root, "raw.db")));
        yield return ("archive validation cancellation and future schemas preserve records", () => Invalid(Path.Combine(root, "invalid.db")));
        yield return ("archive preparation validates and freezes before any persistence", () => Prepare(Path.Combine(root, "prepare.db")));
        yield return ("end turn archives preserve player sources and legacy source-less records", () => EndTurnSources(Path.Combine(root, "end-turn.db")));
        yield return ("short credentials cannot expand prepared records past archive limits", () => ShortCredentials(Path.Combine(root, "short-credentials.db")));
    }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static AdviceRecordDraft Draft(string summary = "优先处理场面") => new("match-a", 8, 15, "255790", "hunter-v2", summary)
    {
        DeckVersionId = "deck-a", Task = "回合分析", Reasons = ["可在当前规则覆盖下完成交换"], Risks = ["对手手牌未知"],
        Verification = "verified", VerifiedLethal = false, Actions = [new("attack", 10, 20)],
        Evidence = [new("evaluate_line", "已验证：1个攻击动作", new string('A', 64))]
    };
    private static async Task Durable(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        var reasons = new List<string> { "初始依据" }; var evidence = new List<AdviceToolEvidence> { new("find_lethal", "未在有界搜索中找到斩杀") };
        var input = Draft() with { Reasons = reasons, Evidence = evidence };
        var saved = await store.SaveAsync(input);
        reasons[0] = "写入后修改"; evidence.Clear();
        var reopened = new AdviceArchiveStore(path); await reopened.InitializeAsync();
        var restored = (await reopened.GetAsync(saved.Id))!;
        Check(restored.RecordedAt == saved.RecordedAt && restored.Advice is { MatchId: "match-a", StateVersion: 8, EventSequence: 15, CardBuild: "255790", PromptTemplateVersion: "hunter-v2", DeckVersionId: "deck-a", Verification: "verified" } &&
            restored.Advice.Reasons.Single() == "初始依据" && restored.Advice.Evidence.Single().ToolName == "find_lethal", "Archive lost binding or retained caller-owned mutable lists.");
        await Throws<NotSupportedException>(() => { ((IList<string>)restored.Advice.Reasons).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<ArchivedAdviceAction>)restored.Advice.Actions).Add(new("end_turn", null)); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<AdviceToolEvidence>)restored.Advice.Evidence).Clear(); return Task.CompletedTask; });
        Check((await reopened.SaveAsync(saved.Advice)).Id == saved.Id, "Identical advice did not retain idempotent identity.");
    }
    private static async Task Idempotent(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync(); var saved = await store.SaveAsync(Draft(), "fixed-advice-id");
        await Throws<InvalidDataException>(() => store.SaveAsync(Draft("不能覆盖的另一条建议"), saved.Id));
        Check((await store.GetAsync(saved.Id))!.Advice.Summary == saved.Advice.Summary, "Conflicting archive overwrote advice.");
        var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new AdviceArchiveStore(path).SaveAsync(Draft("并发相同建议")))));
        Check(repeated.Select(v => v.Id).Distinct().Count() == 1 && (await store.ListAsync()).Count == 2, "Concurrent retry duplicated advice.");
    }
    private static async Task AsOf(string path)
    {
        var clock = new StorageManualClock(); var store = new AdviceArchiveStore(path, clock); await store.InitializeAsync();
        var initial = await store.SaveAsync(Draft("当时的建议"));
        clock.Now = clock.Now.AddHours(1); await store.SaveAsync(Draft("后来重新分析同一旧局面"));
        await store.SaveAsync(Draft("之后的局面建议") with { StateVersion = 9, EventSequence = 20 });
        await store.SaveAsync(Draft("另一套牌版本") with { DeckVersionId = "deck-b" });
        var earlier = await store.ListAsync(new(MatchId: "match-a", StateVersion: 8, ThroughEventSequence: 15, CardBuild: "255790", DeckVersionId: "deck-a", RecordedBefore: initial.RecordedAt));
        Check(earlier.Count == 1 && earlier[0].Id == initial.Id, "Later analysis or future state leaked through archive cutoff.");
        Check((await store.ListAsync(new(MatchId: "match-a", StateVersion: 8, ThroughEventSequence: 15, DeckVersionId: "deck-a"))).Count == 2, "Exact old-state review lost later analysis records.");
        clock.Now = initial.RecordedAt.AddYears(-1);
        var afterRollback = await store.SaveAsync(Draft("回退时钟之后保存"));
        Check(afterRollback.RecordedAt > initial.RecordedAt && (await store.ListAsync(new(RecordedBefore: initial.RecordedAt))).Count == 1, "Clock rollback made a future archive appear in the past.");
    }
    private static async Task Redaction(string path)
    {
        const string secret = "opaque-config-credential";
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        var saved = await store.SaveAsync(Draft($"建议；凭据 {secret}；Bearer opaque-provider-token；https://private.example/path?api_key=x") with
        {
            Reasons = ["检查 sk-abcdefgh123456"], Risks = ["api_key=unknown-private-token"],
            Evidence = [new("evaluate_line", $"依据信息 {secret}")]
        }, sensitiveValues: new[] { secret });
        var json = JsonSerializer.Serialize(saved);
        var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        foreach (var value in new[] { secret, "opaque-provider-token", "private.example", "sk-abcdefgh123456", "unknown-private-token" })
            Check(!json.Contains(value, StringComparison.Ordinal) && !bytes.Contains(value, StringComparison.Ordinal), "Sensitive text reached advice archive.");
        Check(saved.Advice.Summary.Contains("建议"), "Redaction erased useful advice text.");
    }
    private static async Task RawBodies(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        foreach (var raw in new[] { "HTTP/1.1 401 Unauthorized\nsecret", "{\"choices\":[{\"message\":\"raw\"}]}", "{\"error\":{\"message\":\"private body\"}}", "D 09:00:00.123 PowerTaskList.DebugPrintPower() - TAG_CHANGE Entity=private-name" })
        {
            await Throws<ArgumentException>(() => store.SaveAsync(Draft(raw)));
            await Throws<ArgumentException>(() => store.SaveAsync(Draft() with { Evidence = [new("get_snapshot", raw)] }));
        }
        Check((await store.ListAsync()).Count == 0, "Raw provider/source material reached archive.");
    }
    private static async Task Invalid(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        foreach (var invalid in new[] { Draft() with { StateVersion = -1 }, Draft() with { Verification = "strategy" }, Draft() with { VerifiedLethal = true, Actions = [] }, Draft() with { CardBuild = "https://private.example" }, Draft() with { Actions = [new("click", 10)] } })
            await Throws<ArgumentException>(() => store.SaveAsync(invalid));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => store.SaveAsync(Draft(), cancellationToken: cancellation.Token));
        Check((await store.ListAsync()).Count == 0, "Rejected/cancelled advice reached archive.");
        var future = path + ".future";
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = future, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE advice_archive_schema(version INTEGER NOT NULL);INSERT INTO advice_archive_schema VALUES(99)";
        await command.ExecuteNonQueryAsync();
        await Throws<InvalidDataException>(() => new AdviceArchiveStore(future).InitializeAsync());
    }
    private static async Task Prepare(string path)
    {
        const string secret = "opaque-prepare-credential";
        var reasons = new List<string> { "依据 " + secret };
        var prepared = AdviceArchiveStore.Prepare(Draft("建议 " + secret) with { Reasons = reasons }, new[] { secret });
        reasons[0] = "调用方后续修改";
        Check(prepared.Reasons.Single().Contains("已隐藏") && !JsonSerializer.Serialize(prepared).Contains(secret, StringComparison.Ordinal),
            "Preparation retained secret or caller-owned mutable content.");
        await Throws<NotSupportedException>(() => { ((IList<string>)prepared.Reasons).Clear(); return Task.CompletedTask; });
        await Throws<NotSupportedException>(() => { ((IList<AdviceToolEvidence>)prepared.Evidence).Clear(); return Task.CompletedTask; });
        foreach (var invalid in new[] { Draft("bad\0summary"), Draft("{\"choices\":[]}"), Draft() with { Evidence = [new("evaluate_line", new string('x', 1001))] } })
            await Throws<ArgumentException>(() => { AdviceArchiveStore.Prepare(invalid); return Task.CompletedTask; });
        Check(!File.Exists(path), "Preparation opened or created a database before queueing.");
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        var saved = await store.SaveAsync(prepared);
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(prepared), "Save revalidation changed a prepared immutable record.");
    }
    private static async Task EndTurnSources(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        foreach (var source in new int?[] { null, 42 })
        {
            var draft = Draft() with { Actions = [new("end_turn", source)] };
            var saved = await store.SaveAsync(draft);
            Check((await store.GetAsync(saved.Id))!.Advice.Actions.Single() == new ArchivedAdviceAction("end_turn", source),
                "End-turn player source or legacy source-less action did not survive archival.");
            Check((await store.SaveAsync(draft)).Id == saved.Id, "End-turn source changed the idempotent saved identity.");
        }
        foreach (var action in new ArchivedAdviceAction[] { new("end_turn", 0), new("end_turn", -1), new("end_turn", 42, 99), new("end_turn", null, 99), new("attack", null, 99), new("play", 0) })
            await Throws<ArgumentException>(() => store.SaveAsync(Draft() with { Actions = [action] }));
        Check((await store.ListAsync()).Count == 2, "Invalid action shapes entered the archive.");
    }
    private static async Task ShortCredentials(string path)
    {
        var store = new AdviceArchiveStore(path); await store.InitializeAsync();
        foreach (var secret in new[] { "x", "[", "隐", "Bearer", "http" })
        {
            var draft = Draft(string.Concat(Enumerable.Repeat(secret, 2000 / secret.Length))) with
            {
                Reasons = [string.Concat(Enumerable.Repeat(secret, 2000 / secret.Length))],
                Alternative = string.Concat(Enumerable.Repeat(secret, 2000 / secret.Length)),
                Risks = [string.Concat(Enumerable.Repeat(secret, 2000 / secret.Length))],
                Task = string.Concat(Enumerable.Repeat(secret, 80 / secret.Length)),
                Evidence = [new("evaluate_line", string.Concat(Enumerable.Repeat(secret, 1000 / secret.Length)))]
            };
            var prepared = AdviceArchiveStore.Prepare(draft, new[] { secret });
            Check(prepared.Summary.Length <= 2000 && prepared.Reasons[0].Length <= 2000 && prepared.Alternative!.Length <= 2000 &&
                prepared.Risks[0].Length <= 2000 && prepared.Task.Length <= 80 && prepared.Evidence[0].Summary.Length <= 1000,
                "Exact redaction expanded a prepared field beyond its accepted limit.");
            Check(!prepared.Summary.Contains(secret, StringComparison.Ordinal), "Credential remained inside its own generated redaction marker.");
            var repeated = AdviceArchiveStore.Prepare(prepared, new[] { secret });
            Check(JsonSerializer.Serialize(repeated) == JsonSerializer.Serialize(prepared), "Repeated preparation changed redacted short-token content.");
            var saved = await store.SaveAsync(prepared, sensitiveValues: new[] { secret });
            Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(prepared), "Prepared short-token record failed save revalidation or changed after queueing.");
        }
        var conventional = AdviceArchiveStore.Prepare(Draft(new string('x', 2000)), new[] { "x" });
        Check(JsonSerializer.Serialize((await store.SaveAsync(conventional)).Advice) == JsonSerializer.Serialize(conventional),
            "Saving pre-redacted text without resupplying an ordinary short credential changed content.");
    }
}
