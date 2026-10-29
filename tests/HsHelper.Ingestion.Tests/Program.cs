using System.Text;
using HsHelper.Ingestion;

var testRoot = Path.Combine(Directory.GetCurrentDirectory(), ".local", "ingestion-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var tests = new (string Name, Func<Task> Run)[]
{
    ("configuration preview preserves unrelated settings and is idempotent", ConfigurationPreview),
    ("configuration apply restore and optimistic guard", ConfigurationApply),
    ("discovery finds nested sessions and respects explicit log paths", Discovery),
    ("UTF8 partial lines and appended data are emitted once", PartialTail),
    ("truncation replacement and session rotation reset reader", Rotation),
    ("same-header rewrite is detected at the read checkpoint", SameHeaderRewrite),
    ("shared replay pins complete lines while a writer appends", SharedReplay),
    ("client build comes from game metadata instead of Unity version", BuildMetadata),
    ("watch supports cancellation", Cancellation)
};
var failed = 0;
try
{
    foreach (var test in tests)
    {
        try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
    }
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}
Console.WriteLine($"Ingestion: {tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

Task ConfigurationPreview()
{
    var path = In("config-preview", "log.config");
    var original = "; keep comment\r\n[Custom]\r\nAnswer=42\r\n[Power]\r\nLogLevel=0 ; original\r\nFilePrinting=False\r\nVerbose=False\r\nExtra=keep\r\n";
    File.WriteAllText(path, original);
    var plan = LogConfiguration.CreatePlan(path);
    Check(File.ReadAllText(path) == original, "preview wrote configuration");
    Check(plan.ProposedText.Contains("[Custom]\r\nAnswer=42") && plan.ProposedText.Contains("Extra=keep"), "custom settings lost");
    Check(LogConfiguration.IsPowerLoggingEnabled(plan.ProposedText), "power logging not enabled");
    File.WriteAllText(path, plan.ProposedText);
    Check(!LogConfiguration.CreatePlan(path).HasChanges, "preview is not idempotent");
    return Task.CompletedTask;
}

async Task ConfigurationApply()
{
    var path = In("config-apply", "log.config");
    var originalBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[Other]\r\nKey=值\r\n")).ToArray();
    File.WriteAllBytes(path, originalBytes);
    var plan = LogConfiguration.CreatePlan(path);
    var backup = await LogConfiguration.ApplyAsync(plan) ?? throw new Exception("backup missing");
    Check(File.Exists(backup.BackupPath), "backup file missing");
    await LogConfiguration.RestoreAsync(backup);
    Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "restore did not preserve original bytes");
    File.WriteAllText(path, "changed");
    await Throws<InvalidOperationException>(() => LogConfiguration.ApplyAsync(plan));
    File.Delete(path);
    var created = await LogConfiguration.ApplyAsync(LogConfiguration.CreatePlan(path)) ?? throw new Exception("new config result missing");
    File.AppendAllText(path, "\n; external edit");
    await Throws<InvalidOperationException>(() => LogConfiguration.RestoreAsync(created));
    File.WriteAllText(path, LogConfiguration.CreatePlan(In("new-config-source", "log.config")).ProposedText);
    await LogConfiguration.RestoreAsync(created);
    Check(!File.Exists(path), "restoring new config should remove it");
}

Task Discovery()
{
    var root = InDirectory("discovery");
    var old = In("discovery", "Logs", "old", "Power.log");
    var latest = In("discovery", "Logs", "new", "nested", "Power.log");
    File.WriteAllText(old, "old\n"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
    File.WriteAllText(latest, "new\n");
    Check(LogSessionDiscovery.FindLatestPowerLog(root)?.Path == latest, "newest nested session not discovered");
    Check(LogSessionDiscovery.FindLatestPowerLog(old)?.Path == old, "explicit path not respected");
    Check(HearthstoneDiagnostics.Inspect(root, In("missing-config", "log.config")).PowerLog?.Path == latest, "manual log root unavailable in diagnostics");
    return Task.CompletedTask;
}

async Task PartialTail()
{
    var path = In("partial", "Power.log");
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("first\r\n中文\nlast")).ToArray();
    File.WriteAllBytes(path, []);
    using var reader = new PowerLogTailer(2);
    var emitted = new List<string>();
    for (var index = 0; index < bytes.Length; index++)
    {
        await using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await writer.WriteAsync(bytes.AsMemory(index, 1));
        var batch = await reader.ReadAvailableAsync(path);
        emitted.AddRange(batch.Lines);
    }
    Check(emitted.SequenceEqual(new[] { "first", "中文" }), "split UTF8 or pending line damaged");
    Check((await reader.ReadAvailableAsync(path)).Lines.Count == 0, "lines duplicated without append");
    File.AppendAllText(path, "\n");
    Check((await reader.ReadAvailableAsync(path)).Lines.SequenceEqual(new[] { "last" }), "pending last line missing");
}

async Task Rotation()
{
    var root = InDirectory("rotation");
    var path = In("rotation", "Logs", "session1", "Power.log");
    File.WriteAllText(path, "old-long-line\n");
    using var reader = new PowerLogTailer();
    Check((await reader.ReadAvailableAsync(root)).ResetReason == LogResetReason.InitialFile, "initial event missing");
    File.WriteAllText(path, "x\n");
    var truncated = await reader.ReadAvailableAsync(root);
    Check(truncated.ResetReason == LogResetReason.Truncated && truncated.Lines.SequenceEqual(new[] { "x" }), "truncation not handled");
    File.WriteAllText(path, "replacement-is-longer\n");
    var replaced = await reader.ReadAvailableAsync(root);
    Check(replaced.ResetReason == LogResetReason.Replaced && replaced.Lines.Count == 1, "same-path rewrite not detected");
    var newer = In("rotation", "Logs", "session2", "nested", "Power.log");
    File.WriteAllText(newer, "next-session\n");
    File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddSeconds(3));
    var rotated = await reader.ReadAvailableAsync(root);
    Check(rotated.ResetReason == LogResetReason.SessionChanged && rotated.Lines.SequenceEqual(new[] { "next-session" }), "rotation not handled");
}

