using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HsHelper.Agent;

public sealed record ModelConfiguration(ModelSettings Settings, string ApiKey, string Source)
{
    public IReadOnlyList<string> ExplicitSettingKeys { get; init; } = [];
}

public static class EnvironmentModelConfiguration
{
    public static string? FindFile(string? startDirectory = null)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory));
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) break;
        }
        return null;
    }

    public static async Task<ModelConfiguration> LoadAsync(string path, CancellationToken token = default)
        => Parse(await File.ReadAllTextAsync(path, token), Path.GetFileName(path));

    public static ModelConfiguration Parse(string text, string source = ".env")
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var match = Regex.Match(trimmed, @"^(?:export\s+)?([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*(.*)$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success) throw new InvalidDataException("环境配置有无法识别的行，请使用 名称=值 格式。");
            var value = match.Groups[2].Value.Trim();
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var quote = value[0];
                var end = value.IndexOf(quote, 1);
                if (end < 0 || (value[(end + 1)..].Trim() is { Length: > 0 } tail && !tail.StartsWith('#')))
                    throw new InvalidDataException("环境配置引号未闭合或包含多余内容。");
                value = value[1..end];
            }
            else
                value = Regex.Replace(value, @"\s+#.*$", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).TrimEnd();
            values[match.Groups[1].Value] = value;
        }
        string? Read(params string[] names) => names.Select(n => values.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var endpoint = NormalizeEndpoint(Read("api_url", "MODEL_ENDPOINT", "OPENAI_BASE_URL", "OPENAI_API_BASE", "BASE_URL")
            ?? throw new InvalidDataException("环境配置缺少 api_url 或 MODEL_ENDPOINT。"));
        var model = Read("model_name", "MODEL_NAME", "OPENAI_MODEL") ?? throw new InvalidDataException("环境配置缺少 model_name。");
        var key = Read("api_key", "MODEL_API_KEY", "OPENAI_API_KEY") ?? "";
        if (key.Any(char.IsControl)) throw new InvalidDataException("环境配置中的密钥含不支持的控制字符。");
        var explicitKeys = new List<string>();
        string? Capability(string keyName, string propertyName)
        {
            foreach (var name in new[] { "MODEL_" + keyName, keyName })
            {
                if (!values.TryGetValue(name, out var value)) continue;
                explicitKeys.Add(propertyName);
                return value;
            }
            return null;
        }
        var tools = Capability("ENABLE_TOOLS", nameof(ModelSettings.EnableTools));
        var enableTools = tools is null || tools.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
            : tools.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
            : throw new InvalidDataException("环境配置中的模型工具开关无效。");
        var format = Capability("RESPONSE_FORMAT", nameof(ModelSettings.ResponseFormat));
        var responseFormat = format?.ToLowerInvariant() switch
        {
            null or "omitted" => ModelResponseFormat.Omitted,
            "json_object" => ModelResponseFormat.JsonObject,
            "json_schema" => ModelResponseFormat.JsonSchema,
            _ => throw new InvalidDataException("环境配置中的模型输出模式无效。")
        };
        long Number(string keyName, long fallback, long minimum, long maximum)
        {
            var value = keyName == "MAX_CONCURRENT_REQUESTS_PER_MATCH"
                ? Read("MODEL_" + keyName, keyName, "MODEL_MAX_CONCURRENT_REQUESTS", "MAX_CONCURRENT_REQUESTS")
                : Read("MODEL_" + keyName, keyName);
            if (value is null) return fallback;
            if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                number < minimum || number > maximum)
                throw new InvalidDataException("环境配置中的模型预算参数格式或范围无效。");
            explicitKeys.Add(keyName switch
            {
                "MAX_TOOL_ROUNDS" => nameof(ModelSettings.MaxToolRounds), "MAX_OUTPUT_TOKENS" => nameof(ModelSettings.MaxOutputTokens),
                "TIMEOUT_SECONDS" => nameof(ModelSettings.TimeoutSeconds), "MAX_REQUESTS_PER_MATCH" => nameof(AnalysisBudgetLimits.MaxRequestsPerMatch),
                "MAX_TOKENS_PER_MATCH" => nameof(AnalysisBudgetLimits.MaxTokensPerMatch), _ => nameof(AnalysisBudgetLimits.MaxConcurrentRequestsPerMatch)
            });
            return number;
        }
        var defaults = new ModelSettings(endpoint, model);
        var settings = defaults with
        {
            EnableTools = enableTools,
            ResponseFormat = responseFormat,
            MaxToolRounds = (int)Number("MAX_TOOL_ROUNDS", defaults.MaxToolRounds, 0, 4),
            MaxOutputTokens = (int)Number("MAX_OUTPUT_TOKENS", defaults.MaxOutputTokens, 100, 8192),
            TimeoutSeconds = (int)Number("TIMEOUT_SECONDS", defaults.TimeoutSeconds, 1, 120),
            BudgetLimits = new((int)Number("MAX_REQUESTS_PER_MATCH", defaults.BudgetLimits.MaxRequestsPerMatch, 1, 10000),
                Number("MAX_TOKENS_PER_MATCH", defaults.BudgetLimits.MaxTokensPerMatch, 1, 100000000),
                (int)Number("MAX_CONCURRENT_REQUESTS_PER_MATCH", defaults.BudgetLimits.MaxConcurrentRequestsPerMatch, 1, 8))
        };
        return new(settings, key, source) { ExplicitSettingKeys = Array.AsReadOnly(explicitKeys.ToArray()) };
    }

    public static ModelConfiguration MergeTuning(ModelConfiguration environmentConfig, ModelSettings? savedSettings)
    {
        ArgumentNullException.ThrowIfNull(environmentConfig);
        if (savedSettings is null) { environmentConfig.Settings.ValidateTuning(); return environmentConfig; }
        savedSettings.ValidateTuning();
        var environment = environmentConfig.Settings;
        var explicitKeys = environmentConfig.ExplicitSettingKeys ?? [];
        bool Own(string key) => explicitKeys.Contains(key, StringComparer.Ordinal);
        var merged = environment with
        {
            EnableTools = Own(nameof(ModelSettings.EnableTools)) ? environment.EnableTools : savedSettings.EnableTools,
            ResponseFormat = Own(nameof(ModelSettings.ResponseFormat)) ? environment.ResponseFormat : savedSettings.ResponseFormat,
            MaxToolRounds = Own(nameof(ModelSettings.MaxToolRounds)) ? environment.MaxToolRounds : savedSettings.MaxToolRounds,
            MaxOutputTokens = Own(nameof(ModelSettings.MaxOutputTokens)) ? environment.MaxOutputTokens : savedSettings.MaxOutputTokens,
            TimeoutSeconds = Own(nameof(ModelSettings.TimeoutSeconds)) ? environment.TimeoutSeconds : savedSettings.TimeoutSeconds,
            BudgetLimits = new(Own(nameof(AnalysisBudgetLimits.MaxRequestsPerMatch)) ? environment.BudgetLimits.MaxRequestsPerMatch : savedSettings.BudgetLimits.MaxRequestsPerMatch,
                Own(nameof(AnalysisBudgetLimits.MaxTokensPerMatch)) ? environment.BudgetLimits.MaxTokensPerMatch : savedSettings.BudgetLimits.MaxTokensPerMatch,
                Own(nameof(AnalysisBudgetLimits.MaxConcurrentRequestsPerMatch)) ? environment.BudgetLimits.MaxConcurrentRequestsPerMatch : savedSettings.BudgetLimits.MaxConcurrentRequestsPerMatch)
        };
        merged.ValidateTuning();
        return environmentConfig with { Settings = merged };
    }

    public static Uri NormalizeEndpoint(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("模型接口需为不含凭据和查询参数的 HTTPS 地址；本机可使用 HTTP。");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前配置需要 Chat Completions 兼容地址。");
            path = path.Length == 0 ? "/v1/chat/completions" : path + "/chat/completions";
        }
        return new UriBuilder(uri) { Path = path }.Uri;
    }
}

