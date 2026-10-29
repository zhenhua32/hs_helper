using System.Text;
using System.Security.Cryptography;
using HsHelper.Agent;

var config = EnvironmentModelConfiguration.Parse("""
    # dummy configuration only
    api_url="https://model.example/v1" # compatible endpoint
    model_name=fixture-model
    api_key='fake-private-key#literal'
    """);
Check(config.Settings.Endpoint.AbsolutePath == "/v1/chat/completions" && config.ApiKey == "fake-private-key#literal", "env aliases, quoted values and endpoint normalization");
Check(EnvironmentModelConfiguration.Parse("OPENAI_BASE_URL=http://localhost:1234/v1\nOPENAI_MODEL=local-model").ApiKey == "", "local model without key");
Check(EnvironmentModelConfiguration.NormalizeEndpoint("https://model.example/v1/chat/completions/").AbsolutePath == "/v1/chat/completions", "complete endpoint preserved");
var explicitTuning = EnvironmentModelConfiguration.Parse("""
    api_url=https://environment.example/v1
    model_name=environment-model
    api_key=fixture-environment-key
    MODEL_MAX_TOOL_ROUNDS=3
    max_output_tokens=4096
    MODEL_TIMEOUT_SECONDS=90
    MODEL_MAX_REQUESTS_PER_MATCH=42
    MODEL_MAX_TOKENS_PER_MATCH=456789
    MODEL_MAX_CONCURRENT_REQUESTS=2
    """);
Check(explicitTuning.Settings.MaxToolRounds == 3 && explicitTuning.Settings.MaxOutputTokens == 4096 && explicitTuning.Settings.TimeoutSeconds == 90 &&
    explicitTuning.Settings.BudgetLimits == new AnalysisBudgetLimits(42, 456789, 2), "optional tuning aliases and limits are parsed");
Check(explicitTuning.ExplicitSettingKeys.Count == 6 && explicitTuning.ExplicitSettingKeys.Contains(nameof(ModelSettings.MaxOutputTokens)) &&
    explicitTuning.ExplicitSettingKeys.Contains(nameof(AnalysisBudgetLimits.MaxConcurrentRequestsPerMatch)), "explicit tuning keys use public property names");
var savedTuning = new ModelSettings(new Uri("https://saved.example/v1/chat/completions"), "saved-model", 1, 2048, 75)
    { BudgetLimits = new(24, 123456, 2) };
var inherited = EnvironmentModelConfiguration.MergeTuning(config, savedTuning);
Check(inherited.Settings.MaxToolRounds == 1 && inherited.Settings.MaxOutputTokens == 2048 && inherited.Settings.TimeoutSeconds == 75 &&
    inherited.Settings.BudgetLimits == savedTuning.BudgetLimits, "connection-only env inherits saved tuning");
Check(inherited.Settings.Endpoint == config.Settings.Endpoint && inherited.Settings.Model == config.Settings.Model && inherited.ApiKey == config.ApiKey,
    "env connection always takes precedence over saved connection");
var partial = EnvironmentModelConfiguration.Parse("api_url=https://environment.example/v1\nmodel_name=environment-model\nMODEL_MAX_OUTPUT_TOKENS=3000\nMODEL_MAX_TOKENS_PER_MATCH=345678");
var merged = EnvironmentModelConfiguration.MergeTuning(partial, savedTuning);
Check(merged.Settings.MaxOutputTokens == 3000 && merged.Settings.BudgetLimits.MaxTokensPerMatch == 345678 &&
    merged.Settings.MaxToolRounds == 1 && merged.Settings.TimeoutSeconds == 75 && merged.Settings.BudgetLimits.MaxRequestsPerMatch == 24 &&
    merged.Settings.BudgetLimits.MaxConcurrentRequestsPerMatch == 2, "each explicit env field overrides only its matching saved tuning field");
