using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;

internal static class ModelCapabilitiesConfigurationChecks
{
    private const string Connection = "api_url=https://fixture.example/v1\nmodel_name=fixture-model\napi_key=fixture-private-capabilities";

    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "model-capabilities");
        Directory.CreateDirectory(directory);
        await Case("capability defaults and both environment aliases", () => EnvironmentAliases());
        await Case("explicit false and omitted override only their saved fields", () => FieldMerge());
        await Case("invalid or empty environment capability values fail without echo", () => InvalidEnvironment());
        await Case("version two roundtrips every capability combination and clears buffers", () => VersionTwo(directory));
        await Case("genuine legacy document keeps old defaults without rewriting", () => Legacy(directory));
        await Case("protected capability types, missing values and enums fail closed", () => InvalidPayloads(directory));
        await Case("unsupported document versions fail before decrypting", () => FutureVersions(directory));
        await Case("cancelled or invalid save preserves the original and removes temporary files", () => AtomicSave(directory));
        await Case("invalid in-memory enum cannot merge or save", () => InvalidSettings(directory));
        await Case("corrupt protected payload clears plaintext and reports no supplied content", () => Corruption(directory));
        await Case("isolated current-user DPAPI reads old one and reopens new two", () => RealDpapi(directory));
    }

    private static Task EnvironmentAliases()
    {
        var defaults = EnvironmentModelConfiguration.Parse(Connection);
        Require(defaults.Settings.EnableTools && defaults.Settings.ResponseFormat == ModelResponseFormat.Omitted &&
            defaults.ExplicitSettingKeys.Count == 0, "Old environment defaults changed.");
        foreach (var prefix in new[] { "MODEL_", "" })
        foreach (var enabled in new[] { true, false })
        foreach (var (text, format) in Formats())
        {
            var parsed = EnvironmentModelConfiguration.Parse(Connection + $"\n{prefix}ENABLE_TOOLS={enabled.ToString().ToUpperInvariant()}\n{prefix}RESPONSE_FORMAT={text.ToUpperInvariant()}");
            Require(parsed.Settings.EnableTools == enabled && parsed.Settings.ResponseFormat == format &&
                parsed.ExplicitSettingKeys.Order().SequenceEqual(new[] { nameof(ModelSettings.EnableTools), nameof(ModelSettings.ResponseFormat) }.Order()),
                "Capability alias or explicit-key metadata was lost.");
        }
        return Task.CompletedTask;
    }

    private static Task FieldMerge()
    {
        var saved = new ModelSettings(new Uri("https://saved.invalid/v1/chat/completions"), "saved", 4, 2300, 61)
            { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema, BudgetLimits = new(11, 12345, 2) };
        var inherited = EnvironmentModelConfiguration.MergeTuning(EnvironmentModelConfiguration.Parse(Connection), saved);
        Require(!inherited.Settings.EnableTools && inherited.Settings.ResponseFormat == ModelResponseFormat.JsonSchema &&
            inherited.Settings.MaxToolRounds == 4 && inherited.Settings.BudgetLimits == saved.BudgetLimits, "Saved capability inheritance failed.");
        var explicitDefaults = EnvironmentModelConfiguration.Parse(Connection + "\nMODEL_ENABLE_TOOLS=false\nMODEL_RESPONSE_FORMAT=omitted");
        var both = EnvironmentModelConfiguration.MergeTuning(explicitDefaults, saved with { EnableTools = true });
        Require(!both.Settings.EnableTools && both.Settings.ResponseFormat == ModelResponseFormat.Omitted && both.Settings.MaxToolRounds == 4,
            "Explicit default-looking values must override saved values without resetting tool rounds.");
        var toolsOnly = EnvironmentModelConfiguration.MergeTuning(EnvironmentModelConfiguration.Parse(Connection + "\nENABLE_TOOLS=true"), saved);
        var formatOnly = EnvironmentModelConfiguration.MergeTuning(EnvironmentModelConfiguration.Parse(Connection + "\nRESPONSE_FORMAT=json_object"), saved);
        Require(toolsOnly.Settings.EnableTools && toolsOnly.Settings.ResponseFormat == ModelResponseFormat.JsonSchema &&
            !formatOnly.Settings.EnableTools && formatOnly.Settings.ResponseFormat == ModelResponseFormat.JsonObject, "Capability fields were merged together.");
        Require(both.Settings.Endpoint == explicitDefaults.Settings.Endpoint && both.Settings.Model == explicitDefaults.Settings.Model &&
            both.ApiKey == explicitDefaults.ApiKey && both.Source == explicitDefaults.Source &&
            EnvironmentModelConfiguration.MergeTuning(explicitDefaults, null).Settings == explicitDefaults.Settings, "Connection precedence changed.");
        return Task.CompletedTask;
    }

    private static async Task InvalidEnvironment()
    {
        foreach (var prefix in new[] { "MODEL_", "" })
        foreach (var field in new[] { "ENABLE_TOOLS", "RESPONSE_FORMAT" })
        foreach (var value in new[] { "", "''", "fixture-private-rejected", "0", "1", "null", "true false" })
            await Reject(() => Task.FromResult(EnvironmentModelConfiguration.Parse(Connection + $"\n{prefix}{field}={value}")), "fixture-private-rejected");
        await Reject(() => Task.FromResult(EnvironmentModelConfiguration.Parse(Connection + "\nMODEL_ENABLE_TOOLS=\nENABLE_TOOLS=true")));
        await Reject(() => Task.FromResult(EnvironmentModelConfiguration.Parse(Connection + "\nMODEL_RESPONSE_FORMAT=\nRESPONSE_FORMAT=omitted")));
    }

    private static async Task VersionTwo(string directory)
    {
        var path = Path.Combine(directory, "version-two.json");
        foreach (var enabled in new[] { true, false })
        foreach (var (text, format) in Formats())
        {
            var original = EnvironmentModelConfiguration.Parse(Connection + $"\nMODEL_ENABLE_TOOLS={enabled.ToString().ToLowerInvariant()}\nMODEL_RESPONSE_FORMAT={text}\nMODEL_MAX_TOOL_ROUNDS=4");
            var protector = new ProbeProtector();
            await new ProtectedModelConfigurationStore(path, protector).SaveAsync(original);
            var bytes = await File.ReadAllBytesAsync(path);
            using (var document = JsonDocument.Parse(bytes))
                Require(document.RootElement.GetProperty("Version").GetInt32() == 2, "Save did not advance the document version.");
            Require(!Encoding.UTF8.GetString(bytes).Contains(original.ApiKey, StringComparison.Ordinal) &&
                protector.ProtectInput!.All(b => b == 0) && protector.ProtectOutput!.All(b => b == 0), "Save retained secret buffers or plaintext.");
            var reopenedProtector = new ProbeProtector();
            var restored = await new ProtectedModelConfigurationStore(path, reopenedProtector).LoadAsync();
            Require(restored?.Settings == original.Settings && restored.Settings.ResponseFormat == format &&
                restored.ApiKey == original.ApiKey && restored.ExplicitSettingKeys.SequenceEqual(original.ExplicitSettingKeys) &&
                reopenedProtector.UnprotectOutput!.All(b => b == 0), "Reopen lost capability metadata or did not clear plaintext.");
        }
    }

    private static async Task Legacy(string directory)
    {
        var path = Path.Combine(directory, "legacy-one.json");
        var payload = LegacyPayload();
        Require(payload["Settings"]![nameof(ModelSettings.EnableTools)] is null && payload["Settings"]![nameof(ModelSettings.ResponseFormat)] is null,
            "Legacy fixture must genuinely omit the new fields.");
        await WriteDocument(path, 1, payload);
        var before = await File.ReadAllBytesAsync(path);
        var writeTime = File.GetLastWriteTimeUtc(path);
        var store = new ProtectedModelConfigurationStore(path, new ProbeProtector());
        var restored = await store.LoadAsync();
        var after = await File.ReadAllBytesAsync(path);
        Require(restored is not null && restored.Settings.EnableTools && restored.Settings.ResponseFormat == ModelResponseFormat.Omitted &&
            restored.Settings.BudgetLimits == new AnalysisBudgetLimits() && restored.ExplicitSettingKeys.Count == 0 &&
            before.SequenceEqual(after) && writeTime == File.GetLastWriteTimeUtc(path), "Read rewrote old data or changed defaults.");
        await store.SaveAsync(restored! with { Settings = restored!.Settings with { EnableTools = false, ResponseFormat = ModelResponseFormat.JsonSchema } });
        var upgraded = await store.LoadAsync();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Require(document.RootElement.GetProperty("Version").GetInt32() == 2 && upgraded is { Settings.EnableTools: false, Settings.ResponseFormat: ModelResponseFormat.JsonSchema },
            "Explicit save did not upgrade legacy settings.");
    }

    private static async Task InvalidPayloads(string directory)
    {
        var path = Path.Combine(directory, "invalid-capabilities.json");
        foreach (var field in new[] { nameof(ModelSettings.EnableTools), nameof(ModelSettings.ResponseFormat) })
        {
            var missing = CurrentPayload();
            missing["Settings"]!.AsObject().Remove(field);
            await WriteDocument(path, 2, missing);
            await Reject(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync());
        }
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("fixture-private-rejected"), JsonValue.Create(1), new JsonObject() })
        {
            var payload = CurrentPayload();
            payload["Settings"]![nameof(ModelSettings.EnableTools)] = value;
            await WriteDocument(path, 2, payload);
            await Reject(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync(), "fixture-private-rejected");
        }
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("json_schema"), JsonValue.Create(-1), JsonValue.Create(3), JsonValue.Create(1.5), JsonValue.Create(true), new JsonArray() })
        {
            var payload = CurrentPayload();
            payload["Settings"]![nameof(ModelSettings.ResponseFormat)] = value;
            await WriteDocument(path, 2, payload);
            await Reject(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync());
        }
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            (nameof(ModelSettings.EnableTools), JsonValue.Create(false)),
            (nameof(ModelSettings.EnableTools), JsonValue.Create("false")),
            (nameof(ModelSettings.ResponseFormat), JsonValue.Create(2)),
            (nameof(ModelSettings.ResponseFormat), JsonValue.Create(999))
        })
        {
            var payload = LegacyPayload();
            payload["Settings"]![field] = value;
            await WriteDocument(path, 1, payload);
            await Reject(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync());
        }
        var nullSettings = CurrentPayload();
        nullSettings["Settings"] = null;
        await WriteDocument(path, 2, nullSettings);
        await Reject(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync());
    }

    private static async Task FutureVersions(string directory)
    {
        var path = Path.Combine(directory, "future-version.json");
        foreach (var version in new[] { 0, 3, int.MaxValue })
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = version, ProtectedPayload = "not-base64-fixture-private-rejected" }));
            var before = await File.ReadAllBytesAsync(path);
            var protector = new ProbeProtector();
            await Reject(() => new ProtectedModelConfigurationStore(path, protector).LoadAsync(), "fixture-private-rejected");
            var after = await File.ReadAllBytesAsync(path);
            Require(protector.UnprotectCalls == 0 && before.SequenceEqual(after), "Unsupported version was decrypted or rewritten.");
        }
    }

    private static async Task AtomicSave(string directory)
    {
        var path = Path.Combine(directory, "atomic.json");
        var original = EnvironmentModelConfiguration.Parse(Connection);
        await new ProtectedModelConfigurationStore(path, new ProbeProtector()).SaveAsync(original);
        var before = await File.ReadAllBytesAsync(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var untouchedProtector = new ProbeProtector();
        await Cancelled(() => new ProtectedModelConfigurationStore(path, untouchedProtector).SaveAsync(original, cancelled.Token));
        Require(untouchedProtector.ProtectInput is null, "Pre-cancelled save still handled plaintext.");
        await Cancelled(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync(cancelled.Token));
        using var during = new CancellationTokenSource();
        var protector = new ProbeProtector(during.Cancel);
        await Cancelled(() => new ProtectedModelConfigurationStore(path, protector).SaveAsync(original with
            { Settings = original.Settings with { EnableTools = false } }, during.Token));
        var after = await File.ReadAllBytesAsync(path);
        Require(before.SequenceEqual(after) && Directory.GetFiles(directory, "*.tmp").Length == 0 &&
            protector.ProtectInput!.All(b => b == 0) && protector.ProtectOutput!.All(b => b == 0), "Cancelled save modified the original or left sensitive buffers/temp files.");
    }

    private static async Task InvalidSettings(string directory)
    {
        var config = EnvironmentModelConfiguration.Parse(Connection);
        var invalid = config.Settings with { ResponseFormat = (ModelResponseFormat)999 };
        try { invalid.ValidateTuning(); throw new Exception("Invalid enum accepted."); } catch (ArgumentException) { }
        try { EnvironmentModelConfiguration.MergeTuning(config, invalid); throw new Exception("Invalid saved enum merged."); } catch (ArgumentException) { }
        try { EnvironmentModelConfiguration.MergeTuning(config with { Settings = invalid }, null); throw new Exception("Invalid environment enum merged."); } catch (ArgumentException) { }
        var path = Path.Combine(directory, "invalid-save.json");
        var protector = new ProbeProtector();
        try { await new ProtectedModelConfigurationStore(path, protector).SaveAsync(config with { Settings = invalid }); throw new Exception("Invalid enum saved."); } catch (ArgumentException) { }
        Require(!File.Exists(path) && protector.ProtectInput is null, "Invalid settings reached encryption or disk.");
    }

    private static async Task Corruption(string directory)
    {
        var path = Path.Combine(directory, "corrupt.json");
        await File.WriteAllTextAsync(path, "{\"Version\":2,\"ProtectedPayload\":\"not-base64-fixture-private-rejected\"}");
        await Reject(() => new ProtectedModelConfigurationStore(path, new ProbeProtector()).LoadAsync(), "fixture-private-rejected");
        var plaintext = Encoding.UTF8.GetBytes("fixture-private-rejected");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 2, ProtectedPayload = Convert.ToBase64String(plaintext.Select(b => (byte)(b ^ 0x53)).ToArray()) }));
        var protector = new ProbeProtector();
        await Reject(() => new ProtectedModelConfigurationStore(path, protector).LoadAsync(), "fixture-private-rejected");
        Require(protector.UnprotectOutput!.All(b => b == 0), "Rejected plaintext was retained.");
    }

    private static async Task RealDpapi(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(directory, "isolated-dpapi.json");
        await WriteDocument(path, 1, LegacyPayload(), new WindowsSecretProtector());
        var before = await File.ReadAllBytesAsync(path);
        var restored = await new ProtectedModelConfigurationStore(path, new WindowsSecretProtector()).LoadAsync();
        var after = await File.ReadAllBytesAsync(path);
        Require(restored is { Settings.EnableTools: true, Settings.ResponseFormat: ModelResponseFormat.Omitted } &&
            before.SequenceEqual(after), "DPAPI legacy load did not preserve old semantics and bytes.");
        foreach (var (_, format) in Formats())
        {
            var config = restored! with { Settings = restored.Settings with { EnableTools = false, ResponseFormat = format } };
            await new ProtectedModelConfigurationStore(path, new WindowsSecretProtector()).SaveAsync(config);
            var reopened = await new ProtectedModelConfigurationStore(path, new WindowsSecretProtector()).LoadAsync();
            Require(reopened?.Settings == config.Settings && reopened.ApiKey == config.ApiKey &&
                !(await File.ReadAllTextAsync(path)).Contains(config.ApiKey, StringComparison.Ordinal), "DPAPI version two capability reopen failed.");
        }
        await WriteDocument(path, 3, CurrentPayload(), new WindowsSecretProtector());
        await Reject(() => new ProtectedModelConfigurationStore(path, new WindowsSecretProtector()).LoadAsync());
        var invalid = CurrentPayload();
        invalid["Settings"]![nameof(ModelSettings.ResponseFormat)] = 999;
        await WriteDocument(path, 2, invalid, new WindowsSecretProtector());
        await Reject(() => new ProtectedModelConfigurationStore(path, new WindowsSecretProtector()).LoadAsync());
    }

    private static JsonObject LegacyPayload() => new()
    {
        ["Settings"] = new JsonObject
        {
            ["Endpoint"] = "https://fixture.example/v1/chat/completions", ["Model"] = "legacy-fixture",
            ["MaxToolRounds"] = 2, ["MaxOutputTokens"] = 1500, ["TimeoutSeconds"] = 30
        },
        ["ApiKey"] = "fixture-private-legacy", ["Source"] = "fixture-only"
    };

    private static JsonObject CurrentPayload() => JsonSerializer.SerializeToNode(EnvironmentModelConfiguration.Parse(Connection))!.AsObject();

    private static async Task WriteDocument(string path, int version, JsonObject payload, ISecretProtector? protector = null)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[]? encrypted = null;
        try
        {
            encrypted = (protector ?? new ProbeProtector()).Protect(plaintext);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = version, ProtectedPayload = Convert.ToBase64String(encrypted) }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static IEnumerable<(string Text, ModelResponseFormat Format)> Formats() =>
        [("omitted", ModelResponseFormat.Omitted), ("json_object", ModelResponseFormat.JsonObject), ("json_schema", ModelResponseFormat.JsonSchema)];

    private static async Task Case(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string? privateValue = null)
    {
        try { await action(); throw new InvalidOperationException("Invalid configuration accepted."); }
        catch (InvalidDataException error)
        {
            Require(privateValue is null || !error.ToString().Contains(privateValue, StringComparison.Ordinal), "Configuration rejection exposed supplied data.");
        }
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Cancelled configuration operation completed."); }
        catch (OperationCanceledException) { }
    }

    private sealed class ProbeProtector(Action? onProtect = null) : ISecretProtector
    {
        public byte[]? ProtectInput { get; private set; }
        public byte[]? ProtectOutput { get; private set; }
        public byte[]? UnprotectOutput { get; private set; }
        public int UnprotectCalls { get; private set; }
        public byte[] Protect(byte[] data)
        {
            ProtectInput = data;
            ProtectOutput = data.Select(b => (byte)(b ^ 0x53)).ToArray();
            onProtect?.Invoke();
            return ProtectOutput;
        }
        public byte[] Unprotect(byte[] data)
        {
            UnprotectCalls++;
            UnprotectOutput = data.Select(b => (byte)(b ^ 0x53)).ToArray();
            return UnprotectOutput;
        }
    }
}
