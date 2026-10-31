using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

namespace HsHelper.App;

/// <summary>A model-free, read-only calculator bound to one immutable decision frame.</summary>
public sealed class LocalToolsWindow : Window
{
    private readonly CardCatalog _catalog;
    private readonly TextBlock _binding = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _quality = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _deck = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _card = new() { MinWidth = 220, DisplayMemberPath = nameof(CardOption.Text) };
    private readonly TextBox _draws = new() { Text = "1", Width = 55 };
    private readonly Button _odds = new() { Content = "计算抽中概率" };
    private readonly ComboBox _kind = new() { MinWidth = 115, DisplayMemberPath = nameof(KindOption.Text) };
    private readonly ComboBox _source = new() { MinWidth = 190, DisplayMemberPath = nameof(EntityOption.Text) };
    private readonly ComboBox _target = new() { MinWidth = 190, DisplayMemberPath = nameof(EntityOption.Text) };
    private readonly Button _add = new() { Content = "添加动作" };
    private readonly Button _remove = new() { Content = "移除选中动作" };
    private readonly ListBox _actions = new() { MinHeight = 80, MaxHeight = 150, DisplayMemberPath = nameof(LocalAction.Text) };
    private readonly Button _evaluate = new() { Content = "验证候选顺序" };
    private readonly Button _previewDraw = new() { Content = "预览所选手牌抽牌" };
    private readonly Button _previewGeneration = new() { Content = "预览随机生成数量" };
    private readonly Button _previewWolfBlessing = new() { Content = "比较巨狼祝福分支" };
    private readonly Button _previewMagmaSplit = new() { Content = "预览熔岩猎犬分配", ToolTip = "使用当前攻击者与攻击对象预览基础交换及后置逐包随机分配；攻击对象不是随机命中，不改变当前局面。" };
    private readonly ComboBox _trapSource = new() { MinWidth = 180, DisplayMemberPath = nameof(EntityOption.Text) };
    private readonly ComboBox _trapAttacker = new() { MinWidth = 190, DisplayMemberPath = nameof(EntityOption.Text) };
    private readonly Button _previewExplosiveTrap = new() { Content = "预览爆炸陷阱来袭", ToolTip = "仅假设当前敌方角色攻击我方英雄，预览我方已安装陷阱的直接触发；不执行对手动作或修改局面。" };
    private readonly Button _previewPlayTriggers = new() { Content = "预览使用后触发", ToolTip = "预览原生发现、拼装、回溯入口及使用后任务和迅猛龙触发；各段条件独立，实际出牌仍须验证。" };
    private readonly Button _inspectEffect = new() { Content = "查看所选牌当前效果" };
    private readonly TextBox _depth = new() { Text = "4", Width = 45 };
    private readonly TextBox _nodes = new() { Text = "1500", Width = 65 };
    private readonly Button _lethal = new() { Content = "有限搜索斩杀" };
    private readonly TextBlock _choiceLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _choices = new() { MinHeight = 55, MaxHeight = 140, SelectionMode = SelectionMode.Multiple, DisplayMemberPath = nameof(EntityOption.Text) };
    private readonly Button _validateChoice = new() { Content = "仅验证所选候选与数量" };
    private readonly Button _evaluateChoice = new() { Content = "评估选择直接后果" };
    private readonly Button _cancel = new() { Content = "取消计算", IsEnabled = false };
    private readonly TextBox _result = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<LocalAction> _line = [];
    private GameSnapshot _snapshot;
    private string? _fixedBuild;
    private bool _historical;
    private SnapshotCoach? _coach;
    private BattleViewState? _view;
    private IReadOnlyDictionary<string, CardRecord> _cards = new Dictionary<string, CardRecord>();
    private CancellationTokenSource? _bindingCancellation;
    private CancellationTokenSource? _calculation;
    private long _revision;
    private bool _closed;

    public bool IsBindingValid { get; private set; }
    public bool HasPendingCalculation => _calculation is not null;
    public string BoundMatchId => _snapshot.MatchId;
    public long BoundStateVersion => _snapshot.StateVersion;
    public long BoundEventSequence => _snapshot.EventSequence;
    public Task BindingReady { get; private set; } = Task.CompletedTask;
    public string ResultText => _result.Text;

    public LocalToolsWindow(GameSnapshot snapshot, string? fixedBuild, CardCatalog catalog, bool isHistorical = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(catalog);
        _snapshot = snapshot; _catalog = catalog;
        Title = "本地计算 · 无需模型"; Width = 950; Height = 820; MinWidth = 640; MinHeight = 460;
        NameScope.SetNameScope(this, new NameScope());
        Content = BuildContent();
        _kind.ItemsSource = new[] { new KindOption("play", "使用手牌"), new KindOption("attack", "角色攻击"), new KindOption("hero_power", "英雄技能"), new KindOption("end_turn", "结束回合") };
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += (_, _) => RefreshSources();
        _odds.Click += (_, _) =>
        {
            if (_card.SelectedItem is not CardOption card || !Number(_draws, 1, 30, "抽牌张数", out var draws)) return;
            _ = RunToolAsync("calculate_draw_odds", new() { ["card_id"] = card.CardId, ["draws"] = draws });
        };
        _add.Click += (_, _) => AddAction();
        _source.SelectionChanged += (_, _) => RefreshEnabled();
        _target.SelectionChanged += (_, _) => RefreshEnabled();
        _trapSource.SelectionChanged += (_, _) => RefreshEnabled();
        _trapAttacker.SelectionChanged += (_, _) => RefreshEnabled();
        _previewExplosiveTrap.Click += (_, _) =>
        {
            if (_trapSource.SelectedItem is EntityOption source && _trapAttacker.SelectedItem is EntityOption attacker)
                _ = RunToolAsync("evaluate_explosive_trap", new() { ["source_entity_id"] = source.Id, ["attacker_entity_id"] = attacker.Id });
        };
        _previewDraw.Click += (_, _) =>
        {
            if (_source.SelectedItem is EntityOption source)
                _ = RunToolAsync("evaluate_draw", new() { ["source_entity_id"] = source.Id });
        };
        _previewGeneration.Click += (_, _) =>
        {
            if (_source.SelectedItem is EntityOption source)
                _ = RunToolAsync("evaluate_generation", new() { ["source_entity_id"] = source.Id });
        };
        _previewWolfBlessing.Click += (_, _) =>
        {
            if (_source.SelectedItem is EntityOption source)
                _ = RunToolAsync("evaluate_wolf_blessing", new() { ["source_entity_id"] = source.Id });
        };
        _previewMagmaSplit.Click += (_, _) =>
        {
            if (_source.SelectedItem is EntityOption source && _target.SelectedItem is EntityOption target)
                _ = RunToolAsync("evaluate_magma_split", new() { ["source_entity_id"] = source.Id, ["target_entity_id"] = target.Id });
        };
        _previewPlayTriggers.Click += (_, _) =>
        {
            if (_source.SelectedItem is EntityOption source)
                _ = RunToolAsync("evaluate_play_triggers", new() { ["source_entity_id"] = source.Id });
        };
        _inspectEffect.Click += (_, _) =>
        {
            if (_source.SelectedItem is EntityOption source)
                _ = RunToolAsync("get_entity_effect", new() { ["entity_id"] = source.Id });
        };
        _remove.Click += (_, _) => { if (_actions.SelectedIndex is var index && index >= 0 && index < _line.Count) { _line.RemoveAt(index); RefreshLine(); } };
        _evaluate.Click += (_, _) =>
        {
            if (_line.Count == 0) { _result.Text = "请先添加至少一个候选动作。"; return; }
            var actions = new JsonArray();
            foreach (var action in _line)
            {
                var item = new JsonObject { ["kind"] = action.Kind, ["source_entity_id"] = action.SourceId };
                if (action.TargetId.HasValue) item["target_entity_id"] = action.TargetId.Value;
                actions.Add(item);
            }
            _ = RunToolAsync("evaluate_line", new() { ["actions"] = actions });
        };
        _lethal.Click += (_, _) =>
        {
            if (!Number(_depth, 1, 6, "搜索深度", out var depth) || !Number(_nodes, 1, 5000, "节点上限", out var nodes)) return;
            _ = RunToolAsync("find_lethal", new() { ["maximum_depth"] = depth, ["maximum_nodes"] = nodes });
        };
        _validateChoice.Click += (_, _) =>
        {
            if (OwnChoice() is not { } choice) return;
            var selected = new JsonArray();
            foreach (var option in _choices.SelectedItems.OfType<EntityOption>()) selected.Add(option.Id);
            _ = RunToolAsync("validate_choice", new() { ["choice_id"] = choice.Id, ["selected_entity_ids"] = selected });
        };
        _evaluateChoice.Click += (_, _) =>
        {
            if (OwnChoice() is not { } choice) return;
            var selected = new JsonArray();
            foreach (var option in _choices.SelectedItems.OfType<EntityOption>()) selected.Add(option.Id);
            _ = RunToolAsync("evaluate_choice", new() { ["choice_id"] = choice.Id, ["selected_entity_ids"] = selected });
        };
        _cancel.Click += (_, _) => CancelCalculation();
        Closed += (_, _) => { _closed = true; Invalidate("计算窗口已关闭。"); };
        UpdateBinding(snapshot, fixedBuild, isHistorical);
    }

    /// <summary>The host must call when the live frame, perspective, pinned deck or card build changes.</summary>
    public void Invalidate(string reason = "局面已变化，旧结果已失效。")
    {
        Dispatcher.VerifyAccess();
        _revision++; IsBindingValid = false;
        _bindingCancellation?.Cancel(); _bindingCancellation?.Dispose(); _bindingCancellation = null;
        _calculation?.Cancel(); _calculation?.Dispose(); _calculation = null;
        _coach = null;
        _binding.Text = reason;
        _result.Text = reason + "\n请由主窗口重新打开当前局面，或更新此窗口的局面绑定。";
        RefreshEnabled();
    }

