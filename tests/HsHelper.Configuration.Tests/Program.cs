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
foreach (var invalid in new[] { "api_url=not-url\nmodel_name=x", "api_url=https://user:password@model.example/v1\nmodel_name=x", "api_url=https://model.example/v1?api_key=private\nmodel_name=x", "api_url=https://model.example/v1\napi_key='unclosed", "api_url=https://model.example/v1\nmodel_name=x\napi_key=\"x\" trailing" })
{
    try { EnvironmentModelConfiguration.Parse(invalid); throw new Exception("Invalid env accepted"); }
    catch (InvalidDataException error) { Check(!error.Message.Contains("password") && !error.Message.Contains("private"), "invalid env returns no credential"); }
}
var root = Path.Combine(Environment.CurrentDirectory, ".local", "configuration-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "config.json");
var store = new ProtectedModelConfigurationStore(path, new TestProtector());
await store.SaveAsync(config);
Check(!(await File.ReadAllTextAsync(path)).Contains(config.ApiKey), "saved config has no plaintext key");
var loaded = await new ProtectedModelConfigurationStore(path, new TestProtector()).LoadAsync();
Check(loaded?.ApiKey == config.ApiKey && loaded.Settings.Model == config.Settings.Model, "restart restores encrypted settings");
await File.WriteAllTextAsync(path, "{\"Version\":1,\"ProtectedPayload\":\"not-base64\"}");
try { await store.LoadAsync(); throw new Exception("Corrupt config accepted"); }
catch (InvalidDataException) { Check(true, "corrupt config fails closed"); }
if (OperatingSystem.IsWindows())
{
    var protector = new WindowsSecretProtector();
    var original = Encoding.UTF8.GetBytes("fake-test-credential");
    var encrypted = protector.Protect(original);
    Check(!encrypted.SequenceEqual(original) && protector.Unprotect(encrypted).SequenceEqual(original), "Windows current-user DPAPI roundtrip");
}
Console.WriteLine("Configuration checks passed (fixture credentials only).");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
sealed class TestProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data.Select(x => (byte)(x ^ 0x53)).ToArray();
    public byte[] Unprotect(byte[] data) => Protect(data);
}
