using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HsHelper.Core;

/// <summary>
/// Replays PowerTaskList records. The caller supplies the local player ID after confirming perspective.
/// This reconstructs observed state; it neither infers absent tags nor simulates card effects.
/// </summary>
public sealed class PowerLogStateEngine
{
    private sealed class Entity(int id, EntityKind kind = EntityKind.Card)
    {
        public int Id { get; } = id;
        public EntityKind Kind { get; set; } = kind;
        public string? CardId { get; set; }
        public bool Hidden { get; set; }
        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> DormantCache { get; } = new(StringComparer.Ordinal);
        public long Revision { get; set; }
        public long VisibleRevision { get; set; } = -1;
        public long HiddenRevision { get; set; } = -1;
        public EntitySnapshot? VisibleSnapshot { get; set; }
        public EntitySnapshot? HiddenSnapshot { get; set; }
    }

    private sealed class ChoiceBuilder(int id, int? playerId, string type, int? taskList, int minimum, int maximum)
    {
        public int Id { get; } = id;
        public int? PlayerId { get; set; } = playerId;
        public string Type { get; } = type;
        public int? TaskList { get; } = taskList;
        public int Minimum { get; } = minimum;
        public int Maximum { get; } = maximum;
        public int? SourceId { get; set; }
        public List<ChoiceOptionSnapshot> Options { get; } = [];
        public bool Ready { get; set; }
        public bool Chosen { get; set; }
    }

    private static readonly Regex GameEntityPattern = Pattern(@"^GameEntity\s+EntityID=(\d+)");
    private static readonly Regex PlayerPattern = Pattern(@"^Player\s+EntityID=(\d+)\s+PlayerID=(\d+)");
    private static readonly Regex FullEntityPattern = Pattern(@"^FULL_ENTITY\s*-\s*Creating\s+ID=(\d+)\s+CardID=(\S*)");
    private static readonly Regex UpdatingPattern = Pattern(@"^(FULL_ENTITY|SHOW_ENTITY|CHANGE_ENTITY)\s*-\s*Updating\s+(?:Entity=)?(.+?)\s+CardID=(\S*)");
    private static readonly Regex ChangePattern = Pattern(@"^TAG_CHANGE\s+Entity=(.+?)\s+tag=(\S+)\s+value=(.+)$");
    private static readonly Regex HidePattern = Pattern(@"^HIDE_ENTITY\s*-\s*Entity=(.+?)\s+tag=(\S+)\s+value=(.+)$");
    private static readonly Regex TagPattern = Pattern(@"^tag=(\S+)\s+value=(.+)$");
    private static readonly Regex ReferencePattern = Pattern(@"\bid=(\d+)\b");
    private static readonly Regex PlayerNamePattern = Pattern(@"GameState\.DebugPrintGame\(\)\s*-\s*PlayerID=(\d+),\s*PlayerName=(.+)$");
    private static readonly Regex ChoiceHeaderPattern = Pattern(@"id=(\d+)\s+Player=(.+?)\s+TaskList=(\d*)\s+ChoiceType=(\w+)\s+CountMin=(\d+)\s+CountMax=(\d+)");
    private static readonly Regex ChosenHeaderPattern = Pattern(@"id=(\d+)\s+Player=(.+?)\s+EntitiesCount=(\d+)");
    private static readonly Regex ChoiceEntityPattern = Pattern(@"Entities\[(\d+)\]=(.+)$");
    private static readonly Regex DescriptorCardPattern = Pattern(@"\bcardId=([^\s\]]*)");
    private static readonly Regex DescriptorPlayerPattern = Pattern(@"\bplayer=(\d+)\b");
    private static readonly Regex DormantPattern = Pattern(@"^CACHED_TAG_FOR_DORMANT_CHANGE\s+Entity=(.+?)\s+tag=(\S+)\s+value=(.+)$");
    private static readonly HashSet<string> HiddenSafeTags = ["ZONE", "CONTROLLER", "ZONE_POSITION", "ENTITY_ID"];

