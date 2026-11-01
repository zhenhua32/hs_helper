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
    public static SnapshotAccess Evaluate(GameSnapshot snapshot, DateTimeOffset now,
        bool historical, bool fileReplay, bool listening, bool loading, bool catchingUp, bool sourceFailed,
        DateTimeOffset? lastSuccessfulRead, DateTimeOffset? fileLastWrite)
    {
        snapshot = AttachmentVisibility.Project(snapshot);
        var usable = snapshot.Entities.Count > 0 && snapshot.Quality.IsStable &&
            snapshot.Quality.HasLocalPerspective && !snapshot.IsSpectating;
        if (loading) return new(SnapshotViewKind.Unavailable, false, "正在读取日志", "请等待日志追赶完成。");
        if (historical || fileReplay)
            return new(SnapshotViewKind.Review, usable, historical ? "历史局面" : "文件回放",
                usable ? "仅依据这个固定时刻复盘，不作为当前可执行步骤。" : "请先确认视角并选择稳定的历史局面。");
        if (!listening) return new(SnapshotViewKind.Unavailable, false, "最后观察局面", "监听已停止。可重新连接，或显式复盘最后局面。");
        if (sourceFailed) return new(SnapshotViewKind.Unavailable, false, "连接异常", "当前连接无法确认，请重建或复盘最后局面。");
        if (catchingUp) return new(SnapshotViewKind.Unavailable, false, "日志追赶中", "正在读取已有记录，尚未确认实时位置。");
        var readAge = lastSuccessfulRead is { } read ? now - read : (TimeSpan?)null;
        var writeAge = fileLastWrite is { } write ? now - write : (TimeSpan?)null;
        if (readAge is null || readAge < TimeSpan.Zero || readAge > TimeSpan.FromSeconds(5) ||
            writeAge is null || writeAge < TimeSpan.Zero || writeAge > TimeSpan.FromSeconds(90))
            return new(SnapshotViewKind.Unavailable, false, "实时状态未确认", "日志读取或文件更新已停滞；已有局面仍可用于显式复盘。");
        if (snapshot.Phase == GamePhase.Complete)
            return new(SnapshotViewKind.Review, usable, "对局已结束", "这是结束时的局面，仅提供复盘建议。");
        return new(SnapshotViewKind.Live, usable, "最近实时观察", usable ? "每次建议仍绑定当前观察版本。" : "等待结算结束并确认我方视角。");
    }
}
