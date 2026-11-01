using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace HsHelper.App;

/// <summary>Opt-in mouse/keyboard fallback for unresponsive external automation clients.</summary>
public static class InputCompatibility
{
    private static readonly ConditionalWeakTable<Window, WindowHook> Hooks = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        // The fallback also avoids GPU/virtual-display stalls; keep the normal entry unchanged.
        System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        // Also cover subsequently opened tools and overlay windows. The main window is
        // attached explicitly before Show to intercept subsequent provider requests.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach((Window)sender)));
    }

    public static void Attach(Window window)
    {
        if (_enabled) Hooks.GetValue(window, static value => new WindowHook(value));
    }

    private sealed class WindowHook
    {
        private readonly Window _window;
        private readonly HwndSourceHook _hook;
        private HwndSource? _source;

        public WindowHook(Window window)
        {
            _window = window;
            _hook = WindowProcedure;
            window.SourceInitialized += SourceInitialized;
            window.Closed += Closed;
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Connect();
        }

        private void SourceInitialized(object? sender, EventArgs e) => Connect();
        private void Connect()
        {
            if (_source is not null) return;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
            _source?.AddHook(_hook);
        }

        private static IntPtr WindowProcedure(IntPtr handle, int message, IntPtr key, IntPtr data, ref bool handled)
        {
            // WM_GETOBJECT: block UIA roots and the MSAA bridge only in this explicit
            // fallback. Other native messages and normal-mode accessibility are unchanged.
            if (message == 0x003D && unchecked((int)data.ToInt64()) is -25 or -4)
                handled = true;
            return IntPtr.Zero;
        }

        private void Closed(object? sender, EventArgs e)
        {
            _window.SourceInitialized -= SourceInitialized;
            _window.Closed -= Closed;
            if (_source is { IsDisposed: false }) _source.RemoveHook(_hook);
            _source = null;
            Hooks.Remove(_window);
        }
    }
}