public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI current-user protection, with interactive prompts forbidden.</summary>
public sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => Transform(data, false);
    public byte[] Unprotect(byte[] data) => Transform(data, true);
    private static byte[] Transform(byte[] data, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据保护需要 Windows。");
        var input = new Blob { Length = data.Length, Pointer = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Pointer, data.Length);
            var success = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows 凭据保护失败，无法安全保存或读取模型配置。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Pointer, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, input.Pointer, data.Length);
            Marshal.FreeHGlobal(input.Pointer);
            if (output.Pointer != IntPtr.Zero)
            {
                Marshal.Copy(new byte[output.Length], 0, output.Pointer, output.Length);
                LocalFree(output.Pointer);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Pointer; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class ProtectedModelConfigurationStore(string path, ISecretProtector protector)
{
    private sealed record Document(int Version, string ProtectedPayload);
    public async Task SaveAsync(ModelConfiguration config, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        config.Settings.ValidateTuning();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(config);
        byte[] encrypted;
        try { encrypted = protector.Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Document(2, Convert.ToBase64String(encrypted))), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    public async Task<ModelConfiguration?> LoadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return null;
        byte[]? plaintext = null;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(path, token));
            if (doc?.Version is not (1 or 2)) throw new InvalidDataException("模型配置版本无法识别。");
            plaintext = protector.Unprotect(Convert.FromBase64String(doc.ProtectedPayload));
            ValidateCapabilities(plaintext, doc.Version);
            var config = JsonSerializer.Deserialize<ModelConfiguration>(plaintext) ?? throw new InvalidDataException("模型配置为空。");
            if (config.Settings?.Endpoint is null) throw new InvalidDataException("模型配置缺少连接设置。");
            _ = EnvironmentModelConfiguration.NormalizeEndpoint(config.Settings.Endpoint.ToString());
            config.Settings.ValidateTuning();
            return config;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or CryptographicException)
        { throw new InvalidDataException("无法读取受保护的模型配置，请重新输入或加载 .env。"); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void ValidateCapabilities(byte[] plaintext, int version)
    {
        using var json = JsonDocument.Parse(plaintext);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("Settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("模型配置缺少连接设置。");
        var hasTools = settings.TryGetProperty(nameof(ModelSettings.EnableTools), out var tools);
        var hasFormat = settings.TryGetProperty(nameof(ModelSettings.ResponseFormat), out var format);
        if ((version == 2 && (!hasTools || !hasFormat)) ||
            (hasTools && tools.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
            (hasFormat && (format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var number) || !Enum.IsDefined((ModelResponseFormat)number))))
            throw new InvalidDataException("模型配置中的能力设置无效。");
        if (version == 1 && ((hasTools && !tools.GetBoolean()) ||
            (hasFormat && format.GetInt32() != (int)ModelResponseFormat.Omitted)))
            throw new InvalidDataException("旧版模型配置不能包含新的能力设置。");
    }
}
