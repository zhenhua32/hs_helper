using HsHelper.Core;

namespace HsHelper.App;

public partial class MainWindow
{
    private DateTimeOffset? _lastStateObservedAt;
    private DateTimeOffset? _lastLogActivityAt;
    private DateTimeOffset? _lastReadAttemptAt;
    private string? _currentLogFile;
    private long _currentLogOffset;

    private void ResetConnectionClock()
    {
        _lastStateObservedAt = null; _lastLogActivityAt = null; _lastReadAttemptAt = null;
        _currentLogFile = null; _currentLogOffset = 0;
    }
    private void UpdateConnectionStatus()
    {
        if (ConnectionText is null) return;
        var mode = _loadingSource ? "正在导入日志" : !_timer.IsEnabled ? "监听已停止" : _currentLogFile is null ? "等待 Power.log" :
            _lastLogActivityAt is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(30) ? "连接中，日志暂未新增" : "正在监听";
        var snapshot = DisplaySnapshot;
        var deck = _historyContext?.Deck ?? _sourceDeckVersion;
        var updated = _historyContext is not null ? _historyObservedAt : _lastStateObservedAt;
        var format = snapshot.Format switch { "STANDARD" => "标准", "WILD" => "狂野", null => "模式未知", _ => snapshot.Format };
        var gameType = snapshot.GameType switch { "GT_RANKED" => "天梯", "GT_CASUAL" => "休闲", null => "对局类型未知", _ => "其他对局" };
        ConnectionText.Text = $"{mode} · {format} / {gameType} · " +
            $"套牌：{deck?.Name ?? "未确认"} · 局面更新时间（本机）：{(updated is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : "暂无")}" +
            (_lastReadAttemptAt is { } read ? $" · 最近读取 {read.ToLocalTime():HH:mm:ss}" : "") +
            (_currentLogFile is null ? "" : $"\n{_currentLogOffset:N0} 字节 · {_currentLogFile}");
        if (_historyContext is not null) ConnectionText.Text = "历史局面 · " + ConnectionText.Text;
    }
}