    private readonly Dictionary<int, Entity> _entities = [];
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    // Names are lookup-only aliases from game metadata; snapshots never contain them.
    private readonly Dictionary<string, int?> _playerNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _pendingPlayerNames = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ChoiceBuilder> _choices = [];
    private readonly Queue<(ObservedGameEvent Event, int? PrivateOwner)> _recentEvents = new();
    private long _eventHistoryTruncatedThrough;
    private ChoiceBuilder? _currentChoice;
    private bool _readingChosen;
    private readonly bool _autoDetectPerspective;
    private bool _manualPerspective;
    private int? _provenInitialPlayerId;
    private bool _isSpectating;
    private string? _pendingBuild;
    private string? _pendingFormat;
    private string? _pendingGameType;
    private string? _clientBuild;
    private string? _format;
    private string? _gameType;
    private readonly string _sessionId;
    private int _matchNumber;
    private int? _currentEntity;
    private int? _gameEntity;
    private int _blockCount;
    private bool _sawStart;
    private bool _taskComplete;
    private bool _hasGap;
    private long _version;
    private long _eventSequence;
    private int? _localPlayerId;

    public PowerLogStateEngine(int? localPlayerId = null, string? sessionId = null, bool autoDetectPerspective = false)
    {
        if (localPlayerId is <= 0) throw new ArgumentOutOfRangeException(nameof(localPlayerId));
        _localPlayerId = localPlayerId;
        _manualPerspective = localPlayerId.HasValue;
        _autoDetectPerspective = autoDetectPerspective;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    public void SetLocalPlayerId(int? playerId)
    {
        if (playerId is <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        var manual = playerId.HasValue;
        var resolved = manual ? playerId : _autoDetectPerspective && !_isSpectating ? _provenInitialPlayerId : null;
        if (_localPlayerId == resolved && _manualPerspective == manual) return;
        _localPlayerId = resolved;
        _manualPerspective = manual;
        _version++;
    }

    /// <summary>Clears state at a file/session boundary. The next records require a new CREATE_GAME.</summary>
    public void ResetSession()
    {
        ClearGame();
        _isSpectating = false;
        if (!_manualPerspective) _localPlayerId = null;
        _matchNumber++;
        _version++;
    }

    /// <summary>Marks input loss, e.g. a truncated file. Completeness cannot recover until the next game.</summary>
    public void MarkDataGap(string reason = "日志存在缺口")
    {
        _hasGap = true;
        _taskComplete = false;
        if (_warnings.Add(reason)) _version++;
    }

    /// <returns>True when the state or its quality changed. Unrelated log lines return false.</returns>
    public bool ProcessLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            var changed = ProcessLineCore(line);
            return TryAutoDetectPerspective() || changed;
        }
        catch (Exception ex) when (ex is OverflowException or RegexMatchTimeoutException)
        {
            _currentEntity = null;
            MarkDataGap("日志记录超出可解析范围");
            return true;
        }
    }

