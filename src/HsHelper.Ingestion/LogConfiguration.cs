using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HsHelper.Ingestion;

public sealed record LogConfigurationPlan(string Path, string OriginalText, string ProposedText, bool OriginalExists)
{
    public bool HasChanges => !OriginalText.Equals(ProposedText, StringComparison.Ordinal);
    public string RestartNotice => "日志配置在炉石重启后生效。";
}

public sealed record LogConfigurationBackup(string Path, string? BackupPath, bool OriginalExisted, string AppliedHash);

/// <summary>Preview is side-effect free; applying or restoring must be invoked explicitly by the caller.</summary>
public static class LogConfiguration
{
    private static readonly string[] RequiredSections = ["Power", "LoadingScreen"];
    private static readonly UTF8Encoding Utf8 = new(false);

    public static LogConfigurationPlan CreatePlan(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var exists = File.Exists(path);
        var original = exists ? File.ReadAllText(path) : "";
        return new(path, original, BuildProposedText(original), exists);
    }

    public static bool IsPowerLoggingEnabled(string text)
    {
        var values = ReadSection(text, "Power");
        return values.TryGetValue("LogLevel", out var level) && level == "1"
            && values.TryGetValue("FilePrinting", out var printing) && bool.TryParse(printing, out var file) && file
            && values.TryGetValue("Verbose", out var verbose) && bool.TryParse(verbose, out var detail) && detail;
    }

    public static async Task<LogConfigurationBackup?> ApplyAsync(LogConfigurationPlan plan, CancellationToken cancellationToken = default)
    {
        var exists = File.Exists(plan.Path);
        var current = exists ? await File.ReadAllTextAsync(plan.Path, cancellationToken) : "";
        if (exists != plan.OriginalExists || current != plan.OriginalText)
            throw new InvalidOperationException("日志配置自预览后已发生变化，请重新生成预览。");
        if (!plan.HasChanges)
            return null;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(plan.Path)!);
        string? backupPath = null;
        if (exists)
        {
            backupPath = plan.Path + ".hshelper-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(plan.Path, backupPath, overwrite: false);
        }
        await AtomicWriteAsync(plan.Path, plan.ProposedText, cancellationToken);
        return new(plan.Path, backupPath, exists, Hash(plan.ProposedText));
    }

    public static async Task RestoreAsync(LogConfigurationBackup backup, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backup.Path) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(backup.Path, cancellationToken))) != backup.AppliedHash)
            throw new InvalidOperationException("当前日志配置已改变，拒绝覆盖后续修改。");
        if (backup.OriginalExisted)
        {
            if (backup.BackupPath is null || !File.Exists(backup.BackupPath))
                throw new FileNotFoundException("未找到原日志配置备份。", backup.BackupPath);
            // Restore the original bytes, including its encoding, rather than normalizing the original file.
            var temporary = backup.Path + ".restore-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(backup.BackupPath, temporary);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, backup.Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(backup.Path);
        }
    }

    private static string BuildProposedText(string original)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Length == 0 ? new List<string>() : original.Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var section in RequiredSections)
        {
            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LogLevel"] = "1", ["FilePrinting"] = "True", ["ConsolePrinting"] = "False",
                ["ScreenPrinting"] = "False", ["Verbose"] = section == "Power" ? "True" : "False"
            };
            var indices = lines.Select((line, index) => (line, index))
                .Where(x => SectionName(x.line)?.Equals(section, StringComparison.OrdinalIgnoreCase) == true)
                .Select(x => x.index).ToArray();
            if (indices.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
                lines.Add("[" + section + "]");
                foreach (var value in desired) lines.Add(value.Key + "=" + value.Value);
                lines.Add("");
                continue;
            }

            // Walk backwards so insertions in a later duplicate section do not invalidate earlier offsets.
            foreach (var start in indices.Reverse())
            {
                var end = start + 1;
                while (end < lines.Count && SectionName(lines[end]) is null) end++;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = start + 1; i < end; i++)
                {
                    var match = Regex.Match(lines[i], @"^(\s*)([^#;=\s][^=]*?)\s*=\s*([^#;]*)(.*)$");
                    if (!match.Success) continue;
                    var key = match.Groups[2].Value.Trim();
                    if (!desired.TryGetValue(key, out var wanted)) continue;
                    seen.Add(key);
                    if (!match.Groups[3].Value.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        lines[i] = match.Groups[1].Value + key + "=" + wanted + (match.Groups[4].Length == 0 ? "" : " " + match.Groups[4].Value);
                }
                foreach (var missing in desired.Where(x => !seen.Contains(x.Key)))
                    lines.Insert(end++, missing.Key + "=" + missing.Value);
            }
        }
        return string.Join(newline, lines);
    }

    private static Dictionary<string, string> ReadSection(string text, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var active = false;
        foreach (var line in text.Split('\n'))
        {
            var name = SectionName(line);
            if (name is not null)
            {
                active = name.Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!active || line.TrimStart().StartsWith(';') || line.TrimStart().StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals < 1) continue;
            result[line[..equals].Trim()] = line[(equals + 1)..].Split(';', '#')[0].Trim();
        }
        return result;
    }

    private static string? SectionName(string line)
    {
        var match = Regex.Match(line.Trim(), @"^\[([^\]]+)\](?:\s*[;#].*)?$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text)));

    private static async Task AtomicWriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temporary = path + ".write-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text, Utf8, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
