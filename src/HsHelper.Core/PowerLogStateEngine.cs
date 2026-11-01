using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HsHelper.Core;

/// <summary>
/// Replays PowerTaskList records. The caller supplies the local player ID after confirming perspective.
/// This reconstructs observed state; it neither infers absent tags nor simulates card effects.
/// </summary>
public sealed partial class PowerLogStateEngine
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
        public bool Superseded { get; set; }
        public long OfferedAt { get; init; }
        public long PreviousOfferedAt { get; init; }
        public int? PrivateOwner { get; init; }
        public string? SourceCardId { get; set; }
        public string? SourceDescriptorCardId { get; set; }
        public int? SourceDescriptorOwner { get; set; }
        public bool SourceObserved { get; set; }
        public Dictionary<int, EntitySnapshot> OptionEntities { get; } = [];
        public Dictionary<int, int> ChosenByPosition { get; } = [];
        public int? ExpectedChosenCount { get; set; }
        public long ChosenAt { get; set; }
        public bool HistoryInvalid { get; set; }
        public bool HistoryEmitted { get; set; }
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
    private static readonly HashSet<string> PublicSecretSlotTags = ["SECRET", "QUEST", "SIDE_QUEST"];
    private const int MaximumChoiceHistoryEntries = 32;
    private const int MaximumRememberedChoices = 64;
    private const int MaximumChoiceOptions = 64;

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
    private readonly Queue<ChoiceHistoryEntry> _choiceHistory = new();
    private readonly Dictionary<int, HashSet<string>> _choiceHistoryWarnings = [];
    private readonly Dictionary<int, long> _choiceHistoryTruncatedThrough = [];
    private readonly HashSet<int> _unobservedPrivateChoicePlayers = [];
    private bool _unscopedChoiceHistoryGap;
    private long _lastOfferedChoiceSequence;
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
            var beforeVersion = _version;
            var changed = ProcessLineCore(line);
            return TryAutoDetectPerspective() || changed || _version != beforeVersion;
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
            var cardId = NullIfEmpty(match.Groups[2].Value);
            if (entity.CardId is not null && entity.CardId != cardId)
                foreach (var tag in entity.Tags.Keys.Where(tag => !HiddenSafeTags.Contains(tag)).ToArray()) entity.Tags.Remove(tag);
            entity.CardId = cardId;
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
            var oldProof = ObservedIdentityProof(entity);
            var oldCard = IsIdentityVisible(entity) ? entity.CardId : null;
            var oldZone = entity.Tags.GetValueOrDefault("ZONE");
            entity.Hidden = true;
            entity.CardId = null;
            foreach (var tag in entity.Tags.Keys.Where(t => !HiddenSafeTags.Contains(t)).ToArray())
                entity.Tags.Remove(tag);
            entity.Revision++;
            ApplyTag(entity, match.Groups[2].Value, match.Groups[3].Value, recordEvent: false);
            if (oldZone != entity.Tags.GetValueOrDefault("ZONE"))
                AddEvent("ZoneChanged", entity, oldZone, entity.Tags.GetValueOrDefault("ZONE"), "卡牌进入隐藏区域", oldCard, oldProof);
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
            ObserveTurnBlockStart(content);
            return Changed();
        }
        if (content == "BLOCK_END")
        {
            _eventSequence++;
            _currentEntity = null;
            if (_turnBlocks.Count > 0) _turnBlocks.Pop();
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
        var hasPerspective = !_isSpectating && _localPlayerId.HasValue && players.Count(p => IntTag(p, "PLAYER_ID") == _localPlayerId) == 1;
        if (!hasPerspective) warnings.Add("我方玩家身份尚未确认");
        if (_gameEntity is null || players.Length < 2) warnings.Add("开局实体信息尚不完整");
        else if (!playersValid) warnings.Add("双方玩家标识存在重复或缺失");
        var ownChoice = hasPerspective ? GetOwnChoice() : null;
        if (ownChoice is not null && !ownChoice.Ready) warnings.Add("当前选择详情尚未完整读取");
        if (!_taskComplete && ownChoice is not { Ready: true, TaskList: null }) warnings.Add("尚未观察到当前任务列表结算完成");
        var phase = GetPhase();
        var unresolvedPublicType = _entities.Values.Any(entity => entity.Kind == EntityKind.Card &&
            entity.Tags.GetValueOrDefault("ZONE") == "PLAY" && !IsAttachmentEntity(entity) &&
            IdentityObservation(entity).Audience == AttachmentVisibility.Audience.Unresolved);
        if (unresolvedPublicType) warnings.Add("公开区域仍有类型未确认的实体，不能把尚未分清的附魔当作公开卡牌。");
        var isComplete = _sawStart && !_hasGap && _gameEntity.HasValue && playersValid && hasPerspective && !unresolvedPublicType;
        var step = _gameEntity is { } currentGame ? _entities[currentGame].Tags.GetValueOrDefault("STEP") : null;
        var decisionPhase = phase is GamePhase.Mulligan or GamePhase.Complete || step == "MAIN_ACTION";
        var choiceReady = ownChoice is { Ready: true } && (ownChoice.TaskList is null || _taskComplete);
        var stable = isComplete && _blockCount == 0 &&
            (choiceReady || (_taskComplete && ownChoice is null && decisionPhase));
        var visibleChoice = playersValid && ownChoice is { Ready: true } ? ownChoice : null;
        var entities = _entities.Values.ToFrozenDictionary(e => e.Id, e => ToSnapshot(e, hasPerspective, visibleChoice));
        var activePlayers = players.Where(e => IntTag(e, "CURRENT_PLAYER") == 1).ToArray();
        var snapshot = new GameSnapshot($"{_sessionId}-{_matchNumber:D4}", _version, _eventSequence,
            _gameEntity is { } gameId ? IntTag(_entities[gameId], "TURN") : null,
            activePlayers.Length == 1 ? IntTag(activePlayers[0], "PLAYER_ID") : null,
            hasPerspective ? _localPlayerId : null, phase, entities,
            new SnapshotQuality(_sawStart, isComplete, stable, hasPerspective, _blockCount,
                Array.AsReadOnly(warnings.ToArray())))
        {
            Choice = ownChoice is null ? null : new CurrentChoiceSnapshot(ownChoice.Id, ownChoice.PlayerId,
                ownChoice.Type, ownChoice.SourceId, ownChoice.Minimum, ownChoice.Maximum,
                Array.AsReadOnly(ownChoice.Options.ToArray()), ownChoice.Ready)
                { OfferedAtEventSequence = ownChoice.OfferedAt, PreviousOfferedAtEventSequence = ownChoice.PreviousOfferedAt, SourceCardId = ownChoice.SourceCardId },
            ChoiceHistory = SnapshotChoiceHistory(hasPerspective && playersValid),
            TurnHistory = SnapshotTurnHistory(hasPerspective && playersValid),
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
        return AttachmentVisibility.Project(snapshot);
    }

    private EntitySnapshot ToSnapshot(Entity entity, bool hasPerspective, ChoiceBuilder? ownChoice)
    {
        var zone = entity.Tags.GetValueOrDefault("ZONE");
        var own = hasPerspective && IntTag(entity, "CONTROLLER") == _localPlayerId;
        var visible = entity.Kind is EntityKind.Game or EntityKind.Player ||
            (!entity.Hidden && (zone is "PLAY" or "GRAVEYARD" || (own && zone is "HAND" or "SECRET")));
        if (visible && !IsAttachmentEntity(entity) && !IsIdentityVisible(entity)) visible = false;
        // SETASIDE cards remain private unless this exact observed offer names them for the
        // confirmed chooser. This projection has its own whitelist and never enters the
        // general visible cache, so finishing a choice cannot leave private tags exposed.
        var option = !visible ? ownChoice?.Options.FirstOrDefault(candidate => candidate.EntityId == entity.Id) : null;
        if (option is { CardId: not null } && option.ControllerId == _localPlayerId &&
            (IntTag(entity, "CONTROLLER") is not { } controller || controller == _localPlayerId) &&
            entity.CardId is not null && entity.CardId == option.CardId)
        {
            var optionTags = entity.Tags.Where(pair => ChoiceVisibility.IsObservedOptionTag(pair.Key)).ToDictionary(StringComparer.Ordinal);
            optionTags["CONTROLLER"] = _localPlayerId!.Value.ToString(CultureInfo.InvariantCulture);
            return new(entity.Id, option.CardId, entity.Kind, optionTags.ToFrozenDictionary(StringComparer.Ordinal));
        }
        // Only an opponent's already visible secret-zone slot has public Secret/Quest classification.
        // These keywords must never identify a card in HAND/DECK/SETASIDE or an unconfirmed perspective.
        var publicSecretSlot = !visible && hasPerspective && zone == "SECRET" && !own &&
            IntTag(entity, "CONTROLLER") is 1 or 2;
        if (visible && entity.VisibleRevision == entity.Revision && entity.VisibleSnapshot is { } visibleCached) return visibleCached;
        if (!visible && !publicSecretSlot && entity.HiddenRevision == entity.Revision && entity.HiddenSnapshot is { } hiddenCached) return hiddenCached;
        var tags = visible ? entity.Tags : entity.Tags.Where(t => HiddenSafeTags.Contains(t.Key) || publicSecretSlot && PublicSecretSlotTags.Contains(t.Key))
            .Select(pair => publicSecretSlot && PublicSecretSlotTags.Contains(pair.Key) && pair.Value is not ("0" or "1")
                ? new KeyValuePair<string, string>(pair.Key, "unknown") : pair);
        var snapshot = new EntitySnapshot(entity.Id, visible ? entity.CardId : null, entity.Kind,
            tags.ToFrozenDictionary(StringComparer.Ordinal));
        if (visible) { entity.VisibleRevision = entity.Revision; entity.VisibleSnapshot = snapshot; }
        else if (!publicSecretSlot) { entity.HiddenRevision = entity.Revision; entity.HiddenSnapshot = snapshot; }
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
        var oldProof = ObservedIdentityProof(entity);
        var oldCard = IsIdentityVisible(entity) ? entity.CardId : null;
        entity.Tags[tag] = value;
        entity.Revision++;
        ObserveTurnTag(entity, tag, value);
        if (tag == "ZONE") ObserveTurnZone(entity, fromZone, value);
        if (recordEvent && tag == "ZONE")
            AddEvent("ZoneChanged", entity, fromZone, value, "卡牌区域发生变化", oldCard, oldProof);
        else if (recordEvent && tag is "TURN" or "RESOURCES" or "RESOURCES_USED" or "PLAYSTATE" or "DAMAGE" or "HEALTH")
            AddEvent("AttributeChanged", entity, null, null, tag switch
            {
                "TURN" => "进入新的回合", "PLAYSTATE" => "玩家对局结果变化",
                "RESOURCES" or "RESOURCES_USED" => "法力资源变化", _ => "生命或伤害变化"
            });
        else if (recordEvent && oldCard is null && tag is "CARDTYPE" or "ATTACHED" or "CONTROLLER" && IsIdentityVisible(entity) && entity.CardId is not null)
            AddEvent("CardRevealed", entity, null, null, "卡牌身份已确认可见");
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

    private ChoiceBuilder? GetOwnChoice() => _localPlayerId.HasValue && !_isSpectating
        ? _choices.Values.Where(choice => !choice.Chosen && !choice.Superseded && choice.PlayerId == _localPlayerId && choice.PrivateOwner == _localPlayerId).OrderByDescending(choice => choice.OfferedAt).FirstOrDefault()
        : null;

    private void FlushChoice()
    {
        if (_readingChosen && _currentChoice is { } chosen && !chosen.HistoryEmitted) CompleteChoiceHistory(chosen);
        if (_currentChoice is { Chosen: false, Ready: false } choice && (choice.Options.Count > 0 || choice.Maximum == 0))
        {
            choice.Ready = ValidOfferedChoice(choice);
            if (!choice.Ready) ChoiceHistoryProblem(choice, "选择候选的数量或位置记录不完整。");
            _version++;
        }
        _currentChoice = null;
        _readingChosen = false;
    }

    private bool ProcessChoiceLine(string line)
    {
        // Protocol shapes: HearthSim/python-hslog hslog/tokens.py CHOICES_* / ENTITIES_CHOSEN_*,
        // and parser.py ChoicesHandler. A chosen header announces a count; it does not name the selected cards.
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
                var player = ChoicePlayer(playerRef);
                int? task = int.TryParse(header.Groups[3].Value, out var number) ? number : null;
                var minimum = ParseId(header.Groups[5].Value); var maximum = ParseId(header.Groups[6].Value);
                if (_choices.TryGetValue(id, out var duplicateChoice))
                {
                    _currentChoice = duplicateChoice;
                    if (duplicateChoice.PlayerId != player || duplicateChoice.Type != header.Groups[4].Value || duplicateChoice.TaskList != task ||
                        duplicateChoice.Minimum != minimum || duplicateChoice.Maximum != maximum)
                    { duplicateChoice.HistoryInvalid = true; ChoiceHistoryProblem(duplicateChoice, "重复选择标头与原记录冲突，未覆盖历史。"); }
                    return false;
                }
                foreach (var previous in _choices.Values.Where(c => !c.Chosen && !c.Superseded && c.PlayerId == player && player.HasValue))
                { previous.Superseded = true; ChoiceHistoryProblem(previous, "选择被后续同玩家选择取代，但缺少实际已选明细。"); }
                _currentChoice = new(id, player, header.Groups[4].Value, task,
                    minimum, maximum)
                { OfferedAt = _eventSequence, PreviousOfferedAt = _lastOfferedChoiceSequence, PrivateOwner = ConfirmedChoiceOwner() };
                if (_currentChoice.PrivateOwner != player || player is null)
                    MarkUnobservedChoice(player);
                _lastOfferedChoiceSequence = _eventSequence;
                _choices[id] = _currentChoice;
                while (_choices.Count > MaximumRememberedChoices)
                {
                    var oldest = _choices.Values.OrderBy(c => c.OfferedAt).First();
                    if (!oldest.Chosen) ChoiceHistoryProblem(oldest, "尚未完成的选择已超出保留范围。");
                    _choices.Remove(oldest.Id);
                }
                _readingChosen = false;
            }
            else
            {
                _choices.TryGetValue(id, out _currentChoice);
                _readingChosen = true;
                var chooser = ChoicePlayer(header.Groups[2].Value);
                if (_currentChoice is null)
                { ChoiceHistoryProblem(null, "已选明细缺少对应的候选记录，未关联到其他选择。", chooser); return false; }
                var choice = _currentChoice;
                if (choice.HistoryEmitted) return false;
                var latestForPlayer = _choices.Values.Where(c => c.PlayerId == choice.PlayerId).OrderByDescending(c => c.OfferedAt).FirstOrDefault();
                if (choice.Superseded || latestForPlayer != choice || chooser != choice.PlayerId)
                { ChoiceHistoryProblem(choice, "已选标头迟到或玩家范围不匹配，未关联到当前选择。"); _currentChoice = null; return false; }
                choice.Chosen = true;
                choice.ExpectedChosenCount = ParseId(header.Groups[3].Value);
                choice.ChosenAt = _eventSequence;
                if (choice.ExpectedChosenCount == 0) CompleteChoiceHistory(choice);
            }
            _currentEntity = null;
            return Changed();
        }
        if (_currentChoice is null) return false;
        if (_readingChosen == offered)
        {
            _currentChoice.HistoryInvalid = true;
            ChoiceHistoryProblem(_currentChoice, "候选与已选明细的日志通道或先后顺序冲突，未将迟到候选当作实际选择。");
            return false;
        }
        if (_readingChosen) return ProcessChosenEntity(line, _currentChoice);
        if (!ValidateChoiceScope(_currentChoice)) return false;
        var source = line.IndexOf("Source=", StringComparison.Ordinal);
        if (source >= 0)
        {
            var sourceReference = line[(source + 7)..].Trim();
            var sourceId = ResolveReference(sourceReference);
            var sourceCardMatch = DescriptorCardPattern.Match(sourceReference);
            var sourceCard = sourceCardMatch.Success ? NullIfEmpty(sourceCardMatch.Groups[1].Value) : null;
            var sourcePlayerMatch = DescriptorPlayerPattern.Match(sourceReference);
            int? sourcePlayer = sourcePlayerMatch.Success ? ParseId(sourcePlayerMatch.Groups[1].Value) : null;
            if (_currentChoice.SourceObserved)
            {
                if (_currentChoice.SourceId != sourceId || _currentChoice.SourceDescriptorCardId != sourceCard || _currentChoice.SourceDescriptorOwner != sourcePlayer)
                { _currentChoice.HistoryInvalid = true; ChoiceHistoryProblem(_currentChoice, "重复选择来源与原记录冲突。"); }
                return false;
            }
            _currentChoice.SourceObserved = true;
            _currentChoice.SourceId = sourceId;
            _currentChoice.SourceDescriptorCardId = sourceCard;
            _currentChoice.SourceDescriptorOwner = sourcePlayer;
            if (sourceId is { } checkedId && _entities.TryGetValue(checkedId, out var checkedSource) &&
                (sourceCard is not null && checkedSource.CardId is not null && sourceCard != checkedSource.CardId ||
                 sourcePlayer is not null && IntTag(checkedSource, "CONTROLLER") is { } sourceOwner && sourcePlayer != sourceOwner))
            { _currentChoice.HistoryInvalid = true; ChoiceHistoryProblem(_currentChoice, "选择来源描述与当时实体身份冲突，未混合不同来源。"); return Changed(); }
            if (sourceId is { } observed && _entities.TryGetValue(observed, out var sourceEntity) &&
                _currentChoice.PrivateOwner == _localPlayerId && !_isSpectating && IsIdentityVisible(sourceEntity))
                _currentChoice.SourceCardId = sourceEntity.CardId;
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
        if (existing >= 0)
        {
            if (_currentChoice.Options[existing] != optionValue)
            { _currentChoice.HistoryInvalid = true; ChoiceHistoryProblem(_currentChoice, "重复候选位置与原记录冲突，未覆盖历史。"); }
            return false;
        }
        if (_currentChoice.HistoryEmitted) return false;
        if (_currentChoice.Options.Count >= MaximumChoiceOptions)
        { _currentChoice.HistoryInvalid = true; ChoiceHistoryProblem(_currentChoice, "候选数量超出保留范围。"); return false; }
        _currentChoice.Options.Add(optionValue);
        CaptureChoiceOptionEntity(_currentChoice, optionValue);
        return Changed();
    }

    private int? ChoicePlayer(string reference)
    {
        if (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
        {
            if (_entities.Values.Any(e => e.Kind == EntityKind.Player && IntTag(e, "PLAYER_ID") == numeric)) return numeric;
            return _entities.TryGetValue(numeric, out var player) && player.Kind == EntityKind.Player ? IntTag(player, "PLAYER_ID") : numeric;
        }
        if (_playerNames.TryGetValue(reference, out var alias)) return alias;
        var descriptor = DescriptorPlayerPattern.Match(reference);
        if (descriptor.Success) return ParseId(descriptor.Groups[1].Value);
        return ResolveReference(reference) is { } id && _entities.TryGetValue(id, out var entity) && entity.Kind == EntityKind.Player
            ? IntTag(entity, "PLAYER_ID") : null;
    }

    private int? ConfirmedChoiceOwner()
    {
        if (_isSpectating || _localPlayerId is not (1 or 2)) return null;
        var players = _entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => IntTag(entity, "PLAYER_ID")).Order().ToArray();
        return players.SequenceEqual(new int?[] { 1, 2 }) ? _localPlayerId : null;
    }

    private void MarkUnobservedChoice(int? player)
    {
        if (player is 1 or 2) _unobservedPrivateChoicePlayers.Add(player.Value);
        else _unscopedChoiceHistoryGap = true;
    }

    private bool ValidateChoiceScope(ChoiceBuilder choice)
    {
        if (choice.PrivateOwner is { } owner && choice.PlayerId == owner && ConfirmedChoiceOwner() == owner) return true;
        if (!choice.HistoryEmitted)
        {
            MarkUnobservedChoice(choice.PlayerId);
            if (choice.PrivateOwner is { } originalOwner && choice.PlayerId == originalOwner)
            {
                choice.HistoryInvalid = true;
                ChoiceHistoryProblem(choice, "读取选择详情期间我方视角发生变化，未保存其它视角的私有候选或已选明细。");
            }
        }
        return false;
    }

    private static bool ValidOfferedChoice(ChoiceBuilder choice) => !choice.HistoryInvalid &&
        choice.Minimum >= 0 && choice.Maximum >= choice.Minimum && choice.Maximum <= choice.Options.Count &&
        choice.Options.Select(o => o.EntityId).Distinct().Count() == choice.Options.Count &&
        choice.Options.Select(o => o.Position).Order().SequenceEqual(Enumerable.Range(0, choice.Options.Count).Select(i => (int?)i));

    private void CaptureChoiceOptionEntity(ChoiceBuilder choice, ChoiceOptionSnapshot option)
    {
        if (_isSpectating || choice.PrivateOwner is null || choice.PrivateOwner != _localPlayerId || choice.PlayerId != choice.PrivateOwner ||
            !_entities.TryGetValue(option.EntityId, out var entity)) return;
        // The descriptor proves what was offered, but cannot bind unidentified residual
        // attributes to that identity. Missing internal identity is unknown, not a conflict.
        if (entity.CardId is null) return;
        if (IntTag(entity, "CONTROLLER") is { } controller && controller != choice.PlayerId ||
            entity.CardId is { } cardId && cardId != option.CardId)
        { choice.HistoryInvalid = true; ChoiceHistoryProblem(choice, "候选描述与当时实体属性范围冲突，未混合不同身份的属性。"); return; }
        var tags = entity.Tags.Where(pair => ChoiceVisibility.IsObservedOptionTag(pair.Key)).ToDictionary(StringComparer.Ordinal);
        if (option.ControllerId is { } owner) tags["CONTROLLER"] = owner.ToString(CultureInfo.InvariantCulture);
        choice.OptionEntities[option.EntityId] = new(option.EntityId, option.CardId, entity.Kind, tags.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private bool ProcessChosenEntity(string line, ChoiceBuilder choice)
    {
        if (!ValidateChoiceScope(choice)) return false;
        var match = ChoiceEntityPattern.Match(line);
        if (!match.Success) return false;
        var index = ParseId(match.Groups[1].Value);
        var reference = match.Groups[2].Value.Trim();
        var entityId = ResolveReference(reference);
        if (entityId is null || choice.ExpectedChosenCount is not { } expected || expected > MaximumChoiceOptions || index >= expected ||
            !choice.Options.Any(option => option.EntityId == entityId))
        { choice.HistoryInvalid = true; ChoiceHistoryProblem(choice, "已选明细包含未提供、越界或未确认的候选，未推断选中结果。"); return false; }
        var owner = DescriptorPlayerPattern.Match(reference);
        if (owner.Success && ParseId(owner.Groups[1].Value) != choice.PlayerId)
        { choice.HistoryInvalid = true; ChoiceHistoryProblem(choice, "已选候选的归属与选择玩家不匹配。"); return false; }
        var chosenCard = DescriptorCardPattern.Match(reference);
        if (chosenCard.Success && NullIfEmpty(chosenCard.Groups[1].Value) is { } card &&
            choice.Options.First(option => option.EntityId == entityId).CardId != card)
        { choice.HistoryInvalid = true; ChoiceHistoryProblem(choice, "已选描述与当时提供的候选身份冲突，未补写旧候选。"); return false; }
        if (choice.ChosenByPosition.TryGetValue(index, out var previous))
        {
            if (previous != entityId) { choice.HistoryInvalid = true; ChoiceHistoryProblem(choice, "重复已选位置与先前明细冲突，未覆盖历史。"); }
            return false;
        }
        if (choice.HistoryEmitted) return false;
        choice.ChosenByPosition[index] = entityId.Value;
        choice.ChosenAt = _eventSequence;
        if (choice.ChosenByPosition.Count == expected) CompleteChoiceHistory(choice);
        return Changed();
    }

    private void CompleteChoiceHistory(ChoiceBuilder choice)
    {
        if (choice.HistoryEmitted) return;
        choice.HistoryEmitted = true;
        if (choice.PrivateOwner is not { } owner || choice.PlayerId != owner)
        {
            MarkUnobservedChoice(choice.PlayerId);
            return;
        }
        if (_isSpectating || _localPlayerId != owner)
        { ChoiceHistoryProblem(choice, "已选明细不在当时确认的我方视角下，未保存私有选择历史。"); return; }
        var selected = choice.ChosenByPosition.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        var complete = ValidOfferedChoice(choice) && choice.Ready && choice.Chosen && choice.ExpectedChosenCount is { } expected &&
            expected >= choice.Minimum && expected <= choice.Maximum && selected.Length == expected &&
            selected.Distinct().Count() == selected.Length && choice.ChosenByPosition.Keys.Order().SequenceEqual(Enumerable.Range(0, expected)) &&
            choice.ChosenAt > choice.OfferedAt && choice.ChosenAt <= _eventSequence;
        if (!complete) { ChoiceHistoryProblem(choice, "已选标头与实际明细不完整，未把标头当作已选候选。"); return; }
        _choiceHistory.Enqueue(new(choice.Id, choice.PlayerId, choice.Type, choice.SourceId, choice.SourceCardId,
            choice.Minimum, choice.Maximum, choice.OfferedAt, choice.ChosenAt,
            choice.Options, choice.OptionEntities.Values.OrderBy(entity => entity.Id).ToArray(), selected, true));
        while (_choiceHistory.Count > MaximumChoiceHistoryEntries)
        {
            var removed = _choiceHistory.Dequeue();
            if (removed.PlayerId is { } player) _choiceHistoryTruncatedThrough[player] = removed.ChosenAtEventSequence;
        }
    }

    private void ChoiceHistoryProblem(ChoiceBuilder? choice, string reason, int? reportedPlayer = null)
    {
        if (choice is { HistoryInvalid: true, Ready: true }) { choice.Ready = false; _version++; }
        var owner = choice?.PrivateOwner ?? (!_isSpectating && (reportedPlayer is null || reportedPlayer == _localPlayerId) ? _localPlayerId : null);
        if (owner is not (1 or 2) || choice is not null && choice.PlayerId != owner) return;
        if (!_choiceHistoryWarnings.TryGetValue(owner.Value, out var warnings)) _choiceHistoryWarnings[owner.Value] = warnings = new(StringComparer.Ordinal);
        if (warnings.Add(reason)) _version++;
    }

    private ChoiceHistorySnapshot SnapshotChoiceHistory(bool hasPerspective)
    {
        var owner = hasPerspective && !_isSpectating ? _localPlayerId : null;
        var warnings = owner is { } player && _choiceHistoryWarnings.TryGetValue(player, out var recorded)
            ? new HashSet<string>(recorded, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        if (!owner.HasValue) warnings.Add("尚未确认非观战的我方视角，不提供私有选择历史。");
        if (_unscopedChoiceHistoryGap || owner is { } missingOwner && _unobservedPrivateChoicePlayers.Contains(missingOwner))
            warnings.Add("存在未在当时确认的我方视角下观察的选择，不能补回私有选择历史。");
        if (!_sawStart || _hasGap) warnings.Add("日志存在起点或输入缺口，选择历史不能声明完整。");
        var truncated = owner is { } id ? _choiceHistoryTruncatedThrough.GetValueOrDefault(id) : 0;
        if (truncated > 0) warnings.Add("较早选择已超出保留范围，不能跨截断推断多阶段关联。");
        if (_readingChosen && _currentChoice is { HistoryEmitted: false } pending && pending.PlayerId == owner && pending.PrivateOwner == owner)
            warnings.Add("已选明细仍在读取，尚未形成完整选择历史。");
        var entries = owner.HasValue ? _choiceHistory.Where(entry => entry.PlayerId == owner && entry.ChosenAtEventSequence <= _eventSequence).ToArray() : [];
        return new($"{_sessionId}-{_matchNumber:D4}", _version, _eventSequence, entries, warnings.Count == 0, truncated,
            warnings.Order(StringComparer.Ordinal).ToArray());
    }

    private bool IsIdentityVisible(Entity entity) => AttachmentVisibility.CanObserve(IdentityObservation(entity).Audience, IdentityObserver);

    private void AddEvent(string kind, Entity entity, string? fromZone, string? toZone, string summary,
        string? previousVisibleCard = null, EventIdentityVisibility? previousProof = null)
    {
        var owner = IntTag(entity, "CONTROLLER");
        var proof = ObservedIdentityProof(entity);
        var card = proof != EventIdentityVisibility.Unobserved ? entity.CardId : previousVisibleCard;
        if (proof == EventIdentityVisibility.Unobserved && previousVisibleCard is not null) proof = previousProof ?? EventIdentityVisibility.Unobserved;
        if (proof == EventIdentityVisibility.Unobserved) card = null;
        int? privateOwner = proof switch { EventIdentityVisibility.PlayerOne => 1, EventIdentityVisibility.PlayerTwo => 2, _ => null };
        _recentEvents.Enqueue((new ObservedGameEvent(_eventSequence, kind, entity.Id, owner, card, fromZone, toZone, summary)
            { IdentityVisibility = proof }, privateOwner));
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
        ClearTurnHistory();
        _choices.Clear(); _currentChoice = null; _readingChosen = false; _recentEvents.Clear();
        _choiceHistory.Clear(); _choiceHistoryWarnings.Clear(); _choiceHistoryTruncatedThrough.Clear(); _lastOfferedChoiceSequence = 0;
        _unobservedPrivateChoicePlayers.Clear(); _unscopedChoiceHistoryGap = false;
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
        "40" => "ATTACHED", "49" => "ZONE", "50" => "CONTROLLER", "51" => "OWNER", "53" => "ENTITY_ID", "202" => "CARDTYPE",
        "199" => "CLASS", "200" => "CARDRACE", "1635" => "SPELL_SCHOOL", "204" => "STATE", "263" => "ZONE_POSITION", "292" => "ARMOR", "305" => "MULLIGAN_STATE",
        "176" => "MAXRESOURCES", "187" => "DURABILITY", "188" => "SILENCED", "189" => "WINDFURY",
        "190" => "TAUNT", "191" => "STEALTH", "192" => "SPELLPOWER", "194" => "DIVINE_SHIELD", "197" => "CHARGE",
        "219" => "SECRET", "462" => "QUEST",
        "227" => "CANT_ATTACK", "240" => "IMMUNE", "260" => "FROZEN", "271" => "NUM_TURNS_IN_PLAY",
        "295" => "TEMP_RESOURCES", "297" => "NUM_ATTACKS_THIS_TURN", "313" => "CREATOR", "385" => "DISPLAYED_CREATOR",
        "311" => "CANT_BE_TARGETED_BY_SPELLS", "332" => "CANT_BE_TARGETED_BY_HERO_POWERS", "1211" => "ELUSIVE",
        "435" => "TRANSFORMED_FROM_CARD", "448" => "UNTOUCHABLE", "777" => "HERO_POWER_DISABLED",
        "791" => "RUSH", "1518" => "DORMANT", "1565" => "COPIED_FROM_ENTITY_ID", "1646" => "HERO_POWER_ENTITY",
        "4572" => "CRAFTABLE_FIRST_HALF_DBID", "4573" => "CRAFTABLE_SECOND_HALF_DBID",
        "534" => "QUEST_PROGRESS", "535" => "QUEST_PROGRESS_TOTAL", "1192" => "SIDE_QUEST",
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
