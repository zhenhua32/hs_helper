using System.Windows;
using System.IO;
using System.Text.Json;
namespace HsHelper.App;
public partial class App : Application
{
    public static AppLaunchSettings? LaunchSettings { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppLaunchSettings.Parse(e.Args);
        LaunchSettings = options;
        if (options.Diagnostic)
        {
            try
            {
                var report = JsonSerializer.Serialize(new
                {
                    version = AppVersion.DisplayVersion,
                    dataDirectory = options.DataDirectory,
                    baseDirectory = AppContext.BaseDirectory,
                    is64Bit = Environment.Is64BitProcess,
                    dpiManifest = "PerMonitorV2,PerMonitor",
                    error = options.Error
                }, new JsonSerializerOptions { WriteIndented = true });
                if (options.DiagnosticOutput is { } path)
                {
                    // Never overwrite an existing user file, including configuration or credentials.
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream);
                    writer.Write(report);
                }
                Console.WriteLine(report);
                Shutdown(options.Error is null ? 0 : 2);
            }
            catch (Exception) { Console.Error.WriteLine("启动诊断无法写入新报告文件。"); Shutdown(2); }
            return;
        }
        if (options.Error is not null)
        {
            MessageBox.Show("启动参数无效。可使用 --data-dir 指定数据目录，或 --diagnostic-output 生成诊断报告。", "炉石猎人助手");
            Shutdown(2); return;
        }
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("无法初始化助手，请检查数据目录是否可读写。", "炉石猎人助手");
            Shutdown(1);
        }
    }
}
