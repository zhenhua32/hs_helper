using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace HsHelper.Core;

/// <summary>Traits frozen at an actual PLAY block while the source was still in hand.</summary>
public sealed record TurnPlaySnapshot(long Sequence, EntitySnapshot AtPlay);

public sealed record TurnHistoryEntry
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int TurnNumber { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int PlayerId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long StartedAtEventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? EndedAtEventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool HasStartBoundary { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsComplete { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<TurnPlaySnapshot> Plays { get; }

    public TurnHistoryEntry(int turnNumber, int playerId, long startedAtEventSequence, long? endedAtEventSequence,
        bool hasStartBoundary, bool isComplete, IReadOnlyList<TurnPlaySnapshot> plays)
    {
        ArgumentNullException.ThrowIfNull(plays);
        TurnNumber = turnNumber; PlayerId = playerId; StartedAtEventSequence = startedAtEventSequence;
        EndedAtEventSequence = endedAtEventSequence; HasStartBoundary = hasStartBoundary; IsComplete = isComplete;
        Plays = Array.AsReadOnly(plays.Select(play => play is null ? null! : play with
        { AtPlay = play.AtPlay is null ? null! : play.AtPlay with { Tags = play.AtPlay.Tags?.ToFrozenDictionary(StringComparer.Ordinal)! } }).ToArray());
    }
}

public sealed record TurnHistorySnapshot
{
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string MatchId { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long StateVersion { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long EventSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<TurnHistoryEntry> Turns { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool IsComplete { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long TruncatedThroughSequence { get; }
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.Never)] public IReadOnlyList<string> Warnings { get; }

    public TurnHistorySnapshot(string matchId, long stateVersion, long eventSequence,
        IReadOnlyList<TurnHistoryEntry> turns, bool isComplete, long truncatedThroughSequence, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(turns); ArgumentNullException.ThrowIfNull(warnings);
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence;
        Turns = Array.AsReadOnly(turns.ToArray()); IsComplete = isComplete;
        TruncatedThroughSequence = truncatedThroughSequence; Warnings = Array.AsReadOnly(warnings.ToArray());
    }
}

public sealed partial class PowerLogStateEngine
{
    private sealed class TurnWindow(int number, int player, long startedAt, bool hasStart)
    {
        public int Number { get; } = number;
        public int Player { get; } = player;
        public long StartedAt { get; } = startedAt;
        public bool HasStart { get; } = hasStart;
        public long? EndedAt { get; set; }
        public bool HasEnd { get; set; }
        public bool MissingPlays { get; set; }
        public List<TurnPlaySnapshot> Plays { get; } = [];
    }

    private readonly List<TurnWindow> _turnWindows = [];
    private readonly Stack<(string Type, int? Entity)> _turnBlocks = [];
    private TurnWindow? _turnWindow;
    private int? _turnNumberSeen;
    private long _turnStartSeenAt;
    private bool _turnOpeningSeen;
    private bool _turnHistoryGap;
    private long _turnTruncatedThrough;
    private const int MaximumTurnWindows = 64;
    private const int MaximumTurnPlays = 256;
    private static readonly HashSet<string> AtPlayTags = ["CARDTYPE", "CARDRACE", "SPELL_SCHOOL", "CONTROLLER", "ZONE"];

    private int? CurrentTurnPlayer()
    {
        var active = _entities.Values.Where(entity => entity.Kind == EntityKind.Player && IntTag(entity, "CURRENT_PLAYER") == 1)
            .Select(entity => IntTag(entity, "PLAYER_ID")).Take(2).ToArray();
        return active.Length == 1 && active[0] is 1 or 2 ? active[0] : null;
    }

    private void ObserveTurnTag(Entity entity, string tag, string value)
    {
        if (entity.Id != _gameEntity) return;
        if (tag == "TURN")
        {
            var number = IntTag(entity, "TURN");
            if (number == 0 && _turnNumberSeen is null) return;
            if (number is null or < 1) { _turnHistoryGap = true; return; }
            if (_turnNumberSeen == number) return;
            if (_turnNumberSeen is { } previous ? number != previous + 1 : number != 1) _turnHistoryGap = true;
            if (_turnNumberSeen.HasValue && _turnWindow is null) _turnHistoryGap = true;
            if (_turnWindow is { EndedAt: null } unclosed) { unclosed.EndedAt = _eventSequence; unclosed.MissingPlays = true; }
            _turnWindow = null; _turnNumberSeen = number; _turnOpeningSeen = false; _turnStartSeenAt = _eventSequence;
            return;
        }
        if (tag != "STEP") return;
        if (value is "MAIN_BEGIN" or "MAIN_READY" or "MAIN_RESOURCE" or "MAIN_DRAW" or "MAIN_START" or "MAIN_START_TRIGGERS")
        {
            if (!_turnOpeningSeen) _turnStartSeenAt = _eventSequence;
            _turnOpeningSeen = true;
        }
        if (value == "MAIN_ACTION") EnsureTurnWindow();
        if (value is "MAIN_END" or "MAIN_CLEANUP" or "MAIN_NEXT")
        {
            if (_turnWindow is null) EnsureTurnWindow();
            if (_turnWindow is { EndedAt: null } current && current.Number == _turnNumberSeen)
            {
                current.EndedAt = _eventSequence;
                current.HasEnd = CurrentTurnPlayer() == current.Player;
                if (!current.HasEnd) current.MissingPlays = true;
            }
        }
    }

    private void EnsureTurnWindow()
    {
        if (_turnNumberSeen is not { } number || CurrentTurnPlayer() is not { } player) return;
        if (_turnWindow is { } current)
        {
            if (current.Number != number || current.Player != player || current.EndedAt.HasValue) current.MissingPlays = true;
            return;
        }
        _turnWindow = new(number, player, _turnStartSeenAt > 0 ? _turnStartSeenAt : _eventSequence, _turnOpeningSeen);
        _turnWindows.Add(_turnWindow);
        while (_turnWindows.Count > MaximumTurnWindows)
        {
            var old = _turnWindows[0]; _turnWindows.RemoveAt(0);
            _turnTruncatedThrough = old.EndedAt ?? old.StartedAt;
        }
    }

    private void ObserveTurnBlockStart(string content)
    {
        var block = Pattern(@"^BLOCK_START\s+BlockType=(\w+)\s+Entity=(.+?)(?:\s+EffectCardId=|$)").Match(content);
        var type = block.Success ? block.Groups[1].Value : "UNKNOWN";
        var sourceId = block.Success ? ResolveReference(block.Groups[2].Value.Trim()) : null;
        _turnBlocks.Push((type, sourceId));
        if (type != "PLAY") return;
        EnsureTurnWindow();
        if (_turnWindow is not { EndedAt: null } current) { _turnHistoryGap = true; return; }
        if (sourceId is not { } id || !_entities.TryGetValue(id, out var source) || source.Kind != EntityKind.Card)
        { current.MissingPlays = true; return; }
        var descriptor = block.Groups[2].Value.Trim();
        var declaredCard = DescriptorCardPattern.Match(descriptor);
        var declaredOwner = DescriptorPlayerPattern.Match(descriptor);
        var declaredZone = Pattern(@"\bzone=(\w+)").Match(descriptor);
        if (declaredCard.Success && NullIfEmpty(declaredCard.Groups[1].Value) is { } declared && declared != source.CardId ||
            declaredOwner.Success && ParseId(declaredOwner.Groups[1].Value) != IntTag(source, "CONTROLLER") ||
            declaredZone.Success && declaredZone.Groups[1].Value != source.Tags.GetValueOrDefault("ZONE"))
        { current.MissingPlays = true; return; }
        if (source.Tags.GetValueOrDefault("ZONE") is "PLAY" or "GRAVEYARD" or "DECK" or "SETASIDE") return;
        if (source.Tags.GetValueOrDefault("ZONE") != "HAND" || IntTag(source, "CONTROLLER") != current.Player || CurrentTurnPlayer() != current.Player)
        { current.MissingPlays = true; return; }
        if (current.Plays.Count >= MaximumTurnPlays) { current.MissingPlays = true; return; }
        // A known source identity at this boundary owns these traits. No later SHOW/CHANGE or catalog lookup fills it.
        var knownIdentity = ConfirmedChoiceOwner() == current.Player && !source.Hidden && !string.IsNullOrWhiteSpace(source.CardId);
        var tags = source.Tags.Where(pair => AtPlayTags.Contains(pair.Key) &&
            (knownIdentity || pair.Key is "CONTROLLER" or "ZONE")).ToFrozenDictionary(StringComparer.Ordinal);
        current.Plays.Add(new(_eventSequence, new(source.Id, knownIdentity ? source.CardId : null, source.Kind, tags)));
    }

    private void ObserveTurnZone(Entity entity, string? from, string to)
    {
        if (from != "HAND" || to is not ("PLAY" or "SECRET")) return;
        // Explicit non-PLAY trigger blocks can summon from hand. They do not establish a played card.
        if (_turnBlocks.Count > 0 && _turnBlocks.Peek().Type != "UNKNOWN") return;
        EnsureTurnWindow();
        if (_turnWindow is { } current) current.MissingPlays = true;
        else _turnHistoryGap = true;
    }

    private TurnHistorySnapshot SnapshotTurnHistory(bool hasPerspective)
    {
        var warnings = new List<string>();
        if (!hasPerspective || _isSpectating) warnings.Add("未确认非观战我方视角，不提供回合出牌历史。");
        if (!_sawStart || _hasGap || _turnHistoryGap) warnings.Add("回合起点、连续编号或日志记录不完整，不能补写之前回合。");
        if (_turnWindows.Any(window => window.MissingPlays)) warnings.Add("实际出牌块或当前回合归属存在缺口，不能用旧出牌记录替代未记录的重打。");
        if (_turnWindows.Any(window => !window.HasStart)) warnings.Add("未观察到回合开始阶段，回合窗口不声明完整。");
        if (_turnTruncatedThrough > 0) warnings.Add("较早回合已超出保留范围，不能跨截断恢复上个己方回合。");
        var turns = hasPerspective && !_isSpectating ? _turnWindows.Select(window => new TurnHistoryEntry(window.Number,
            window.Player, window.StartedAt, window.EndedAt, window.HasStart,
            window.HasStart && window.HasEnd && !window.MissingPlays, window.Plays.Select(play =>
                play.AtPlay.ControllerId == _localPlayerId ? play : play with
                {
                    AtPlay = play.AtPlay with { CardId = null, Tags = play.AtPlay.Tags.Where(pair => pair.Key is "CONTROLLER" or "ZONE").ToFrozenDictionary(StringComparer.Ordinal) }
                }).ToArray())).ToArray() : [];
        return new($"{_sessionId}-{_matchNumber:D4}", _version, _eventSequence, turns, warnings.Count == 0,
            _turnTruncatedThrough, warnings);
    }

    private void ClearTurnHistory()
    {
        _turnWindows.Clear(); _turnBlocks.Clear(); _turnWindow = null; _turnNumberSeen = null;
        _turnStartSeenAt = 0; _turnOpeningSeen = false; _turnHistoryGap = false; _turnTruncatedThrough = 0;
    }
}
