using System.Windows;
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
    private async void ModelOptionsClicked(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var panel = CreateModelOptionsPanel();
        var dialog = new Window { Owner = this, Title = "模型与预算设置", Width = 470, Height = 650,
            MinWidth = 380, MinHeight = 400, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        InputCompatibility.Attach(dialog);
        panel.Completed += (_, result) => dialog.DialogResult = result.Saved;
        dialog.Closing += (_, closing) => { if (panel.IsBusy) closing.Cancel = true; };
        dialog.ShowDialog();
        return Task.CompletedTask;
    });

    private ModelOptionsPanel CreateModelOptionsPanel() => new(ConfigurationFromFields().Settings, SaveModelOptionsAsync);

    private async Task<bool> SaveModelOptionsAsync(ModelSettings selected)
    {
        if (_closing) return false;
        _loadedModelSettings = selected;
        ModelConfigurationChanged(this, new RoutedEventArgs());
        var revision = _configurationRevision;
        var capturedConfiguration = ConfigurationFromFields();
        await ApplyBudgetPolicyAsync(selected.BudgetLimits);
        if (await SaveModelConfigurationAsync(capturedConfiguration, revision))
        {
            ModelConfigText.Text = "模型能力与调优设置已加密保存；重新加载时 .env 显式填写的同名字段优先。";
            return true;
        }
        return false;
    }
}
