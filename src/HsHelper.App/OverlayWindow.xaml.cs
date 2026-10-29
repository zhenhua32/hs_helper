using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HsHelper.App;

/// <summary>Passive, manually shown overlay. It never finds or sends input to the game.</summary>
public partial class OverlayWindow : Window
{
    private const int ExtendedStyle = -20;
    private const long TransparentStyle = 0x20;
    private const long NoActivateStyle = 0x08000000;
    private IntPtr _handle;
    private HwndSource? _windowSource;
    private readonly HwndSourceHook _windowHook;
    private bool _inMoveSize;
    private bool _fitting;
    private bool _fitQueued;
    private Window? _settings;
    private CheckBox? _throughCheck;
    private TextBlock? _settingsStatus;
    private bool _updatingSettings;
    private bool _closed;
    private double _scale = 1;
    private OverlayState? _pendingState;
    private int _updateQueued;

    public bool IsClickThrough { get; private set; }
    public double ScaleFactor => _scale;
    public string? InteractionError { get; private set; }
    public OverlayState State { get; private set; } = OverlayState.Empty;

    public OverlayWindow()
    {
        _windowHook = OverlayWindowProcedure;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(_handle);
            _windowSource?.AddHook(_windowHook);
            ApplyInteraction();
        };
        Loaded += (_, _) => FitToWorkArea();
        LocationChanged += (_, _) => { if (!_inMoveSize && !_fitting && IsLoaded) QueueFitToWorkArea(); };
        DpiChanged += (_, _) => QueueFitToWorkArea();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _settings?.Close(); };
        Closed += (_, _) =>
        {
            _closed = true; _settings?.Close(); _settings = null;
            if (_windowSource is { IsDisposed: false }) _windowSource.RemoveHook(_windowHook);
            _windowSource = null;
        };
        Update(OverlayState.Empty);
        RestorePlacement();
    }

    public void Update(OverlayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Interlocked.Exchange(ref _pendingState, state);
        if (Interlocked.Exchange(ref _updateQueued, 1) != 0) return;
        if (Dispatcher.CheckAccess()) ApplyPendingState();
        else
        {
            try { Dispatcher.BeginInvoke(new Action(ApplyPendingState), DispatcherPriority.Background); }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            { Interlocked.Exchange(ref _updateQueued, 0); Interlocked.Exchange(ref _pendingState, null); }
        }
    }

    private void ApplyPendingState()
    {
        // Release the queue slot before taking the latest state, so a racing producer is either
        // consumed here or schedules its own callback rather than leaving an unrendered state.
        Interlocked.Exchange(ref _updateQueued, 0);
        var state = Interlocked.Exchange(ref _pendingState, null);
        if (_closed || state is null) return;
        State = state;
        HeaderText.Text = state.Title;
        HandHeader.Text = $"我方手牌 · {state.Hand.Count}";
        HandList.ItemsSource = state.Hand.Count == 0 ? new[] { "尚无已知手牌" } : state.Hand;
        RemainingHeader.Text = "剩余牌库";
        RemainingList.ItemsSource = state.Remaining.Count == 0 ? new[] { "等待确认套牌与完整记录" } : state.Remaining;
        OpponentHeader.Text = "对手已出牌";
        OpponentList.ItemsSource = state.OpponentPlayed.Count == 0 ? new[] { "尚无公开出牌记录" } : state.OpponentPlayed;
        AdviceText.Text = string.IsNullOrWhiteSpace(state.Advice) ? "等待 AI 建议。" : state.Advice;
        AdviceText.ToolTip = state.Advice;
        QualityText.Text = state.Quality;
        QualityText.Foreground = state.IsComplete && state.IsStable
            ? new SolidColorBrush(Color.FromRgb(177, 222, 185))
            : new SolidColorBrush(Color.FromRgb(244, 208, 141));
    }

    public void SetScale(double scale)
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        _scale = OverlayLayout.ClampScale(scale);
        ContentRoot.LayoutTransform = new ScaleTransform(_scale, _scale);
        Width = 360 * _scale;
        Height = 620 * _scale;
        FitToWorkArea();
    }

    public void SetClickThrough(bool enabled)
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        IsClickThrough = enabled;
        ApplyInteraction();
        // A separate, taskbar-accessible settings window remains available even if hotkeys are occupied.
        if (enabled) OpenSettings();
        SynchronizeSettings();
    }

    public void OpenSettings()
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        if (_settings is not null) { _settings.Show(); _settings.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "悬浮窗设置", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "缩放", Margin = new Thickness(0, 0, 0, 6) });
        var slider = new Slider { Minimum = 0.65, Maximum = 1.8, Value = _scale, TickFrequency = 0.05, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 12) };
        slider.ValueChanged += (_, _) => SetScale(slider.Value);
        panel.Children.Add(slider);
        var alwaysOnTop = new CheckBox { Content = "始终置顶", IsChecked = Topmost, Margin = new Thickness(0, 0, 0, 10) };
        alwaysOnTop.Checked += (_, _) => Topmost = true;
        alwaysOnTop.Unchecked += (_, _) => Topmost = false;
        panel.Children.Add(alwaysOnTop);
        _throughCheck = new CheckBox { Content = "鼠标穿透（可在此窗口关闭）", IsChecked = IsClickThrough, Margin = new Thickness(0, 0, 0, 10) };
        _throughCheck.Checked += (_, _) => { if (!_updatingSettings) SetClickThrough(true); };
        _throughCheck.Unchecked += (_, _) => { if (!_updatingSettings) SetClickThrough(false); };
        panel.Children.Add(_throughCheck);
        _settingsStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_settingsStatus);
        var restore = new Button { Content = "恢复默认位置和大小", Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };
        restore.Click += (_, _) => { slider.Value = 1; RestorePlacement(); };
        panel.Children.Add(restore);
        var hide = new Button { Content = "隐藏悬浮窗", Padding = new Thickness(8) };
        hide.Click += (_, _) => { SetClickThrough(false); Hide(); _settings?.Close(); };
        panel.Children.Add(hide);
        _settings = new Window { Title = "猎人助手悬浮窗设置", Content = panel, Width = 340, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.CanMinimize, Topmost = true, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _settings.Closing += (_, _) => { if (!_closed) SetClickThrough(false); };
        _settings.Closed += (_, _) => { _settings = null; _throughCheck = null; _settingsStatus = null; };
        SynchronizeSettings();
        _settings.Show();
    }

    private void SynchronizeSettings()
    {
        _updatingSettings = true;
        try
        {
            if (_throughCheck is not null) _throughCheck.IsChecked = IsClickThrough;
            if (_settingsStatus is not null) _settingsStatus.Text = InteractionError ??
                (IsClickThrough ? "穿透已开启。可最小化设置窗，之后从任务栏恢复；关闭此窗会恢复可操作状态。" : "穿透已关闭，可拖动标题移动，拖动右下角调整大小。");
        }
        finally { _updatingSettings = false; }
    }

    private void ApplyInteraction()
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            var current = ReadWindowStyle(_handle).ToInt64();
            var updated = current | NoActivateStyle;
            updated = IsClickThrough ? updated | TransparentStyle : updated & ~TransparentStyle;
            Marshal.SetLastPInvokeError(0);
            var result = WriteWindowStyle(_handle, new IntPtr(updated));
            var error = Marshal.GetLastPInvokeError();
            if (result == IntPtr.Zero && error != 0) throw new Win32Exception(error);
            InteractionError = null;
            InteractionText.Text = IsClickThrough ? "鼠标穿透已开启 · 设置窗可关闭穿透" : "拖动标题移动 · 设置中调整缩放与穿透";
        }
        catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException)
        {
            IsClickThrough = false;
            InteractionError = "无法切换鼠标穿透，悬浮窗仍可通过主窗口隐藏。";
            InteractionText.Text = InteractionError;
        }
    }

    private void RestorePlacement()
    {
        SetScale(1);
        var area = CurrentWorkArea() ?? SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Top + 60;
        FitToWorkArea();
    }

    private void FitToWorkArea()
    {
        if (_closed || _fitting || _inMoveSize || CurrentWorkArea() is not { } area) return;
        _fitting = true;
        try
        {
            var minimumWidth = Math.Min(220, area.Width);
            var minimumHeight = Math.Min(300, area.Height);
            var placement = OverlayLayout.Fit(Left, Top, Math.Max(minimumWidth, Width), Math.Max(minimumHeight, Height),
                area.Left, area.Top, area.Width, area.Height);
            MinWidth = minimumWidth; MinHeight = minimumHeight;
            if (Math.Abs(Width - placement.Width) > 0.1) Width = placement.Width;
            if (Math.Abs(Height - placement.Height) > 0.1) Height = placement.Height;
            if (!double.IsFinite(Left) || Math.Abs(Left - placement.Left) > 0.1) Left = placement.Left;
            if (!double.IsFinite(Top) || Math.Abs(Top - placement.Top) > 0.1) Top = placement.Top;
        }
        finally { _fitting = false; }
    }

    private Rect? CurrentWorkArea()
    {
        if (_handle == IntPtr.Zero) return SystemParameters.WorkArea;
        if (_windowSource is not { IsDisposed: false, CompositionTarget: { } target }) return null;
        var monitor = MonitorFromWindow(_handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) ||
            info.Work.Right <= info.Work.Left || info.Work.Bottom <= info.Work.Top) return null;
        // Match WPF Window's own DeviceToLogicalUnits conversion, including the signed desktop origin.
        // Using monitor DPI independently would disagree with WPF in a system-DPI-aware process.
        var toDevice = target.TransformToDevice;
        if (!double.IsFinite(toDevice.M11) || !double.IsFinite(toDevice.M22) || toDevice.M11 <= 0 || toDevice.M22 <= 0) return null;
        var area = OverlayMonitorLayout.WorkAreaInLogicalUnits(new(info.Work.Left, info.Work.Top,
            info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), toDevice.M11, toDevice.M22);
        return new Rect(area.Left, area.Top, area.Width, area.Height);
    }

    private void QueueFitToWorkArea()
    {
        if (_closed || _fitQueued || Dispatcher.HasShutdownStarted) return;
        _fitQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _fitQueued = false;
            if (!_closed && !_inMoveSize) FitToWorkArea();
        }), DispatcherPriority.Background);
    }

    private IntPtr OverlayWindowProcedure(IntPtr handle, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (message == 0x0231) _inMoveSize = true; // WM_ENTERSIZEMOVE: let the window cross monitor edges freely.
        else if (message == 0x0232) { _inMoveSize = false; QueueFitToWorkArea(); }
        else if (message is 0x007E or 0x001A or 0x02E0) QueueFitToWorkArea(); // Display/work-area/DPI changes.
        return IntPtr.Zero; // WPF still handles its normal DPI update and suggested rectangle.
    }

    private void HeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsClickThrough && e.LeftButton == MouseButtonState.Pressed && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }
    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private void SettingsClicked(object sender, RoutedEventArgs e) { e.Handled = true; OpenSettings(); }

    private static IntPtr ReadWindowStyle(IntPtr handle) => IntPtr.Size == 8 ? GetWindowLongPtr(handle, ExtendedStyle) : new(GetWindowLong(handle, ExtendedStyle));
    private static IntPtr WriteWindowStyle(IntPtr handle, IntPtr value) => IntPtr.Size == 8 ? SetWindowLongPtr(handle, ExtendedStyle, value) : new(SetWindowLong(handle, ExtendedStyle, value.ToInt32()));
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

/// <summary>Pure screen-coordinate conversion; the scale is obtained from the current WPF HwndTarget.</summary>
public static class OverlayMonitorLayout
{
    public static OverlayPlacement WorkAreaInLogicalUnits(OverlayPlacement deviceArea, double pixelsPerDipX, double pixelsPerDipY)
    {
        ArgumentNullException.ThrowIfNull(deviceArea);
        if (!double.IsFinite(pixelsPerDipX) || !double.IsFinite(pixelsPerDipY) || pixelsPerDipX <= 0 || pixelsPerDipY <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDipX), "DPI 缩放必须为有限正数。");
        if (!double.IsFinite(deviceArea.Left) || !double.IsFinite(deviceArea.Top) || !double.IsFinite(deviceArea.Width) ||
            !double.IsFinite(deviceArea.Height) || deviceArea.Width <= 0 || deviceArea.Height <= 0)
            throw new ArgumentException("显示器工作区必须具有有限坐标和正尺寸。", nameof(deviceArea));
        return new(deviceArea.Left / pixelsPerDipX, deviceArea.Top / pixelsPerDipY,
            deviceArea.Width / pixelsPerDipX, deviceArea.Height / pixelsPerDipY);
    }
}