Check(EnvironmentModelConfiguration.MergeTuning(config, null).Settings == config.Settings, "legacy env without saved settings keeps defaults");
foreach (var invalidField in new[] { "MODEL_MAX_TOOL_ROUNDS=5", "MODEL_MAX_OUTPUT_TOKENS=99", "MODEL_TIMEOUT_SECONDS=0", "MODEL_MAX_REQUESTS_PER_MATCH=-1", "MODEL_MAX_TOKENS_PER_MATCH=not-a-number", "MODEL_MAX_CONCURRENT_REQUESTS=9" })
{
    try { EnvironmentModelConfiguration.Parse("api_url=https://model.example/v1\nmodel_name=x\n" + invalidField); throw new Exception("Invalid tuning accepted"); }
    catch (InvalidDataException) { Check(true, "invalid optional tuning is rejected without echoing its value"); }
}
foreach (var invalid in new[] { "api_url=not-url\nmodel_name=x", "api_url=https://user:password@model.example/v1\nmodel_name=x", "api_url=https://model.example/v1?api_key=private\nmodel_name=x", "api_url=https://model.example/v1\napi_key='unclosed", "api_url=https://model.example/v1\nmodel_name=x\napi_key=\"x\" trailing" })
{
    try { EnvironmentModelConfiguration.Parse(invalid); throw new Exception("Invalid env accepted"); }
    catch (InvalidDataException error) { Check(!error.Message.Contains("password") && !error.Message.Contains("private"), "invalid env returns no credential"); }
}
var root = Path.Combine(Environment.CurrentDirectory, ".local", "configuration-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "config.json");
var store = new ProtectedModelConfigurationStore(path, new TestProtector());
await store.SaveAsync(explicitTuning);
Check(!(await File.ReadAllTextAsync(path)).Contains(explicitTuning.ApiKey), "saved config has no plaintext key");
var loaded = await new ProtectedModelConfigurationStore(path, new TestProtector()).LoadAsync();
Check(loaded?.ApiKey == explicitTuning.ApiKey && loaded.Settings == explicitTuning.Settings &&
    loaded.ExplicitSettingKeys.SequenceEqual(explicitTuning.ExplicitSettingKeys), "restart restores encrypted connection and all tuning fields");
var legacyPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Settings = new { config.Settings.Endpoint, config.Settings.Model,
    MaxToolRounds = 2, MaxOutputTokens = 1500, TimeoutSeconds = 30 }, config.ApiKey, config.Source });
await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { Version = 1, ProtectedPayload = Convert.ToBase64String(new TestProtector().Protect(legacyPayload)) }));
var legacy = await store.LoadAsync();
Check(legacy?.Settings.BudgetLimits == new AnalysisBudgetLimits() && legacy.ExplicitSettingKeys.Count == 0, "legacy protected configuration restores new tuning defaults");
await File.WriteAllTextAsync(path, "{\"Version\":1,\"ProtectedPayload\":\"not-base64\"}");
try { await store.LoadAsync(); throw new Exception("Corrupt config accepted"); }
catch (InvalidDataException) { Check(true, "corrupt config fails closed"); }
if (OperatingSystem.IsWindows())
{
    var protector = new WindowsSecretProtector();
    var original = Encoding.UTF8.GetBytes("fake-test-credential");
    var encrypted = protector.Protect(original);
    Check(!encrypted.SequenceEqual(original) && protector.Unprotect(encrypted).SequenceEqual(original), "Windows current-user DPAPI roundtrip");
    var dpapiStore = new ProtectedModelConfigurationStore(Path.Combine(root, "dpapi-settings.json"), protector);
    await dpapiStore.SaveAsync(explicitTuning);
    var dpapiLoaded = await dpapiStore.LoadAsync();
    Check(dpapiLoaded?.Settings == explicitTuning.Settings && dpapiLoaded.ExplicitSettingKeys.SequenceEqual(explicitTuning.ExplicitSettingKeys),
        "Windows DPAPI stores and restores optional tuning with explicit-key metadata");
}
Console.WriteLine("Configuration checks passed (fixture credentials only).");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
sealed class TestProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data.Select(x => (byte)(x ^ 0x53)).ToArray();
    public byte[] Unprotect(byte[] data) => Protect(data);
}
