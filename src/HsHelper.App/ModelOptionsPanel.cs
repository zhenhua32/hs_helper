using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Agent;

namespace HsHelper.App;

public sealed class ModelOptionsCompletedEventArgs(bool saved) : EventArgs
{
    public bool Saved { get; } = saved;
}

/// <summary>The same settings editor is hosted by the modal window and by hidden UI checks.</summary>
public sealed class ModelOptionsPanel : UserControl
{
    private readonly ModelSettings _initial;
    private readonly Func<ModelSettings, Task<bool>> _save;
    private readonly Dictionary<string, TextBox> _numbers = [];
    private readonly CheckBox _tools;
    private readonly ComboBox _format;
    private readonly TextBlock _error;
    private readonly Button _saveButton, _cancelButton;
    private bool _completed;

    public event EventHandler<ModelOptionsCompletedEventArgs>? Completed;
    public bool IsBusy { get; private set; }
    public ModelSettings? SelectedSettings { get; private set; }
    public Task PendingOperation { get; private set; } = Task.CompletedTask;

    public ModelOptionsPanel(ModelSettings initial, Func<ModelSettings, Task<bool>> save)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(save);
        initial.ValidateTuning(); _initial = initial; _save = save;
        NameScope.SetNameScope(this, new NameScope());
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var fields = new StackPanel();
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        _tools = Named(new CheckBox { Content = "启用模型工具调用", IsChecked = initial.EnableTools, Margin = new(0, 0, 0, 6) }, "ModelEnableToolsBox");
        fields.Children.Add(_tools);
        fields.Children.Add(new TextBlock { Text = "关闭工具会省略工具请求字段；交互轮数会保留，重新启用时恢复。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        fields.Children.Add(new TextBlock { Text = "输出格式", Margin = new(0, 0, 0, 4) });
        _format = Named(new ComboBox { ItemsSource = new[] { "提示 JSON", "JSON 对象", "JSON Schema" }, SelectedIndex = (int)initial.ResponseFormat,
            Margin = new(0, 0, 0, 6), MinWidth = 180 }, "ModelResponseFormatBox");
        fields.Children.Add(_format);
        fields.Children.Add(new TextBlock { Text = "提示 JSON 仅在提示中要求 JSON；JSON 对象和 JSON Schema 需要服务支持。不会自动探测或失败回退，输出格式不能代替本地规则校验。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        Number("rounds", "ModelToolRoundsBox", "工具交互轮数（0–4）", initial.MaxToolRounds);
        Number("output", "ModelOutputTokensBox", "单次输出令牌（100–8192）", initial.MaxOutputTokens);
        Number("timeout", "ModelTimeoutBox", "分析超时秒数（1–120）", initial.TimeoutSeconds);
        Number("requests", "ModelBudgetRequestsBox", "单局请求预算（1–10000，含工具交互请求）", initial.BudgetLimits.MaxRequestsPerMatch);
        Number("tokens", "ModelBudgetTokensBox", "单局令牌预算（1–100000000）", initial.BudgetLimits.MaxTokensPerMatch);
        Number("concurrent", "ModelBudgetConcurrentBox", "单局并发请求上限（1–8）", initial.BudgetLimits.MaxConcurrentRequestsPerMatch);
        fields.Children.Add(new TextBlock { Text = "变更预算会先取消并结算现有请求，再从已保存用量恢复。仅修改工具或输出设置不会重置预算。服务未报告用量时使用保守预留。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 10) });
        fields.Children.Add(new TextBlock { Text = "通过校验后先应用设置，再加密保存。写入失败时，已应用设置仍在当前会话生效；关闭面板不会还原，请重试保存。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        var actions = new StackPanel(); Grid.SetRow(actions, 1); root.Children.Add(actions);
        _error = Named(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 8) }, "ModelOptionsErrorText"); actions.Children.Add(_error);
        var buttons = new WrapPanel(); actions.Children.Add(buttons);
        _saveButton = Named(new Button { Content = "保存设置", Margin = new(0, 0, 8, 0), Padding = new(14, 7, 14, 7) }, "SaveModelOptionsButton");
        _cancelButton = Named(new Button { Content = "取消", Padding = new(14, 7, 14, 7) }, "CancelModelOptionsButton");
        buttons.Children.Add(_saveButton); buttons.Children.Add(_cancelButton); Content = root;
        _tools.Checked += (_, _) => RefreshEnabled(); _tools.Unchecked += (_, _) => RefreshEnabled();
        _saveButton.Click += (_, _) => { if (!IsBusy && !_completed) PendingOperation = SaveAsync(); };
        _cancelButton.Click += (_, _) => { if (!IsBusy && !_completed) { _completed = true; RefreshEnabled(); Completed?.Invoke(this, new(false)); } };
        RefreshEnabled();

        void Number(string key, string name, string label, long value)
        {
            fields.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 3) });
            var box = Named(new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), MinWidth = 180, Margin = new(0, 0, 0, 6) }, name);
            _numbers[key] = box; fields.Children.Add(box);
        }
    }

    private T Named<T>(T element, string name) where T : FrameworkElement
    { element.Name = name; RegisterName(name, element); return element; }

    private async Task SaveAsync()
    {
        ModelSettings selected;
        try
        {
            int Read(string key, int min, int max)
            {
                if (!int.TryParse(_numbers[key].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                    throw new ArgumentException("请输入说明范围内的整数。");
                return value;
            }
            if (_format.SelectedIndex is < 0 or > 2) throw new ArgumentException("请选择支持的输出格式。");
            selected = _initial with { EnableTools = _tools.IsChecked == true, ResponseFormat = (ModelResponseFormat)_format.SelectedIndex,
                MaxToolRounds = Read("rounds", 0, 4), MaxOutputTokens = Read("output", 100, 8192), TimeoutSeconds = Read("timeout", 1, 120),
                BudgetLimits = new(Read("requests", 1, 10000), Read("tokens", 1, 100000000), Read("concurrent", 1, 8)) };
            selected.ValidateTuning();
        }
        catch (ArgumentException error) { _error.Text = error.Message; return; }
        IsBusy = true; _error.Text = "正在保存设置…"; RefreshEnabled();
        _cancelButton.Content = "关闭（不还原已应用设置）";
        try
        {
            if (!await _save(selected)) { _error.Text = "配置已变化或保存已取消，请重新打开设置核对。"; return; }
            SelectedSettings = selected; _completed = true; _error.Text = "设置已保存。";
            IsBusy = false;
            Completed?.Invoke(this, new(true));
        }
        catch { _error.Text = "当前会话可能已应用新设置，但尚未保存；关闭不还原，请重试保存。"; }
        finally { IsBusy = false; RefreshEnabled(); }
    }

    private void RefreshEnabled()
    {
        var ready = !IsBusy && !_completed;
        _tools.IsEnabled = _format.IsEnabled = _saveButton.IsEnabled = _cancelButton.IsEnabled = ready;
        foreach (var number in _numbers) number.Value.IsEnabled = ready && (number.Key != "rounds" || _tools.IsChecked == true);
    }
}