    public void UpdateBinding(GameSnapshot snapshot, string? fixedBuild, bool isHistorical = false)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(snapshot);
        if (_closed) throw new ObjectDisposedException(nameof(LocalToolsWindow));
        Invalidate();
        _snapshot = snapshot; _fixedBuild = fixedBuild; _historical = isHistorical;
        _cards = new Dictionary<string, CardRecord>(); _line.Clear(); RefreshLine();
        _source.ItemsSource = Array.Empty<EntityOption>(); _target.ItemsSource = Array.Empty<EntityOption>();
        _trapSource.ItemsSource = Array.Empty<EntityOption>(); _trapAttacker.ItemsSource = Array.Empty<EntityOption>();
        _choices.ItemsSource = Array.Empty<EntityOption>();
        try
        {
            _coach = new SnapshotCoach(snapshot, CompactAdviceContextBuilder.Build(snapshot, fixedBuild), _catalog, fixedBuild,
                taskKind: AdviceTaskKind.Turn);
            IsBindingValid = true;
        }
        catch (ArgumentException error) { _binding.Text = error.Message; _result.Text = "局面绑定无效，无法开始计算。"; }
        RenderBinding();
        if (!IsBindingValid) return;
        _result.Text = "所有计算仅作用于本窗口的固定局面，不会执行游戏操作或调用模型。\n未覆盖效果会明确显示为条件成立或无法验证。";
        _bindingCancellation = new CancellationTokenSource();
        BindingReady = LoadNamesAsync(_revision, _bindingCancellation.Token);
    }

    private async Task LoadNamesAsync(long revision, CancellationToken token)
    {
        var snapshot = _snapshot;
        try
        {
            if (_fixedBuild is { Length: > 0 } build && build != "unknown")
            {
                var visible = BattleViewBuilder.VisibleCardIds(snapshot, build).Concat(OwnInstalledTraps(snapshot).Select(entity => entity.CardId!)).Distinct(StringComparer.Ordinal);
                var cards = await _catalog.GetByIdsAsync(visible, build, cancellationToken: token);
                var completeCards = await SnapshotCardEffects.LoadDonorsAsync(snapshot, build, _catalog, cards, token);
                if (!Current(revision)) return;
                _cards = completeCards;
                RenderBinding();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current(revision)) _quality.Text += "\n本地中文卡名读取失败：" + error.Message;
        }
    }

    private void RenderBinding()
    {
        var selectedTarget = (_target.SelectedItem as EntityOption)?.Id;
        var selectedTrap = (_trapSource.SelectedItem as EntityOption)?.Id;
        var selectedAttacker = (_trapAttacker.SelectedItem as EntityOption)?.Id;
        var selectedChoices = _choices.SelectedItems.OfType<EntityOption>().Select(option => option.Id).ToHashSet();
        _view = BattleViewBuilder.Build(_snapshot, _cards, _fixedBuild);
        if (IsBindingValid) _binding.Text = (_historical ? "历史局面（只复算此时信息）" : "当前绑定局面") +
            $" · 回合 {_snapshot.Turn?.ToString(CultureInfo.InvariantCulture) ?? "未知"} · 序号 {_snapshot.EventSequence} · 状态 {_snapshot.StateVersion} · 卡库 {_fixedBuild ?? "未知"}";
        _quality.Text = $"视角：{(_view.HasLocalPerspective ? "已确认" : "未知")}；日志：{(_view.IsComplete ? "完整" : "有缺口")}；局面：{(_view.IsStable ? "稳定" : "结算中")}。" +
            (_view.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", _view.Warnings));
        _deck.Text = (_view.DeckIsExact ? "已确认牌库组成" : "牌库组成不完整，不能给出精确概率；以下仅列已知剩余下限") +
            $" · 剩余总数 {_view.Local.DeckCount?.ToString(CultureInfo.InvariantCulture) ?? "未知"} · 未知牌数 {_view.UnknownDeckCards?.ToString(CultureInfo.InvariantCulture) ?? "未知"}\n" +
            string.Join("\n", _view.RemainingDeck.Select(card => $"{card.Name} × {card.Copies}"));
        var oldCard = (_card.SelectedItem as CardOption)?.CardId;
        var ids = _view.RemainingDeck.Select(card => card.CardId).Concat(_view.Local.Hand.Concat(_view.Local.Board)
            .Select(card => card.CardId).OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        _card.ItemsSource = ids.Select(id => new CardOption(id, CardName(id))).OrderBy(option => option.Text, StringComparer.Ordinal).ToArray();
        _card.SelectedItem = _card.Items.OfType<CardOption>().FirstOrDefault(option => option.CardId == oldCard) ?? _card.Items.OfType<CardOption>().FirstOrDefault();
        var targets = _view.Local.Board.Concat(_view.Opponent.Board).Concat(new[] { _view.Local.Hero, _view.Opponent.Hero }.OfType<BattleCardView>())
            .Select(card => new EntityOption(card.EntityId, (card.EntityId == _view.Local.Hero?.EntityId || _view.Local.Board.Any(c => c.EntityId == card.EntityId) ? "我方 · " : "对手 · ") + CardLabel(card))).ToList();
        targets.Insert(0, new(0, "无指定目标"));
        _target.ItemsSource = targets;
        _target.SelectedItem = targets.FirstOrDefault(target => target.Id == selectedTarget) ?? targets[0];
        RefreshSources();
        var traps = OwnInstalledTraps(_snapshot).Select(entity => new EntityOption(entity.Id, CardName(entity.CardId) + $" #{entity.Id}")).ToArray();
        _trapSource.ItemsSource = traps;
        _trapSource.SelectedItem = traps.FirstOrDefault(entity => entity.Id == selectedTrap) ?? traps.FirstOrDefault();
        var attackers = ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is { } trapLocal
            ? _view.Opponent.Board.Concat(new[] { _view.Opponent.Hero }.OfType<BattleCardView>())
                .Where(card => _snapshot.Entities.TryGetValue(card.EntityId, out var entity) && PreviewPublicCharacter(entity, trapLocal))
                .Select(card => new EntityOption(card.EntityId, CardLabel(card))).ToArray() : [];
        _trapAttacker.ItemsSource = attackers;
        _trapAttacker.SelectedItem = attackers.FirstOrDefault(entity => entity.Id == selectedAttacker) ?? attackers.FirstOrDefault();
        if (OwnChoice() is { } choice)
        {
            _choiceLabel.Text = $"当前我方选择 #{choice.Id}，选择 {choice.CountMin}～{choice.CountMax} 个。可仅核对范围，或评估已覆盖来源的直接后果；预览不是已经发生的局面。";
            _choices.ItemsSource = (_view.Choice?.Options ?? []).OrderBy(option => option.Position ?? int.MaxValue)
                .Select(option => new EntityOption(option.EntityId, option.Name + $" #{option.EntityId}" +
                    (option.IsAssembled ? " · " + option.EffectTextStatus + " · " + option.EffectTextDisplay : ""))).ToArray();
            foreach (var option in _choices.Items.OfType<EntityOption>().Where(option => selectedChoices.Contains(option.Id))) _choices.SelectedItems.Add(option);
        }
        else { _choiceLabel.Text = "当前没有已确认的我方选择；不能使用残留或对方候选。"; _choices.ItemsSource = Array.Empty<EntityOption>(); }
        RefreshEnabled();
    }

    private CurrentChoiceSnapshot? OwnChoice() => _view is { Choice: { } current } && _view.MatchId == _snapshot.MatchId &&
        _view.StateVersion == _snapshot.StateVersion && _snapshot.Choice is { } choice && current.Id == choice.Id ? choice : null;

    private void RefreshSources()
    {
        if (_view is null) return;
        var selectedSource = (_source.SelectedItem as EntityOption)?.Id;
        var kind = (_kind.SelectedItem as KindOption)?.Kind;
        if (kind == "end_turn")
        {
            var players = _snapshot.Phase == GamePhase.Main && _snapshot.Quality.HasLocalPerspective && _snapshot.LocalPlayerId.HasValue && !_snapshot.IsSpectating ? _snapshot.Entities.Values.Where(entity =>
                entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == _snapshot.LocalPlayerId).Take(2).ToArray() : [];
            _source.ItemsSource = players is [var player] ? new[] { new EntityOption(player.Id, "结束我方回合") } : Array.Empty<EntityOption>();
            _source.SelectedIndex = _source.Items.Count > 0 ? 0 : -1;
            _target.SelectedIndex = 0;
            RefreshEnabled();
            return;
        }
        IEnumerable<BattleCardView> sources = kind switch
        {
            "play" => _view.Local.Hand,
            "attack" => _view.Local.Board.Where(card => card.Type == "MINION").Concat(new[] { _view.Local.Hero }.OfType<BattleCardView>()),
            "hero_power" => new[] { _view.Local.HeroPower }.OfType<BattleCardView>(), _ => []
        };
        _source.ItemsSource = sources.Select(card => new EntityOption(card.EntityId, CardLabel(card))).ToArray();
        _source.SelectedItem = _source.Items.OfType<EntityOption>().FirstOrDefault(option => option.Id == selectedSource) ??
            _source.Items.OfType<EntityOption>().FirstOrDefault();
        RefreshEnabled();
    }

    private void AddAction()
    {
        if (!IsBindingValid || _source.SelectedItem is not EntityOption source || _kind.SelectedItem is not KindOption kind) return;
        if (_line.Count >= 16) { _result.Text = "最多验证 16 个候选动作；较长序列请分段核对。"; return; }
        var target = kind.Kind == "end_turn" ? null : _target.SelectedItem as EntityOption;
        _line.Add(new(kind.Kind, source.Id, target is { Id: > 0 } ? target.Id : null,
            $"{kind.Text}：{source.Text}" + (target is { Id: > 0 } ? " → " + target.Text : "")));
        RefreshLine();
    }

    private void RefreshLine()
    {
        _actions.ItemsSource = _line.Select((action, index) => action with { Text = $"{index + 1}. {action.Text}" }).ToArray();
        RefreshEnabled();
    }

    private async Task RunToolAsync(string tool, JsonObject args)
    {
        if (!IsBindingValid || _coach is null || _calculation is not null) return;
        var revision = _revision; var coach = _coach;
        var cancellation = new CancellationTokenSource(); _calculation = cancellation;
        _result.Text = "正在本地计算…"; RefreshEnabled();
        try
        {
            var result = await coach.ExecuteAsync(tool, args, cancellation.Token);
            if (Current(revision) && ReferenceEquals(_calculation, cancellation))
                _result.Text = (_historical ? "【历史局面结果，不用于当前游戏操作】\n" : "") +
                    LocalToolResultFormatter.Format(tool, result, id => EntityName(id));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (Current(revision)) _result.Text = "计算已取消。"; }
        catch (Exception error)
        { if (Current(revision)) _result.Text = "本地计算未完成：" + error.Message; }
        finally
        {
            if (ReferenceEquals(_calculation, cancellation)) { _calculation = null; RefreshEnabled(); }
            cancellation.Dispose();
        }
    }

    private void CancelCalculation()
    {
        if (_calculation is not { } calculation) return;
        calculation.Cancel(); _cancel.IsEnabled = false; _result.Text = "正在取消计算…";
    }

    private bool Current(long revision) => !_closed && IsBindingValid && revision == _revision;
    private string CardName(string? cardId) => cardId is not null && _cards.TryGetValue(cardId, out var card) ? card.Name :
        cardId is null ? "未知卡牌" : "未识别卡牌（" + cardId + "）";
    private static string CardLabel(BattleCardView card) => card.Name + $" #{card.EntityId}" +
        (card.Zone == "HAND" || card.Type == "HERO_POWER" ? $"（费用 {card.Cost?.ToString(CultureInfo.InvariantCulture) ?? "未知"}）" :
            $"（攻 {card.Attack?.ToString(CultureInfo.InvariantCulture) ?? "未知"} / 血 {card.Health?.ToString(CultureInfo.InvariantCulture) ?? "未知"}）");
    private string EntityName(int id)
    {
        var cards = _view is null ? [] : _view.Local.Hand.Concat(_view.Local.Board).Concat(_view.Opponent.Board)
            .Concat(new[] { _view.Local.Hero, _view.Opponent.Hero, _view.Local.HeroPower, _view.Opponent.HeroPower }.OfType<BattleCardView>());
        return cards.FirstOrDefault(card => card.EntityId == id)?.Name ??
            (OwnInstalledTraps(_snapshot).FirstOrDefault(entity => entity.Id == id) is { } trap ? CardName(trap.CardId) : "实体 #" + id);
    }

    private void RefreshEnabled()
    {
        var ready = IsBindingValid && _calculation is null && _snapshot.Phase != GamePhase.Unknown;
        _odds.IsEnabled = ready && _card.Items.Count > 0;
        _evaluate.IsEnabled = ready && _line.Count > 0; _lethal.IsEnabled = ready;
        _previewDraw.IsEnabled = ready && _snapshot.Phase == GamePhase.Main && _source.SelectedItem is EntityOption selected &&
            _snapshot.Entities.TryGetValue(selected.Id, out var source) && source.Zone == "HAND" && source.CardId is "FIR_921" or "EDR_226";
        _previewGeneration.IsEnabled = ready && _snapshot.Phase == GamePhase.Main && _source.SelectedItem is EntityOption generation &&
            _snapshot.Entities.TryGetValue(generation.Id, out var generator) && generator.Zone == "HAND" && generator.CardId == "CORE_UNG_912";
        _previewWolfBlessing.IsEnabled = ready && _snapshot.Phase == GamePhase.Main && _source.SelectedItem is EntityOption wolf &&
            _snapshot.Entities.TryGetValue(wolf.Id, out var power) && power.Zone == "PLAY" && power.CardId == "EDR_850p";
        _previewMagmaSplit.IsEnabled = ready && _snapshot.ClientBuild == "255790" && _snapshot.Phase == GamePhase.Main &&
            _snapshot.Quality.HasGameStart && _snapshot.Quality.IsStable && _snapshot.Quality.IsComplete && _snapshot.Quality.OpenBlockCount == 0 &&
            (_kind.SelectedItem as KindOption)?.Kind == "attack" && ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is { } magmaLocal && magmaLocal is (1 or 2) &&
            _snapshot.ActivePlayerId == magmaLocal && _source.SelectedItem is EntityOption magma && _target.SelectedItem is EntityOption attacked &&
            _snapshot.Entities.TryGetValue(magma.Id, out var magmaSource) && magmaSource.CardId == "FIR_953" &&
            PreviewBoardMinion(magmaSource, magmaLocal) && !AssembledCardText.RequiresCustomText(magmaSource.Tags) &&
            _snapshot.Entities.TryGetValue(attacked.Id, out var magmaTarget) && magmaTarget.ControllerId is { } targetOwner &&
            targetOwner is (1 or 2) && targetOwner != magmaLocal && PreviewBoardMinion(magmaTarget, targetOwner);
        var trapReady = ready && _snapshot.ClientBuild == "255790" && _snapshot.Phase == GamePhase.Main && _snapshot.Quality.HasGameStart &&
            _snapshot.Quality.IsStable && _snapshot.Quality.IsComplete && _snapshot.Quality.OpenBlockCount == 0 &&
            ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is (1 or 2);
        _trapSource.IsEnabled = _trapAttacker.IsEnabled = trapReady;
        _previewExplosiveTrap.IsEnabled = trapReady && _trapSource.SelectedItem is EntityOption selectedTrap &&
            OwnInstalledTraps(_snapshot).Any(entity => entity.Id == selectedTrap.Id) &&
            _trapAttacker.SelectedItem is EntityOption selectedAttacker && _snapshot.Entities.TryGetValue(selectedAttacker.Id, out var incoming) &&
            ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is { } trapLocal && PreviewPublicCharacter(incoming, trapLocal);
        _previewPlayTriggers.IsEnabled = ready && _snapshot.Phase == GamePhase.Main && _snapshot.Quality.HasGameStart && _snapshot.Quality.IsStable &&
            _snapshot.Quality.IsComplete && _snapshot.Quality.OpenBlockCount == 0 && (_kind.SelectedItem as KindOption)?.Kind == "play" &&
            ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is (1 or 2) and var local && _snapshot.ActivePlayerId == local && _source.SelectedItem is EntityOption played &&
            _snapshot.Entities.TryGetValue(played.Id, out var playedSource) && playedSource.Zone == "HAND" &&
            playedSource.ControllerId == local && !string.IsNullOrWhiteSpace(playedSource.CardId);
        _inspectEffect.IsEnabled = ready && _source.SelectedItem is EntityOption selectedEffect &&
            SnapshotCardEffects.VisibleCards(_snapshot, includePublicGraveyard: true).Any(entity => entity.Id == selectedEffect.Id);
        _add.IsEnabled = ready && _source.Items.Count > 0; _remove.IsEnabled = ready && _line.Count > 0;
        _kind.IsEnabled = _source.IsEnabled = _target.IsEnabled = _actions.IsEnabled = ready;
        if ((_kind.SelectedItem as KindOption)?.Kind == "end_turn") _target.IsEnabled = false;
        _validateChoice.IsEnabled = ready && OwnChoice() is not null;
        _evaluateChoice.IsEnabled = _validateChoice.IsEnabled; _choices.IsEnabled = _validateChoice.IsEnabled;
        _cancel.IsEnabled = _calculation is not null;
    }

    private bool Number(TextBox box, int minimum, int maximum, string name, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum) return true;
        _result.Text = $"{name}应为 {minimum}～{maximum} 的整数。"; return false;
    }

    private static bool PreviewBoardMinion(EntitySnapshot entity, int owner) => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" &&
        entity.ControllerId == owner && (!entity.Tags.TryGetValue("49", out var zone) || zone is "PLAY" or "1") &&
        (!entity.Tags.TryGetValue("50", out var alias) || int.TryParse(alias, out var controller) && controller == owner) &&
        (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named is "MINION" or "4") &&
        (!entity.Tags.TryGetValue("202", out var numeric) || numeric is "MINION" or "4");

    private static IEnumerable<EntitySnapshot> OwnInstalledTraps(GameSnapshot snapshot) =>
        ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not { } local ? [] : snapshot.Entities.Values.Where(entity =>
            entity.Kind == EntityKind.Card && entity.Id > 0 && entity.ControllerId == local && entity.Zone == "SECRET" &&
            entity.CardId is "CORE_EX1_610" or "EX1_610" &&
            (!entity.Tags.TryGetValue("50", out var owner) || int.TryParse(owner, out var value) && value == local) &&
            (!entity.Tags.TryGetValue("49", out var zone) || zone is "SECRET" or "7") && PreviewType(entity, "SPELL", "5") &&
            !AssembledCardText.RequiresCustomText(entity.Tags));
    private static bool PreviewPublicCharacter(EntitySnapshot entity, int local) => entity.Kind == EntityKind.Card && entity.Id > 0 &&
        entity.Zone == "PLAY" && entity.ControllerId is (1 or 2) and var owner && owner != local &&
        (!entity.Tags.TryGetValue("50", out var alias) || int.TryParse(alias, out var value) && value == owner) &&
        (!entity.Tags.TryGetValue("49", out var zone) || zone is "PLAY" or "1") &&
        (PreviewType(entity, "MINION", "4") || PreviewType(entity, "HERO", "3"));
    private static bool PreviewType(EntitySnapshot entity, string type, string code) =>
        (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named == type || named == code) &&
        (!entity.Tags.TryGetValue("202", out var alias) || alias == type || alias == code);

    private FrameworkElement BuildContent()
    {
        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(_binding); body.Children.Add(_quality);
        body.Children.Add(new TextBlock { Text = "牌库与无放回抽牌概率", FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) });
        body.Children.Add(_deck); body.Children.Add(Row(_card, Label("抽取张数"), _draws, _odds));
        body.Children.Add(new TextBlock { Text = "候选顺序：编辑动作只影响本地计算", FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) });
        body.Children.Add(Row(_kind, _source, Label("目标"), _target, _add));
        body.Children.Add(_actions); body.Children.Add(Row(_remove, _evaluate, _inspectEffect));
        body.Children.Add(Row(_previewDraw, _previewGeneration, _previewWolfBlessing, _previewMagmaSplit, _previewPlayTriggers));
        body.Children.Add(Row(Label("搜索深度"), _depth, Label("节点上限"), _nodes, _lethal));
        body.Children.Add(new TextBlock { Text = "我方陷阱来袭预览：固定攻击我方英雄，不执行敌方动作", FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) });
        body.Children.Add(Row(Label("我方陷阱"), _trapSource, Label("敌方来袭者"), _trapAttacker, _previewExplosiveTrap));
        body.Children.Add(new TextBlock { Text = "我方当前选择", FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) });
        body.Children.Add(_choiceLabel); body.Children.Add(_choices); body.Children.Add(Row(_validateChoice, _evaluateChoice, _cancel));
        body.Children.Add(_result);
        Register("LocalBindingText", _binding); Register("LocalQualityText", _quality); Register("LocalDeckText", _deck);
        Register("LocalCardBox", _card); Register("LocalDrawsBox", _draws); Register("LocalOddsButton", _odds);
        Register("LocalKindBox", _kind); Register("LocalSourceBox", _source); Register("LocalTargetBox", _target);
        Register("LocalAddActionButton", _add); Register("LocalActionsList", _actions); Register("LocalEvaluateButton", _evaluate);
        Register("LocalLethalButton", _lethal); Register("LocalChoicesList", _choices); Register("LocalValidateChoiceButton", _validateChoice);
        Register("LocalEvaluateChoiceButton", _evaluateChoice);
        Register("LocalPreviewDrawButton", _previewDraw);
        Register("LocalPreviewGenerationButton", _previewGeneration);
        Register("LocalPreviewWolfBlessingButton", _previewWolfBlessing);
        Register("LocalPreviewMagmaSplitButton", _previewMagmaSplit);
        Register("LocalTrapSourceBox", _trapSource); Register("LocalTrapAttackerBox", _trapAttacker);
        Register("LocalPreviewExplosiveTrapButton", _previewExplosiveTrap);
        Register("LocalPreviewPlayTriggersButton", _previewPlayTriggers);
        Register("LocalInspectEffectButton", _inspectEffect);
        Register("LocalCancelButton", _cancel); Register("LocalResultBox", _result);
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Register(string name, FrameworkElement element) { element.Name = name; RegisterName(name, element); }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new(4) };
    private static WrapPanel Row(params FrameworkElement[] elements)
    {
        var panel = new WrapPanel { Margin = new(0, 4, 0, 4) };
        foreach (var element in elements) { element.Margin = new(3); panel.Children.Add(element); }
        return panel;
    }
    private sealed record CardOption(string CardId, string Text);
    private sealed record EntityOption(int Id, string Text);
    private sealed record KindOption(string Kind, string Text);
    private sealed record LocalAction(string Kind, int SourceId, int? TargetId, string Text);
}

