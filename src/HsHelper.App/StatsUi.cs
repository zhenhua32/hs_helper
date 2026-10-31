using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using HsHelper.Storage;

namespace HsHelper.App;

/// <summary>Keeps the user's current text distinct from DatePicker's retained last valid SelectedDate.</summary>
public sealed class StatsDatePicker : DatePicker
{
    private TextBox? _editor;
    private int _updating;
    private bool _ready;
    private CultureInfo _culture = CultureInfo.CurrentCulture;
    private string _lastValidText = "";
    private bool _validationRejected;
    public string RawText { get; private set; } = "";
    public DateTime? DateValue { get; private set; }
    public bool HasInputError { get; private set; }
    public string? InputError { get; private set; }
    public event EventHandler? InputChanged;
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            ArgumentNullException.ThrowIfNull(value); _culture = value;
            Language = XmlLanguage.GetLanguage(value.Name.Length == 0 ? "en-US" : value.IetfLanguageTag);
            if (_ready) Capture(RawText);
        }
    }
    public StatsDatePicker()
    {
        Culture = CultureInfo.CurrentCulture; _ready = true;
        DateValidationError += (_, e) =>
        {
            e.ThrowException = false;
            if (_updating != 0) return;
            Capture(e.Text); _validationRejected = HasInputError; RestoreEditor();
        };
    }
    public void SetDate(DateTime? value)
    {
        _validationRejected = false; _updating++;
        var raw = value?.Date.ToString("d", Culture) ?? "";
        try { SetCurrentValue(SelectedDateProperty, value?.Date); SetCurrentValue(TextProperty, raw); }
        finally { _updating--; }
        Capture(raw); RestoreEditor();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (!_ready || _updating != 0 || e.Property != TextProperty && e.Property != SelectedDateProperty)
        { base.OnPropertyChanged(e); return; }
        var raw = e.Property == TextProperty ? e.NewValue as string ?? "" : (e.NewValue as DateTime?)?.Date.ToString("d", Culture) ?? "";
        var rollback = e.Property == TextProperty && _validationRejected && raw == _lastValidText;
        _updating++;
        try { base.OnPropertyChanged(e); }
        finally { _updating--; }
        if (!rollback) { _validationRejected = false; Capture(raw); }
        RestoreEditor();
    }
    public override void OnApplyTemplate()
    {
        if (_editor is not null)
        {
            _editor.TextChanged -= RawTextChanged; _editor.LostFocus -= EditorLostFocus;
            _editor.LostKeyboardFocus -= EditorLostFocus; _editor.PreviewKeyDown -= EditorPreviewKeyDown;
        }
        // Register before DatePicker's own handlers so invalid text is not replaced with its last valid value.
        _editor = GetTemplateChild("PART_TextBox") as TextBox;
        if (_editor is not null)
        {
            _editor.TextChanged += RawTextChanged; _editor.LostFocus += EditorLostFocus;
            _editor.LostKeyboardFocus += EditorLostFocus; _editor.PreviewKeyDown += EditorPreviewKeyDown;
        }
        _updating++;
        try { base.OnApplyTemplate(); }
        finally { _updating--; }
        RestoreEditor();
    }
    private void RawTextChanged(object sender, TextChangedEventArgs e)
    {
        e.Handled = true;
        if (_updating != 0 || _editor is null) return;
        var raw = _editor.Text; _validationRejected = false; _updating++;
        try { SetCurrentValue(TextProperty, raw); }
        finally { _updating--; }
        Capture(raw); RestoreEditor();
    }
    private void EditorLostFocus(object sender, RoutedEventArgs e)
    { if (HasInputError) { e.Handled = true; RestoreEditor(); } }
    private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    { if (HasInputError && e.Key == Key.Enter) { e.Handled = true; RestoreEditor(); } }
    private void RestoreEditor()
    {
        if (_editor is null || _editor.Text == RawText) return;
        _updating++; try { _editor.SetCurrentValue(TextBox.TextProperty, RawText); } finally { _updating--; }
    }
    private void Capture(string raw)
    {
        var previousDate = DateValue; var previousError = HasInputError; var previousText = RawText;
        RawText = raw; DateValue = null; HasInputError = false; InputError = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var patterns = Culture.DateTimeFormat.GetAllDateTimePatterns('d').Append("yyyy-MM-dd").Distinct().ToArray();
            if (DateTime.TryParseExact(raw, patterns, Culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) DateValue = parsed.Date;
            else { HasInputError = true; InputError = "请输入完整有效日期，或清空日期。"; }
        }
        if (!HasInputError) _lastValidText = raw;
        if (previousDate != DateValue || previousError != HasInputError || HasInputError && previousText != raw)
            InputChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record HistoryStatsQueryResult(SessionPage Page, PersonalStats Stats);
public sealed record HistoryMatchupRow(string OpponentClass, string Name, int Games, int Wins, int Losses, int Ties,
    int Unknown, int EffectiveGames, string WinRateText)
{
    public static HistoryMatchupRow From(ClassMatchupStats value) => new(value.OpponentClass, value.OpponentClass.ToUpperInvariant() switch
    {
        "DEATHKNIGHT" => "死亡骑士", "DEMONHUNTER" => "恶魔猎手", "DRUID" => "德鲁伊", "HUNTER" => "猎人", "MAGE" => "法师",
        "PALADIN" => "圣骑士", "PRIEST" => "牧师", "ROGUE" => "潜行者", "SHAMAN" => "萨满祭司", "WARLOCK" => "术士", "WARRIOR" => "战士",
        "UNKNOWN" or "" => "职业未知", _ => "其他职业"
    }, value.Games, value.Wins, value.Losses, value.Ties, value.Unknown, value.Wins + value.Losses,
        value.WinRate is { } rate ? rate.ToString("P1", CultureInfo.CurrentCulture) : "样本不足");
}

public partial class MainWindow
{
    private bool _historyFilterValid = true, _updatingStatsDates;
    private Func<DateTimeOffset> _historyNow = () => DateTimeOffset.UtcNow;
    private TimeZoneInfo _statsTimeZone = TimeZoneInfo.Local;
    private Func<SessionFilter, int, CancellationToken, Task<HistoryStatsQueryResult>>? _historyQueryOverride = null;

    private bool TryHistoryFilter(out SessionFilter filter, out string? error)
    {
        filter = HistoryFilter(); error = null;
        if (StatsSincePicker.HasInputError || StatsUntilPicker.HasInputError)
        { error = StatsSincePicker.HasInputError ? "开始日期无效：" + StatsSincePicker.InputError : "结束日期无效：" + StatsUntilPicker.InputError; return false; }
        try
        {
            var range = HistoryDateRange.FromDates(StatsSincePicker.DateValue, StatsUntilPicker.DateValue, _statsTimeZone);
            filter = filter with { Since = range.Since, Until = range.Until }; return true;
        }
        catch (ArgumentException failure) { error = failure.Message; return false; }
    }
    private void CancelHistoryQueryAndClear()
    {
        var old = _historyListCancellation; _historyListCancellation = null;
        try { old?.Cancel(); } finally { old?.Dispose(); }
        _historyPaging.Reset(_historyPaging.Scope ?? new());
        ClearHistoryPageSelection(); StatsMatchupsGrid.ItemsSource = null; StatsText.Text = "尚未查询。";
    }
    private void ShowHistoryDateError(string? error)
    {
        _historyFilterValid = false; StatsDateErrorText.Text = error ?? "日期范围无效。";
        StatsDateRangeText.Text = "本机观察/导入日期无效，尚未查询；请修正或清空日期。";
        HistoryPageText.Text = "第 1 页 · 日期无效，尚未查询"; StatsText.Text = "日期无效，尚未查询。";
        RefreshHistoryPageButtons();
    }
    private async Task ApplyHistoryFilterChangeAsync()
    {
        CancelHistoryQueryAndClear();
        if (!TryHistoryFilter(out _, out var error)) { ShowHistoryDateError(error); return; }
        _historyFilterValid = true; StatsDateErrorText.Text = "";
        await LoadHistoryPageAsync(1, refresh: true);
    }
    private async void StatsDatesChanged(object? sender, EventArgs e)
    { if (!_updatingStatsDates && _store is not null && !_closing) await Run(ApplyHistoryFilterChangeAsync); }
    private async void StatsRecentDaysClicked(object sender, RoutedEventArgs e)
    {
        if (_store is null || _closing || sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var days)) return;
        var today = TimeZoneInfo.ConvertTime(_historyNow(), _statsTimeZone).Date;
        var dates = HistoryDateRange.RecentDates(today, days);
        _updatingStatsDates = true;
        try { StatsSincePicker.SetDate(dates.Since); StatsUntilPicker.SetDate(dates.Until); }
        finally { _updatingStatsDates = false; }
        await Run(ApplyHistoryFilterChangeAsync);
    }
    private async void StatsAllDatesClicked(object sender, RoutedEventArgs e)
    {
        if (_store is null || _closing) return;
        _updatingStatsDates = true; try { StatsSincePicker.SetDate(null); StatsUntilPicker.SetDate(null); } finally { _updatingStatsDates = false; }
        await Run(ApplyHistoryFilterChangeAsync);
    }
    private void PublishHistoryStats(PersonalStats stats, HistoryPageRequest request)
    {
        StatsMatchupsGrid.ItemsSource = stats.Matchups.Select(HistoryMatchupRow.From).ToArray();
        StatsText.Text = $"{(request.Scope.GameType is null ? "标准猎人" : "标准猎人天梯")} · {stats.Games} 局 · {stats.Wins}胜 {stats.Losses}负 {stats.Ties}平 · 未完成/未知 {stats.Unknown} 局 · " +
            $"有效胜负 {stats.Wins + stats.Losses} 局 · 胜率 {(stats.WinRate is { } rate ? rate.ToString("P1") : "样本不足")}";
        string DateText(DateTime? date) => date?.ToString("d", StatsSincePicker.Culture) ?? "不限";
        StatsDateRangeText.Text = $"本机首次观察/导入日期：{DateText(StatsSincePicker.DateValue)} 至 {DateText(StatsUntilPicker.DateValue)}（含首尾）" +
            (request.Query.Until is { } cutoff ? $" · 仅含 {TimeZoneInfo.ConvertTime(cutoff, _statsTimeZone):yyyy-MM-dd HH:mm:ss zzz} 及以前开始记录的对局" : "") +
            "；胜负采用本次读取时已保存的结果。";
    }
}
