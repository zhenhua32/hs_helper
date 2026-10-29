using System.Diagnostics;
using Microsoft.Win32;

namespace HsHelper.Ingestion;

public sealed record GameInstallation(string GameDirectory, string? ClientVersion, string Source, string? ExecutableVersion = null);

public sealed record PowerLogDiagnostic(string Path, long Length, DateTime LastWriteUtc)
{
    public string? SessionDirectory => System.IO.Path.GetDirectoryName(Path);
}

public sealed record GameDiagnosticReport(
    GameInstallation? Installation,
    PowerLogDiagnostic? PowerLog,
    string ConfigurationPath,
    bool ConfigurationEnabled,
    string? LogSearchRoot,
    IReadOnlyList<string> Messages,
    string? ObservedLogBuild = null);

/// <summary>Read-only diagnostics. Calling Inspect never changes the game or its configuration.</summary>
public static class HearthstoneDiagnostics
{
    public static string DefaultConfigurationPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Blizzard", "Hearthstone", "log.config");

    public static GameDiagnosticReport Inspect(string? gameDirectory = null, string? configPath = null)
    {
        var messages = new List<string>();
        var installation = HearthstoneLocator.Find(gameDirectory);
        var configurationPath = System.IO.Path.GetFullPath(configPath ?? DefaultConfigurationPath);
        var localDataRoot = System.IO.Path.GetDirectoryName(DefaultConfigurationPath)!;
        var root = !string.IsNullOrWhiteSpace(gameDirectory)
            ? System.IO.Path.GetFullPath(gameDirectory)
            : installation?.GameDirectory;
        var powerLog = root is null ? null : LogSessionDiscovery.FindLatestPowerLog(root);
        if (powerLog is null && string.IsNullOrWhiteSpace(gameDirectory))
        {
            var localLog = LogSessionDiscovery.FindLatestPowerLog(localDataRoot);
            if (localLog is not null || root is null && Directory.Exists(localDataRoot)
                || Directory.Exists(System.IO.Path.Combine(localDataRoot, "Logs")))
            {
                root = localDataRoot;
                powerLog = localLog;
            }
        }

        if (installation is null)
            messages.Add("未检测到可验证的炉石安装目录，可手动指定安装目录或日志目录。");
        if (powerLog is null)
            messages.Add("未找到 Power.log。请检查日志配置并在重启游戏后完成一局对局。");
        else if (powerLog.Length == 0)
            messages.Add("Power.log 当前为空，等待游戏写入对局事件。");
        else if (DateTime.UtcNow - powerLog.LastWriteUtc > TimeSpan.FromMinutes(5))
            messages.Add("日志超过五分钟未更新，可用于离线回放；实时连接尚未确认。");

        var observedBuild = powerLog is null ? null : ClientBuildMetadata.ReadPowerLogBuild(powerLog.Path);
        if (observedBuild is not null)
            messages.Add($"该日志会话报告炉石 build {observedBuild}；安装文件版本与当前卡库仍需对应核对。");
        else if (installation?.ClientVersion is null)
            messages.Add("炉石客户端 build 尚未确认，可执行文件的 Unity 版本不能作为卡牌 build。");
        var enabled = false;
        try
        {
            enabled = File.Exists(configurationPath) && LogConfiguration.IsPowerLoggingEnabled(File.ReadAllText(configurationPath));
            if (!enabled)
                messages.Add("未确认 Power 日志已开启，可先预览日志配置方案。当前诊断没有修改配置。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add("无法读取日志配置：" + ex.Message);
        }

        return new(installation, powerLog, configurationPath, enabled, root, messages.AsReadOnly(), observedBuild);
    }
}

public static class HearthstoneLocator
{
    public static GameInstallation? Find(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return InspectCandidate(explicitPath, "manual");

        foreach (var process in Process.GetProcessesByName("Hearthstone"))
        {
            using (process)
            {
                try
                {
                    var candidate = InspectCandidate(process.MainModule?.FileName, "running_process");
                    if (candidate is not null)
                        return candidate;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or exited processes must not prevent manual discovery.
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var path in RegistryCandidates())
            {
                var candidate = InspectCandidate(path, "registry");
                if (candidate is not null)
                    return candidate;
            }
        }

        foreach (var parent in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var relative in new[] { "Hearthstone", System.IO.Path.Combine("Battle.net", "Hearthstone") })
            {
                var candidate = InspectCandidate(System.IO.Path.Combine(parent, relative), "default_installation_path");
                if (candidate is not null)
                    return candidate;
            }
        }
        return null;
    }

    private static GameInstallation? InspectCandidate(string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var normalized = System.IO.Path.GetFullPath(path.Trim('"'));
            var executable = Directory.Exists(normalized)
                ? System.IO.Path.Combine(normalized, "Hearthstone.exe") : normalized;
            if (!File.Exists(executable) || !System.IO.Path.GetFileName(executable).Equals("Hearthstone.exe", StringComparison.OrdinalIgnoreCase))
                return null;
            var version = FileVersionInfo.GetVersionInfo(executable);
            var directory = System.IO.Path.GetDirectoryName(executable)!;
            return new(directory, ClientBuildMetadata.ReadBuildInfoVersion(directory), source, version.ProductVersion ?? version.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryCandidates()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone");
                if (key?.GetValue("InstallLocation") is string location)
                    result.Add(location);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Registry access is optional; the explicit path remains available.
            }
        }
        return result;
    }
}

public static class LogSessionDiscovery
{
    /// <summary>Searches only the supplied file/directory, its Logs directory, and bounded session descendants.</summary>
    public static PowerLogDiagnostic? FindLatestPowerLog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = System.IO.Path.GetFullPath(root);
        if (File.Exists(root))
            return System.IO.Path.GetFileName(root).Equals("Power.log", StringComparison.OrdinalIgnoreCase) ? Describe(root) : null;
        if (!Directory.Exists(root))
            return null;

        var logRoot = Directory.Exists(System.IO.Path.Combine(root, "Logs")) ? System.IO.Path.Combine(root, "Logs") : root;
        var found = new List<PowerLogDiagnostic>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((logRoot, 0));
        while (queue.TryDequeue(out var current))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(current.Directory, "Power.log", SearchOption.TopDirectoryOnly))
                {
                    var diagnostic = Describe(file);
                    if (diagnostic is not null)
                        found.Add(diagnostic);
                }
                if (current.Depth >= 4)
                    continue;
                foreach (var directory in Directory.EnumerateDirectories(current.Directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        queue.Enqueue((directory, current.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A transient or protected session directory does not hide other sessions.
            }
        }
        return found.OrderByDescending(x => x.LastWriteUtc).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static PowerLogDiagnostic? Describe(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? new(info.FullName, info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
