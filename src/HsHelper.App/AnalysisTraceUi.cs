using System.Collections.Concurrent;
using System.IO;
using HsHelper.Agent;
using HsHelper.Storage;

namespace HsHelper.App;

public partial class MainWindow
{
    private AnalysisTraceStore _traces = null!;
    private readonly ConcurrentQueue<ModelRequestTrace> _pendingRequests = new();
    private readonly ConcurrentQueue<ToolExecutionTrace> _pendingTools = new();
    private readonly HashSet<string> _seededBudgets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _traceGate = new(1, 1);

    private async Task InitializeTracesAsync()
    {
        _traces = new AnalysisTraceStore(Path.Combine(_dataRoot, "analysis-traces.db"));
        await _traces.InitializeAsync();
    }
    private void RememberUsage(ModelUsage usage, string? build, string task)
    {
        _pendingRequests.Enqueue(new(usage.RequestId, usage.MatchId, usage.StateVersion, usage.Model, usage.Round,
            usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ChargedTokens, usage.ElapsedMilliseconds,
            usage.Success ? AnalysisTraceStatus.Completed : AnalysisTraceStatus.Failed, usage.ConservativeCharge)
            { CardBuild = build is null or "" or "unknown" ? null : build, Task = task });
    }
    private void RememberTool(string match, long version, string name, long elapsed, bool succeeded)
        => _pendingTools.Enqueue(new(Guid.NewGuid().ToString("N"), match, version, name, elapsed,
            succeeded ? AnalysisTraceStatus.Completed : AnalysisTraceStatus.Failed));

    private async Task FlushTracesAsync()
    {
        await _traceGate.WaitAsync();
        try { await FlushTracesCoreAsync(); }
        finally { _traceGate.Release(); }
    }
    private async Task FlushTracesCoreAsync()
    {
        while (_pendingRequests.TryPeek(out var request))
        { await _traces.AppendRequestAsync(request); _pendingRequests.TryDequeue(out _); }
        while (_pendingTools.TryPeek(out var tool))
        { await _traces.AppendToolAsync(tool); _pendingTools.TryDequeue(out _); }
    }
    private async Task SeedBudgetAsync(string match)
    {
        await _traceGate.WaitAsync();
        try
        {
            await FlushTracesCoreAsync();
            if (_seededBudgets.Contains(match)) return;
            var totals = await _traces.GetBudgetTotalsAsync(match);
            _analysisBudget.SeedMatch(match, totals.RequestCount, totals.ChargedTokens);
            _seededBudgets.Add(match);
        }
        finally { _traceGate.Release(); }
    }
    private void DisplayBudget(string match)
    {
        var budget = _analysisBudget.Snapshot(match);
        BudgetText.Text = $"本局请求 {budget.RequestsStarted}/60 · 令牌消耗 {budget.ChargedTokens:N0}/200,000" +
            (budget.ConservativeCharges > 0 ? $" · {budget.ConservativeCharges} 次未报告用量，已保守计入预算" : "");
    }
}
