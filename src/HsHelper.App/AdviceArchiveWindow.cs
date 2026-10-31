using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HsHelper.Storage;

namespace HsHelper.App;

public enum AdviceArchiveScopeKind { Session, DeckPlan }
public sealed record AdviceArchiveScope(string MatchId, string DisplayName, AdviceArchiveScopeKind Kind,
    string? DeckVersionId = null, long? OriginEventSequence = null);
public sealed record AdviceArchivePageRequest(long Revision, AdviceArchiveFilter Filter, int PageNumber, DateTimeOffset AsOf);

public sealed class AdviceArchivePagingState
{
    private long _revision;
    public AdviceArchiveFilter? Filter { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public bool HasMore { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsClosed { get; private set; }
    public DateTimeOffset? BrowsingAsOf { get; private set; }
    public AdviceArchivePageRequest Begin(AdviceArchiveFilter filter, int pageNumber, DateTimeOffset now, bool refresh = false)
    {
        if (IsClosed) throw new InvalidOperationException("归档窗口已关闭。");
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (Filter != filter || refresh) pageNumber = 1;
        if (BrowsingAsOf is null || refresh) BrowsingAsOf = now;
        Filter = filter; IsLoading = true;
        var cutoff = filter.RecordedBefore is { } earlier && earlier < BrowsingAsOf.Value ? earlier : BrowsingAsOf.Value;
        return new(++_revision, filter, pageNumber, cutoff);
    }
    public bool IsCurrent(AdviceArchivePageRequest request) => !IsClosed && request.Revision == _revision && request.Filter == Filter;
    public bool TryApply(AdviceArchivePageRequest request, AdviceArchivePage page)
    {
        if (!IsCurrent(request) || page.PageNumber != request.PageNumber || page.AsOf != request.AsOf) return false;
        PageNumber = page.PageNumber; HasMore = page.HasMore; IsLoading = false; return true;
    }
    public bool TryFail(AdviceArchivePageRequest request)
    { if (!IsCurrent(request)) return false; IsLoading = false; return true; }
    public void Close() { _revision++; IsClosed = true; IsLoading = false; }
}

/// <summary>A read-only, independently scoped browser. Its cutoff never changes model or game context.</summary>
public sealed class AdviceArchiveWindow : Window
{
    private readonly AdviceArchiveStore _store;
    private readonly AdviceArchivePagingState _paging = new();
    private CancellationTokenSource? _readCancellation;
    private Task? _initialization;
    private bool _ready, _settingSelection;
    private readonly CheckBox _allTasks;
    private readonly ComboBox _task;
    private readonly TextBox _query, _detail;
    private readonly TextBlock _page, _status, _selection;
    private readonly Button _previous, _next, _refresh;
    private readonly ListBox _list;
    private readonly DependencyPropertyDescriptor _taskTextProperty;
    public AdviceArchiveScope Scope { get; }
    public IReadOnlyList<ArchivedAdviceRecord> Records { get; private set; } = Array.Empty<ArchivedAdviceRecord>();
    public ArchivedAdviceRecord? SelectedRecord { get; private set; }
    public bool IsLoading => _paging.IsLoading;
    public int PageNumber => _paging.PageNumber;
    public DateTimeOffset? BrowsingAsOf => _paging.BrowsingAsOf;
    private sealed record TaskChoice(string Value, string Label);
    private sealed record ArchiveRow(ArchivedAdviceRecord Value)
    { public string Label => $"{Value.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {TaskName(Value.Advice.Task)} · #{Value.Advice.EventSequence}"; }

    public AdviceArchiveWindow(AdviceArchiveStore store, AdviceArchiveScope scope)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.MatchId) || !Enum.IsDefined(scope.Kind) ||
            scope.Kind == AdviceArchiveScopeKind.DeckPlan && (string.IsNullOrWhiteSpace(scope.DeckVersionId) || scope.MatchId != "deck-" + scope.DeckVersionId))
            throw new ArgumentException("归档浏览必须固定对局或套牌版本范围。", nameof(scope));
        _store = store; Scope = scope;
        NameScope.SetNameScope(this, new NameScope());
        Title = scope.Kind == AdviceArchiveScopeKind.DeckPlan ? "套牌分析记录（事后资料）" : "本局建议记录（事后资料）";
        Width = 1040; Height = 720; MinWidth = 700; MinHeight = 460;
        var root = new Grid { Margin = new Thickness(12) };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new() { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var scopeText = Named(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Text =
            $"{scope.DisplayName}\n固定范围：{scope.MatchId}" + (scope.DeckVersionId is { } deck ? $" · 套牌版本 {deck}" : "") +
            (scope.OriginEventSequence is { } seq ? $" · 入口局面 #{seq}（本窗列出该范围各决策点的记录）" : "") +
            "\n事后资料：可能包含后来对旧局面生成的复盘；不改变当前局面、建议或模型知识截止。" }, "ArchiveScopeText");
        root.Children.Add(scopeText);
        var filters = new WrapPanel(); Grid.SetRow(filters, 1); root.Children.Add(filters);
        _allTasks = Named(new CheckBox { Content = "全部任务", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) }, "ArchiveAllTasksBox");
        _task = Named(new ComboBox { Width = 160, IsEditable = true, IsTextSearchEnabled = false, DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 12, 6),
            ToolTip = "选择任务，或输入归档任务原值精确匹配；留空只查旧空任务。", ItemsSource = new[] {
                new TaskChoice("Turn", "回合建议"), new TaskChoice("Mulligan", "留牌建议"), new TaskChoice("Choice", "选择建议"),
                new TaskChoice("Review", "局面复盘"), new TaskChoice("DeckPlan", "套牌计划") } }, "ArchiveTaskFilterBox");
        filters.Children.Add(_allTasks); filters.Children.Add(_task);
        filters.Children.Add(new TextBlock { Text = "摘要或当时提问包含", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) });
        _query = Named(new TextBox { Width = 280, MaxLength = 200, Margin = new Thickness(0, 0, 8, 6), ToolTip = "字面包含，空格和符号按原样匹配。" }, "ArchiveTextFilterBox"); filters.Children.Add(_query);
        var navigation = new WrapPanel(); Grid.SetRow(navigation, 2); root.Children.Add(navigation);
        _previous = Named(new Button { Content = "上一页", Margin = new Thickness(0, 0, 8, 6) }, "ArchivePreviousButton");
        _next = Named(new Button { Content = "下一页", Margin = new Thickness(0, 0, 8, 6) }, "ArchiveNextButton");
        _refresh = Named(new Button { Content = "刷新并回到第一页", Margin = new Thickness(0, 0, 8, 6) }, "ArchiveRefreshButton");
        _page = Named(new TextBlock { Text = "第 1 页 · 尚未读取", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) }, "ArchivePageText");
        navigation.Children.Add(_previous); navigation.Children.Add(_next); navigation.Children.Add(_refresh); navigation.Children.Add(_page);
        _status = Named(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, Margin = new Thickness(0, 0, 0, 6) }, "ArchiveStatusText");
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(310) }); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 4); root.Children.Add(body);
        _list = Named(new ListBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 12, 0) }, "ArchiveRecordsList"); body.Children.Add(_list);
        var detailPanel = new Grid(); detailPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); detailPanel.RowDefinitions.Add(new()); Grid.SetColumn(detailPanel, 1); body.Children.Add(detailPanel);
        _selection = Named(new TextBlock { Text = "请选择一条已保存建议。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }, "ArchiveSelectionText"); detailPanel.Children.Add(_selection);
        _detail = Named(new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "ArchiveDetailText");
        Grid.SetRow(_detail, 1); detailPanel.Children.Add(_detail); Content = root;
        _previous.Click += async (_, _) => { if (!_paging.IsLoading && _paging.PageNumber > 1) await LoadPageAsync(_paging.PageNumber - 1); };
        _next.Click += async (_, _) => { if (!_paging.IsLoading && _paging.HasMore && _paging.PageNumber < int.MaxValue) await LoadPageAsync(_paging.PageNumber + 1); };
        _refresh.Click += async (_, _) => await LoadPageAsync(1, refresh: true);
        _allTasks.Checked += FilterChanged; _allTasks.Unchecked += FilterChanged; _task.SelectionChanged += FilterChanged;
        _taskTextProperty = DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
        _taskTextProperty.AddValueChanged(_task, TaskTextChanged); _query.TextChanged += FilterChanged;
        _list.SelectionChanged += (_, _) => { if (!_settingSelection && _list.SelectedItem is ArchiveRow row) { SelectedRecord = row.Value; _detail.Text = Render(row.Value); RefreshSelection(); } };
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _paging.Close(); _taskTextProperty.RemoveValueChanged(_task, TaskTextChanged); CancelRead(); RefreshControls(); };
        _ready = true; RefreshControls();
    }
    private T Named<T>(T element, string name) where T : FrameworkElement
    { element.Name = name; RegisterName(name, element); return element; }
    public Task InitializeAsync() => _initialization ??= LoadPageAsync(1, refresh: true);
    private AdviceArchiveFilter Filter()
    {
        var exactTask = _task.SelectedItem is TaskChoice choice && _task.Text == choice.Label ? choice.Value : _task.Text;
        return new(MatchId: Scope.MatchId) { Task = _allTasks.IsChecked == true ? null : exactTask, TextContains = _query.Text.Length == 0 ? null : _query.Text };
    }
    private async void FilterChanged(object sender, RoutedEventArgs e)
    { if (_ready && !_paging.IsClosed) { RefreshControls(); await LoadPageAsync(1); } }
    private void TaskTextChanged(object? sender, EventArgs e) => FilterChanged(_task, new RoutedEventArgs());
    private void CancelRead()
    { var previous = _readCancellation; _readCancellation = null; try { previous?.Cancel(); } finally { previous?.Dispose(); } }
    private async Task LoadPageAsync(int pageNumber, bool refresh = false)
    {
        if (_paging.IsClosed) return;
        CancelRead();
        var request = _paging.Begin(Filter(), pageNumber, DateTimeOffset.UtcNow, refresh);
        var cancellation = _readCancellation = new CancellationTokenSource(); var token = cancellation.Token;
        _status.Text = "正在读取固定范围内的事后建议…"; RefreshControls();
        try
        {
            var page = await Task.Run(() => _store.ListPageAsOfAsync(request.AsOf, request.Filter, request.PageNumber, 100, token), token);
            if (!_paging.TryApply(request, page)) return;
            Records = page.Records;
            var rows = Records.Select(record => new ArchiveRow(record)).ToArray();
            _settingSelection = true;
            try { _list.ItemsSource = rows; _list.SelectedItem = rows.FirstOrDefault(row => row.Value.Id == SelectedRecord?.Id); }
            finally { _settingSelection = false; }
            if (SelectedRecord is null && rows.Length > 0) { _list.SelectedIndex = 0; }
            _page.Text = $"第 {page.PageNumber} 页 · 本页 {Records.Count} 条 · " + (page.HasMore ? "后面还有记录" : "已到末页");
            _status.Text = Records.Count == 0 ? "当前筛选没有已保存的建议；可调整筛选或刷新。" : $"浏览截止 {request.AsOf.ToLocalTime():yyyy-MM-dd HH:mm:ss}；刷新可包含新归档。";
            RefreshSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        { if (_paging.TryFail(request)) _status.Text = "归档读取失败，请检查筛选参数后刷新重试（任务最长80字，查询最长200字，均不能含控制符）。"; }
        finally
        {
            if (ReferenceEquals(_readCancellation, cancellation)) { _readCancellation = null; cancellation.Dispose(); }
            if (_paging.IsCurrent(request)) RefreshControls();
        }
    }
    private void RefreshSelection()
    {
        if (SelectedRecord is not { } record) return;
        _selection.Text = $"正在查看 {record.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 记录 {record.Id}" +
            (Records.Any(row => row.Id == record.Id) ? "（本页）" : "（不在当前页或筛选中，仍保留所选详情）");
    }
    private void RefreshControls()
    {
        var ready = !_paging.IsClosed;
        _previous.IsEnabled = ready && !_paging.IsLoading && _paging.PageNumber > 1;
        _next.IsEnabled = ready && !_paging.IsLoading && _paging.HasMore && _paging.PageNumber < int.MaxValue;
        _list.IsEnabled = ready && !_paging.IsLoading; _refresh.IsEnabled = _allTasks.IsEnabled = _query.IsEnabled = ready;
        _task.IsEnabled = ready && _allTasks.IsChecked != true;
    }
    private static string TaskName(string? value) => value switch
    { "Turn" => "回合建议", "Mulligan" => "留牌建议", "Choice" => "选择建议", "Review" => "局面复盘", "DeckPlan" => "套牌计划", "" or null => "未记录任务", _ => "其他任务" };
    public static string Render(ArchivedAdviceRecord record)
    {
        var a = record.Advice;
        var text = new StringBuilder($"事后资料：这是当时生成的建议，不是当前可执行步骤。\n记录：{record.Id}\n保存时间：{record.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n" +
            $"对局：{a.MatchId}\n套牌版本：{a.DeckVersionId ?? "未记录"}\n卡库：{a.CardBuild}\n任务：{TaskName(a.Task)}（原值：{(a.Task is null ? "未记录" : a.Task.Length == 0 ? "空" : a.Task)}）\n" +
            $"局面版本：{a.StateVersion} · 事件：#{a.EventSequence}\n原验证状态：{a.Verification}（仅当时的记录）\n模型：{a.Model ?? "旧记录未保存来源"}\n模板：{a.PromptTemplateVersion}\n分析：{a.AnalysisRunId ?? "未知"}\n" +
            $"知识截止：{a.KnowledgeAsOf?.ToString("O") ?? "旧记录未知"}\n请求来源：{(a.RequestIds is { Count: > 0 } ? string.Join(", ", a.RequestIds) : "未记录")}\n此前解释：{(a.ConversationArchiveIds is { Count: > 0 } ? string.Join(", ", a.ConversationArchiveIds) : "未记录")}\n");
        if (a.Task == "DeckPlan") text.AppendLine("赛前套牌分析，不是实时对局局面。");
        if (!string.IsNullOrWhiteSpace(a.UserQuestion)) text.AppendLine($"\n当时提问\n{a.UserQuestion}");
        text.AppendLine($"\n建议\n{a.Summary}\n\n依据"); foreach (var reason in a.Reasons) text.AppendLine(reason);
        text.AppendLine($"\n备选\n{a.Alternative ?? "无"}");
        text.Append(AdviceLimitationsPresentation.Render(a));
        text.AppendLine($"\n当时的归档判断\n斩杀标记：{(a.VerifiedLethal ? "是" : "否")}（不是对当前局面的认证）\n\n当时的归档动作（仅供复盘）");
        if (a.Actions.Count == 0) text.AppendLine("未保存动作。");
        foreach (var action in a.Actions)
        {
            var name = action.Kind.ToLowerInvariant() switch { "play" => "使用卡牌", "attack" => "攻击", "choose" => "选择候选", "end_turn" => "结束回合",
                "hero_power" => "使用英雄技能", _ => "其他动作（" + action.Kind + "）" };
            text.AppendLine($"{name} · 来源 {(action.SourceEntityId is { } source ? "#" + source : "未知")} · 目标 {(action.TargetEntityId is { } target ? "#" + target : "未指定")}");
        }
        text.AppendLine("\n工具依据"); foreach (var evidence in a.Evidence) text.AppendLine($"{evidence.ToolName} · {evidence.Summary}");
        return text.ToString();
    }
}
