using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Ingestion;
using Microsoft.Win32;

namespace HsHelper.App;

/// <summary>Review first, then explicitly apply or restore. Opening the window never changes game configuration.</summary>
public sealed class LogSetupWindow : Window
{
    private readonly TextBox _path = new();
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "备份并启用日志", IsEnabled = false };
    private readonly Button _restore = new() { Content = "恢复上次原配置", IsEnabled = false };
    private readonly string _receiptPath;
    private LogConfigurationPlan? _plan;
    private LogConfigurationBackup? _backup;
    private long _revision;
    private bool _busy;
    private Task? _pendingWrite;

    public string ConfigurationPath { get => _path.Text; set => _path.Text = value; }
    public string StatusText => _status.Text;
    public bool CanApply => _apply.IsEnabled;
    public bool CanRestore => _restore.IsEnabled;
    public Task WaitForPendingWriteAsync() => _pendingWrite ?? Task.CompletedTask;

    public LogSetupWindow(string configurationPath, string dataDirectory)
    {
        InputCompatibility.Attach(this);
        Title = "炉石日志设置与恢复"; Width = 760; Height = 650; MinWidth = 500; MinHeight = 400;
        _receiptPath = Path.Combine(Path.GetFullPath(dataDirectory), "log-configuration-receipt.json");
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var pick = new Button { Content = "选择 log.config" };
        var refresh = new Button { Content = "重新预览" };
        var top = new DockPanel(); DockPanel.SetDock(pick, Dock.Right); DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(pick); top.Children.Add(refresh); top.Children.Add(_path); root.Children.Add(top);
        var notice = new TextBlock { Text = "先核对目标路径和建议内容，再点击启用。修改在炉石重启后生效，助手不会关闭或操作游戏。恢复会检查文件是否被其他程序改动。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        Grid.SetRow(notice, 1); root.Children.Add(notice); Grid.SetRow(_preview, 2); root.Children.Add(_preview);
        Grid.SetRow(_status, 3); _status.Margin = new Thickness(0, 10, 0, 8); root.Children.Add(_status);
        var buttons = new WrapPanel(); buttons.Children.Add(_apply); buttons.Children.Add(_restore);
        Grid.SetRow(buttons, 4); root.Children.Add(buttons); Content = root;
        _path.TextChanged += (_, _) => { _revision++; _plan = null; _apply.IsEnabled = false; };
        ConfigurationPath = configurationPath;
        pick.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "炉石日志配置|log.config", CheckFileExists = false, FileName = "log.config" };
            if (dialog.ShowDialog(this) == true) { ConfigurationPath = dialog.FileName; await Run(ReloadPreviewAsync); }
        };
        refresh.Click += async (_, _) => await Run(ReloadPreviewAsync);
        _apply.Click += async (_, _) => await Run(ApplyConfigurationAsync);
        _restore.Click += async (_, _) => await Run(RestoreConfigurationAsync);
        Loaded += async (_, _) => await Run(ReloadPreviewAsync);
    }

    public async Task ReloadPreviewAsync()
    {
        var path = ValidatePath(ConfigurationPath); var revision = _revision;
        var plan = await Task.Run(() => LogConfiguration.CreatePlan(path));
        LogConfigurationBackup? backup = null;
        if (File.Exists(_receiptPath))
        {
            var saved = JsonSerializer.Deserialize<LogConfigurationBackup>(await File.ReadAllTextAsync(_receiptPath));
            if (saved is not null && Path.GetFullPath(saved.Path).Equals(path, StringComparison.OrdinalIgnoreCase)) backup = saved;
        }
        if (revision != _revision) return;
        _plan = plan; _backup = backup;
        _preview.Text = $"目标：{plan.Path}\n\n建议内容：\n{plan.ProposedText}";
        _status.Text = plan.HasChanges ? "预览尚未写入；启用时先备份原文件。" : "当前配置已满足日志要求，无需改动。";
        UpdateButtons();
    }

    public Task ApplyConfigurationAsync() => TrackWriteAsync(ApplyCoreAsync);
    private async Task ApplyCoreAsync()
    {
        if (_busy) return;
        var plan = _plan ?? throw new InvalidOperationException("路径已变化，请重新预览后再启用。");
        if (!ValidatePath(ConfigurationPath).Equals(plan.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标已变化，请重新预览。");
        _busy = true; UpdateButtons();
        try
        {
            var backup = await LogConfiguration.ApplyAsync(plan);
            if (backup is not null)
            {
                _backup = backup;
                Directory.CreateDirectory(Path.GetDirectoryName(_receiptPath)!);
                var temporary = _receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(backup)); File.Move(temporary, _receiptPath, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                _status.Text = "日志配置已写入并保存恢复记录。请在方便时手动重启炉石。\n备份：" + (backup.BackupPath ?? "原文件不存在，恢复时移除新文件。");
            }
            else _status.Text = "当前已满足要求，没有修改。";
            _plan = LogConfiguration.CreatePlan(plan.Path);
        }
        finally { _busy = false; UpdateButtons(); }
    }

    public Task RestoreConfigurationAsync() => TrackWriteAsync(RestoreCoreAsync);
    private async Task RestoreCoreAsync()
    {
        if (_busy) return;
        var backup = _backup ?? throw new InvalidOperationException("尚无对应的恢复记录。");
        if (!ValidatePath(ConfigurationPath).Equals(Path.GetFullPath(backup.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标与恢复记录不一致。");
        _busy = true; UpdateButtons();
        try
        {
            await LogConfiguration.RestoreAsync(backup);
            _backup = null;
            if (File.Exists(_receiptPath)) File.Delete(_receiptPath);
            _plan = LogConfiguration.CreatePlan(backup.Path);
            _status.Text = "原日志配置已恢复，请在方便时手动重启炉石。";
        }
        finally { _busy = false; UpdateButtons(); }
    }

    public static string ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.GetFileName(full).Equals("log.config", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择完整的 log.config 文件路径。");
        return full;
    }
    private void UpdateButtons()
    {
        IsEnabled = !_busy;
        _apply.IsEnabled = !_busy && _plan is { HasChanges: true };
        _restore.IsEnabled = !_busy && _backup is not null;
    }
    private async Task TrackWriteAsync(Func<Task> action)
    {
        if (_busy) return;
        var operation = action(); _pendingWrite = operation;
        try { await operation; }
        finally { if (ReferenceEquals(_pendingWrite, operation)) _pendingWrite = null; }
    }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _status.Text = "操作未完成：" + ex.Message; }
    }
}
