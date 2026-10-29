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

    private void ModelConfigurationChanged(object sender, RoutedEventArgs e)
    {
        _configurationRevision++;
        _analysis?.Cancel(); _connectionCheck?.Cancel();
        InvalidateAdvice();
        if (ModelConfigText is not null) ModelConfigText.Text = "模型配置已修改，可保存或重新检查连接。";
    }

    private async Task LoadModelConfigurationAsync(bool environmentOnly = false)
    {
        var file = EnvironmentModelConfiguration.FindFile() ?? EnvironmentModelConfiguration.FindFile(AppContext.BaseDirectory);
        var config = file is not null ? await EnvironmentModelConfiguration.LoadAsync(file)
            : environmentOnly ? throw new FileNotFoundException("未在项目目录找到 .env。") : await _modelStore.LoadAsync();
        if (config is null) { ModelConfigText.Text = "尚未配置模型。可编辑项目 .env 或在下方填写。"; return; }
        EndpointBox.Text = config.Settings.Endpoint.ToString();
        ModelBox.Text = config.Settings.Model;
        KeyBox.Password = config.ApiKey;
        _loadedModelSettings = config.Settings;
        ModelConfigText.Text = file is null ? "已加载 Windows 用户加密配置。" : "已从项目 .env 加载模型配置；密钥已填入掩码输入框。";
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
        await LoadModelConfigurationAsync(true);
        StatusText.Text = "已重新加载 .env，密钥不会写入诊断输出。";
    });

    private async void SaveModelClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await _modelStore.SaveAsync(ConfigurationFromFields());
        ModelConfigText.Text = "模型配置已使用 Windows 当前用户凭据加密保存。存在项目 .env 时优先加载 .env。";
        StatusText.Text = "模型配置已保存。";
    });

    private async void CheckModelClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var config = ConfigurationFromFields();
        var configurationRevision = _configurationRevision;
        _connectionCheck?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _connectionCheck = cancellation;
        CheckModelButton.IsEnabled = false;
        ModelConfigText.Text = "正在检查模型连接，本次不发送对局信息…";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _ = await new AdviceService(_http).AnalyzeAsync(config.Settings with { MaxToolRounds = 0, MaxOutputTokens = 512 }, config.ApiKey,
                new AdviceContext("connection-check", 0,
                    "连接检查：直接返回summary=连接成功，reasons和risks为空数组，alternative为null，不调用工具，不分析对局。", new JsonObject()),
                (_, _, _) => Task.FromResult<JsonNode>(new JsonObject()), () => configurationRevision == _configurationRevision, cancellation.Token);
            if (configurationRevision == _configurationRevision) ModelConfigText.Text = $"模型连接成功，用时 {clock.Elapsed.TotalSeconds:F1} 秒。";
        }
        catch (OperationCanceledException) { if (configurationRevision == _configurationRevision) ModelConfigText.Text = "连接检查已取消或超时。"; }
        finally { CheckModelButton.IsEnabled = true; if (ReferenceEquals(_connectionCheck, cancellation)) _connectionCheck = null; }
    });
}
