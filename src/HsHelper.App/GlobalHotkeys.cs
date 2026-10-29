using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HsHelper.App;

public sealed record HotkeyBindingStatus(string Name, string Shortcut, bool Registered, string? Error);

/// <summary>Optional application hotkeys; registration conflicts never prevent startup.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private sealed record Binding(int Id, string Name, string Shortcut, uint Key, Action Callback);
    private readonly Window _owner;
    private readonly HwndSourceHook _hook;
    private readonly Binding[] _bindings;
    private readonly Dictionary<int, Action> _registered = [];
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;
    private static int _nextId = 0x4800;
    private IReadOnlyList<HotkeyBindingStatus> _statuses = [];

    public IReadOnlyList<HotkeyBindingStatus> Bindings => _statuses;
    public string Status => _statuses.Count == 0 ? "快捷键等待主窗口初始化。" : string.Join("；", _statuses.Select(value =>
        value.Error is not null ? $"{value.Shortcut} {value.Error}" :
            value.Registered ? $"{value.Shortcut} {value.Name}" : $"{value.Shortcut} 不可用"));
    public event EventHandler? StatusChanged;

    public GlobalHotkeys(Window owner, Action toggleVisibility, Action requestAdvice, Action? toggleClickThrough = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(toggleVisibility);
        ArgumentNullException.ThrowIfNull(requestAdvice);
        _owner = owner;
        _hook = WindowProcedure;
        var bindings = new List<Binding>
        {
            new(Interlocked.Increment(ref _nextId), "显示或隐藏悬浮窗", "Ctrl+Shift+H", 0x48, toggleVisibility),
            new(Interlocked.Increment(ref _nextId), "请求局面建议", "Ctrl+Shift+A", 0x41, requestAdvice)
        };
        if (toggleClickThrough is not null) bindings.Add(new(Interlocked.Increment(ref _nextId), "切换鼠标穿透", "Ctrl+Shift+T", 0x54, toggleClickThrough));
        _bindings = bindings.ToArray();
        _owner.SourceInitialized += OwnerInitialized;
        _owner.Closed += OwnerClosed;
        if (new WindowInteropHelper(owner).Handle != IntPtr.Zero) Register();
    }

    private void OwnerInitialized(object? sender, EventArgs e) => Register();
    private void OwnerClosed(object? sender, EventArgs e) => Dispose();
    private void Register()
    {
        if (_disposed || _source is not null) return;
        _handle = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(_handle);
        if (_handle == IntPtr.Zero || _source is null)
        {
            _statuses = Array.AsReadOnly(_bindings.Select(binding => new HotkeyBindingStatus(binding.Name, binding.Shortcut, false, "窗口句柄尚不可用")).ToArray());
            Notify(); return;
        }
        _source.AddHook(_hook);
        var statuses = new List<HotkeyBindingStatus>();
        foreach (var binding in _bindings)
        {
            var success = RegisterHotKey(_handle, binding.Id, 0x0002 | 0x0004 | 0x4000, binding.Key);
            if (success) _registered.Add(binding.Id, binding.Callback);
            var error = success ? null : $"组合键已被占用或无法注册（{Marshal.GetLastPInvokeError()}）";
            statuses.Add(new(binding.Name, binding.Shortcut, success, error));
        }
        _statuses = statuses.AsReadOnly();
        Notify();
    }

    private IntPtr WindowProcedure(IntPtr handle, int message, IntPtr key, IntPtr data, ref bool handled)
    {
        if (message != 0x0312 || _disposed || !_registered.TryGetValue(key.ToInt32(), out var callback)) return IntPtr.Zero;
        handled = true;
        try { callback(); }
        catch
        {
            var binding = _bindings.First(value => value.Id == key.ToInt32());
            _statuses = Array.AsReadOnly(_statuses.Select(status => status.Name == binding.Name ? status with
                { Error = "动作未完成，请在主窗口重试。" } : status).ToArray());
            Notify();
        }
        return IntPtr.Zero;
    }

    private void Notify()
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* Optional status rendering must not break native message dispatch. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.SourceInitialized -= OwnerInitialized;
        _owner.Closed -= OwnerClosed;
        foreach (var id in _registered.Keys) UnregisterHotKey(_handle, id);
        _registered.Clear();
        if (_source is not null && !_source.IsDisposed) _source.RemoveHook(_hook);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr handle, int id);
}
