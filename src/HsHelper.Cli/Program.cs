using System.Text.Json;
using System.Text.Json.Serialization;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Ingestion;

return await MainAsync(args);
static async Task<int> MainAsync(string[] args)
{
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("""
            hs-helper — Windows 国服日志与卡库实验版
            diagnose [--game-dir path] [--config path]
            config-preview [--config path]
            replay --log path [--player 1|2] [--out snapshot.json]
            watch --log-root path [--player 1|2]
            cards-sync [--build latest|number] [--data-dir path]
            cards-import --json path --build number [--data-dir path]
            cards-search [--query text] [--class HUNTER] [--build number] [--data-dir path]
            deck-decode --code base64

            diagnose 与 config-preview 只读，不修改游戏配置。省略 --player 时屏蔽隐藏区身份。
            """);
        return 0;
    }
    try
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length || !opts.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("参数必须是唯一的 --名称 值 对。");
        }
        string? Option(string key) => opts.GetValueOrDefault("--" + key);
        string Required(string key) => Option(key) ?? throw new ArgumentException($"缺少 --{key}");
        int? Player()
        {
            var text = Option("player");
            if (text is null) return null;
            return text is "1" or "2" ? int.Parse(text) : throw new ArgumentException("--player 需要为1或2。");
        }
        void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
        switch (args[0])
        {
            case "diagnose": Print(HearthstoneDiagnostics.Inspect(Option("game-dir"), Option("config"))); return 0;
            case "config-preview":
                Print(LogConfiguration.CreatePlan(Option("config") ?? HearthstoneDiagnostics.DefaultConfigurationPath)); return 0;
            case "replay":
            {
                var logPath = Path.GetFullPath(Required("log"));
                if (Option("out") is { } destination && Path.GetFullPath(destination).Equals(logPath, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("输出文件不能覆盖原始日志。");
                var engine = new PowerLogStateEngine(Player(), "offline-replay");
                await foreach (var line in SharedLogReplay.ReadLinesAsync(logPath)) engine.ProcessLine(line);
                var snapshot = engine.Snapshot();
                if (Option("out") is { } output) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(snapshot, jsonOptions));
                else Print(snapshot);
                return 0;
            }
            case "watch":
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                using var tailer = new PowerLogTailer();
                var engine = new PowerLogStateEngine(Player());
                await foreach (var batch in tailer.WatchAsync(Required("log-root"), TimeSpan.FromSeconds(1), cancellation.Token))
                {
                    if (batch.ResetReason != LogResetReason.None) engine.ResetSession();
                    foreach (var line in batch.Lines) engine.ProcessLine(line);
                    if (batch.Lines.Count > 0) Print(engine.Snapshot());
                }
                return 0;
            }
            case "deck-decode": Print(DeckCode.Decode(Required("code"))); return 0;
            case "cards-sync":
            case "cards-import":
            case "cards-search":
            {
                var dataRoot = Option("data-dir") ?? Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper");
                var catalog = new CardCatalog(Path.Combine(dataRoot, "cards.db"));
                await catalog.InitializeAsync();
                if (args[0] == "cards-import")
                {
                    using var file = File.OpenRead(Required("json"));
                    Print(await catalog.ImportAsync(file, Required("build")));
                }
                else if (args[0] == "cards-sync")
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    Print(await catalog.SyncAsync(http, Option("build") ?? "latest"));
                }
                else Print(await catalog.SearchAsync(new CardSearchQuery(Option("query"), Option("class")), Option("build")));
                return 0;
            }
            default: throw new ArgumentException("未知命令，运行 help 查看用法。");
        }
    }
    catch (OperationCanceledException) { return 130; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
