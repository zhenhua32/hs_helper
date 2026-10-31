using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Storage;

/// <summary>Real Window.Close/Closing/Closed events on the existing isolated STA, without bypassing shutdown.</summary>
internal static class WindowClosingRegression
{
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOnDispatcher(string directory)
    {
        Directory.CreateDirectory(directory);
        SynchronousSaveClosesAfterCallback();
        PendingWriteBlocksCloseAndDuplicateAttempts(Path.Combine(directory, "pending"));
        FailedWriteLeavesCloseRetryable(Path.Combine(directory, "failed"));
    }

    private static void SynchronousSaveClosesAfterCallback()
    {
        var window = new MainWindow();
        var paging = (HistoryPagingState)Get(window, "_historyPaging")!;
        var pendingPage = paging.Begin(new SessionFilter(), 1, DateTimeOffset.UtcNow);
        var historyRead = new CancellationTokenSource(); var historyToken = historyRead.Token;
        Set(window, "_historyListCancellation", historyRead);
        var knowledgePaging = (KnowledgePagingState)Get(window, "_knowledgePaging")!;
        var pendingKnowledge = knowledgePaging.Begin(new CoachKnowledgeFilter(), 1, DateTimeOffset.UtcNow);
        var knowledgeRead = new CancellationTokenSource(); var knowledgeToken = knowledgeRead.Token;
        Set(window, "_knowledgeListCancellation", knowledgeRead);
        var closed = false;
        var insideFirstClose = true;
        var closingCallbacks = 0;
        window.Closing += (_, _) => closingCallbacks++;
        window.Closed += (_, _) =>
        {
            Check(!insideFirstClose, "Synchronous persistence closed the window inside its first Closing callback.");
            closed = true;
        };
        Passive(window);
        window.Close();
        insideFirstClose = false;
        Check(!closed && Closing(window), "The initial close did not defer shutdown until Closing returned.");
        Check(historyToken.IsCancellationRequested && Get(window, "_historyListCancellation") is null && !paging.IsCurrent(pendingPage) &&
            !paging.TryApply(pendingPage, new(Array.AsReadOnly(Array.Empty<GameSession>()), 1, 100, false)),
            "Closing retained a history read or allowed its delayed page to publish after shutdown began.");
        Check(knowledgeToken.IsCancellationRequested && Get(window, "_knowledgeListCancellation") is null && !knowledgePaging.IsCurrent(pendingKnowledge) &&
            !knowledgePaging.TryApply(pendingKnowledge, new(Array.AsReadOnly(Array.Empty<CoachKnowledgeVersion>()), 1, 100, false, pendingKnowledge.AsOf)),
            "Closing retained a knowledge read or allowed its delayed result to overwrite the editor.");
        WaitUntil(() => closed, "A synchronously completed save left the window open after the initial close.");
        Check(closingCallbacks == 2 && SourceGeneration(window) == 1,
            "Deferred shutdown repeated persistence or did not complete the final WPF close.");
        Check(!Status(window).Contains("记录尚未安全保存", StringComparison.Ordinal), "A synchronous save caused a false close failure.");
    }

    private static void PendingWriteBlocksCloseAndDuplicateAttempts(string directory)
    {
        Directory.CreateDirectory(directory);
        var window = new MainWindow();
        var setup = new LogSetupWindow(Path.Combine(directory, "log.config"), directory);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedFile = Path.Combine(directory, "synthetic-write.txt");
        var write = SaveSyntheticAsync();
        Set(setup, "_pendingWrite", write);
        Set(window, "_logSetupWindow", setup);
        var closed = false;
        var savedBeforeClosed = false;
        window.Closed += (_, _) =>
        {
            savedBeforeClosed = File.Exists(savedFile) && File.ReadAllText(savedFile) == "SYNTHETIC shutdown write";
            closed = true;
        };
        try
        {
            Passive(window); Passive(setup);
            window.Close();
            Drain();
            Check(Closing(window) && !closed && !AllowClose(window) && !write.IsCompleted && SourceGeneration(window) == 1,
                "Shutdown did not wait for the existing write to finish.");
            window.Close(); window.Close();
            Drain();
            Check(!closed && SourceGeneration(window) == 1,
                "Repeated user close attempts started overlapping persistence workflows.");
            release.SetResult();
            WaitUntil(() => closed, "Window did not close after its pending write completed.");
            Check(write.IsCompletedSuccessfully && savedBeforeClosed,
                "Window closed before its pending data was safely written.");
        }
        finally
        {
            release.TrySetResult();
            if (!closed) { window.Close(); WaitUntil(() => closed, "Pending-write fixture cleanup could not close."); }
            setup.Close();
        }

        async Task SaveSyntheticAsync()
        {
            await release.Task;
            await File.WriteAllTextAsync(savedFile, "SYNTHETIC shutdown write");
        }
    }

    private static void FailedWriteLeavesCloseRetryable(string directory)
    {
        Directory.CreateDirectory(directory);
        var window = new MainWindow();
        var setup = new LogSetupWindow(Path.Combine(directory, "log.config"), directory);
        Set(setup, "_pendingWrite", Task.FromException(new IOException("SYNTHETIC write failure")));
        Set(window, "_logSetupWindow", setup);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Close();
            WaitUntil(() => !Closing(window), "A failed write did not release the shutdown guard for retry.");
            Check(!closed && !AllowClose(window) && Status(window).Contains("SYNTHETIC write failure", StringComparison.Ordinal),
                "A failed write closed the window, retained close permission or hid the retryable failure.");
            Set(setup, "_pendingWrite", Task.CompletedTask);
            window.Close();
            WaitUntil(() => closed, "A successful retry did not close the window after its prior write failure.");
            Check(SourceGeneration(window) == 2, "Retry did not rerun the required persistence workflow.");
        }
        finally
        {
            Set(setup, "_pendingWrite", Task.CompletedTask);
            if (!closed) { window.Close(); WaitUntil(() => closed, "Failed-write fixture cleanup could not close."); }
            setup.Close();
        }
    }

    private static bool Closing(MainWindow window) => (bool)Get(window, "_closing")!;
    private static bool AllowClose(MainWindow window) => (bool)Get(window, "_allowClose")!;
    private static long SourceGeneration(MainWindow window) => (long)Get(window, "_sourceGeneration")!;
    private static string Status(MainWindow window) => ((TextBlock)window.FindName("StatusText")).Text;
    private static object? Get(object target, string name) => target.GetType().GetField(name, Fields)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Fields)!.SetValue(target, value);
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
        "A shutdown regression displayed a window or created a window handle.");
    private static void Drain()
    {
        var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        WaitUntil(() => operation.Task.IsCompleted, "Dispatcher did not drain shutdown work.");
        operation.Task.GetAwaiter().GetResult();
    }
    private static void WaitUntil(Func<bool> complete, string message)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(complete(), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