    private bool ProcessLineCore(string line)
    {
        if (line.Length > 131072) { MarkDataGap("日志行过长，已跳过"); return true; }
        if (line.Contains("Begin Spectating", StringComparison.Ordinal) || line.Contains("Start Spectator", StringComparison.Ordinal))
        {
            _isSpectating = true;
            _provenInitialPlayerId = null;
            if (!_manualPerspective) _localPlayerId = null;
            _version++;
            return true;
        }
        if (line.Contains("End Spectator", StringComparison.Ordinal)) { _isSpectating = false; return false; }
        if (line.Contains("GameState.DebugPrintEntityChoices", StringComparison.Ordinal) ||
            line.Contains("GameState.DebugPrintEntitiesChosen", StringComparison.Ordinal))
            return ProcessChoiceLine(line);
        FlushChoice();
        if (line.Contains("GameState.DebugPrintGame", StringComparison.Ordinal))
        {
            if (line.Contains("BuildNumber=", StringComparison.Ordinal)) _pendingPlayerNames.Clear();
            var build = Pattern(@"\bBuildNumber=(\d+)").Match(line);
            if (build.Success) _pendingBuild = build.Groups[1].Value;
            var format = Pattern(@"\bFormatType=(\w+)").Match(line);
            if (format.Success) _pendingFormat = format.Groups[1].Value switch
            {
                "2" or "FT_STANDARD" => "Standard", "1" or "FT_WILD" => "Wild", var value => value
            };
            var gameType = Pattern(@"\bGameType=(\w+)").Match(line);
            if (gameType.Success) _pendingGameType = gameType.Groups[1].Value;
            var playerName = PlayerNamePattern.Match(line);
            if (playerName.Success)
            {
                var playerId = ParseId(playerName.Groups[1].Value);
                var name = playerName.Groups[2].Value.Trim();
                AddPlayerAlias(_pendingPlayerNames, name, playerId);
                if (_sawStart) AddPlayerAlias(_playerNames, name, playerId);
            }
            return false;
        }
        if (line.Contains("PowerProcessor.EndCurrentTaskList", StringComparison.Ordinal))
        {
            _eventSequence++;
            _currentEntity = null;
            if (_taskComplete) return false;
            _taskComplete = true;
            _version++;
            return true;
        }
        // GameState.DebugPrintPower duplicates the task-list stream; ingest one source only.
        if (line.Contains("GameState.", StringComparison.Ordinal)) return false;
        var marker = line.IndexOf("PowerTaskList.DebugPrintPower", StringComparison.Ordinal);
        string content;
        if (marker >= 0)
        {
            var separator = line.IndexOf(" - ", marker, StringComparison.Ordinal);
            if (separator < 0) return false;
            content = line[(separator + 3)..].Trim();
        }
        else
        {
            content = line.Trim(); // Also accepts extracted payloads for replay and diagnostics.
        }

        if (content == "CREATE_GAME")
        {
            // GameState's player descriptors precede PowerTaskList's CREATE_GAME in current client logs.
            var names = _pendingPlayerNames.ToArray();
            var build = _pendingBuild;
            var format = _pendingFormat;
            var gameType = _pendingGameType;
            ClearGame();
            foreach (var name in names) _playerNames[name.Key] = name.Value;
            _clientBuild = build;
            _format = format;
            _gameType = gameType;
            if (!_manualPerspective) _localPlayerId = null;
            _matchNumber++;
            _sawStart = true;
            _eventSequence = 1;
            _version = 1;
            return true;
        }
        if (content.StartsWith("RESET_GAME", StringComparison.Ordinal))
        {
            MarkDataGap("遇到回溯或重置，需重新建立完整局面");
            _eventSequence++;
            _currentEntity = null;
            return true;
        }

        var match = GameEntityPattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = ParseId(match.Groups[1].Value);
            _gameEntity = _currentEntity;
            GetEntity(_currentEntity.Value).Kind = EntityKind.Game;
            GetEntity(_currentEntity.Value).Revision++;
            return Changed();
        }
        match = PlayerPattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = ParseId(match.Groups[1].Value);
            var entity = GetEntity(_currentEntity.Value);
            entity.Kind = EntityKind.Player;
            entity.Tags["PLAYER_ID"] = match.Groups[2].Value;
            entity.Revision++;
            return Changed();
        }
        match = FullEntityPattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = ParseId(match.Groups[1].Value);
            var entity = GetEntity(_currentEntity.Value);
            entity.CardId = NullIfEmpty(match.Groups[2].Value);
            entity.Hidden = false;
            entity.Revision++;
            AddEvent("EntityCreated", entity, null, null, "创建卡牌实体");
            return Changed();
        }
        match = UpdatingPattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = ResolveReference(match.Groups[2].Value);
            if (_currentEntity is not { } id) return Unresolved();
            var entity = GetEntity(id);
            if (match.Groups[1].Value is "CHANGE_ENTITY" or "FULL_ENTITY")
            {
                // A new card definition must not inherit old observed attack/cost/health.
                foreach (var tag in entity.Tags.Keys.Where(t => !HiddenSafeTags.Contains(t)).ToArray())
                    entity.Tags.Remove(tag);
            }
            entity.CardId = NullIfEmpty(match.Groups[3].Value);
            entity.Hidden = false;
            entity.Revision++;
            AddEvent(match.Groups[1].Value == "CHANGE_ENTITY" ? "CardChanged" : "CardRevealed", entity,
                null, null, match.Groups[1].Value == "CHANGE_ENTITY" ? "卡牌发生变形或替换" : "卡牌身份已公开");
            // Following continuation tags belong to this revealed/transformed entity.
            return Changed();
        }
        match = HidePattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = null;
            if (ResolveReference(match.Groups[1].Value) is not { } id) return Unresolved();
            var entity = GetEntity(id);
            var oldCard = IsIdentityVisible(entity) ? entity.CardId : null;
            var oldZone = entity.Tags.GetValueOrDefault("ZONE");
            entity.Hidden = true;
            entity.CardId = null;
            foreach (var tag in entity.Tags.Keys.Where(t => !HiddenSafeTags.Contains(t)).ToArray())
                entity.Tags.Remove(tag);
            entity.Revision++;
            ApplyTag(entity, match.Groups[2].Value, match.Groups[3].Value, recordEvent: false);
            if (oldZone != entity.Tags.GetValueOrDefault("ZONE"))
                AddEvent("ZoneChanged", entity, oldZone, entity.Tags.GetValueOrDefault("ZONE"), "卡牌进入隐藏区域", oldCard);
            return Changed();
        }
        match = ChangePattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = null;
            if (ResolveReference(match.Groups[1].Value, allowPlayerPlaceholder: true) is not { } id) return Unresolved();
            return ApplyTag(GetEntity(id), match.Groups[2].Value, match.Groups[3].Value) && Changed();
        }
        match = TagPattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            if (_currentEntity is not { } id) return Unresolved();
            return ApplyTag(GetEntity(id), match.Groups[1].Value, match.Groups[2].Value) && Changed();
        }
        if (content.StartsWith("BLOCK_START", StringComparison.Ordinal))
        {
            _eventSequence++;
            _currentEntity = null;
            _blockCount++;
            return Changed();
        }
        if (content == "BLOCK_END")
        {
            _eventSequence++;
            _currentEntity = null;
            if (_blockCount > 0) _blockCount--;
            else { _hasGap = true; _warnings.Add("结算块结束缺少对应开始"); }
            return Changed();
        }
        if (content.StartsWith("META_DATA", StringComparison.Ordinal))
        {
            _currentEntity = null;
            return false;
        }
        match = DormantPattern.Match(content);
        if (match.Success)
        {
            _eventSequence++;
            _currentEntity = null;
            if (ResolveReference(match.Groups[1].Value) is not { } id) return Unresolved();
            var entity = GetEntity(id);
            // The client caches these values before emitting actual TAG_CHANGE records. A cached
            // TAUNT=1 can coexist with the current TAUNT=0 while dormant; do not apply it as state.
            entity.DormantCache[NormalizeTag(match.Groups[2].Value)] = match.Groups[3].Value.Trim();
            return false;
        }
        if (content.StartsWith("TAG_LIST_CHANGE", StringComparison.Ordinal) ||
            content.StartsWith("CACHED_TAG_FOR_DORMANT_CHANGE", StringComparison.Ordinal))
        {
            _currentEntity = null;
            MarkDataGap("遇到尚未支持的属性变更记录");
            return true;
        }
        if (content.StartsWith("FULL_ENTITY", StringComparison.Ordinal) ||
            content.StartsWith("SHOW_ENTITY", StringComparison.Ordinal) ||
            content.StartsWith("CHANGE_ENTITY", StringComparison.Ordinal) ||
            content.StartsWith("TAG_CHANGE", StringComparison.Ordinal) ||
            content.StartsWith("HIDE_ENTITY", StringComparison.Ordinal))
        {
            _currentEntity = null;
            MarkDataGap("遇到无法解析的状态记录");
            return true;
        }
        return false;
    }

    public GameSnapshot Snapshot()
    {
        var warnings = new SortedSet<string>(_warnings, StringComparer.Ordinal);
        if (!_sawStart) warnings.Add("未观察到 CREATE_GAME，局面可能从中途开始");
        var players = _entities.Values.Where(e => e.Kind == EntityKind.Player).ToArray();
        var uniquePlayers = players.Select(player => IntTag(player, "PLAYER_ID")).OrderBy(id => id).ToArray();
        var playersValid = uniquePlayers.Length == 2 && uniquePlayers[0] == 1 && uniquePlayers[1] == 2;
        var hasPerspective = _localPlayerId.HasValue && players.Any(p => IntTag(p, "PLAYER_ID") == _localPlayerId);
        if (!hasPerspective) warnings.Add("我方玩家身份尚未确认");
        if (_gameEntity is null || players.Length < 2) warnings.Add("开局实体信息尚不完整");
        else if (!playersValid) warnings.Add("双方玩家标识存在重复或缺失");
        var ownChoice = hasPerspective ? GetOwnChoice() : null;
        if (ownChoice is not null && !ownChoice.Ready) warnings.Add("当前选择详情尚未完整读取");
        if (!_taskComplete && ownChoice is not { Ready: true, TaskList: null }) warnings.Add("尚未观察到当前任务列表结算完成");
        var phase = GetPhase();
        var isComplete = _sawStart && !_hasGap && _gameEntity.HasValue && playersValid && hasPerspective;
        var step = _gameEntity is { } currentGame ? _entities[currentGame].Tags.GetValueOrDefault("STEP") : null;
        var decisionPhase = phase is GamePhase.Mulligan or GamePhase.Complete || step == "MAIN_ACTION";
        var choiceReady = ownChoice is { Ready: true } && (ownChoice.TaskList is null || _taskComplete);
        var stable = isComplete && _blockCount == 0 &&
            (choiceReady || (_taskComplete && ownChoice is null && decisionPhase));
        var entities = _entities.Values.ToFrozenDictionary(e => e.Id, e => ToSnapshot(e, hasPerspective));
        var activePlayers = players.Where(e => IntTag(e, "CURRENT_PLAYER") == 1).ToArray();
        return new GameSnapshot($"{_sessionId}-{_matchNumber:D4}", _version, _eventSequence,
            _gameEntity is { } gameId ? IntTag(_entities[gameId], "TURN") : null,
            activePlayers.Length == 1 ? IntTag(activePlayers[0], "PLAYER_ID") : null,
            hasPerspective ? _localPlayerId : null, phase, entities,
            new SnapshotQuality(_sawStart, isComplete, stable, hasPerspective, _blockCount,
                Array.AsReadOnly(warnings.ToArray())))
        {
            Choice = ownChoice is null ? null : new CurrentChoiceSnapshot(ownChoice.Id, ownChoice.PlayerId,
                ownChoice.Type, ownChoice.SourceId, ownChoice.Minimum, ownChoice.Maximum,
                Array.AsReadOnly(ownChoice.Options.ToArray()), ownChoice.Ready),
            RecentEvents = Array.AsReadOnly(_recentEvents.Select(item =>
                item.Event.CardId is not null && item.PrivateOwner.HasValue && item.PrivateOwner != (hasPerspective ? _localPlayerId : null)
                    ? item.Event with { CardId = null } : item.Event).ToArray()),
            EventHistoryTruncatedThrough = _eventHistoryTruncatedThrough,
            Format = _format,
            ClientBuild = _clientBuild,
            GameType = _gameType,
            PerspectiveSource = hasPerspective ? (_manualPerspective ? "manual" : "initial_private_hand") : "unconfirmed",
            IsSpectating = _isSpectating
        };
    }

    private EntitySnapshot ToSnapshot(Entity entity, bool hasPerspective)
    {
        var zone = entity.Tags.GetValueOrDefault("ZONE");
        var own = hasPerspective && IntTag(entity, "CONTROLLER") == _localPlayerId;
        var visible = entity.Kind is EntityKind.Game or EntityKind.Player ||
            (!entity.Hidden && (zone is "PLAY" or "GRAVEYARD" || (own && zone is "HAND" or "SECRET")));
        if (visible && entity.VisibleRevision == entity.Revision && entity.VisibleSnapshot is { } visibleCached) return visibleCached;
        if (!visible && entity.HiddenRevision == entity.Revision && entity.HiddenSnapshot is { } hiddenCached) return hiddenCached;
        var tags = visible ? entity.Tags : entity.Tags.Where(t => HiddenSafeTags.Contains(t.Key));
        var snapshot = new EntitySnapshot(entity.Id, visible ? entity.CardId : null, entity.Kind,
            tags.ToFrozenDictionary(StringComparer.Ordinal));
        if (visible) { entity.VisibleRevision = entity.Revision; entity.VisibleSnapshot = snapshot; }
        else { entity.HiddenRevision = entity.Revision; entity.HiddenSnapshot = snapshot; }
        return snapshot;
    }

    private GamePhase GetPhase()
    {
        if (_gameEntity is not { } gameId) return GamePhase.Unknown;
        var entity = _entities[gameId];
        var state = entity.Tags.GetValueOrDefault("STATE");
        var step = entity.Tags.GetValueOrDefault("STEP");
        if (state == "COMPLETE" || step is "FINAL_GAMEOVER" or "FINAL_WRAPUP") return GamePhase.Complete;
        if (step == "BEGIN_MULLIGAN") return GamePhase.Mulligan;
        if (GetOwnChoice() is not null) return GamePhase.Choice;
        if (step?.StartsWith("MAIN_", StringComparison.Ordinal) == true) return GamePhase.Main;
        return _sawStart ? GamePhase.Starting : GamePhase.Unknown;
    }

    private bool ApplyTag(Entity entity, string rawTag, string rawValue, bool recordEvent = true)
    {
        var tag = NormalizeTag(rawTag);
        var value = NormalizeValue(tag, rawValue.Trim());
        if (entity.Tags.GetValueOrDefault(tag) == value) return false;
        var fromZone = entity.Tags.GetValueOrDefault("ZONE");
        var oldCard = IsIdentityVisible(entity) ? entity.CardId : null;
        entity.Tags[tag] = value;
        entity.Revision++;
        if (recordEvent && tag == "ZONE")
            AddEvent("ZoneChanged", entity, fromZone, value, "卡牌区域发生变化", oldCard);
        else if (recordEvent && tag is "TURN" or "RESOURCES" or "RESOURCES_USED" or "PLAYSTATE" or "DAMAGE" or "HEALTH")
            AddEvent("AttributeChanged", entity, null, null, tag switch
            {
                "TURN" => "进入新的回合", "PLAYSTATE" => "玩家对局结果变化",
                "RESOURCES" or "RESOURCES_USED" => "法力资源变化", _ => "生命或伤害变化"
            });
        return true;
    }

    /// <summary>Only infers from exclusive, initially private hand identities; manual selection wins.</summary>
    public bool TryAutoDetectPerspective()
    {
        if (!_autoDetectPerspective || _manualPerspective || _localPlayerId.HasValue || _isSpectating || !_sawStart || !_taskComplete ||
            GetPhase() != GamePhase.Mulligan) return false;
        var privateHands = _entities.Values.Where(e => e.Kind == EntityKind.Card && !e.Hidden &&
            e.Tags.GetValueOrDefault("ZONE") == "HAND" && !string.IsNullOrWhiteSpace(e.CardId) &&
            e.CardId != "GAME_005" && IntTag(e, "CONTROLLER") is 1 or 2)
            .GroupBy(e => IntTag(e, "CONTROLLER")!.Value).ToArray();
        if (privateHands.Length != 1 || privateHands[0].Count() < 2) return false;
        var candidate = privateHands[0].Key;
        if (!_entities.Values.Any(e => e.Kind == EntityKind.Player && IntTag(e, "PLAYER_ID") == candidate)) return false;
        _localPlayerId = candidate;
        _provenInitialPlayerId = candidate;
        _version++;
        return true;
    }

    private ChoiceBuilder? GetOwnChoice() => _localPlayerId.HasValue
        ? _choices.Values.Where(choice => !choice.Chosen && choice.PlayerId == _localPlayerId).OrderByDescending(choice => choice.Id).FirstOrDefault()
        : null;

    private void FlushChoice()
    {
        if (_currentChoice is { Chosen: false, Ready: false } choice && choice.Options.Count > 0)
        {
            choice.Ready = true;
            _version++;
        }
        _currentChoice = null;
        _readingChosen = false;
    }

    private bool ProcessChoiceLine(string line)
    {
        var offered = line.Contains("DebugPrintEntityChoices", StringComparison.Ordinal);
        var header = offered ? ChoiceHeaderPattern.Match(line) : ChosenHeaderPattern.Match(line);
        _eventSequence++;
        if (header.Success)
        {
            FlushChoice();
            var id = ParseId(header.Groups[1].Value);
            if (offered)
            {
                var playerRef = header.Groups[2].Value;
                int? player = int.TryParse(playerRef, out var numeric) ? numeric : _playerNames.GetValueOrDefault(playerRef);
                int? task = int.TryParse(header.Groups[3].Value, out var number) ? number : null;
                _currentChoice = new(id, player, header.Groups[4].Value, task,
                    ParseId(header.Groups[5].Value), ParseId(header.Groups[6].Value));
                _choices[id] = _currentChoice;
                _readingChosen = false;
            }
            else
            {
                _choices.TryGetValue(id, out _currentChoice);
                if (_currentChoice is not null) _currentChoice.Chosen = true;
                _readingChosen = true;
            }
            _currentEntity = null;
            return Changed();
        }
        if (_currentChoice is null || _readingChosen) return false;
        var source = line.IndexOf("Source=", StringComparison.Ordinal);
        if (source >= 0)
        {
            _currentChoice.SourceId = ResolveReference(line[(source + 7)..].Trim());
            return Changed();
        }
        var option = ChoiceEntityPattern.Match(line);
        if (!option.Success) return false;
        var entityRef = option.Groups[2].Value.Trim();
        var entityId = ResolveReference(entityRef);
        if (entityId is null) { MarkDataGap("选择候选缺少可识别的实体标识"); return true; }
        var playerMatch = DescriptorPlayerPattern.Match(entityRef);
        int? owner = playerMatch.Success ? ParseId(playerMatch.Groups[1].Value) :
            _entities.TryGetValue(entityId.Value, out var entity) ? IntTag(entity, "CONTROLLER") : null;
        if (_currentChoice.PlayerId is null && owner is not null) _currentChoice.PlayerId = owner;
        if (owner is not null && _currentChoice.PlayerId != owner)
        {
            MarkDataGap("选择候选的控制者与选择身份不一致");
            return true;
        }
        var card = DescriptorCardPattern.Match(entityRef);
        var cardId = card.Success ? NullIfEmpty(card.Groups[1].Value) :
            _entities.GetValueOrDefault(entityId.Value)?.CardId;
        var optionValue = new ChoiceOptionSnapshot(entityId.Value, cardId, owner, ParseId(option.Groups[1].Value));
        var existing = _currentChoice.Options.FindIndex(value => value.Position == optionValue.Position);
        if (existing >= 0) _currentChoice.Options[existing] = optionValue;
        else _currentChoice.Options.Add(optionValue);
        return Changed();
    }

    private bool IsIdentityVisible(Entity entity) => entity.Kind is EntityKind.Game or EntityKind.Player ||
        (!entity.Hidden && (entity.Tags.GetValueOrDefault("ZONE") is "PLAY" or "GRAVEYARD" ||
            (_localPlayerId.HasValue && IntTag(entity, "CONTROLLER") == _localPlayerId &&
             entity.Tags.GetValueOrDefault("ZONE") is "HAND" or "SECRET")));

    private void AddEvent(string kind, Entity entity, string? fromZone, string? toZone, string summary, string? previousVisibleCard = null)
    {
        var owner = IntTag(entity, "CONTROLLER");
        var zone = entity.Tags.GetValueOrDefault("ZONE");
        var card = IsIdentityVisible(entity) ? entity.CardId : previousVisibleCard;
        // Entering PLAY or leaving an already public zone preserves the identity as a public fact.
        // Private hand/deck observations remain tied to the perspective that actually saw them.
        var publiclyVisible = zone is "PLAY" or "GRAVEYARD" || fromZone is "PLAY" or "GRAVEYARD";
        int? privateOwner = !publiclyVisible && (zone is "HAND" or "SECRET" || fromZone is "HAND" or "SECRET") ? owner : null;
        _recentEvents.Enqueue((new(_eventSequence, kind, entity.Id, owner, card, fromZone, toZone, summary), privateOwner));
        while (_recentEvents.Count > 80) _eventHistoryTruncatedThrough = _recentEvents.Dequeue().Event.Sequence;
    }

    private bool Changed() { _taskComplete = false; _version++; return true; }
    private bool Unresolved() { MarkDataGap("实体引用或连续标签缺少上下文"); return true; }
    private Entity GetEntity(int id)
    {
        if (!_entities.TryGetValue(id, out var entity)) _entities[id] = entity = new Entity(id);
        return entity;
    }

    private int? ResolveReference(string reference, bool allowPlayerPlaceholder = false)
    {
        if (reference == "GameEntity") return _gameEntity;
        if (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0) return numeric;
        if (_playerNames.TryGetValue(reference, out var mapped) && mapped is { } playerId)
            return ResolvePlayerEntity(playerId)?.Id;
        var match = ReferencePattern.Match(reference);
        if (match.Success) return ParseId(match.Groups[1].Value);
        // Current client metadata can initially label one player UNKNOWN HUMAN PLAYER and then
        // use that player's resolved name in TAG_CHANGE. Only bind this one unassigned placeholder;
        // other ambiguous/missing references still invalidate completeness.
        if (allowPlayerPlaceholder && !reference.StartsWith("UNKNOWN ENTITY", StringComparison.Ordinal) &&
            !_playerNames.ContainsKey(reference) && _playerNames.TryGetValue("UNKNOWN HUMAN PLAYER", out var placeholder) &&
            placeholder is { } placeholderId && !_playerNames.Any(x => x.Key != "UNKNOWN HUMAN PLAYER" && x.Value == placeholderId))
        {
            var player = ResolvePlayerEntity(placeholderId);
            if (player is not null)
            {
                _playerNames[reference] = placeholderId;
                return player.Id;
            }
        }
        return null;
    }

    private Entity? ResolvePlayerEntity(int playerId)
    {
        var matches = _entities.Values.Where(entity => entity.Kind == EntityKind.Player && IntTag(entity, "PLAYER_ID") == playerId)
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void ClearGame()
    {
        _entities.Clear(); _warnings.Clear();
        _choices.Clear(); _currentChoice = null; _readingChosen = false; _recentEvents.Clear();
        _provenInitialPlayerId = null;
        _eventHistoryTruncatedThrough = 0;
        _clientBuild = null; _format = null; _gameType = null; _pendingBuild = null; _pendingFormat = null; _pendingGameType = null;
        _playerNames.Clear(); _pendingPlayerNames.Clear();
        _currentEntity = null; _gameEntity = null;
        _blockCount = 0; _sawStart = false; _taskComplete = false; _hasGap = false;
        _eventSequence = 0; _version = 0;
    }

    private static void AddPlayerAlias(Dictionary<string, int?> names, string name, int playerId)
    {
        if (names.TryGetValue(name, out var existing) && existing != playerId) names[name] = null;
        else names.TryAdd(name, playerId);
    }

    private static int? IntTag(Entity entity, string tag) => entity.Tags.TryGetValue(tag, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static int ParseId(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static Regex Pattern(string value) => new(value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static string NormalizeTag(string tag) => tag switch
    {
        "17" => "PLAYSTATE", "19" => "STEP", "20" => "TURN", "23" => "CURRENT_PLAYER", "24" => "FIRST_PLAYER",
        "25" => "RESOURCES_USED", "26" => "RESOURCES", "27" => "HERO_ENTITY", "30" => "PLAYER_ID",
        "43" => "EXHAUSTED", "44" => "DAMAGE", "45" => "HEALTH", "47" => "ATK", "48" => "COST",
        "49" => "ZONE", "50" => "CONTROLLER", "51" => "OWNER", "53" => "ENTITY_ID", "202" => "CARDTYPE",
        "199" => "CLASS", "204" => "STATE", "263" => "ZONE_POSITION", "292" => "ARMOR", "305" => "MULLIGAN_STATE",
        "176" => "MAXRESOURCES", "187" => "DURABILITY", "188" => "SILENCED", "189" => "WINDFURY",
        "190" => "TAUNT", "191" => "STEALTH", "192" => "SPELLPOWER", "194" => "DIVINE_SHIELD", "197" => "CHARGE",
        "227" => "CANT_ATTACK", "240" => "IMMUNE", "260" => "FROZEN", "271" => "NUM_TURNS_IN_PLAY",
        "295" => "TEMP_RESOURCES", "297" => "NUM_ATTACKS_THIS_TURN", "313" => "CREATOR", "385" => "DISPLAYED_CREATOR",
        "435" => "TRANSFORMED_FROM_CARD", "448" => "UNTOUCHABLE", "777" => "HERO_POWER_DISABLED",
        "791" => "RUSH", "1518" => "DORMANT", "1565" => "COPIED_FROM_ENTITY_ID", "1646" => "HERO_POWER_ENTITY",
        _ => tag
    };

    private static string NormalizeValue(string tag, string value) => (tag, value) switch
    {
        ("ZONE", "0") => "INVALID", ("ZONE", "1") => "PLAY", ("ZONE", "2") => "DECK",
        ("ZONE", "3") => "HAND", ("ZONE", "4") => "GRAVEYARD", ("ZONE", "5") => "REMOVEDFROMGAME",
        ("ZONE", "6") => "SETASIDE", ("ZONE", "7") => "SECRET",
        ("STATE", "1") => "LOADING", ("STATE", "2") => "RUNNING", ("STATE", "3") => "COMPLETE",
        ("STEP", "4") => "BEGIN_MULLIGAN", ("STEP", "5") => "MAIN_BEGIN", ("STEP", "6") => "MAIN_READY",
        ("STEP", "7") => "MAIN_RESOURCE", ("STEP", "8") => "MAIN_DRAW", ("STEP", "9") => "MAIN_START",
        ("STEP", "10") => "MAIN_ACTION", ("STEP", "11") => "MAIN_COMBAT", ("STEP", "12") => "MAIN_END",
        ("STEP", "13") => "MAIN_NEXT", ("STEP", "14") => "FINAL_WRAPUP", ("STEP", "15") => "FINAL_GAMEOVER",
        ("STEP", "16") => "MAIN_CLEANUP", ("STEP", "17") => "MAIN_START_TRIGGERS",
        _ => value
    };
}
