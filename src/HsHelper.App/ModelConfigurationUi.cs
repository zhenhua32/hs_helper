using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using HsHelper.Agent;

namespace HsHelper.App;

public partial class MainWindow
{
    private ProtectedModelConfigurationStore _modelStore = null!;
    private CancellationTokenSource? _connectionCheck;
    private ModelSettings? _loadedModelSettings;
    private long _configurationRevision;
    private long _configurationReadRequest;
    private readonly SemaphoreSlim _configurationIoGate = new(1, 1);
    private CancellationTokenSource? _configurationWrite;
    private readonly string _configurationStartDirectory = Environment.CurrentDirectory;

    private void ModelConfigurationChanged(object sender, RoutedEventArgs e)
    {
        _configurationRevision++;
        _configurationWrite?.Cancel();
        _analysis?.Cancel(); _connectionCheck?.Cancel();
        InvalidateAdvice();
        if (ModelConfigText is not null) ModelConfigText.Text = "模型配置已修改，可保存或重新检查连接。";
    }

    private async Task<bool> LoadModelConfigurationAsync(bool environmentOnly = false, long? expectedRevision = null)
    {
        var revision = expectedRevision ?? _configurationRevision;
        if (_closing || revision != _configurationRevision) return false;
        var request = ++_configurationReadRequest;
        _configurationWrite?.Cancel();
        await _configurationIoGate.WaitAsync();
        try
        {
            bool Current() => !_closing && revision == _configurationRevision && request == _configurationReadRequest;
            if (!Current()) return false;
            var file = EnvironmentModelConfiguration.FindFile(_configurationStartDirectory) ?? EnvironmentModelConfiguration.FindFile(AppContext.BaseDirectory);
            ModelConfiguration? saved = null;
            try { saved = await _modelStore.LoadAsync(); }
            catch (InvalidDataException) when (file is not null) { /* A valid .env can recover an unreadable saved configuration. */ }
            var config = file is not null ? EnvironmentModelConfiguration.MergeTuning(await EnvironmentModelConfiguration.LoadAsync(file), saved?.Settings)
                : environmentOnly ? throw new FileNotFoundException("未在项目目录找到 .env。") : saved;
            if (!Current()) return false;
            if (config is null) { ModelConfigText.Text = "尚未配置模型。可编辑项目 .env 或在下方填写。"; return true; }
            _loadedModelSettings = config.Settings;
            EndpointBox.Text = config.Settings.Endpoint.ToString();
            ModelBox.Text = config.Settings.Model;
            KeyBox.Password = config.ApiKey;
            ModelConfigText.Text = file is null ? "已加载 Windows 用户加密配置。" : "已从项目 .env 加载模型配置；密钥已填入掩码输入框。";
            return true;
        }
        finally { _configurationIoGate.Release(); }
    }

    private async Task<bool> SaveModelConfigurationAsync(ModelConfiguration config, long revision)
    {
        if (_closing || revision != _configurationRevision) return false;
        _configurationReadRequest++;
        _configurationWrite?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _configurationWrite = cancellation;
        try
        {
            await _configurationIoGate.WaitAsync(cancellation.Token);
            try
            {
                if (_closing || revision != _configurationRevision || !ReferenceEquals(_configurationWrite, cancellation)) return false;
                await _modelStore.SaveAsync(config, cancellation.Token);
                return revision == _configurationRevision && ReferenceEquals(_configurationWrite, cancellation);
            }
            finally { _configurationIoGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        finally { if (ReferenceEquals(_configurationWrite, cancellation)) _configurationWrite = null; }
    }
    private async Task WaitForConfigurationIoAsync()
    {
        await _configurationIoGate.WaitAsync();
        _configurationIoGate.Release();
    }

    private ModelConfiguration ConfigurationFromFields()
    {
        var endpoint = EnvironmentModelConfiguration.NormalizeEndpoint(EndpointBox.Text);
        if (string.IsNullOrWhiteSpace(ModelBox.Text)) throw new ArgumentException("请填写模型名称。");
        return new((_loadedModelSettings ?? new ModelSettings(endpoint, ModelBox.Text.Trim()))
            with { Endpoint = endpoint, Model = ModelBox.Text.Trim() }, KeyBox.Password, "Windows 用户加密配置");
    }

    private async void LoadEnvironmentClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        InvalidateAdvice();
        if (await LoadModelConfigurationAsync(true)) StatusText.Text = "已重新加载 .env，密钥不会写入诊断输出。";
    });

    private async void SaveModelClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var config = ConfigurationFromFields();
        var revision = _configurationRevision;
        if (await SaveModelConfigurationAsync(config, revision))
        {
            ModelConfigText.Text = "模型配置已使用 Windows 当前用户凭据加密保存。存在项目 .env 时优先加载 .env。";
            StatusText.Text = "模型配置已保存。";
        }
    });

    private async void CheckModelClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var config = ConfigurationFromFields();
        var configurationRevision = _configurationRevision;
        _connectionCheck?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _connectionCheck = cancellation;
        bool Current() => configurationRevision == _configurationRevision && ReferenceEquals(_connectionCheck, cancellation) && !_closing;
        CheckModelButton.IsEnabled = false;
        ModelConfigText.Text = "正在检查模型连接，本次不发送对局信息…";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _ = await new AdviceService(_http).AnalyzeAsync(config.Settings with { MaxToolRounds = 0, MaxOutputTokens = 512 }, config.ApiKey,
                new AdviceContext("connection-check", 0,
                    "连接检查：直接返回summary=连接成功，reasons和risks为空数组，alternative为null，不调用工具，不分析对局。", new JsonObject()),
                (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), Current, cancellation.Token);
            if (Current()) ModelConfigText.Text = $"模型连接成功，用时 {clock.Elapsed.TotalSeconds:F1} 秒。";
        }
        catch (OperationCanceledException) { if (Current()) ModelConfigText.Text = "连接检查已取消或超时。"; }
        catch (Exception) { if (Current()) ModelConfigText.Text = "连接检查失败，请检查接口地址、模型及凭据。"; }
        finally { if (ReferenceEquals(_connectionCheck, cancellation)) { CheckModelButton.IsEnabled = true; _connectionCheck = null; } }
    });
}
