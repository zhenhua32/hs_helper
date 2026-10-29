using System.Text;
using System.Text.RegularExpressions;

namespace HsHelper.Ingestion;

/// <summary>Game build metadata is distinct from the Unity executable's file version.</summary>
public static class ClientBuildMetadata
{
    public static string? ReadBuildInfoVersion(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, ".build.info");
        if (!File.Exists(path)) return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            var header = reader.ReadLine()?.Split('|');
            if (header is null) return null;
            int Index(string name) => Array.FindIndex(header, value => value.Split('!')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
            var version = Index("Version");
            var active = Index("Active");
            var product = Index("Product");
            if (version < 0) return null;
            for (var count = 0; count < 128 && reader.ReadLine() is { } line; count++)
            {
                var fields = line.Split('|');
                if (version >= fields.Length || active >= fields.Length || product >= fields.Length) continue;
                if (active >= 0 && fields[active] != "1") continue;
                if (product >= 0 && !fields[product].StartsWith("hs", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(fields[version], @"^\d+(?:\.\d+){3}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    return fields[version];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing metadata does not prevent log diagnostics.
        }
        return null;
    }

    public static string? ReadPowerLogBuild(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            for (var count = 0; count < 4096 && reader.ReadLine() is { } line; count++)
            {
                if (!line.Contains("GameState.DebugPrintGame", StringComparison.Ordinal)) continue;
                var match = Regex.Match(line, @"\bBuildNumber\s*=\s*(\d+)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A rotated or locked file can be diagnosed on the next refresh.
        }
        return null;
    }
}
