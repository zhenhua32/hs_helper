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
    private static readonly HashSet<string> HiddenSafeTags = ["ZONE", "CONTROLLER", "ZONE_POSITION", "ENTITY_ID"];

    private readonly Dictionary<int, Entity> _entities = [];
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    // Names are lookup-only aliases from game metadata; snapshots never contain them.
    private readonly Dictionary<string, int?> _playerNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _pendingPlayerNames = new(StringComparer.Ordinal);
    private readonly string _sessionId;
    private int _matchNumber;
    private int? _currentEntity;
    private int? _gameEntity;
    private int _blockCount;
    private bool _sawStart;
    private bool _taskComplete;
    private bool _hasGap;
    private bool _choicePending;
    private long _version;
    private long _eventSequence;
    private int? _localPlayerId;

    public PowerLogStateEngine(int? localPlayerId = null, string? sessionId = null)
    {
        if (localPlayerId is <= 0) throw new ArgumentOutOfRangeException(nameof(localPlayerId));
        _localPlayerId = localPlayerId;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    public void SetLocalPlayerId(int? playerId)
    {
        if (playerId is <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (_localPlayerId == playerId) return;
        _localPlayerId = playerId;
        _version++;
    }

    /// <summary>Clears state at a file/session boundary. The next records require a new CREATE_GAME.</summary>
    public void ResetSession()
    {
        ClearGame();
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
        try { return ProcessLineCore(line); }
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
        if (line.Contains("GameState.DebugPrintGame", StringComparison.Ordinal))
        {
            if (line.Contains("BuildNumber=", StringComparison.Ordinal)) _pendingPlayerNames.Clear();
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
        if (line.Contains("GameState.DebugPrintEntityChoices", StringComparison.Ordinal))
        {
            _eventSequence++;
            _choicePending = true;
            _taskComplete = false;
            _version++;
            return true;
        }
        if (line.Contains("GameState.DebugPrintEntitiesChosen", StringComparison.Ordinal))
        {
            _eventSequence++;
            _choicePending = false;
            _taskComplete = false;
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
            ClearGame();
            foreach (var name in names) _playerNames[name.Key] = name.Value;
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
            entity.Hidden = true;
            entity.CardId = null;
            foreach (var tag in entity.Tags.Keys.Where(t => !HiddenSafeTags.Contains(t)).ToArray())
                entity.Tags.Remove(tag);
            ApplyTag(entity, match.Groups[2].Value, match.Groups[3].Value);
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
        var hasPerspective = _localPlayerId.HasValue && players.Any(p => IntTag(p, "PLAYER_ID") == _localPlayerId);
        if (!hasPerspective) warnings.Add("我方玩家身份尚未确认");
        if (_gameEntity is null || players.Length < 2) warnings.Add("开局实体信息尚不完整");
        if (_choicePending) warnings.Add("当前选择详情尚未解析");
        if (!_taskComplete) warnings.Add("尚未观察到当前任务列表结算完成");
        var phase = GetPhase();
        var isComplete = _sawStart && !_hasGap && _gameEntity.HasValue && players.Length == 2 && hasPerspective;
        var step = _gameEntity is { } currentGame ? _entities[currentGame].Tags.GetValueOrDefault("STEP") : null;
        var decisionPhase = phase is GamePhase.Mulligan or GamePhase.Complete || step == "MAIN_ACTION";
        var stable = isComplete && _blockCount == 0 && _taskComplete && !_choicePending && decisionPhase;
        var entities = _entities.Values.ToFrozenDictionary(e => e.Id, e => ToSnapshot(e, hasPerspective));
        var activePlayers = players.Where(e => IntTag(e, "CURRENT_PLAYER") == 1).ToArray();
        return new GameSnapshot($"{_sessionId}-{_matchNumber:D4}", _version, _eventSequence,
            _gameEntity is { } gameId ? IntTag(_entities[gameId], "TURN") : null,
            activePlayers.Length == 1 ? IntTag(activePlayers[0], "PLAYER_ID") : null,
            hasPerspective ? _localPlayerId : null, phase, entities,
            new SnapshotQuality(_sawStart, isComplete, stable, hasPerspective, _blockCount,
                Array.AsReadOnly(warnings.ToArray())));
    }

    private EntitySnapshot ToSnapshot(Entity entity, bool hasPerspective)
    {
        var zone = entity.Tags.GetValueOrDefault("ZONE");
        var own = hasPerspective && IntTag(entity, "CONTROLLER") == _localPlayerId;
        var visible = entity.Kind is EntityKind.Game or EntityKind.Player ||
            (!entity.Hidden && (zone is "PLAY" or "GRAVEYARD" || (own && zone is "HAND" or "SECRET")));
        var tags = visible ? entity.Tags : entity.Tags.Where(t => HiddenSafeTags.Contains(t.Key));
        return new EntitySnapshot(entity.Id, visible ? entity.CardId : null, entity.Kind,
            tags.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private GamePhase GetPhase()
    {
        if (_gameEntity is not { } gameId) return GamePhase.Unknown;
        var entity = _entities[gameId];
        var state = entity.Tags.GetValueOrDefault("STATE");
        var step = entity.Tags.GetValueOrDefault("STEP");
        if (state == "COMPLETE" || step is "FINAL_GAMEOVER" or "FINAL_WRAPUP") return GamePhase.Complete;
        if (_choicePending) return GamePhase.Choice;
        if (step == "BEGIN_MULLIGAN") return GamePhase.Mulligan;
        if (step?.StartsWith("MAIN_", StringComparison.Ordinal) == true) return GamePhase.Main;
        return _sawStart ? GamePhase.Starting : GamePhase.Unknown;
    }

    private bool ApplyTag(Entity entity, string rawTag, string rawValue)
    {
        var tag = NormalizeTag(rawTag);
        var value = NormalizeValue(tag, rawValue.Trim());
        if (entity.Tags.GetValueOrDefault(tag) == value) return false;
        entity.Tags[tag] = value;
        return true;
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
            return _entities.Values.SingleOrDefault(e => e.Kind == EntityKind.Player && IntTag(e, "PLAYER_ID") == playerId)?.Id;
        var match = ReferencePattern.Match(reference);
        if (match.Success) return ParseId(match.Groups[1].Value);
        // Current client metadata can initially label one player UNKNOWN HUMAN PLAYER and then
        // use that player's resolved name in TAG_CHANGE. Only bind this one unassigned placeholder;
        // other ambiguous/missing references still invalidate completeness.
        if (allowPlayerPlaceholder && !reference.StartsWith("UNKNOWN ENTITY", StringComparison.Ordinal) &&
            !_playerNames.ContainsKey(reference) && _playerNames.TryGetValue("UNKNOWN HUMAN PLAYER", out var placeholder) &&
            placeholder is { } placeholderId && !_playerNames.Any(x => x.Key != "UNKNOWN HUMAN PLAYER" && x.Value == placeholderId))
        {
            var player = _entities.Values.SingleOrDefault(e => e.Kind == EntityKind.Player && IntTag(e, "PLAYER_ID") == placeholderId);
            if (player is not null)
            {
                _playerNames[reference] = placeholderId;
                return player.Id;
            }
        }
        return null;
    }

    private void ClearGame()
    {
        _entities.Clear(); _warnings.Clear();
        _playerNames.Clear(); _pendingPlayerNames.Clear();
        _currentEntity = null; _gameEntity = null;
        _blockCount = 0; _sawStart = false; _taskComplete = false; _hasGap = false; _choicePending = false;
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
        "17" => "PLAYSTATE", "19" => "STEP", "20" => "TURN", "23" => "CURRENT_PLAYER",
        "25" => "RESOURCES_USED", "26" => "RESOURCES", "27" => "HERO_ENTITY", "30" => "PLAYER_ID",
        "43" => "EXHAUSTED", "44" => "DAMAGE", "45" => "HEALTH", "47" => "ATK", "48" => "COST",
        "49" => "ZONE", "50" => "CONTROLLER", "53" => "ENTITY_ID", "202" => "CARDTYPE",
        "204" => "STATE", "263" => "ZONE_POSITION", "292" => "ARMOR", "305" => "MULLIGAN_STATE",
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
