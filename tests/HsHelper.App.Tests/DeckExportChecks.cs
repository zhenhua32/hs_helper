using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Storage;

public static class DeckExportChecks
{
    private static readonly DateTimeOffset Created = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset Exported = Created.AddDays(7);
    private const string OldBuild = "111111";
    private const string NewBuild = "222222";

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("deck export preserves the saved version's identity and original build", SavedSource);
        yield return ("deck export compares canonical contents across comments and ordering", CanonicalSource);
        yield return ("deck export edits to main cards, sideboards, hero or format become drafts", EditedContents);
        yield return ("deck export requires agreement with both saved code and definition", ConflictingSavedContents);
        yield return ("deck export drafts retain explicit names and unknown provenance", DraftSource);
        yield return ("deck export freezes every nested deck collection", ImmutableDeck);
        yield return ("deck export JSON distinguishes schema, source identity and timestamps", JsonSource);
        yield return ("deck export plain text roundtrips all sideboards", TextRoundtrip);
    }

    private static DeckDefinition Definition() => new(2, new List<int> { 31 },
        new List<DeckCard> { new(91, 2), new(30, 1), new(125998, 1) },
        new List<DeckSideboard>
        {
            new(125998, new List<DeckCard> { new(44, 1), new(17, 1), new(53, 1) }),
            new(30, new List<DeckCard> { new(60, 2) })
        });
    private static DeckVersion Version(DeckDefinition? definition = null) => new("saved-export-id", "原保存名称", DeckCode.Encode(definition ?? Definition()),
        OldBuild, Created, "previous-export-id", definition ?? Definition());
    private static DeckExportDocument Saved() { var saved = Version(); return DeckExportBuilder.Create(saved.Code, "编辑框临时名称", saved, Exported); }

    private static void AssertSaved(DeckExportDocument doc, DeckVersion saved)
    {
        Check(doc.SchemaVersion > 0 && doc.ExportedAt == Exported && doc.IsSavedVersion && doc.DeckVersionId == saved.Id &&
            doc.CardBuild == saved.CardBuild && doc.Name == saved.Name && doc.DeckVersionCreatedAt == saved.CreatedAt &&
            doc.PreviousVersionId == saved.PreviousVersionId && doc.Code == DeckCode.Encode(saved.Definition),
            "Export changed or fabricated a saved version's provenance.");
    }
    private static void AssertDraft(DeckExportDocument doc, string name)
    {
        Check(!doc.IsSavedVersion && doc.DeckVersionId is null && doc.CardBuild is null && doc.DeckVersionCreatedAt is null &&
            doc.PreviousVersionId is null && doc.Name == name && doc.ExportedAt == Exported,
            "An unsaved draft inherited a saved version or active catalog's provenance.");
    }
    private static void SavedSource() => AssertSaved(Saved(), Version());

    private static void CanonicalSource()
    {
        // Every value is below 128 except the sideboard owner; this fixture is an independently
        // written version-one deckstring whose singleton and sideboard records are deliberately unsorted.
        var input = UnorderedCode();
        var definition = Definition();
        var saved = Version(definition) with { Code = input };
        var doc = DeckExportBuilder.Create("### 仅改说明\r\n# 无来源含义\r\n  " + input + "  \r\n", "另一个名字", saved, Exported);
        AssertSaved(doc, saved);
        Check(input != doc.Code && DeckCode.Encode(DeckCode.Decode(input)) == doc.Code,
            "The ordering fixture was canonical already or export kept noncanonical bytes.");
    }
    private static string UnorderedCode()
    {
        using var bytes = new MemoryStream();
        void Number(int value) { while (value >= 128) { bytes.WriteByte((byte)((value & 127) | 128)); value >>= 7; } bytes.WriteByte((byte)value); }
        foreach (var n in new[] { 0, 1, 2, 1, 31, 2, 125998, 30, 1, 91, 0, 1, 3, 53, 125998, 44, 125998, 17, 125998, 1, 60, 30, 0 }) Number(n);
        return Convert.ToBase64String(bytes.ToArray());
    }
    private static void EditedContents()
    {
        var saved = Version(); var original = saved.Definition;
        var edits = new[]
        {
            original with { Cards = original.Cards.Select(c => c.DbfId == 91 ? c with { Count = 1 } : c).ToArray() },
            original with { Sideboards = original.Sideboards.Select(b => b.OwnerDbfId == 125998 ? b with { Cards = b.Cards.Where(c => c.DbfId != 44).ToArray() } : b).ToArray() },
            original with { Sideboards = original.Sideboards.Select(b => b.OwnerDbfId == 125998 ? b with { OwnerDbfId = 125999 } : b).ToArray() },
            original with { Heroes = new[] { 32 } },
            original with { Format = 1 }
        };
        foreach (var edited in edits)
        {
            var doc = DeckExportBuilder.Create(DeckCode.Encode(edited), "  修改后的草稿  ", saved, Exported);
            AssertDraft(doc, "修改后的草稿");
            Check(doc.Code == DeckCode.Encode(edited), "Draft export replaced the editor's actual deck with the selected saved deck.");
        }
    }
    private static void ConflictingSavedContents()
    {
        var saved = Version(); var changed = saved.Definition with { Format = 1 };
        AssertDraft(DeckExportBuilder.Create(saved.Code, "草稿", saved with { Definition = changed }, Exported), "草稿");
        AssertDraft(DeckExportBuilder.Create(saved.Code, "草稿", saved with { Code = DeckCode.Encode(changed) }, Exported), "草稿");
        AssertDraft(DeckExportBuilder.Create(saved.Code, "草稿", saved with { Code = "invalid saved code" }, Exported), "草稿");
        AssertDraft(DeckExportBuilder.Create(saved.Code, "草稿", saved with { Definition = changed with { Heroes = Array.Empty<int>() } }, Exported), "草稿");
    }
    private static void DraftSource()
    {
        var code = DeckCode.Encode(Definition());
        AssertDraft(DeckExportBuilder.Create(code, "  自定义草稿  ", null, Exported), "自定义草稿");
        AssertDraft(DeckExportBuilder.Create(code, " \t\r\n", null, Exported), "未命名套牌");
        foreach (var bad in new[] { "", "not a deck code", code + "\n" + code })
            Throws<Exception>(() => DeckExportBuilder.Create(bad, "不应创建", null, Exported));
    }
    private static void ImmutableDeck()
    {
        var mutable = Definition(); var selected = Version(mutable);
        var doc = DeckExportBuilder.Create(selected.Code, "draft", selected, Exported);
        var before = DeckExportBuilder.ToJson(doc);
        ((IList<int>)mutable.Heroes)[0] = 999;
        ((IList<DeckCard>)mutable.Cards).Clear();
        ((IList<DeckCard>)mutable.Sideboards[0].Cards).Clear();
        ((IList<DeckSideboard>)mutable.Sideboards).Clear();
        Check(DeckExportBuilder.ToJson(doc) == before, "Mutating source deck collections changed an already frozen export.");
        Throws<NotSupportedException>(() => ((IList<int>)doc.Deck.Heroes)[0] = 1);
        Throws<NotSupportedException>(() => ((IList<DeckCard>)doc.Deck.Cards).Clear());
        Throws<NotSupportedException>(() => ((IList<DeckSideboard>)doc.Deck.Sideboards).Clear());
        Throws<NotSupportedException>(() => ((IList<DeckCard>)doc.Deck.Sideboards[0].Cards).Clear());
        Check(DeckExportBuilder.ToJson(doc) == before, "A consumer changed a frozen export through a nested collection.");
    }
    private static void JsonSource()
    {
        var doc = Saved(); using var json = JsonDocument.Parse(DeckExportBuilder.ToJson(doc)); var root = json.RootElement;
        Check(root.GetProperty("SchemaVersion").GetInt32() == doc.SchemaVersion && root.GetProperty("DeckVersionId").GetString() == doc.DeckVersionId &&
            root.GetProperty("CardBuild").GetString() == OldBuild && root.GetProperty("ExportedAt").GetDateTimeOffset() == Exported &&
            root.GetProperty("DeckVersionCreatedAt").GetDateTimeOffset() == Created && root.GetProperty("PreviousVersionId").GetString() == "previous-export-id" &&
            root.GetProperty("Code").GetString() == doc.Code && root.GetProperty("Deck").GetProperty("Sideboards").GetArrayLength() == 2,
            "JSON lost the version identity, original time, build, code or sideboards.");
    }
    private static void TextRoundtrip()
    {
        var doc = Saved(); var text = DeckExportBuilder.ToCodeText(doc);
        Check(text == doc.Code + Environment.NewLine && DeckCode.Encode(DeckCode.Decode(text)) == doc.Code &&
            DeckCode.Decode(text).Sideboards.Single(b => b.OwnerDbfId == 125998).Cards.Sum(c => c.Count) == 3,
            "The text export was not one directly usable canonical deck code with its sideboard intact.");
    }

    /// <summary>Exercise actual routed buttons on the existing isolated hidden STA main window.</summary>
    public static void RunOnDispatcher(MainWindow window)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Deck export UI check needs the existing STA dispatcher.");
        Passive(window);
        var store = (SessionStore)Field(window, "_store")!;
        var first = Await(store.SaveDeckVersionAsync("导出来源A", Definition(), OldBuild));
        var second = Await(store.SaveDeckVersionAsync("导出来源B", Definition() with { Heroes = new[] { 32 } }, NewBuild));
        var selected = (DeckVersion?)Field(window, "_selectedDeckVersion");
        var oldDeck = Field(window, "_deck"); var oldManifest = Field(window, "_manifest");
        var box = Control<TextBox>(window, "DeckBox"); var name = Control<TextBox>(window, "DeckNameBox");
        var oldInput = box.Text; var oldName = name.Text;
        var savedBox = Control<ComboBox>(window, "SavedDeckBox");
        var oldSelectedId = Deck(savedBox.SelectedItem)?.Id;
        var oldChooser = Field(window, "_chooseDeckExportPath"); var oldWriter = Field(window, "_writeDeckExportText");
        var writes = new List<(string Path, string Text)>(); var chooseCalls = 0;
        try
        {
            Await((Task)typeof(MainWindow).GetMethod("LoadDeckLibraryAsync", Private)!.Invoke(window, new object?[] { second.Id })!);
            Set(window, "_manifest", new CardManifest(NewBuild, "zhCN", "synthetic", Exported, "synthetic", 0));
            Select(first);
            Set(window, "_writeDeckExportText", (Func<string, string, Task>)((path, text) => { writes.Add((path, text)); return Task.CompletedTask; }));
            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(format =>
            {
                chooseCalls++;
                Check(format == DeckExportFormat.Json, "The JSON button chose the text export format.");
                Select(second); // The dialog runs a nested dispatcher in production; emulate a real editor/source change before it returns.
                return "synthetic-export.json";
            }));
            Click(window, "ExportDeckJsonButton"); WaitIdle(window);
            Check(writes.Count == 1 && writes[0].Path == "synthetic-export.json", "JSON routed export did not use the injected writer exactly once.");
            using (var json = JsonDocument.Parse(writes[0].Text))
            {
                var value = json.RootElement;
                Check(value.GetProperty("DeckVersionId").GetString() == first.Id && value.GetProperty("CardBuild").GetString() == OldBuild &&
                    value.GetProperty("Name").GetString() == first.Name && value.GetProperty("DeckVersionCreatedAt").GetDateTimeOffset() == first.CreatedAt &&
                    value.GetProperty("Code").GetString() == first.Code && ((DeckVersion?)Field(window, "_selectedDeckVersion"))?.Id == second.Id,
                    "Dialog-time deck changes or the active manifest changed the click-time saved export.");
            }
            Check(Control<TextBlock>(window, "DeckExportStatusText").Text.Length > 0, "The export result was not visible in the deck panel.");

            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(_ => { chooseCalls++; return null; }));
            Click(window, "ExportDeckJsonButton"); WaitIdle(window);
            Check(writes.Count == 1 && chooseCalls == 2 && Control<TextBlock>(window, "DeckExportStatusText").Text.Contains("已取消", StringComparison.Ordinal),
                "Cancelling the path dialog still wrote an export or left a misleading status.");
            box.Text = "invalid deck code";
            Click(window, "ExportDeckJsonButton"); WaitIdle(window);
            Check(writes.Count == 1 && chooseCalls == 2, "Invalid contents opened a path dialog or wrote an export.");

            Select(first); box.Text = "# 说明不改变来源\n" + first.Code;
            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(format =>
            {
                chooseCalls++; Check(format == DeckExportFormat.DeckCode, "The text button chose JSON.");
                box.Text = second.Code; name.Text = second.Name; return "synthetic-deck.txt";
            }));
            Click(window, "ExportDeckCodeButton"); WaitIdle(window);
            Check(writes.Count == 2 && writes[1] == ("synthetic-deck.txt", first.Code + Environment.NewLine) &&
                DeckCode.Decode(writes[1].Text).Sideboards.Count == 2, "Text routed export lost the click-time deck or sideboards.");

            Select(first); box.Text = second.Code; name.Text = "  未保存的编辑  ";
            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(_ => "synthetic-draft.json"));
            Click(window, "ExportDeckJsonButton"); WaitIdle(window);
            using (var json = JsonDocument.Parse(writes[^1].Text))
            {
                var value = json.RootElement;
                Check(value.GetProperty("DeckVersionId").ValueKind == JsonValueKind.Null && value.GetProperty("CardBuild").ValueKind == JsonValueKind.Null &&
                    value.GetProperty("Name").GetString() == "未保存的编辑" && value.GetProperty("Code").GetString() == second.Code,
                    "Editing a selected saved deck reused its provenance or the active build.");
            }
        }
        finally
        {
            Set(window, "_chooseDeckExportPath", oldChooser); Set(window, "_writeDeckExportText", oldWriter);
            Set(window, "_manifest", oldManifest); Set(window, "_selectedDeckVersion", selected); Set(window, "_deck", oldDeck);
            savedBox.SelectedItem = savedBox.Items.Cast<object>().FirstOrDefault(item => Deck(item)?.Id == oldSelectedId);
            box.Text = oldInput; name.Text = oldName;
        }
        PendingWriteBlocksShutdown();
        FailedWriteLeavesShutdownRetryable(false);
        FailedWriteLeavesShutdownRetryable(true);
        ClosingDuringChooserDoesNotWrite();
        Passive(window);

        void Select(DeckVersion deck)
        {
            var item = savedBox.Items.Cast<object>().Single(item => Deck(item)?.Id == deck.Id);
            if (ReferenceEquals(savedBox.SelectedItem, item)) savedBox.SelectedItem = null;
            savedBox.SelectedItem = item;
            WaitUntil(() => ((DeckVersion?)Field(window, "_selectedDeckVersion"))?.Id == deck.Id && box.Text == deck.Code,
                "Real saved-deck selection did not update the deck editor.");
        }
    }
    private static void FailedWriteLeavesShutdownRetryable(bool failBeforeCloseContinuation)
    {
        var window = new MainWindow(); var closed = false; window.Closed += (_, _) => closed = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Control<TextBox>(window, "DeckBox").Text = DeckCode.Encode(Definition());
            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(_ => "synthetic-failure.txt"));
            Set(window, "_writeDeckExportText", (Func<string, string, Task>)((_, _) => release.Task));
            Click(window, "ExportDeckCodeButton"); window.Close();
            if (!failBeforeCloseContinuation) Pump();
            Check(!closed && (bool)Field(window, "_closing")!, "The failing-write fixture did not first wait during shutdown.");
            // Without pumping, the export fails after Closing returned but before its Background continuation.
            // Closing must await the task captured synchronously, even if the export clears its field first.
            release.SetException(new IOException("SYNTHETIC_EXPORT_FAILURE"));
            WaitUntil(() => !(bool)Field(window, "_closing")! && !(bool)Field(window, "_deckExportBusy")!, "Export failure did not release the close/export guards.");
            Check(!closed && Control<Button>(window, "ExportDeckJsonButton").IsEnabled && Control<Button>(window, "ExportDeckCodeButton").IsEnabled &&
                Control<TextBlock>(window, "StatusText").Text.Contains("SYNTHETIC_EXPORT_FAILURE", StringComparison.Ordinal),
                "A failed export closed the window, hid the failure, or left the buttons disabled.");
            Check(Control<TextBlock>(window, "DeckExportStatusText").Text.Contains("未完成", StringComparison.Ordinal) &&
                !Control<TextBlock>(window, "DeckExportStatusText").Text.Contains("正在保存", StringComparison.Ordinal),
                "The failed export still claimed it was being saved.");
            var retried = 0;
            Set(window, "_writeDeckExportText", (Func<string, string, Task>)((_, _) => { retried++; return Task.CompletedTask; }));
            Click(window, "ExportDeckCodeButton"); WaitIdle(window);
            Check(retried == 1, "Export could not be retried after its failed write.");
            window.Close(); WaitUntil(() => closed, "Successful export retry did not leave shutdown usable.");
            Passive(window);
        }
        finally { release.TrySetResult(); if (!closed) { window.Close(); WaitUntil(() => closed, "Failed export fixture did not close."); } }
    }

    private static void PendingWriteBlocksShutdown()
    {
        var window = new MainWindow(); var closed = false; window.Closed += (_, _) => closed = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var writes = 0; var choices = 0;
        try
        {
            Control<TextBox>(window, "DeckBox").Text = DeckCode.Encode(Definition());
            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(_ => { choices++; return "synthetic-pending.txt"; }));
            Set(window, "_writeDeckExportText", (Func<string, string, Task>)((_, _) => { writes++; return release.Task; }));
            Click(window, "ExportDeckCodeButton");
            Check(Field(window, "_deckExportWrite") is Task { IsCompleted: false } && writes == 1, "Pending export had no observable persistence task.");
            // A second routed event must not produce an overlapping dialog or write, even if dispatched programmatically.
            Control<Button>(window, "ExportDeckJsonButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(writes == 1 && choices == 1, "A second export overlapped the pending write.");
            window.Close(); Pump();
            Check(!closed && (bool)Field(window, "_closing")! && !release.Task.IsCompleted, "The real Closing workflow passed an unfinished export write.");
            release.SetResult();
            WaitUntil(() => closed, "Window did not finish shutdown after its export write completed.");
            Passive(window);
        }
        finally { release.TrySetResult(); if (!closed) { window.Close(); WaitUntil(() => closed, "Pending export fixture did not close."); } }
    }
    private static void ClosingDuringChooserDoesNotWrite()
    {
        var window = new MainWindow(); var closed = false; window.Closed += (_, _) => closed = true; var writes = 0;
        try
        {
            Control<TextBox>(window, "DeckBox").Text = DeckCode.Encode(Definition());
            Set(window, "_chooseDeckExportPath", (Func<DeckExportFormat, string?>)(_ => { window.Close(); return "synthetic-too-late.json"; }));
            Set(window, "_writeDeckExportText", (Func<string, string, Task>)((_, _) => { writes++; return Task.CompletedTask; }));
            Click(window, "ExportDeckJsonButton");
            WaitUntil(() => closed, "Window did not close after returning from a cancelled-by-close export dialog.");
            Check(writes == 0, "A path dialog returned during shutdown and started a late file write.");
            Passive(window);
        }
        finally { if (!closed) { window.Close(); WaitUntil(() => closed, "Closing-dialog fixture did not close."); } }
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)?.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => (typeof(MainWindow).GetField(name, Private) ?? throw new MissingFieldException(name)).SetValue(window, value);
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    private static DeckVersion? Deck(object? item) => item?.GetType().GetProperty("Value")?.GetValue(item) as DeckVersion;
    private static void Click(MainWindow window, string name)
    { var button = Control<Button>(window, name); Check(button.IsEnabled, "Deck export action was disabled: " + name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void WaitIdle(MainWindow window) => WaitUntil(() => Field(window, "_deckExportWrite") is not Task { IsCompleted: false }, "Deck export did not complete.");
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Deck export regression displayed a window or created a window handle.");
    private static T Await<T>(Task<T> task) { WaitUntil(() => task.IsCompleted, "Deck export fixture storage operation timed out."); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Deck export fixture operation timed out."); task.GetAwaiter().GetResult(); }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void WaitUntil(Func<bool> done, string message)
    {
        if (done()) return; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || clock.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), message);
    }
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + " was not thrown."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