/// <summary>Chinese explanations retain the difference between a proof, a conditional estimate and missing coverage.</summary>
public static class LocalToolResultFormatter
{
    public static string Format(string tool, JsonNode result, Func<int, string>? entityName = null)
    {
        entityName ??= id => "实体 #" + id;
        if (result["error"] is { } error) return "无法验证：" + error;
        var lines = new List<string>();
        if (tool == "calculate_draw_odds")
        {
            if (result["is_exact"]?.GetValue<bool>() == true && result["probability"]?.GetValue<double>() is { } probability)
                lines.Add($"至少抽中一张：{probability.ToString("P2", CultureInfo.GetCultureInfo("zh-CN"))}（牌库 {result["population"]} 张，其中目标 {result["target_copies"]} 张，抽取 {result["draws"]} 张）");
            else lines.Add("抽中概率：未知（没有足够证据给出精确数字）。");
            lines.Add(result["reason"]?.ToString() ?? "");
            return string.Join("\n", lines);
        }
        if (tool == "get_entity_effect")
        {
            lines.Add("所选实体效果 · " + (result["name"]?.ToString() ?? "未知卡牌"));
            if (result["effect"] is JsonObject effect)
            {
                lines.Add(effect["is_offer"]?.ToString() == "true" ? "当前为拼装候选半段，不是成品。" : "根据固定版本资料与当前已观察标签读取。动态参数和属性仍以日志为准。");
                lines.Add(effect["is_offer"]?.ToString() == "true" ? "所提供半段：" + (effect["offered_half"]?.ToString() == "Trigger" ? "触发" : "效果") :
                    effect["is_resolved"]?.ToString() == "true" ? "可读文字：" : "当前文字尚未确认：");
                var text = effect["text"]?.ToString() ?? effect["offered_half_text"]?.ToString();
                lines.Add(text is null ? "当前效果未知，不能回退使用原卡文字。" : text.Replace("<b>", "", StringComparison.Ordinal).Replace("</b>", "", StringComparison.Ordinal).Replace("<i>", "", StringComparison.Ordinal).Replace("</i>", "", StringComparison.Ordinal));
                if (effect["unknowns"] is JsonArray unknowns) foreach (var unknown in unknowns) lines.Add("· " + unknown);
            }
            lines.Add("文字查询不证明可执行动作或结算结果，也不改变当前费用和属性。");
            return string.Join("\n", lines);
        }
        if (tool == "evaluate_draw")
        {
            AppendDraw(lines, result); return string.Join("\n", lines);
        }
        if (tool == "evaluate_generation")
        {
            AppendGeneration(lines, result); return string.Join("\n", lines);
        }
        if (tool == "evaluate_wolf_blessing")
        {
            AppendWolfBlessing(lines, result, entityName); return string.Join("\n", lines);
        }
        if (tool == "evaluate_explosive_trap")
        {
            lines.Add("我方爆炸陷阱来袭 · 当前条件预览（未执行对手动作）");
            lines.Add("当前触发条件：" + (result["TriggerActive"]?.ToString() switch
            {
                "true" => "在列出条件下满足，尚未证明攻击实际发生",
                "false" => "当前不触发；不预测未来回合",
                _ => "未知，不能默认触发或不触发"
            }));
            lines.Add("陷阱来源：" + TrapEntity("SecretEntityId") + "；假设来袭者：" + TrapEntity("AttackerEntityId") +
                "；固定攻击对象：" + TrapEntity("DefendingHeroEntityId"));
            lines.Add($"当前法术伤害加成：{result["SpellDamage"]?.ToString() ?? "未知"}；每个敌方角色的防御处理前伤害：{result["DamagePerEnemy"]?.ToString() ?? "未知"}；陷阱消耗：{TrapYesNo(result["TrapConsumed"])}。");
            if (result["PriorResolutionPending"]?.ToString() == "true") lines.Add("前置奥秘或触发仍待结算，不能把本预览当作其后的实际状态。");
            if (result["ResolvesDirectDamage"]?.ToString() != "true") lines.Add("直接伤害尚未完整覆盖；缺失数值保持未知，空列表不表示没有伤害。");
            if (result["Targets"] is JsonArray trapTargets)
                foreach (var target in trapTargets.OfType<JsonObject>())
                {
                    var targetName = target["EntityId"] is JsonValue value && value.TryGetValue<int>(out var id) ? entityName(id) : "未知公开角色";
                    lines.Add($"· {targetName}：直接伤害 {target["Damage"]?.ToString() ?? "未知"}；生命 {target["HealthBefore"]?.ToString() ?? "未知"} → {target["HealthAfter"]?.ToString() ?? "未知"}；" +
                        $"护甲 {target["ArmorBefore"]?.ToString() ?? "未知"} → {target["ArmorAfter"]?.ToString() ?? "未知"}；圣盾 {TrapYesNo(target["DivineShieldBefore"])} → {TrapYesNo(target["DivineShieldAfter"])}；直接伤害后存活 {TrapYesNo(target["IsAliveAfter"])}。");
                }
            lines.Add("来袭者直接伤害后存活：" + TrapYesNo(result["AttackerSurvivesDirectDamage"]) + "。");
            lines.Add("攻击是否被本次直接伤害停止：" + (result["AttackStoppedByDirectDamage"]?.ToString() switch
            {
                "true" => "在所列条件下会停止；不表示其它触发已结算",
                "false" => "未被本次直接伤害停止；完整攻击是否继续仍未知",
                _ => "未知，不能补成攻击继续或停止"
            }));
            foreach (var key in new[] { "Unknowns", "Assumptions" })
                if (result[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
            lines.Add("以上仅为当前来袭假设下的直接触发后果；不模拟未来回合、亡语或完整攻击，不修改原局面，不提供后续己方动作或斩杀证明。");
            return string.Join("\n", lines);
            string TrapEntity(string key) => result[key] is JsonValue value && value.TryGetValue<int>(out var id) ? entityName(id) : "未知";
            static string TrapYesNo(JsonNode? value) => value?.ToString() switch { "true" => "是", "false" => "否", _ => "未知" };
        }
        if (tool == "evaluate_magma_split")
        {
            lines.Add("熔岩猎犬攻击与后置随机分配 · 条件预览");
            lines.Add("攻击与相关机制合法性：" + (result["AttackIsLegal"]?.ToString() switch
            { "true" => "在已覆盖范围内成立", "false" => "不成立", _ => "未知或仍有条件，不能确认完整动作合法" }));
            lines.Add("已预览基础攻击动作数：" + (result["AppliedActions"]?.ToString() ?? "未知") + "（未修改原局面）");
            if (result["Split"] is JsonObject split) AppendMagmaSplit(lines, split, entityName);
            else lines.Add("攻击尚未形成可用的后置分配证据，不能生成随机结果。");
            foreach (var key in new[] { "Errors", "Assumptions" })
                if (result[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
            lines.Add("攻击对象不是随机命中；不能选择分支、拼接有利区间或据此证明斩杀，后续动作须等待新局面。");
            return string.Join("\n", lines);
        }
        if (tool == "evaluate_play_triggers")
        {
            lines.Add("原生使用入口与使用后触发预览（各段条件独立；不证明出牌合法或完整结算）");
            if (result["Assumptions"] is JsonArray assumptions) foreach (var assumption in assumptions) lines.Add("· " + assumption);
            if (result["Discovery"] is JsonObject discovery) AppendDiscovery(lines, discovery);
            if (result["QuestProgress"] is JsonArray quests)
            {
                if (quests.Count == 0) lines.Add("当前没有已确认的我方围攻城门任务。");
                foreach (var quest in quests.OfType<JsonObject>()) AppendQuestProgress(lines, quest);
            }
            if (result["Raptor"] is JsonObject raptor) AppendRaptor(lines, raptor, entityName);
            lines.Add("本次预览不改变当前局面；候选顺序仍须验证，奖励与随机结果不能自行指定。");
            return string.Join("\n", lines);
        }
        if (tool == "evaluate_choice")
        {
            lines.Add("选择直接后果预览（尚未发生，不作为后续出牌或斩杀证明）");
            lines.Add("候选是否合法：" + (result["IsLegal"]?.ToString() switch { "true" => "是", "false" => "否", _ => "未知" }));
            lines.Add("直接后果验证：" + (result["Verification"]?.ToString() switch
            { "Verified" => "已核算列出的直接后果", "Conditional" => "有条件 / 仍有未知", "Invalid" => "无效选择", _ => "未覆盖 / 信息不足" }));
            lines.Add("选择来源：" + (result["SourceName"]?.ToString() ?? "尚未确认"));
            lines.Add($"手牌数量（仅本次直接变化）：{result["HandCountBefore"]?.ToString() ?? "未知"} → {result["HandCountAfter"]?.ToString() ?? "未知"}；上限 {result["HandLimit"]?.ToString() ?? "未知"}");
            if (result["CardOutcomes"] is JsonArray outcomes)
                foreach (var outcome in outcomes.OfType<JsonObject>())
                {
                    var destination = outcome["AddedToHand"]?.ToString() switch
                    { "true" => "直接加入手牌", "false" => "不能加入手牌（手牌已满）", _ => "是否入手尚未确认" };
                    var origin = outcome["Origin"]?.ToString() switch
                    { "Deck" => "牌库", "Sideboard" => "边栏", "DiscoverPool" => "发现池", "MapRemainingOptions" => "地图原剩余选项", _ => "未知来源" };
                    lines.Add($"· {outcome["Name"]?.ToString() ?? "未知卡牌"}：来自{origin}；{destination}。");
                    if (outcome["GrantedCostReduction"] is { } reduction) lines.Add("  授予减费：" + reduction + "；不从候选费用重复扣减。");
                    lines.Add("  入手后当前费用：" + (outcome["ResultingCost"]?.ToString() ?? "未知，等待客户端结算"));
                }
            if (result["DirectDeckChange"] is JsonObject change)
                lines.Add($"牌库直接移出 {change["RemovedCopies"]} 张：{change["TotalCardsBefore"]?.ToString() ?? "未知"} → {change["TotalCardsAfter"]?.ToString() ?? "未知"}；不包含其他触发或后续发现。");
            if (result["SideboardEvidence"] is JsonObject sideboard) AppendSideboardEvidence(lines, sideboard);
            if (result["Map"] is JsonObject map) AppendMap(lines, map, entityName);
            if (result["Crafting"] is JsonObject crafting)
            {
                lines.Add("制造方式：" + (crafting["Mechanism"]?.ToString() switch
                { "BuildABeast" => "僵尸兽合成", "Assemble" => "拼装（保留主随从属性，替换文字）", _ => "未知" }));
                lines.Add("当前阶段：" + (crafting["Stage"]?.ToString() switch
                { "ComponentOne" => "选择第一组件，尚未获得成品", "ComponentTwo" => "选择第二组件，预览合成的直接后果", "HalfSelection" => "选择替换文字的半段；主随从尚未绑定", _ => "尚未确认，不能提前当作成品" }));
                if (crafting["OfferedHalfText"] is { } offeredText)
                    lines.Add("本次选择" + (crafting["OfferedHalf"]?.ToString() == "Trigger" ? "触发半段" : "效果半段") + "：" + offeredText.ToString().Replace("<b>", "", StringComparison.Ordinal).Replace("</b>", "", StringComparison.Ordinal));
                if (crafting["Components"] is JsonArray components)
                    foreach (var component in components.OfType<JsonObject>())
                        lines.Add($"· {(component["FromHistory"]?.ToString() == "true" ? "前次实际已选" : "本次候选")}：{component["Name"]?.ToString() ?? "未知组件"}；观察费用 {component["ObservedCost"]?.ToString() ?? "未知"} / 攻击 {component["ObservedAttack"]?.ToString() ?? "未知"} / 生命 {component["ObservedHealth"]?.ToString() ?? "未知"}。");
                if (crafting["GrantedCostReduction"] is { } reduction) lines.Add("合成时授予减费：" + reduction + "。数值预览不作为新手牌的当前费用。");
                if (crafting["Stage"]?.ToString() == "ComponentTwo")
                    lines.Add($"合成数值预览：费用 {crafting["ResultingCost"]?.ToString() ?? "未知"} / 攻击 {crafting["ResultingAttack"]?.ToString() ?? "未知"} / 生命 {crafting["ResultingHealth"]?.ToString() ?? "未知"}；成品实体与其他触发等待客户端结算。");
                lines.Add("本次选择新增手牌：" + (crafting["AddedToHand"]?.ToString() switch
                { "true" => "可直接加入一张成品", "false" => "本次没有新增", _ => "未知" }));
            }
            if (result["Rewind"] is JsonObject rewind)
            {
                lines.Add("迅猛龙先锋选择阶段：" + (rewind["Stage"]?.ToString() switch
                { "DiscoverBeast" => "野兽候选", "DarkGift" => "黑暗之赐", "KeepTimeline" => "维持当前时间线", "RewindTimeline" => "请求回溯重做", _ => "未知" }));
                if (rewind["DirectCardName"] is { } beast) lines.Add("已选野兽：" + beast + "；不据此假定新增手牌。");
                if (rewind["GrantedDarkGiftName"] is { } gift) lines.Add("所选黑暗之赐：" + gift);
                if (rewind["AttackDelta"] is not null || rewind["HealthDelta"] is not null)
                    lines.Add($"授予属性增量：攻击 {rewind["AttackDelta"]?.ToString() ?? "无额外增量"} / 生命 {rewind["HealthDelta"]?.ToString() ?? "无额外增量"}；最终属性等待结算。");
                if (rewind["GrantedEffectText"] is { } effect) lines.Add("授予效果：" + effect.ToString().Replace("<b>", "", StringComparison.Ordinal).Replace("</b>", "", StringComparison.Ordinal));
                if (rewind["DestinationAfterGift"]?.ToString() == "DeckTop") lines.Add("所选赐福将目标置于我方牌库顶，不能按直接加入手牌计算。");
                if (rewind["GrantedCostReduction"] is { } reduction) lines.Add("已确认授予减费：" + reduction + "；入手后当前费用等待结算。");
                lines.Add("延系条件：" + (rewind["KindredActive"]?.ToString() switch { "true" => "已由上个己方回合实际出牌确认", "false" => "未满足", _ => "未知，历史类型或回合证据不足" }));
                if (rewind["KindredCostReduction"] is { } kindred) lines.Add("本来源延系授予减费：" + kindred + "；与赐福自身减费分别记录，不从候选费用再次扣减。");
                if (rewind["RequestsRewind"]?.ToString() == "true")
                    lines.Add("仅确认回溯请求；不自行恢复法力、手牌、旧局面或随机结果。");
                if (rewind["KeepsTimeline"]?.ToString() == "true") lines.Add("维持当前已观察结果；不重复生成发现牌。");
            }
            if (result["Malorne"] is JsonObject malorne)
            {
                lines.Add("所选荒野之神：" + (malorne["Name"]?.ToString() ?? "未知"));
                lines.Add("本局累计灌注：" + (malorne["Imbues"]?["Count"]?.ToString() ?? "未知"));
                lines.Add("累计四次条件：" + (malorne["ImbueConditionMet"]?.ToString() switch { "true" => "满足", "false" => "未满足", _ => "未知" }));
                if (malorne["GrantedSetCost"] is { } setCost) lines.Add("授予设费为 " + setCost + "；最终入手费用等待结算，不能当作减少一点费用。");
                lines.Add("本次是否入手：" + (malorne["AddedToHand"]?.ToString() switch { "true" => "可加入一张", "false" => "满手不能加入", _ => "未知" }));
            }
            foreach (var (label, key) in new[] { ("失败原因", "Errors"), ("成立条件", "Assumptions"), ("未知与未结算", "Unknowns"), ("已覆盖规则", "RuleCoverage") })
                if (result[key] is JsonArray { Count: > 0 } values)
                    lines.Add(label + "：\n" + string.Join("\n", values.Select(value => "· " + value)));
            lines.Add("不能据此继续执行后续动作；请等待选择完成后的新局面。");
            return string.Join("\n", lines);
        }
        var evaluation = result;
        if (tool == "find_lethal")
        {
            var found = result["VerifiedLethalFound"]?.GetValue<bool>() == true && !PendingChoiceBoundary(result["Evaluation"]);
            lines.Add(found ? "找到已验证的斩杀路线。" : "在本次有限搜索中未找到已验证的斩杀；不代表没有斩杀。");
            lines.Add($"已搜索节点：{result["ExploredNodes"]}；穷尽当前覆盖分支：{(result["IsExhaustive"]?.GetValue<bool>() == true ? "是" : "否")}");
            if (result["Coverage"] is { } coverage) lines.Add("搜索范围：" + coverage);
            if (result["Actions"] is JsonArray actions)
                foreach (var action in actions.OfType<JsonObject>())
                    lines.Add($"· {ActionName(action["Kind"]?.ToString())}：{entityName(action["SourceEntityId"]!.GetValue<int>())}" +
                        (action["TargetEntityId"] is { } target ? " → " + entityName(target.GetValue<int>()) : ""));
            if (result["Evaluation"] is not JsonObject nested) return string.Join("\n", lines);
            evaluation = nested;
        }
        var verification = evaluation["Verification"]?.ToString();
        if (verification == "Verified" && PendingChoiceBoundary(evaluation)) verification = "Conditional";
        lines.Add("验证级别：" + (verification switch
        {
            "Verified" => "已验证（仅限已实现规则范围）", "Conditional" => "条件成立（不能作确定结果）",
            "Invalid" => "无效", "Unsupported" => "未覆盖 / 信息不足", _ => "未知"
        }));
        lines.Add("是否合法：" + (evaluation["IsLegal"]?.ToString() switch { "true" => "是", "false" => "否", _ => "未知" }));
        if (tool == "validate_choice") lines.Add("仅核对候选范围与数量，不模拟选择后效果，也不证明斩杀。");
        else lines.Add($"已应用动作：{evaluation["AppliedActions"]}；已验证斩杀：{(verification == "Verified" && evaluation["IsLethal"]?.GetValue<bool>() == true ? "是" : "否 / 未能证明")}");
        Add("失败原因", "Errors"); Add("成立条件", "Assumptions"); Add("未覆盖机制", "UnsupportedMechanics"); Add("已覆盖规则", "RuleCoverage");
        if (evaluation["Result"] is JsonObject state)
        {
            if (state["ImbueCountBefore"] is not null || state["ImbueCountAfter"] is not null)
                lines.Add($"累计灌注预览：{state["ImbueCountBefore"]?.ToString() ?? "未知"} → {state["ImbueCountAfter"]?.ToString() ?? "未知或有后置灌注待确认"}");
            if (state["DrawBoundaryReached"]?.ToString() == "true") lines.Add("实体和资源投影停在抽牌前；抽牌直接后果列于下方，后续动作须等待新局面。");
            if (state["DrawPreviews"] is JsonArray drawPreviews)
                foreach (var draw in drawPreviews.OfType<JsonObject>()) AppendDraw(lines, draw);
            if (state["GenerationBoundaryReached"]?.ToString() == "true") lines.Add("实体和资源投影停在随机生成前；后续动作须等待新局面。");
            if (state["GenerationPreviews"] is JsonArray generationPreviews)
                foreach (var generation in generationPreviews.OfType<JsonObject>()) AppendGeneration(lines, generation);
            if (state["WolfBlessingBoundaryReached"]?.ToString() == "true") lines.Add("巨狼祝福的实际命中待确认；未修改任一候选，后续动作须等待新局面。");
            if (state["WolfBlessingPreviews"] is JsonArray wolfPreviews)
                foreach (var wolf in wolfPreviews.OfType<JsonObject>()) AppendWolfBlessing(lines, wolf, entityName);
            if (state["MagmaSplitPreviews"] is JsonArray magmaPreviews)
                foreach (var magma in magmaPreviews.OfType<JsonObject>()) AppendMagmaSplit(lines, magma, entityName);
            if (state["MagmaSplitBoundaryReached"]?.ToString() == "true")
                lines.Add("熔岩猎犬后置随机分配尚未观察；当前投影不是任一分支的实际结果，后续动作须等待新局面，也不能证明斩杀。");
            if (state["DiscoveryPreviews"] is JsonArray discoveryPreviews)
                foreach (var discovery in discoveryPreviews.OfType<JsonObject>()) AppendDiscovery(lines, discovery);
            if (state["DiscoveryBoundaryReached"]?.ToString() == "true")
                lines.Add("原生发现、拼装或回溯尚待真实选择或新观测；投影停在该边界，不能宣称完整结算、继续后续动作或证明斩杀。");
            if (state["MapPlayPreviews"] is JsonArray mapPreviews)
                foreach (var map in mapPreviews.OfType<JsonObject>())
                {
                    lines.Add($"奇异的地图 #{map["MapSourceEntityId"]}：原首选{entityName(map["PlayedEntityId"]!.GetValue<int>())}的模拟使用已记录为条件请求。");
                    lines.Add("当前身份与原首选：" + (map["IdentityMatch"]?.ToString() switch
                    { "true" => "相符", "false" => "已改变，脚本关联仍待确认", _ => "未知" }));
                    if (map["PriorResolutionPending"]?.ToString() == "true") lines.Add("前序结算尚待确认，地图请求也需等待。");
                    lines.Add("这不是实际使用记录或追加候选；不写入真实历史，不创建第二张手牌。");
                }
            if (state["MapFollowupBoundaryReached"]?.ToString() == "true")
                lines.Add("地图追加选择尚待真实提供；投影停在该边界，不能继续后续动作或证明斩杀。");
            if (state["QuestProgressPreviews"] is JsonArray questPreviews)
                foreach (var quest in questPreviews.OfType<JsonObject>()) AppendQuestProgress(lines, quest);
            if (state["QuestRewardBoundaryReached"]?.ToString() == "true")
                lines.Add("任务奖励尚未结算；等待真实奖励选择的新局面，不能生成候选或成品，也不能继续后续动作。");
            if (state["RaptorPreviews"] is JsonArray raptorPreviews)
                foreach (var raptor in raptorPreviews.OfType<JsonObject>()) AppendRaptor(lines, raptor, entityName);
            if (state["RaptorBoundaryReached"]?.ToString() == "true")
                lines.Add("梦缚迅猛龙的随机额外效果尚未观察；后续动作须等待新局面。");
            lines.Add("模拟后可用法力：" + state["AvailableMana"]);
            if (state["TurnEnded"]?.GetValue<bool>() == true)
                lines.Add("本地模拟已结束我方回合；仅计算本次回合末边界，不推断对手起手或后续回合。");
            if (state["UnresolvedEndTurnFields"] is JsonArray { Count: > 0 })
                lines.Add("普通攻击次数或就绪状态未推导，仍保留最近观察值，不能用于后续动作验证。");
            if (state["ForcedAttacks"] is JsonArray forcedAttacks)
                foreach (var attack in forcedAttacks.OfType<JsonObject>())
                    lines.Add($"· 强制战斗：{entityName(attack["SourceEntityId"]!.GetValue<int>())} → {entityName(attack["TargetEntityId"]!.GetValue<int>())}；造成 {attack["DamageToTarget"]?.ToString() ?? "未知"} / 受到 {attack["DamageToSource"]?.ToString() ?? "未知"} 点伤害。");
            if (state["PendingForcedAttacks"] is JsonArray pendingAttacks)
                foreach (var pending in pendingAttacks.OfType<JsonObject>())
                {
                    var candidates = (pending["CandidateSourceEntityIds"] as JsonArray ?? []).Select(id => entityName(id!.GetValue<int>()));
                    lines.Add("尚未展开的强制战斗候选：" + string.Join("、", candidates) + "；" + pending["Reason"]);
                }
            if (state["OpponentDefeatedByPayment"]?.GetValue<bool>() == true)
                lines.Add("对手因生命支付达到零生命，已停止计算该牌后续效果。");
            if (state["NextCardCostsOpponentHealth"]?.GetValue<bool>() == true)
                lines.Add("下一张牌将以对手生命支付（当前费用必须不超过10，仍需满足出牌条件）。");
            else if (state.ContainsKey("NextCardCostsOpponentHealth") && state["NextCardCostsOpponentHealth"] is null)
                lines.Add("下一张牌的生命支付效果：未确认。");
            if (state["OpponentHealthPayments"] is JsonArray payments)
                foreach (var payment in payments.OfType<JsonObject>())
                    lines.Add($"· {entityName(payment["CardEntityId"]!.GetValue<int>())}：由{entityName(payment["PayingHeroEntityId"]!.GetValue<int>())}的生命支付 {payment["Cost"]}（生命 {payment["HealthBefore"]} → {payment["HealthAfter"]}，护甲不参与支付，支付不视为伤害）。");
            if (state["Entities"] is JsonArray entities)
                foreach (var entity in entities.OfType<JsonObject>().Where(e => e["Type"]?.ToString() is "HERO" or "MINION"))
                    lines.Add($"· {entityName(entity["EntityId"]!.GetValue<int>())}：攻击 {entity["Attack"]?.ToString() ?? "未知"} / 生命 {entity["RemainingHealth"]?.ToString() ?? "未知"} / 护甲 {entity["Armor"]}；{(entity["IsAlive"]?.GetValue<bool>() == true ? "存活" : "已离场")}");
        }
        return string.Join("\n", lines);

        void Add(string label, string key)
        {
            if (evaluation[key] is JsonArray array && array.Count > 0) lines.Add(label + "：\n" + string.Join("\n", array.Select(item => "· " + item)));
        }
    }

    private static void AppendDraw(List<string> lines, JsonNode draw)
    {
        static string Range(JsonNode? range)
        {
            if (range is null) return "未知";
            var minimum = range["Minimum"]?.ToString() ?? "未知"; var maximum = range["Maximum"]?.ToString();
            return maximum == minimum ? minimum : maximum is null ? minimum + "～上界未知" : minimum + "～" + maximum;
        }
        lines.Add("抽牌直接后果预览（假设本体成功离手；不证明出牌合法）");
        lines.Add($"抽取次数 {Range(draw["RequestedDraws"])}；牌库移出 {Range(draw["CardsRemovedFromDeck"])}；入手 {Range(draw["CardsAddedToHand"])}；满手损失 {Range(draw["CardsBurned"])}。");
        lines.Add($"空库尝试 {Range(draw["EmptyDeckAttempts"])}；直接疲劳伤害 {Range(draw["FatigueDamage"])}（护甲吸收前，免疫另行计算）。");
        lines.Add($"直接后果手牌数 {Range(draw["HandCountAfter"])} / 牌库数 {Range(draw["DeckCountAfter"])} / 疲劳计数 {Range(draw["FatigueAfter"])}。");
        lines.Add($"仅本次疲劳后英雄生命 {Range(draw["HeroHealthAfter"])} / 护甲 {Range(draw["HeroArmorAfter"])}；其他触发未展开。");
        if (draw["MayStopAtHeroDeath"]?.ToString() == "true") lines.Add("存在直接疲劳致死分支，可能提前停止后续抽取。");
        foreach (var key in new[] { "Unknowns", "Assumptions" })
            if (draw[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
        lines.Add("数量区间互相关联，不能独立拼接上下界；不生成新手牌实体，后续动作须等待新局面。");
    }

    private static void AppendGeneration(List<string> lines, JsonNode generation)
    {
        static string Range(JsonNode? range)
        {
            if (range is null) return "未知";
            var min = range["Minimum"]?.ToString() ?? "未知"; var max = range["Maximum"]?.ToString();
            return min == max ? min : max is null ? min + "～上界未知" : min + "～" + max;
        }
        lines.Add("随机生成直接后果预览（假设本体成功离手；不证明出牌合法）");
        lines.Add($"生成数量 {Range(generation["RequestedCards"])}；入手 {Range(generation["CardsAddedToHand"])}；满手损失 {Range(generation["CardsLostToHandLimit"])}；直接后果手牌数 {Range(generation["HandCountAfter"])}。");
        foreach (var key in new[] { "Unknowns", "Assumptions" })
            if (generation[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
        lines.Add("不从牌库移出卡牌；随机身份、费用和新实体未知，不生成可执行的后续动作。");
    }

    private static void AppendWolfBlessing(List<string> lines, JsonNode wolf, Func<int, string> entityName)
    {
        lines.Add("巨狼祝福随机分支预览（不证明技能使用合法）");
        lines.Add("当前增益/减费参数：" + (wolf["Level"]?.ToString() ?? "未知") + "；完整候选集合：" + (wolf["IsCandidateSetComplete"]?.ToString() == "true" ? "已确认" : "尚未确认"));
        if (wolf["Branches"] is JsonArray branches)
            foreach (var branch in branches.OfType<JsonObject>())
                lines.Add($"· 若命中{entityName(branch["TargetEntityId"]!.GetValue<int>())}：攻击 {branch["CurrentAttack"]?.ToString() ?? "未知"} → {branch["ResultingAttack"]?.ToString() ?? "未知"}；费用 {branch["CurrentCost"]?.ToString() ?? "未知"} → {branch["ResultingCost"]?.ToString() ?? "未知"}。");
        if (wolf["Unknowns"] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
        lines.Add("各分支互斥，不能指定实际命中或合并结果；当前手牌不变，后续动作须等待新局面。");
    }

    private static void AppendSideboardEvidence(List<string> lines, JsonNode evidence)
    {
        lines.Add("下水道之王边栏选择证据（已观察记录，不是当前完整池）");
        if (evidence["ResolvesEvidence"]?.ToString() != "true") lines.Add("当前来源或证据绑定尚未确认，不能据此补出候选或剩余牌。");
        else
        {
            var complete = evidence["IsHistoryComplete"]?.ToString() == "true";
            lines.Add(complete ? "已选历史：所列来源的实际选择记录完整。" : "已选历史：有缺口，所列次数仅为已观察下限，缺失明细不当作零次。");
            if (evidence["IsCurrentSourceConfirmed"]?.ToString() != "true") lines.Add("当前来源原生身份未确认；下列只展示过去冻结的选择事实。");
            if (evidence["Sources"] is JsonArray sources)
                foreach (var source in sources.OfType<JsonObject>())
                {
                    lines.Add("历史来源实体 #" + source["SourceEntityId"] + "（提供时为下水道之王）：");
                    if (source["Selections"] is JsonArray selections)
                        foreach (var selection in selections.OfType<JsonObject>())
                            lines.Add($"· 实际已选 {selection["CardId"]}；选择 #{selection["ChoiceId"]}，候选 #{selection["SelectedEntityId"]}，事件 {selection["ChosenAtEventSequence"]}。");
                }
            if (evidence["SelectedCounts"] is JsonArray counts)
                foreach (var count in counts.OfType<JsonObject>())
                    lines.Add($"已选次数：{count["CardId"]} {(count["IsLowerBound"]?.ToString() == "true" ? "至少 " : "")}{count["ObservedSelections"]} 次（实际选择，不等于成功入手）。");
            if (evidence["CurrentOffer"] is JsonObject offer)
                lines.Add("当前实际提供候选：" + (offer["OptionCount"]?.ToString() ?? "未知") + " 项；" +
                    (offer["HasCompleteCardIdentities"]?.ToString() == "true" ? "身份已确认" : "部分身份未知") + "，仅指本次提供数量。");
            else lines.Add("当前没有绑定到该来源的已确认候选，不从历史或起始牌码补出。");
        }
        if (evidence["InitialReference"] is JsonObject reference)
            lines.Add("起始牌码参考数量：" + (reference["IsValid"]?.ToString() == "true" ? reference["TotalCards"]?.ToString() ?? "未知" : "未知") + "；只作初始参考，不代表当前余量。");
        if (evidence["Unknowns"] is JsonArray unknowns) foreach (var unknown in unknowns) lines.Add("· " + unknown);
        lines.Add("完整剩余边栏池未知；不合并复制或偷取来源为精确共享池，也不从初始三张扣出虚构余量。");
    }

    private static bool PendingChoiceBoundary(JsonNode? evaluation) =>
        evaluation?["Result"]?["DiscoveryBoundaryReached"]?.ToString() == "true" ||
        evaluation?["Result"]?["MapFollowupBoundaryReached"]?.ToString() == "true" ||
        evaluation?["Result"]?["MagmaSplitBoundaryReached"]?.ToString() == "true";

    private static void AppendMagmaSplit(List<string> lines, JsonNode split, Func<int, string> entityName)
    {
        lines.Add("熔岩猎犬逐包分配预览：触发条件" + (split["TriggerActive"]?.ToString() switch
            { "true" => "在列出条件下满足", "false" => "未满足或已抑制", _ => "未知" }) + "。");
        lines.Add($"请求伤害包数：{split["RequestedPackets"]?.ToString() ?? "未知"}；每包伤害（含已覆盖修正）：{split["DamagePerPacket"]?.ToString() ?? "未知"}。");
        if (split["PriorResolutionPending"]?.ToString() == "true") lines.Add("前序结算仍待确认，以下后置分配只作条件预览。");
        lines.Add(split["IsEnumerationComplete"]?.ToString() == "true"
            ? "本次已覆盖分配枚举完整；这仍不是实际随机命中或完整对局结算。"
            : "枚举未完成或已截断；已列分支不代表全部可能结果，不能据此排除未列出的结果。");
        if (split["ResolvesBranches"]?.ToString() != "true") lines.Add("当前证据不足以完整展开随机分支，缺失数值保持未知。");
        if (split["Targets"] is JsonArray ranges)
            foreach (var target in ranges.OfType<JsonObject>())
                lines.Add($"· {TargetName(target)}：分配伤害 {MagmaRange(target["Damage"])}；生命 {MagmaRange(target["RemainingHealth"])}；护甲 {MagmaRange(target["RemainingArmor"])}；存活 {YesNo(target["Survives"])}。");
        if (split["Branches"] is JsonArray branches)
        {
            if (branches.Count == 0 && split["TriggerActive"]?.ToString() != "false") lines.Add("未列出可确认分支，不表示没有伤害或没有其它可能。");
            foreach (var branch in branches.OfType<JsonObject>())
            {
                lines.Add($"候选分支 {branch["Id"]?.ToString() ?? "未知"}：预览分配 {branch["PacketsApplied"]?.ToString() ?? "未知"} 包，剩余 {branch["PacketsRemaining"]?.ToString() ?? "未知"} 包；" +
                    (branch["IsComplete"]?.ToString() == "true" ? "该分支已展开" : "该分支未完整展开") + "；" + MagmaStop(branch["StopReason"]?.ToString()) + "。");
                if (branch["Targets"] is JsonArray targets)
                    foreach (var target in targets.OfType<JsonObject>())
                        lines.Add($"  {TargetName(target)}：生命 {target["RemainingHealth"]?.ToString() ?? "未知"} / 护甲 {target["RemainingArmor"]?.ToString() ?? "未知"}；圣盾 {YesNo(target["DivineShield"])}；存活 {YesNo(target["IsAlive"])}。");
                lines.Add($"  我方英雄生命预览 {branch["SourceHeroHealthAfter"]?.ToString() ?? "未知"}；本分支吸血恢复 {branch["LifestealHealing"]?.ToString() ?? "未知"}。");
            }
        }
        foreach (var key in new[] { "Unknowns", "Assumptions" })
            if (split[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
        lines.Add("各目标区间互相关联，不能拼接上下界为一个有利局面；不指定随机顺序、命中或概率，不修改当前局面。");
        string TargetName(JsonNode target) => target["EntityId"] is JsonValue value && value.TryGetValue<int>(out var id) ? entityName(id) : "未知公开目标";
        static string YesNo(JsonNode? value) => value?.ToString() switch { "true" => "是", "false" => "否", _ => "未知" };
    }
    private static string MagmaRange(JsonNode? range)
    {
        if (range is null) return "未知";
        var minimum = range["Minimum"]?.ToString(); var maximum = range["Maximum"]?.ToString();
        return minimum is null ? "未知" : maximum == minimum ? minimum : maximum is null ? minimum + "～上界未知" : minimum + "～" + maximum;
    }
    private static string MagmaStop(string? reason) => reason switch
    {
        "AllPacketsApplied" => "请求伤害包已用尽",
        "NoNativeTrigger" => "本次没有已覆盖的原生后置触发",
        "EnemyHeroDied" or "HeroDied" or "HeroDeath" => "英雄死亡，停止后续包",
        "NoEligibleTargets" or "NoTargets" => "没有已确认可继续分配的目标",
        "EnumerationLimit" or "Truncated" or "StateLimit" or "PacketLimit" => "达到枚举限制，结果已截断",
        "DeathrattleOrReborn" => "出现亡语或复生，等待后续观测",
        "UncoveredTargetEffect" => "目标存在未覆盖的受伤或其它效果，等待观测",
        "ImmuneTargetPolicyUnknown" => "免疫角色的随机分配规则尚未确认",
        _ => "停止原因尚待确认"
    };

    private static void AppendMap(List<string> lines, JsonNode map, Func<int, string> entityName)
    {
        lines.Add("奇异的地图选择阶段：" + (map["Stage"]?.ToString() switch
        {
            "InitialChoice" => "首次发现，尚未证明使用首选",
            "FollowupChoice" => "已核对本回合实际使用首选，当前为原剩余选项",
            "AwaitingFollowupOffer" => "已确认首选实际使用，等待追加候选",
            "AwaitingSelectedCardPlay" => "首选已实际选择，尚待使用",
            "ConditionNotMet" => "本次已确认不满足追加条件",
            "FollowupAlreadyChosen" => "追加选择已被实际选择，不重复入手",
            "FollowupWindowEnded" => "原使用回合已结束",
            _ => "证据不足，不能确认追加选择"
        }));
        if (map["InitialSelectedEntityId"] is { } selected)
            lines.Add("原首选实体：" + entityName(selected.GetValue<int>()) + "；实际使用事件：" +
                (map["InitialSelectedPlayedAtEventSequence"]?.ToString() ?? "尚未确认"));
        if (map["RemainingOptions"] is JsonArray options)
            foreach (var option in options.OfType<JsonObject>())
                lines.Add("· 已观察的原剩余选项：" + (option["Name"]?.ToString() ?? "未知卡牌") + "；候选 #" + option["EntityId"]);
        lines.Add("只核算当前实际提供的选项；不按同名牌、卡牌身份或创建者猜测新实体关联，不生成后续候选或可执行手牌。");
    }

    private static void AppendDiscovery(List<string> lines, JsonNode discovery)
    {
        var kind = discovery["Kind"]?.ToString();
        lines.Add("原生使用入口 · " + (kind switch
        {
            "DeckDiscover" => "追踪术：从当前牌库发现",
            "OddAttackBeastDiscover" => "奇异的地图：发现奇数攻击野兽",
            "WildGodDiscover" => "护路者玛洛恩：发现传说荒野之神",
            "SideboardDiscover" => "下水道之王：从私藏野兽边栏发现",
            "AssembleFourCostMinion" => "寻回机：拼装四费随从",
            "RewindDarkGiftBeastDiscover" => "迅猛龙先锋：发现野兽、黑暗之赐与回溯",
            _ => "来源或原生效果尚未确认"
        }));
        if (discovery["IsConditionalPreview"]?.ToString() == "true") lines.Add("前序效果尚未确认，本段仅列条件预览。");
        lines.Add("原生入口证据：" + (discovery["ResolvesEffects"]?.ToString() == "true" ? "已核对该来源；完整结算仍待确认" : "不足，保持未知"));
        lines.Add("发现或拼装效果：" + (discovery["EffectActive"]?.ToString() switch
        { "true" => "以成功使用为条件生效", "false" => "本次已抑制", _ => "是否生效未知" }));
        if (kind == "DeckDiscover") lines.Add("当前牌库数：" + (discovery["DeckCountBeforeEffect"]?.ToString() ?? "未知"));
        if (kind == "SideboardDiscover") lines.Add("当前私藏野兽可选数：" + (discovery["SideboardCandidateCount"]?.ToString() ?? "未知"));
        if (kind == "RewindDarkGiftBeastDiscover")
            lines.Add("独立回溯选择：" + (discovery["RewindActive"]?.ToString() switch
            { "true" => "仍需等待维持或回溯选项；战吼被抑制不等于回溯已移除", "false" => "当前未生效", _ => "未知，不能假定已经用尽或移除" }));
        lines.Add("外部选择：" + (discovery["RequiresChoice"]?.ToString() switch
        { "true" => "需要等待真实选项", "false" => "本次原生效果无需选择；其他触发另行核验", _ => "未知，等待真实观测" }));
        if (discovery["Scope"] is { } scope) lines.Add("范围与条件：" + scope);
        foreach (var key in new[] { "Unknowns", "Assumptions" })
            if (discovery[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
        lines.Add("直接预览不证明出牌合法；未生成候选、手牌实体或最终费用，也不能证明斩杀。");
    }

    private static void AppendQuestProgress(List<string> lines, JsonNode quest)
    {
        if (quest["IsConditionalPreview"]?.ToString() == "true") lines.Add("前序效果尚待确认，本段只列条件预览。");
        lines.Add($"围攻城门 #{quest["QuestEntityId"]}：当前进度 {quest["CurrentProgress"]?.ToString() ?? "未知"} / {quest["Total"]?.ToString() ?? "未知"}。");
        lines.Add("所选手牌是否符合野兽或亡灵条件：" + (quest["PlayedCardQualifies"]?.ToString() switch { "true" => "符合", "false" => "不符合", _ => "未知" }) +
            $"；本次增量 {quest["Increment"]?.ToString() ?? "未知"}；直接后果进度 {quest["ResultingProgress"]?.ToString() ?? "未知"}。");
        lines.Add("进度可直接投影：" + (quest["CanProjectProgress"]?.ToString() == "true" ? "是（仅本次已覆盖变化）" : "尚未确认"));
        if (quest["RequiresRewardChoice"]?.ToString() == "true")
            lines.Add("达到或可能达到任务完成条件，等待真实奖励选择；不生成僵尸兽组件、成品或提前授予减费。");
        if (quest["Unknowns"] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
    }

    private static void AppendRaptor(List<string> lines, JsonNode raptor, Func<int, string> entityName)
    {
        static string Range(JsonNode? range)
        {
            if (range is null) return "未知";
            var minimum = range["Minimum"]?.ToString() ?? "未知";
            var maximum = range["Maximum"]?.ToString();
            return maximum == minimum ? minimum : maximum is null ? minimum + "～上界未知" : minimum + "～" + maximum;
        }
        lines.Add("梦缚迅猛龙额外效果触发次数：" + Range(raptor["TriggerCount"]) + "；次数核算：" +
            (raptor["ResolvesTriggerCount"]?.ToString() == "true" ? "已覆盖列出的直接触发" : "仍有未知"));
        if (raptor["Triggers"] is JsonArray triggers)
            foreach (var trigger in triggers.OfType<JsonObject>())
                lines.Add($"· 来源 {entityName(trigger["SourceEntityId"]!.GetValue<int>())} → {entityName(trigger["TargetEntityId"]!.GetValue<int>())}：{Range(trigger["Count"])} 次；{Eligibility(trigger["Eligibility"]?.ToString())}。");
        if (raptor["IsBonusPoolComplete"]?.ToString() != "true") lines.Add("随机词条候选池未确认，不能从空列表推断没有可用效果。");
        foreach (var key in new[] { "Unknowns", "Assumptions" })
            if (raptor[key] is JsonArray values) foreach (var value in values) lines.Add("· " + value);
        if (raptor["RequiresObservation"]?.ToString() == "true")
            lines.Add("实际随机额外效果等待新局面；不能选定词条、拼接分支或继续后续动作。");
        static string Eligibility(string? value) => value switch
        {
            "EligibleNativeAfterPlay" => "本次原生触发条件已满足",
            "InactiveOrNoLiveTarget" => "来源未生效或目标已离场",
            "AwaitingAfterPlayState" => "等待使用后局面",
            "UnconfirmedAfterPlayIdentity" => "使用后的身份或归属尚未确认",
            "InactiveNewSource" => "新来源未生效",
            "SelfRegistrationUnconfirmed" => "自身触发时点尚未确认",
            _ => "触发资格尚未确认"
        };
    }

    private static string ActionName(string? kind) => kind switch { "Attack" => "攻击", "PlayCard" => "使用手牌", "HeroPower" => "英雄技能", "EndTurn" => "结束回合", _ => "动作" };
}
