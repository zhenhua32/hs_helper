namespace HsHelper.Agent;

public sealed record AnalysisBudgetLimits(int MaxRequestsPerMatch = 60, long MaxTokensPerMatch = 200000,
    int MaxConcurrentRequestsPerMatch = 1);

public sealed record AnalysisBudgetSnapshot(string MatchId, int RequestsStarted, int CompletedRequests,
    int FailedRequests, long ChargedTokens, long ReservedTokens, int InFlightRequests,
    int ConservativeCharges);

public enum AnalysisBudgetExceededReason { RequestLimit, TokenLimit, ConcurrentLimit }

public sealed class AnalysisBudgetExceededException(AnalysisBudgetExceededReason reason, string message)
    : InvalidOperationException(message)
{
    public AnalysisBudgetExceededReason Reason { get; } = reason;
}

/// <summary>Thread-safe per-match HTTP request and token budget; reservations cannot oversubscribe the limit.</summary>
public sealed class AnalysisBudget
{
    private sealed class State
    {
        public int Started, Completed, Failed, InFlight, Conservative;
        public long Charged, Reserved;
    }
    private readonly AnalysisBudgetLimits _limits;
    private readonly object _sync = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public AnalysisBudget(AnalysisBudgetLimits? limits = null)
    {
        _limits = limits ?? new();
        if (_limits.MaxRequestsPerMatch < 1 || _limits.MaxTokensPerMatch < 1 || _limits.MaxConcurrentRequestsPerMatch < 1)
            throw new ArgumentException("模型预算上限必须为正数。");
    }

    public AnalysisReservation Reserve(string matchId, long reservedTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        if (reservedTokens < 0) throw new ArgumentOutOfRangeException(nameof(reservedTokens));
        lock (_sync)
        {
            if (!_states.TryGetValue(matchId, out var state)) _states[matchId] = state = new();
            if (state.Started >= _limits.MaxRequestsPerMatch)
                throw new AnalysisBudgetExceededException(AnalysisBudgetExceededReason.RequestLimit, "本局模型请求次数已达到上限。");
            if (state.InFlight >= _limits.MaxConcurrentRequestsPerMatch)
                throw new AnalysisBudgetExceededException(AnalysisBudgetExceededReason.ConcurrentLimit, "本局已有模型请求正在进行。");
            var remaining = state.Charged >= _limits.MaxTokensPerMatch ? 0 : _limits.MaxTokensPerMatch - state.Charged;
            if (state.Reserved > remaining || reservedTokens > remaining - state.Reserved)
                throw new AnalysisBudgetExceededException(AnalysisBudgetExceededReason.TokenLimit, "本局令牌预算不足，本次请求未发送。");
            state.Started++; state.InFlight++; state.Reserved += reservedTokens;
            return new(this, matchId, reservedTokens);
        }
    }

    /// <summary>Seeds persisted usage before this process has started any request for the match.</summary>
    public void SeedMatch(string matchId, int requestsStarted, long chargedTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        if (requestsStarted < 0 || chargedTokens < 0) throw new ArgumentOutOfRangeException(nameof(requestsStarted));
        lock (_sync)
        {
            if (_states.ContainsKey(matchId)) throw new InvalidOperationException("该局预算已经初始化。");
            _states[matchId] = new State { Started = requestsStarted, Charged = chargedTokens };
        }
    }

    public AnalysisBudgetSnapshot Snapshot(string matchId)
    {
        lock (_sync)
        {
            var state = _states.GetValueOrDefault(matchId) ?? new();
            return new(matchId, state.Started, state.Completed, state.Failed, state.Charged, state.Reserved, state.InFlight, state.Conservative);
        }
    }

    private void Settle(string matchId, long reservedTokens, long? actualTokens, bool success)
    {
        lock (_sync)
        {
            var state = _states[matchId];
            state.Reserved -= reservedTokens; state.InFlight--;
            if (!success) { state.Failed++; return; }
            state.Completed++;
            if (actualTokens is null) state.Conservative++;
            var charge = actualTokens ?? reservedTokens;
            state.Charged = charge > long.MaxValue - state.Charged ? long.MaxValue : state.Charged + charge;
        }
    }

    public sealed class AnalysisReservation : IDisposable
    {
        private readonly AnalysisBudget _owner;
        private readonly string _matchId;
        private readonly long _reserved;
        private int _settled;
        internal AnalysisReservation(AnalysisBudget owner, string matchId, long reserved)
            => (_owner, _matchId, _reserved) = (owner, matchId, reserved);
        public void Commit(long? actualTokens)
        {
            if (actualTokens < 0) throw new ArgumentOutOfRangeException(nameof(actualTokens));
            if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0) throw new InvalidOperationException("模型预算预留已经结算。");
            _owner.Settle(_matchId, _reserved, actualTokens, success: true);
        }
        public void Fail()
        {
            if (Interlocked.CompareExchange(ref _settled, 1, 0) == 0) _owner.Settle(_matchId, _reserved, null, success: false);
        }
        public void Dispose() => Fail();
    }
}
