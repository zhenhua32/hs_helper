using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Agent;

namespace HsHelper.App;

public partial class MainWindow
{
    private AnalysisBudgetLimits _activeBudgetLimits = new();
    private readonly SemaphoreSlim _budgetPolicyGate = new(1, 1);

    private async Task ApplyBudgetPolicyAsync(AnalysisBudgetLimits limits)
    {
        await _budgetPolicyGate.WaitAsync();
        try
        {
            if (_activeBudgetLimits == limits) return;
            _analysis?.Cancel();
            try { await Task.WhenAll(_analysisTasks.ToArray()); }
            catch (Exception) { /* Existing request finally blocks retain all reported usage. */ }
            await FlushTracesAsync();
            _analysisBudget = new(limits); _activeBudgetLimits = limits; _seededBudgets.Clear();
        }
        finally { _budgetPolicyGate.Release(); }
    }
    private async void ModelOptionsClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var config = ConfigurationFromFields();
        var options = config.Settings;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var fields = new Dictionary<string, TextBox>();
        void Field(string name, string label, long value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 3) });
            var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), MinWidth = 180 };
            fields[name] = box; panel.Children.Add(box);
        }
        Field("rounds", "工具交互轮数（0–4）", options.MaxToolRounds);
        Field("output", "单次输出令牌（100–8192）", options.MaxOutputTokens);
        Field("timeout", "分析超时秒数（1–120）", options.TimeoutSeconds);
        Field("requests", "单局请求预算（1–10000，含工具交互请求）", options.BudgetLimits.MaxRequestsPerMatch);
        Field("tokens", "单局令牌预算（1–100000000）", options.BudgetLimits.MaxTokensPerMatch);
        Field("concurrent", "单局并发请求上限（1–8）", options.BudgetLimits.MaxConcurrentRequestsPerMatch);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock { Text = "变更预算会先取消并结算现有请求，再从已保存用量恢复。服务未报告用量时使用保守预留。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        var save = new Button { Content = "保存调优设置", HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(save);
        var dialog = new Window { Owner = this, Title = "模型与预算设置", Width = 470, Height = 650,
            Content = new ScrollViewer { Content = panel }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ModelSettings? selected = null;
        save.Click += (_, _) =>
        {
            try
            {
                int Read(string key, int min, int max)
                {
                    if (!int.TryParse(fields[key].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                        throw new ArgumentException("请输入说明范围内的整数。");
                    return value;
                }
                selected = options with { MaxToolRounds = Read("rounds", 0, 4), MaxOutputTokens = Read("output", 100, 8192),
                    TimeoutSeconds = Read("timeout", 1, 120), BudgetLimits = new(Read("requests", 1, 10000),
                        Read("tokens", 1, 100000000), Read("concurrent", 1, 8)) };
                dialog.DialogResult = true;
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        if (dialog.ShowDialog() != true || selected is null) return;
        _loadedModelSettings = selected;
        ModelConfigurationChanged(this, new RoutedEventArgs());
        var revision = _configurationRevision;
        var capturedConfiguration = ConfigurationFromFields();
        await ApplyBudgetPolicyAsync(selected.BudgetLimits);
        if (await SaveModelConfigurationAsync(capturedConfiguration, revision))
            ModelConfigText.Text = "调优设置已加密保存；.env 显式填写的同名调优字段在重新加载时优先。";
    });
}