async Task Cancellation()
{
    using var reader = new PowerLogTailer();
    using var source = new CancellationTokenSource();
    source.Cancel();
    await Throws<OperationCanceledException>(() => reader.ReadAvailableAsync(testRoot, source.Token));
    using var watching = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
    await Throws<OperationCanceledException>(async () =>
    {
        await foreach (var batch in reader.WatchAsync(InDirectory("empty-watch"), TimeSpan.FromMilliseconds(5), watching.Token))
            throw new InvalidOperationException("Empty directory unexpectedly emitted a batch.");
    });
}

async Task SameHeaderRewrite()
{
    var path = In("same-header", "Power.log");
    var header = new string('h', 80) + "\n";
    File.WriteAllText(path, header + "old body\n");
    var created = File.GetCreationTimeUtc(path);
    using var reader = new PowerLogTailer();
    await reader.ReadAvailableAsync(path);
    File.WriteAllText(path, header + "new replacement body\n");
    File.SetCreationTimeUtc(path, created);
    var batch = await reader.ReadAvailableAsync(path);
    Check(batch.ResetReason == LogResetReason.Replaced, "rewrite with identical header missed");
    Check(batch.Lines.SequenceEqual(new[] { new string('h', 80), "new replacement body" }), "replacement not read from beginning");
}

async Task SharedReplay()
{
    var path = In("shared-replay", "Power.log");
    File.WriteAllText(path, "first\n中文\npartial");
    await using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    await using var replay = SharedLogReplay.ReadLinesAsync(path).GetAsyncEnumerator();
    Check(await replay.MoveNextAsync() && replay.Current == "first", "active shared log could not be opened");
    await writer.WriteAsync(Encoding.UTF8.GetBytes("\nnewly-appended\n"));
    await writer.FlushAsync();
    var lines = new List<string>();
    while (await replay.MoveNextAsync()) lines.Add(replay.Current);
    Check(lines.SequenceEqual(new[] { "中文" }), "replay read beyond its original complete-line boundary");
    var empty = In("shared-replay", "empty", "Power.log");
    File.WriteAllText(empty, "unterminated");
    await foreach (var unexpected in SharedLogReplay.ReadLinesAsync(empty))
        throw new Exception("Unterminated line was emitted: " + unexpected);
}

Task BuildMetadata()
{
    var directory = InDirectory("build-info");
    File.WriteAllText(Path.Combine(directory, ".build.info"),
        "Active!DEC:1|Product!STRING:0|Version!STRING:0\n0|hs|36.0.0.111111\n1|wow|12.0.0.222222\n1|hs|37.0.0.255790\n");
    Check(ClientBuildMetadata.ReadBuildInfoVersion(directory) == "37.0.0.255790", "active Hearthstone version not selected");
    var log = In("build-info", "Power.log");
    File.WriteAllText(log, "D 12:00:00.0 GameState.DebugPrintGame() - BuildNumber=255790\n");
    using var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    Check(ClientBuildMetadata.ReadPowerLogBuild(log) == "255790", "shared game log build unavailable");
    var report = HearthstoneDiagnostics.Inspect(directory, In("build-info", "missing.config"));
    Check(report.ObservedLogBuild == "255790", "diagnostic lacks observed log build");
    return Task.CompletedTask;
}

string InDirectory(params string[] parts)
{
    var directory = Path.Combine(new[] { testRoot }.Concat(parts).ToArray());
    Directory.CreateDirectory(directory);
    return directory;
}
string In(params string[] parts)
{
    var path = Path.Combine(new[] { testRoot }.Concat(parts).ToArray());
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    return path;
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
