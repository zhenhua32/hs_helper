using System.IO;

namespace HsHelper.App;

/// <summary>Resolves startup paths without opening a window, loading configuration, or creating data.</summary>
public sealed record AppLaunchSettings(string DataDirectory, bool Diagnostic, string? DiagnosticOutput, string? Error)
{
    public static AppLaunchSettings Parse(IReadOnlyList<string> args)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR");
        var diagnostic = args.Contains("--diagnostic") || args.Contains("--diagnostic-output");
        string? diagnosticOutput = null;
        string? error = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--diagnostic": diagnostic = true; break;
                case "--data-dir" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    dataDirectory = args[++i]; break;
                case "--diagnostic-output" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    diagnostic = true;
                    diagnosticOutput = args[++i]; break;
                default: error = "Unknown startup option or missing option value."; break;
            }
        }
        try
        {
            dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HsHelper")
                : Path.GetFullPath(dataDirectory);
            if (string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR"))
                && !args.Contains("--data-dir"))
                error ??= "The local application data directory is unavailable; specify --data-dir.";
            if (diagnosticOutput is not null) diagnosticOutput = Path.GetFullPath(diagnosticOutput);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error ??= "A startup path is invalid.";
            dataDirectory = "";
            diagnosticOutput = null;
        }
        return new(dataDirectory, diagnostic, diagnosticOutput, error);
    }
}
