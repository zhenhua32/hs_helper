using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Storage;

public sealed record DeckVersion(string Id, string Name, string Code, string CardBuild,
    DateTimeOffset CreatedAt, string? PreviousVersionId, DeckDefinition Definition);

public sealed record SessionBinding(string? DeckVersionId, string CardBuild, int? LocalPlayerId,
    string Format = "STANDARD", string Region = "CN", string? PlayerClass = null);

public sealed record GameSession(string Id, string? DeckVersionId, string CardBuild, int? LocalPlayerId,
    string Format, string Region, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, DateTimeOffset? EndedAt,
    int? TurnCount, string? Outcome, bool? WentFirst, string? PlayerClass, string? OpponentClass,
    bool IsComplete, long LastEventSequence, int SnapshotCount, string? GameType = null);

public sealed record SessionEvent(long EventSequence, int EventIndex, long SourceLineNumber, string Kind,
    int? EntityId, string? CardId, string? FromZone, string? ToZone, string Summary, string DataJson);

public sealed record DecisionPoint(long Id, string SessionId, long EventSequence, long StateVersion,
    int? Turn, GamePhase Phase, int? ActivePlayerId, bool IsStable, bool IsComplete,
    bool IsDecisionPoint, DateTimeOffset RecordedAt);

public sealed record SessionFilter(string? CardBuild = null, string? DeckVersionId = null,
    string? OpponentClass = null, bool? WentFirst = null, string? Format = null,
    DateTimeOffset? Since = null, DateTimeOffset? Until = null, int Limit = 100,
    string? PlayerClass = null, string? Region = null, string? GameType = null,
    DateTimeOffset? EndedBefore = null, string? ExcludeSessionId = null);

/// <summary>One bounded history page. HasMore comes from a single extra row, not an unbounded load.</summary>
public sealed record SessionPage(IReadOnlyList<GameSession> Sessions, int PageNumber, int PageSize, bool HasMore)
{
    public bool HasPrevious => PageNumber > 1;
}

public sealed record ClassMatchupStats(string OpponentClass, int Games, int Wins, int Losses, int Ties,
    int Unknown, double? WinRate);

public sealed record PersonalStats(int Games, int Wins, int Losses, int Ties, int Unknown,
    double? WinRate, IReadOnlyList<ClassMatchupStats> Matchups);

public sealed record CleanupResult(int SessionsDeleted, int EventsDeleted, int SnapshotsDeleted);

public sealed record HistoricalContext(string SessionId, string CardBuild, string? DeckVersionId,
    DeckVersion? Deck, GameSnapshot Snapshot, IReadOnlyList<SessionEvent> RecentEvents);

internal sealed record PendingObservation(GameSnapshot Snapshot, long SourceLineNumber,
    DateTimeOffset ObservedAt, IReadOnlyList<SessionEvent> Events, bool SaveSnapshot);
