using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.Agent;
using HsHelper.App;
using HsHelper.Core;
using HsHelper.Storage;
using Microsoft.Data.Sqlite;

public static class AdviceLimitationsChecks
{
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("advice separates recorded host classifications from model opinions and mixed risks", Sources);
        yield return ("advice distinguishes unprovided classifications from explicitly empty lists", MissingAndEmpty);
        yield return ("legacy advice retains unknown limitation sources without guessing categories", Legacy);
        yield return ("overlay puts bounded local boundaries before long model text", OverlayPriority);
        yield return ("verified actions can have unlisted categories without being described as unvalidated", VerifiedPartial);
        yield return ("advice explicitly marks truncated local lists and retains their last recorded entry", Truncation);
        yield return ("limitation rendering is read-only and overlay clipping preserves surrogate pairs", UnicodeAndReadOnly);
    }
    private static AdviceRecordDraft Draft() => new("limit-render", 7, 11, "255790", "test/limits", "MODEL_SUMMARY")
    {
        LimitationsVersion = 1, Risks = ["COMPAT_MIXED"], Assumptions = ["MODEL_CONDITION"], Unknowns = ["MODEL_UNKNOWN"],
        UnsupportedMechanics = ["MODEL_UNCOVERED"], HostRisks = ["HOST_RISK"], HostAssumptions = ["HOST_CONDITION"],
        HostUnknowns = ["HOST_UNKNOWN"], HostUnsupportedMechanics = ["HOST_UNCOVERED"]
    };
    private static string Section(string text, string from, string to) => text[text.IndexOf(from, StringComparison.Ordinal)..text.IndexOf(to, StringComparison.Ordinal)];
    private static void Sources()
    {
        var text = AdviceLimitationsPresentation.Render(Draft());
        var host = Section(text, "本地规则限制", "模型意见（未经"); var model = Section(text, "模型意见（未经", "风险摘要（");
        Check(new[] { "HOST_RISK", "HOST_CONDITION", "HOST_UNKNOWN", "HOST_UNCOVERED" }.All(host.Contains) && !host.Contains("MODEL_", StringComparison.Ordinal) &&
            new[] { "MODEL_CONDITION", "MODEL_UNKNOWN", "MODEL_UNCOVERED" }.All(model.Contains) && !model.Contains("HOST_", StringComparison.Ordinal) &&
            text.Contains("本地规则与模型意见汇总", StringComparison.Ordinal) && !host.Contains("COMPAT_MIXED", StringComparison.Ordinal),
            "Display merged model opinions into local classifications or inferred a mixed risk's source.");
    }
    private static void MissingAndEmpty()
    {
        var missing = Draft() with { HostRisks = null, HostAssumptions = null, HostUnknowns = null, HostUnsupportedMechanics = null, Assumptions = null, Unknowns = null, UnsupportedMechanics = null };
        var empty = missing with { HostRisks = [], HostAssumptions = [], HostUnknowns = [], HostUnsupportedMechanics = [], Assumptions = [], Unknowns = [], UnsupportedMechanics = [] };
        var nullText = AdviceLimitationsPresentation.Render(missing); var emptyText = AdviceLimitationsPresentation.Render(empty);
        Check(nullText.Contains("最终动作校验限制未提供", StringComparison.Ordinal) && nullText.Contains("未单列", StringComparison.Ordinal) &&
            emptyText.Contains("未列出", StringComparison.Ordinal) && !emptyText.Contains("最终动作校验限制未提供", StringComparison.Ordinal) &&
            emptyText.Contains("未列出不表示不存在", StringComparison.Ordinal) && nullText != emptyText,
            "Null and empty lists were displayed as the same complete absence of limitations.");
    }
    private static AdviceRecordDraft LegacyDraft() => new("legacy-limits", 1, 2, "111111", "old-template", "旧建议")
    { Task = null!, Risks = ["LEGACY_FLAT_RISK"] };
    private static void Legacy()
    {
        var text = AdviceArchiveWindow.Render(new("old-record", At, LegacyDraft()));
        Check(text.Contains("旧记录未区分本地与模型限制", StringComparison.Ordinal) && text.Contains("原风险（来源未区分）", StringComparison.Ordinal) &&
            text.Contains("LEGACY_FLAT_RISK", StringComparison.Ordinal) && !text.Contains("本地规则限制", StringComparison.Ordinal) &&
            text.Contains("旧记录未保存来源", StringComparison.Ordinal) && text.Contains("未记录任务", StringComparison.Ordinal),
            "Legacy null fields acquired invented classifications/provenance or the renderer rejected a missing task.");
    }
    private static void OverlayPriority()
    {
        var draft = Draft() with { Summary = "MODEL_LONG_SUMMARY_" + new string('x', 1900), Risks = ["MODEL_LONG_RISK_" + new string('r', 1900)], HostLimitationsTruncated = true };
        var text = AdviceLimitationsPresentation.OverlayText(draft);
        Check(text.Length <= 500 && text.IndexOf("HOST_UNCOVERED", StringComparison.Ordinal) < text.IndexOf("MODEL_LONG_SUMMARY", StringComparison.Ordinal) &&
            text.Contains("已截断", StringComparison.Ordinal) && !text.Contains("MODEL_LONG_RISK", StringComparison.Ordinal),
            "Long model risk/summary hid the local boundary or overflowed the overlay.");
    }
    private static void VerifiedPartial()
    {
        var draft = Draft() with { Verification = "verified", HostRisks = [], HostAssumptions = [], HostUnknowns = null, HostUnsupportedMechanics = [] };
        var text = AdviceLimitationsPresentation.OverlayText(draft);
        Check(text.Contains("本地分类未单列", StringComparison.Ordinal) && text.Contains("verified", StringComparison.Ordinal) && !text.Contains("未执行", StringComparison.Ordinal),
            "An unlisted unknowns category was described as a missing final action validation.");
    }
    private static void Truncation()
    {
        var draft = Draft() with { HostLimitationsTruncated = true, HostUnsupportedMechanics = Enumerable.Range(1, 64).Select(i => "HOST_LIMIT_" + i).ToArray() };
        var text = AdviceLimitationsPresentation.Render(draft);
        Check(text.Contains("已截断", StringComparison.Ordinal) && text.Contains("不是完整清单", StringComparison.Ordinal) && text.Contains("HOST_LIMIT_64", StringComparison.Ordinal) &&
            !text.Contains("请查看工具结果", StringComparison.Ordinal), "Display hid recorded host entries or claimed that an archive contains the complete original tool result.");
    }
    private static void UnicodeAndReadOnly()
    {
        var draft = Draft() with { Summary = new string('a', 199) + "🐺tail", HostUnsupportedMechanics = [new string('b', 221) + "🐺boundary"] };
        var before = JsonSerializer.Serialize(draft);
        AdviceLimitationsPresentation.Render(draft); var overlay = AdviceLimitationsPresentation.OverlayText(draft);
        for (var i = 0; i < overlay.Length; i++)
        {
            if (char.IsHighSurrogate(overlay[i])) Check(++i < overlay.Length && char.IsLowSurrogate(overlay[i]), "Overlay clipping split a high surrogate.");
            else Check(!char.IsLowSurrogate(overlay[i]), "Overlay clipping left an unmatched low surrogate.");
        }
        Check(JsonSerializer.Serialize(draft) == before, "Rendering mutated the archived advice or its limitation lists.");
    }

    public static void RunOnDispatcher(MainWindow main)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Limitation display checks require the existing hidden STA.");
        var archive = (AdviceArchiveStore)Field(main, "_adviceArchive")!;
        var deck = Await(((SessionStore)Field(main, "_store")!).ListDeckVersionsAsync()).First();
        var frame = Frame(); var credential = "LIMITATION_UI_SYNTHETIC_SECRET";
        var modelConditions = new List<string> { "UI_MODEL_CONDITION", "MODEL_SECRET " + credential };
        var hostRisk = new List<string> { "UI_HOST_RISK" }; var hostConditions = new List<string> { "UI_HOST_CONDITION" };
        var hostUnknowns = new List<string> { "UI_HOST_UNKNOWN " + credential }; var hostUnsupported = new List<string> { "UI_HOST_UNCOVERED" };
        var advice = new StrategyAdvice(frame.MatchId, frame.StateVersion, "UI_CONDITIONAL_SUMMARY", ["UI_REASON"], null, ["UI_COMPAT_MIXED"], "conditional")
        {
            LimitationsVersion = 1, Assumptions = modelConditions, Unknowns = [], UnsupportedMechanics = ["UI_MODEL_UNCOVERED"],
            HostRisks = hostRisk, HostAssumptions = hostConditions, HostUnknowns = hostUnknowns, HostUnsupportedMechanics = hostUnsupported,
            HostLimitationsTruncated = true, Model = "UI_MODEL_SOURCE", AnalysisRunId = "UI_RUN_SOURCE", RequestIds = ["UI_REQUEST_SOURCE"],
            ConversationArchiveIds = ["UI_PRIOR_SOURCE"], KnowledgeAsOf = At, Actions = [], VerifiedLethal = false
        };
        var context = new AdviceContext(frame.MatchId, frame.StateVersion, "Review", new JsonObject())
        { CardBuild = "255790", EventSequence = frame.EventSequence, DeckVersionId = deck.Id, TemplateVersion = "ui/limitations-v1", KnowledgeAsOf = At, UserQuestion = "UI_QUESTION" };
        var evidence = new[] { new AdviceToolEvidence("evaluate_line", "UI_HOST_EVIDENCE", new string('a', 64)) };
        var saveTask = (Task<ArchivedAdviceRecord>)typeof(MainWindow).GetMethod("ArchiveAdviceAsync", Private)!.Invoke(main,
            new object?[] { advice, frame, context, "255790", deck.Id, evidence, credential })!;
        var saved = Await(saveTask);
        foreach (var list in new[] { modelConditions, hostRisk, hostConditions, hostUnknowns, hostUnsupported }) { list.Clear(); list.Add("MUTATED_AFTER_ARCHIVE"); }
        var restored = Await(archive.GetAsync(saved.Id)) ?? throw new Exception("The actual App archive mapping did not save its advice.");
        var a = restored.Advice;
        var expectedEvidence = evidence.Single() with { ResultHash = evidence.Single().ResultHash!.ToUpperInvariant() };
        Check(a.LimitationsVersion == 1 && a.HostLimitationsTruncated && a.Assumptions is { Count: 2 } && a.Unknowns is { Count: 0 } &&
            a.UnsupportedMechanics!.Single() == "UI_MODEL_UNCOVERED" && a.HostRisks!.Single() == "UI_HOST_RISK" && a.HostAssumptions!.Single() == "UI_HOST_CONDITION" &&
            a.HostUnknowns!.Single().Contains("UI_HOST_UNKNOWN", StringComparison.Ordinal) && a.HostUnsupportedMechanics!.Single() == "UI_HOST_UNCOVERED" &&
            a.MatchId == frame.MatchId && a.StateVersion == frame.StateVersion && a.EventSequence == frame.EventSequence && a.CardBuild == "255790" && a.DeckVersionId == deck.Id &&
            a.Model == advice.Model && a.AnalysisRunId == advice.AnalysisRunId && a.RequestIds!.SequenceEqual(advice.RequestIds) &&
            a.ConversationArchiveIds!.SequenceEqual(advice.ConversationArchiveIds) && a.KnowledgeAsOf == At && a.Evidence.Single() == expectedEvidence &&
            a.Actions.Count == 0 && !a.VerifiedLethal && a.Verification == "conditional" && !JsonSerializer.Serialize(restored).Contains(credential, StringComparison.Ordinal),
            "Actual App→Prepare→Save→Get lost source fields, null/empty distinctions, local classifications, redaction or action/lethal gating. " +
            JsonSerializer.Serialize(new { a.LimitationsVersion, a.HostLimitationsTruncated, modelConditions = a.Assumptions?.Count, modelUnknowns = a.Unknowns?.Count,
                modelMechanics = a.UnsupportedMechanics?.Count, hostRisks = a.HostRisks?.Count, hostConditions = a.HostAssumptions?.Count, hostUnknowns = a.HostUnknowns?.Count,
                hostMechanics = a.HostUnsupportedMechanics?.Count, a.Verification, actions = a.Actions.Count, a.VerifiedLethal,
                metadata = a.MatchId == frame.MatchId && a.StateVersion == frame.StateVersion && a.EventSequence == frame.EventSequence && a.DeckVersionId == deck.Id,
                sources = a.Model == advice.Model && a.AnalysisRunId == advice.AnalysisRunId && a.RequestIds!.SequenceEqual(advice.RequestIds) && a.ConversationArchiveIds!.SequenceEqual(advice.ConversationArchiveIds),
                knowledge = a.KnowledgeAsOf == At, evidence = a.Evidence.Single() == expectedEvidence, redacted = !JsonSerializer.Serialize(restored).Contains(credential, StringComparison.Ordinal) }));
        Throws<NotSupportedException>(() => ((IList<string>)a.HostAssumptions!).Clear());
        var oldLive = Field(main, "_liveFrame"); var oldHistory = Field(main, "_historyContext"); var oldOverlay = Field(main, "_overlayAdvice");
        var box = Control<TextBox>(main, "AdviceBox"); var oldText = box.Text; AdviceArchiveWindow? browser = null;
        try
        {
            Set(main, "_historyContext", null); Set(main, "_liveFrame", frame);
            Invoke(main, "DisplayArchivedAdvice", restored);
            var shared = Normalize(AdviceLimitationsPresentation.Render(a));
            Check(Normalize(box.Text).Contains(shared, StringComparison.Ordinal) && box.Text.Contains("有条件建议（具体动作已移除）", StringComparison.Ordinal) && !box.Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal) &&
                !box.Text.Contains("已验证动作", StringComparison.Ordinal) && ((string)Field(main, "_overlayAdvice")!).Contains("UI_HOST_UNCOVERED", StringComparison.Ordinal),
                "The main advice lost structured limits, revived removed actions/lethal, or put mixed model risk before the host boundary.");
            Invoke(main, "DisplayArchivedAdvice", restored with { Advice = a with { Verification = "unsupported" } });
            Check(box.Text.Contains("规则未覆盖（具体动作已移除）", StringComparison.Ordinal) && !box.Text.Contains("已验证动作", StringComparison.Ordinal),
                "Unsupported advice was shown as an ordinary strategy or revived a removed action.");
            Invoke(main, "DisplayArchivedAdvice", restored);
            browser = new(archive, new(frame.MatchId, "合成限制回归", AdviceArchiveScopeKind.Session, deck.Id)); Await(browser.InitializeAsync());
            Select(browser, restored.Id);
            var detail = Control<TextBox>(browser, "ArchiveDetailText");
            Check(Normalize(detail.Text).Contains(shared, StringComparison.Ordinal) && new[] { saved.Id, frame.MatchId, deck.Id, "255790", "UI_MODEL_SOURCE", "UI_RUN_SOURCE", "UI_REQUEST_SOURCE", "UI_PRIOR_SOURCE", "ui/limitations-v1", At.ToString("O") }.All(detail.Text.Contains) &&
                detail.Text.Contains("不是当前可执行步骤", StringComparison.Ordinal) && !detail.Text.Contains(credential, StringComparison.Ordinal),
                "Actual archive selection showed different limitation fields or lost historical source attribution/privacy.");

            var legacy = Await(archive.SaveAsync(LegacyDraft() with { MatchId = frame.MatchId, Task = "Review" }));
            MakeLegacyTaskNull(main, legacy);
            var old = Await(archive.GetAsync(legacy.Id)) ?? throw new Exception("Missing real legacy advice.");
            Check(old.Advice.Task is null && old.Advice.LimitationsVersion is null && old.Advice.HostUnknowns is null, "Legacy fixture silently acquired a task/version/local classification.");
            Invoke(main, "DisplayArchivedAdvice", old);
            Check(box.Text.Contains("旧记录未区分", StringComparison.Ordinal) && box.Text.Contains("LEGACY_FLAT_RISK", StringComparison.Ordinal), "Main legacy rendering crashed on null Task or guessed risk categories.");
            browser.Close(); browser = new(archive, new(frame.MatchId, "合成旧记录回归", AdviceArchiveScopeKind.Session)); Await(browser.InitializeAsync()); Select(browser, old.Id);
            Check(Control<TextBox>(browser, "ArchiveDetailText").Text.Contains("旧记录未区分", StringComparison.Ordinal) &&
                Control<TextBox>(browser, "ArchiveDetailText").Text.Contains("旧记录未保存来源", StringComparison.Ordinal), "Real legacy archive lost its explicit unknown origin.");
            Passive(main); Passive(browser);
        }
        finally { browser?.Close(); Set(main, "_liveFrame", oldLive); Set(main, "_historyContext", oldHistory); Set(main, "_overlayAdvice", oldOverlay); box.Text = oldText; }
    }
    private static void MakeLegacyTaskNull(MainWindow main, ArchivedAdviceRecord record)
    {
        var payload = JsonSerializer.SerializeToNode(record.Advice)!.AsObject(); payload["Task"] = null; var json = payload.ToJsonString();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine((string)Field(main, "_dataRoot")!, "advice-archive.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE coach_advice_records SET payload_json=$json,content_hash=$hash WHERE id=$id";
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))); command.Parameters.AddWithValue("$id", record.Id);
        Check(command.ExecuteNonQuery() == 1, "The synthetic legacy advice payload was not found.");
    }
    private static GameSnapshot Frame()
    {
        EntitySnapshot Entity(int id, string? card, EntityKind kind, params (string Key, string Value)[] tags) => new(id, card, kind, tags.ToFrozenDictionary(p => p.Key, p => p.Value));
        return new("limitations-ui-" + Guid.NewGuid().ToString("N"), 17, 47, 1, 1, 1, GamePhase.Main, new[]
        {
            Entity(1, null, EntityKind.Game, ("STATE", "RUNNING"), ("TURN", "1")),
            Entity(2, null, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "11"), ("CURRENT_PLAYER", "1")),
            Entity(3, null, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "21"), ("CURRENT_PLAYER", "0")),
            Entity(11, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("CARDTYPE", "HERO"), ("ZONE", "PLAY"), ("HEALTH", "30"), ("DAMAGE", "0")),
            Entity(21, "HERO_08", EntityKind.Card, ("CONTROLLER", "2"), ("CARDTYPE", "HERO"), ("ZONE", "PLAY"), ("HEALTH", "30"), ("DAMAGE", "0"))
        }.ToFrozenDictionary(e => e.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static void Select(AdviceArchiveWindow browser, string id)
    { var list = Control<ListBox>(browser, "ArchiveRecordsList"); list.SelectedItem = list.Items.Cast<object>().Single(row => ((ArchivedAdviceRecord)row.GetType().GetProperty("Value")!.GetValue(row)!).Id == id); }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name, object value) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, new[] { value });
    private static T Control<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T ?? throw new Exception("Missing control " + name);
    private static string Normalize(string text) => text.Replace("\r\n", "\n");
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted || clock.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        }
        Check(task.IsCompleted, "Limitation display fixture timed out."); task.GetAwaiter().GetResult();
    }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A limitation regression displayed or created a native window.");
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
