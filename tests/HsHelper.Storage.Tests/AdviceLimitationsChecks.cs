using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class AdviceLimitationsChecks
{
    private static readonly string[] Names = ["Assumptions", "Unknowns", "UnsupportedMechanics", "HostRisks", "HostAssumptions", "HostUnknowns", "HostUnsupportedMechanics"];
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string database)
    {
        var root = Path.Combine(Path.GetDirectoryName(database)!, "advice-limitations");
        yield return ("structured archive limitations retain separate model and host categories after reopening", () => Durable(Path.Combine(root, "durable.db")));
        yield return ("limitation preparation owns and deeply freezes every caller collection", () => PrepareFreeze(Path.Combine(root, "prepare.db")));
        yield return ("all archive read paths return deeply read-only limitation collections", () => ReadPaths(Path.Combine(root, "read-paths.db")));
        yield return ("all new limitation fields receive the existing credential and source-text redaction", () => Redaction(Path.Combine(root, "redaction.db")));
        yield return ("model twelve host sixty-four and text two-thousand limits are inclusive", () => MaximumBounds(Path.Combine(root, "maximum.db")));
        yield return ("new limitation categories and truncation require exactly supported version one", () => VersionRules(Path.Combine(root, "versions.db")));
        yield return ("invalid counts and texts cannot enter any model or host category", () => InvalidTexts(Path.Combine(root, "invalid.db")));
        yield return ("persisted malformed limitation payloads are rejected without rewriting history", () => InvalidRead(Path.Combine(root, "invalid-read.db")));
        yield return ("legacy payload serialization and content-hash identity remain byte-for-byte stable", () => LegacyHash(Path.Combine(root, "legacy.db")));
        yield return ("null missing and explicitly empty limitation categories retain distinct identities", () => NullAndEmpty(Path.Combine(root, "null-empty.db")));
        yield return ("short-secret redaction stays bounded and re-preparation is hash-idempotent", () => Revalidation(Path.Combine(root, "revalidation.db")));
        yield return ("model-only and host-only limitations never fabricate the other source", () => NoSourceInference(Path.Combine(root, "sources.db")));
    }

    private static AdviceRecordDraft Draft(string summary = "structured baseline") => new("limitations-match", 8, 15, "255790", "hunter-v14/turn", summary)
    { DeckVersionId = "limitations-deck", Task = "Review" };
    private static AdviceRecordDraft Full(string summary = "structured baseline")
    {
        var result = Draft(summary) with { LimitationsVersion = 1, HostLimitationsTruncated = true, Risks = ["model risk"] };
        foreach (var name in Names) result = Set(result, name, [name + " one", name + " two"]);
        return result;
    }
    private static async Task Durable(string path)
    {
        var store = await Open(path); var saved = await store.SaveAsync(Full());
        var reopened = new AdviceArchiveStore(path); await reopened.InitializeAsync(); var loaded = (await reopened.GetAsync(saved.Id))!;
        Check(loaded.RecordedAt == saved.RecordedAt && JsonSerializer.Serialize(loaded.Advice) == JsonSerializer.Serialize(saved.Advice) &&
            loaded.Advice is { LimitationsVersion: 1, HostLimitationsTruncated: true }, "Reopening lost limitation version, flags, time or fields.");
        foreach (var name in Names) Check(Get(loaded.Advice, name)!.SequenceEqual([name + " one", name + " two"]), "A limitation category crossed source boundaries.");
        Check(loaded.Advice.Risks.Single() == "model risk" && loaded.Advice.HostRisks![0] == "HostRisks one", "Existing model Risks was merged into host risks.");
    }
    private static async Task PrepareFreeze(string path)
    {
        var originals = Names.ToDictionary(name => name, name => new List<string> { name + " original" });
        var input = Draft() with { LimitationsVersion = 1 };
        foreach (var name in Names) input = Set(input, name, originals[name]);
        var prepared = AdviceArchiveStore.Prepare(input);
        foreach (var name in Names) originals[name].Clear();
        foreach (var name in Names)
        {
            Check(Get(prepared, name)!.Single() == name + " original", "Preparation retained a caller collection alias.");
            await ReadOnly(Get(prepared, name)!);
        }
        Check(!File.Exists(path), "Prepare opened a database before queueing.");
        var store = await Open(path); var saved = await store.SaveAsync(prepared);
        Check(JsonSerializer.Serialize(saved.Advice) == JsonSerializer.Serialize(prepared), "Save revalidation changed prepared limitation data.");
    }
    private static async Task ReadPaths(string path)
    {
        var store = await Open(path); var saved = await store.SaveAsync(Full());
        var query = new AdviceConversationQuery(saved.Advice.MatchId, saved.Advice.DeckVersionId, saved.Advice.CardBuild, 8, 15, DateTimeOffset.MaxValue);
        var records = new[] { (await store.GetAsync(saved.Id))!, (await store.ListAsync()).Single(), (await store.ListPageAsync()).Records.Single(),
            (await store.ListPageAsOfAsync(saved.RecordedAt)).Records.Single(), (await store.GetConversationHistoryAsync(query)).Records.Single() };
        foreach (var record in records)
        foreach (var name in Names) await ReadOnly(Get(record.Advice, name)!);
        Check(records.All(record => JsonSerializer.Serialize(record) == JsonSerializer.Serialize(saved)), "An archive reader dropped or changed limitations.");
    }
    private static async Task Redaction(string path)
    {
        const string secret = "synthetic-limitations-credential";
        var input = Draft() with { LimitationsVersion = 1 };
        foreach (var name in Names) input = Set(input, name, [name + " " + secret + " Bearer synthetic-bearer-token sk-syntheticcredential123 https://private-fixture.invalid/path api_key=synthetic-key"]);
        var store = await Open(path); var prepared = AdviceArchiveStore.Prepare(input, [secret]); var saved = await store.SaveAsync(prepared);
        var json = JsonSerializer.Serialize((await store.GetAsync(saved.Id))!);
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        foreach (var value in new[] { secret, "synthetic-bearer-token", "sk-syntheticcredential123", "private-fixture.invalid", "synthetic-key" })
            Check(!json.Contains(value, StringComparison.Ordinal) && !raw.Contains(value, StringComparison.Ordinal), "A new limitation field bypassed established redaction.");
        foreach (var name in Names) Check(Get(saved.Advice, name)!.Single().Contains(name, StringComparison.Ordinal), "Redaction erased category attribution.");
    }
    private static async Task MaximumBounds(string path)
    {
        var input = Draft() with { LimitationsVersion = 1 };
        foreach (var name in Names) input = Set(input, name, Enumerable.Repeat(new string('界', 2000), Maximum(name)).ToArray());
        var store = await Open(path); var saved = await store.SaveAsync(input); var loaded = (await store.GetAsync(saved.Id))!;
        foreach (var name in Names) Check(Get(loaded.Advice, name)!.Count == Maximum(name) && Get(loaded.Advice, name)!.All(value => value.Length == 2000),
            "An inclusive count/text boundary was rejected, shortened or deduplicated.");
        Check(!loaded.Advice.HostLimitationsTruncated, "The archive invented host truncation for valid maximum inputs.");
    }
    private static async Task VersionRules(string path)
    {
        var store = await Open(path);
        foreach (var version in new int?[] { -1, 0, 2, int.MaxValue }) await Throws<ArgumentException>(() => store.SaveAsync(Draft() with { LimitationsVersion = version }));
        foreach (var name in Names) await Throws<ArgumentException>(() => store.SaveAsync(Set(Draft(), name, [])));
        await Throws<ArgumentException>(() => store.SaveAsync(Draft() with { HostLimitationsTruncated = true }));
        var versionOnly = await store.SaveAsync(Draft("version only") with { LimitationsVersion = 1 });
        var flagOnly = await store.SaveAsync(Draft("version and flag") with { LimitationsVersion = 1, HostLimitationsTruncated = true });
        Check(Names.All(name => Get(versionOnly.Advice, name) is null && Get(flagOnly.Advice, name) is null) && flagOnly.Advice.HostLimitationsTruncated &&
            (await store.ListAsync()).Count == 2, "Nullable version-one fields were filled in or inconsistent payloads were persisted.");
    }
    private static async Task InvalidTexts(string path)
    {
        var store = await Open(path);
        foreach (var name in Names)
        {
            await Throws<ArgumentException>(() => store.SaveAsync(Set(Draft() with { LimitationsVersion = 1, HostLimitationsTruncated = true }, name,
                Enumerable.Repeat("too many", Maximum(name) + 1).ToArray())));
            foreach (var text in new[] { null, "", "  ", new string('界', 2001), "invalid\0text", "HTTP/1.1 401 Unauthorized", "{\"choices\":[]}",
                "D 09:00:00.123 PowerTaskList.DebugPrintPower() - TAG_CHANGE Entity=synthetic" })
                await Throws<ArgumentException>(() => store.SaveAsync(Set(Draft() with { LimitationsVersion = 1 }, name, [text!])));
        }
        Check((await store.ListAsync()).Count == 0, "An invalid limitation category reached persistence.");
    }
    private static async Task InvalidRead(string path)
    {
        var store = await Open(path); var saved = await store.SaveAsync(Full()); var raw = await Raw(path, saved.Id);
        var changes = new Action<JsonObject>[]
        {
            node => node["LimitationsVersion"] = 2, node => node.Remove("LimitationsVersion"),
            node => node["Unknowns"] = JsonNode.Parse("[null]"), node => node["Assumptions"] = JsonSerializer.SerializeToNode(Enumerable.Repeat("x", 13).ToArray()),
            node => node["HostUnknowns"] = JsonSerializer.SerializeToNode(Enumerable.Repeat("x", 65).ToArray()),
            node => node["HostRisks"] = JsonSerializer.SerializeToNode(new[] { new string('x', 2001) })
        };
        foreach (var change in changes)
        {
            var node = JsonNode.Parse(raw.Json)!.AsObject(); change(node); var invalid = node.ToJsonString(); await Rewrite(path, saved.Id, invalid, Hash(invalid));
            var before = await Raw(path, saved.Id);
            await Throws<InvalidDataException>(() => store.GetAsync(saved.Id));
            await Throws<InvalidDataException>(() => store.ListPageAsync());
            await Throws<InvalidDataException>(() => store.GetConversationHistoryAsync(new("limitations-match", "limitations-deck", "255790", 8, 15, DateTimeOffset.MaxValue)));
            Check(await Raw(path, saved.Id) == before, "Rejected reading rewrote invalid history instead of preserving its source payload.");
            await Rewrite(path, saved.Id, raw.Json, raw.Hash);
        }
        Check(JsonSerializer.Serialize((await store.GetAsync(saved.Id))!.Advice) == JsonSerializer.Serialize(saved.Advice), "Restoring the raw fixture did not recover the unchanged valid record.");
    }
    private static async Task LegacyHash(string path)
    {
        const string legacy = """{"MatchId":"limitations-match","StateVersion":8,"EventSequence":15,"CardBuild":"255790","PromptTemplateVersion":"hunter-v14/turn","Summary":"legacy baseline","DeckVersionId":"limitations-deck","Task":"Review","UserQuestion":null,"Reasons":[],"Alternative":null,"Risks":[],"Verification":"strategy","VerifiedLethal":false,"Actions":[],"Evidence":[]}""";
        Check(JsonSerializer.Serialize(Draft("legacy baseline")) == legacy, "Null/default new fields changed the exact pre-limitations JSON representation.");
        var store = await Open(path); var saved = await store.SaveAsync(Draft("legacy baseline")); var raw = await Raw(path, saved.Id);
        Check(saved.Id == Hash(legacy) && raw == (legacy, Hash(legacy)), "The old payload's content hash or default identity changed.");
        var reopened = new AdviceArchiveStore(path); var restored = (await reopened.GetAsync(saved.Id))!;
        var repeated = await reopened.SaveAsync(restored.Advice);
        Check(restored.Advice.LimitationsVersion is null && !restored.Advice.HostLimitationsTruncated && Names.All(name => Get(restored.Advice, name) is null) &&
            repeated.Id == saved.Id && repeated.RecordedAt == saved.RecordedAt && await Raw(path, saved.Id) == raw && (await reopened.ListAsync()).Count == 1,
            "Legacy retry changed immutable hash/time or fabricated newly classified data.");
    }
    private static async Task NullAndEmpty(string path)
    {
        var store = await Open(path); var absent = Draft() with { LimitationsVersion = 1 }; var empty = absent;
        foreach (var name in Names) empty = Set(empty, name, []);
        var missing = await store.SaveAsync(absent); var explicitEmpty = await store.SaveAsync(empty);
        var truncated = await store.SaveAsync(absent with { HostLimitationsTruncated = true });
        Check(new[] { missing.Id, explicitEmpty.Id, truncated.Id }.Distinct().Count() == 3, "Distinct null/empty/truncation statements shared a content identity.");
        var missingNode = JsonNode.Parse((await Raw(path, missing.Id)).Json)!.AsObject(); var emptyNode = JsonNode.Parse((await Raw(path, explicitEmpty.Id)).Json)!.AsObject();
        foreach (var name in Names)
            Check(!missingNode.ContainsKey(name) && emptyNode[name] is JsonArray { Count: 0 } && Get((await store.GetAsync(missing.Id))!.Advice, name) is null &&
                Get((await store.GetAsync(explicitEmpty.Id))!.Advice, name) is { Count: 0 }, "Missing/null or explicit empty categories lost their different meanings.");
        Check(!missingNode.ContainsKey("HostLimitationsTruncated") && JsonNode.Parse((await Raw(path, truncated.Id)).Json)!["HostLimitationsTruncated"]!.GetValue<bool>(),
            "The default truncation flag altered legacy JSON or a real truncation flag disappeared.");
    }
    private static async Task Revalidation(string path)
    {
        var store = await Open(path); var input = Draft() with { LimitationsVersion = 1, HostLimitationsTruncated = true };
        foreach (var name in Names) input = Set(input, name, [new string('x', 2000)]);
        var prepared = AdviceArchiveStore.Prepare(input, ["x"]); var again = AdviceArchiveStore.Prepare(prepared);
        Check(Names.All(name => Get(prepared, name)!.Single().Length <= 2000 && !Get(prepared, name)!.Single().Contains('x')) &&
            JsonSerializer.Serialize(prepared) == JsonSerializer.Serialize(again), "Redaction expanded a new text field beyond its limit or preparation was not stable.");
        var saved = await store.SaveAsync(prepared); var repeated = await store.SaveAsync((await store.GetAsync(saved.Id))!.Advice);
        Check(repeated.Id == saved.Id && repeated.RecordedAt == saved.RecordedAt && (await store.ListAsync()).Count == 1,
            "Revalidation changed a structured record's hash identity.");
        foreach (var name in Names.Where(name => name.StartsWith("Host", StringComparison.Ordinal)))
        {
            var clipped = AdviceArchiveStore.Prepare(Set(Draft(name + " clipped") with { LimitationsVersion = 1 }, name, [new string('a', 1990) + "qTAIL"]), ["q"]);
            Check(clipped.HostLimitationsTruncated && Get(clipped, name)!.Single().Length == 2000 && !Get(clipped, name)!.Single().EndsWith("TAIL", StringComparison.Ordinal),
                "Host text clipped after short-secret redaction did not set the host truncation flag.");
            var stored = await store.SaveAsync(clipped); var retried = await store.SaveAsync((await store.GetAsync(stored.Id))!.Advice);
            Check(retried.Id == stored.Id && retried.RecordedAt == stored.RecordedAt && retried.Advice.HostLimitationsTruncated,
                "Redaction clipping or its host flag changed on save/read/retry.");
        }
        var shortHost = AdviceArchiveStore.Prepare(Draft() with { LimitationsVersion = 1, HostUnknowns = ["short q tail"] }, ["q"]);
        var modelOnly = AdviceArchiveStore.Prepare(Draft() with { LimitationsVersion = 1, Assumptions = [new string('a', 1990) + "qTAIL"] }, ["q"]);
        var legacyOnly = AdviceArchiveStore.Prepare(Draft() with { Risks = [new string('a', 1990) + "qTAIL"] }, ["q"]);
        Check(!shortHost.HostLimitationsTruncated && !modelOnly.HostLimitationsTruncated && !legacyOnly.HostLimitationsTruncated && legacyOnly.LimitationsVersion is null,
            "Non-clipping host text or clipping in a model/legacy field fabricated host truncation metadata.");
    }
    private static async Task NoSourceInference(string path)
    {
        var store = await Open(path); var model = Draft("model only") with { LimitationsVersion = 1, Assumptions = ["model assumption"], Unknowns = [], UnsupportedMechanics = ["model unsupported"], Risks = ["model risk"] };
        var host = Draft("host only") with { LimitationsVersion = 1, HostRisks = [], HostAssumptions = ["host assumption"], HostUnknowns = [], HostUnsupportedMechanics = ["host unsupported"], HostLimitationsTruncated = true };
        var one = (await store.GetAsync((await store.SaveAsync(model)).Id))!.Advice; var two = (await store.GetAsync((await store.SaveAsync(host)).Id))!.Advice;
        Check(Names.Where(name => name.StartsWith("Host", StringComparison.Ordinal)).All(name => Get(one, name) is null) && !one.HostLimitationsTruncated &&
            two.Assumptions is null && two.Unknowns is null && two.UnsupportedMechanics is null && two.Risks.Count == 0 && two.HostRisks is { Count: 0 } &&
            two.HostUnknowns is { Count: 0 } && one.Unknowns is { Count: 0 }, "One source's fields were copied into the other source or unknown categories became empty.");
    }

    private static int Maximum(string name) => name.StartsWith("Host", StringComparison.Ordinal) ? 64 : 12;
    private static AdviceRecordDraft Set(AdviceRecordDraft draft, string name, IReadOnlyList<string>? values) => name switch
    {
        "Assumptions" => draft with { Assumptions = values }, "Unknowns" => draft with { Unknowns = values }, "UnsupportedMechanics" => draft with { UnsupportedMechanics = values },
        "HostRisks" => draft with { HostRisks = values }, "HostAssumptions" => draft with { HostAssumptions = values }, "HostUnknowns" => draft with { HostUnknowns = values },
        "HostUnsupportedMechanics" => draft with { HostUnsupportedMechanics = values }, _ => throw new ArgumentException("Unknown test category")
    };
    private static IReadOnlyList<string>? Get(AdviceRecordDraft draft, string name) => name switch
    {
        "Assumptions" => draft.Assumptions, "Unknowns" => draft.Unknowns, "UnsupportedMechanics" => draft.UnsupportedMechanics, "HostRisks" => draft.HostRisks,
        "HostAssumptions" => draft.HostAssumptions, "HostUnknowns" => draft.HostUnknowns, "HostUnsupportedMechanics" => draft.HostUnsupportedMechanics, _ => throw new ArgumentException("Unknown test category")
    };
    private static async Task ReadOnly(IReadOnlyList<string> values) => await Throws<NotSupportedException>(() => { ((IList<string>)values).Add("caller mutation"); return Task.CompletedTask; });
    private static async Task<AdviceArchiveStore> Open(string path) { var store = new AdviceArchiveStore(path); await store.InitializeAsync(); return store; }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task<(string Json, string Hash)> Raw(string path, string id)
    {
        await using var connection = await Connect(path); await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload_json,content_hash FROM coach_advice_records WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(); Check(await reader.ReadAsync(), "Missing raw archive fixture."); return (reader.GetString(0), reader.GetString(1));
    }
    private static async Task Rewrite(string path, string id, string json, string hash)
    {
        await using var connection = await Connect(path); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE coach_advice_records SET payload_json=$json,content_hash=$hash WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$hash", hash); await command.ExecuteNonQueryAsync();
    }
    private static async Task<SqliteConnection> Connect(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync(); return connection; }
    private static async Task Throws<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
}
