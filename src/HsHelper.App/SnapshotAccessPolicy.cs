using HsHelper.Core;

namespace HsHelper.App;

public enum SnapshotViewKind { Unavailable, Live, Review }
public sealed record SnapshotAccess(SnapshotViewKind Kind, bool CanAnalyze, string Label, string Reason)
{
    public bool IsHistorical => Kind == SnapshotViewKind.Review;
}

/// <summary>A readable file and a stable snapshot do not by themselves prove a current live connection.</summary>
public static class SnapshotAccessPolicy
{
    private const string VisibleTurnAdvice = "对手有未公开的准备效果。可按已知牌面请求策略建议。";

    public static SnapshotAccess Evaluate(GameSnapshot snapshot, DateTimeOffset now,
        bool historical, bool fileReplay, bool listening, bool loading, bool catchingUp, bool sourceFailed,
        DateTimeOffset? lastSuccessfulRead, DateTimeOffset? fileLastWrite)
    {
        snapshot = AttachmentVisibility.Project(snapshot);
        var usable = snapshot.Entities.Count > 0 && snapshot.CanAnalyzeDecision &&
            snapshot.Quality.HasLocalPerspective && !snapshot.IsSpectating;
        if (loading) return new(SnapshotViewKind.Unavailable, false, "正在读取日志", "请等待日志追赶完成。");
        if (historical || fileReplay)
            return new(SnapshotViewKind.Review, usable, historical ? "历史局面" : "文件回放",
                snapshot.CanAnalyzeVisibleTurn ? VisibleTurnAdvice :
                usable ? "仅依据这个固定时刻复盘，不作为当前可执行步骤。" : BlockedReason(snapshot));
        if (!listening) return new(SnapshotViewKind.Unavailable, false, "最后观察局面", "监听已停止。可重新连接，或显式复盘最后局面。");
        if (sourceFailed) return new(SnapshotViewKind.Unavailable, false, "连接异常", "当前连接无法确认，请重建或复盘最后局面。");
        if (catchingUp) return new(SnapshotViewKind.Unavailable, false, "日志追赶中", "正在读取已有记录，尚未确认实时位置。");
        if (!HasRecentLog(now, lastSuccessfulRead, fileLastWrite))
            return new(SnapshotViewKind.Unavailable, false, "实时状态未确认", "日志读取或文件更新已停滞；已有局面仍可用于显式复盘。");
        if (snapshot.Phase == GamePhase.Complete)
            return new(SnapshotViewKind.Review, usable, "对局已结束",
                usable ? "这是结束时的局面，仅提供复盘建议。" : BlockedReason(snapshot));
        return new(SnapshotViewKind.Live, usable, "最近实时观察", snapshot.CanAnalyzeVisibleTurn ? VisibleTurnAdvice :
            usable ? "每次建议仍绑定当前观察版本。" : BlockedReason(snapshot));
    }

    private static string BlockedReason(GameSnapshot snapshot)
    {
        if (snapshot.IsSpectating) return "当前是观战视角，分析需要我方玩家视角。";
        if (!snapshot.Quality.HasGameStart || snapshot.Entities.Count == 0) return "尚未读到完整开局，请检查“对局与日志”。";
        if (!snapshot.Quality.HasLocalPerspective) return "助手尚未确认我方玩家，请在“对局与日志”核对我方视角。";
        if (snapshot.Quality.OpenBlockCount > 0) return "游戏动作仍在结算，请等游戏恢复操作后再试。";
        if (!snapshot.Quality.IsComplete) return "日志中的局面尚不完整。" + string.Join(" ", snapshot.Quality.Warnings.Take(2));
        return "当前阶段尚未形成可分析的局面。";
    }

    internal static bool HasRecentLog(DateTimeOffset now, DateTimeOffset? read, DateTimeOffset? write) =>
        read.HasValue && now - read.Value >= TimeSpan.Zero && now - read.Value <= TimeSpan.FromSeconds(5) &&
        write.HasValue && now - write.Value >= TimeSpan.Zero && now - write.Value <= TimeSpan.FromSeconds(90);
}
