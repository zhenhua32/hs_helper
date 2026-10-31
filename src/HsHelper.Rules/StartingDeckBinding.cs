using System.Text.RegularExpressions;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>A host-provided original deck reference for one fixed frame; not evidence of the current sideboard pool.</summary>
public sealed record StartingDeckBinding(string MatchId, long StateVersion, long EventSequence,
    string CardBuild, string DeckVersionId, DeckDefinition Definition)
{
    /// <summary>
    /// Checks frame consistency and returns an owned copy, or null without echoing rejected contents.
    /// The host must obtain the original definition/version from its recorded session or established source binding.
    /// </summary>
    public static StartingDeckBinding? FreezeForSnapshot(GameSnapshot snapshot, string? fixedBuild, StartingDeckBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (binding is null || string.IsNullOrWhiteSpace(snapshot.MatchId) || snapshot.StateVersion < 0 || snapshot.EventSequence < 0 ||
            binding.MatchId != snapshot.MatchId || binding.StateVersion != snapshot.StateVersion || binding.EventSequence != snapshot.EventSequence ||
            !KnownBuild(fixedBuild) || binding.CardBuild != fixedBuild ||
            snapshot.ClientBuild is { } observed && observed != fixedBuild || !Identifier(binding.DeckVersionId)) return null;
        if (snapshot.DeckTracking is { } tracking && tracking.MatchId == snapshot.MatchId && tracking.StateVersion == snapshot.StateVersion &&
            (tracking.CardBuild != fixedBuild || tracking.DeckVersion != binding.DeckVersionId)) return null;
        var definition = FreezeDefinition(binding.Definition);
        return definition is null ? null : binding with { Definition = definition };
    }

    internal static bool SameDefinition(DeckDefinition first, DeckDefinition second)
    {
        var left = FreezeDefinition(first); var right = FreezeDefinition(second);
        return left is not null && right is not null && left.Format == right.Format && left.Heroes.SequenceEqual(right.Heroes) &&
            left.Cards.OrderBy(card => card.DbfId).SequenceEqual(right.Cards.OrderBy(card => card.DbfId)) &&
            left.Sideboards.Count == right.Sideboards.Count && left.Sideboards.OrderBy(board => board.OwnerDbfId)
                .Zip(right.Sideboards.OrderBy(board => board.OwnerDbfId)).All(pair => pair.First.OwnerDbfId == pair.Second.OwnerDbfId &&
                    pair.First.Cards.OrderBy(card => card.DbfId).SequenceEqual(pair.Second.Cards.OrderBy(card => card.DbfId)));
    }

    private static DeckDefinition? FreezeDefinition(DeckDefinition? definition)
    {
        if (definition?.Heroes is null || definition.Cards is null || definition.Sideboards is null ||
            definition.Heroes.Count != 1 || definition.Cards.Count > 1000 || definition.Sideboards.Count > 1000) return null;
        var heroes = definition.Heroes.ToArray(); var main = definition.Cards.ToArray(); var boards = definition.Sideboards.ToArray();
        if (main.Any(card => card is null) || boards.Any(board => board is null || board.Cards is null || board.Cards.Count > 1000)) return null;
        if (boards.Sum(board => (long)board.Cards.Count) > 1000) return null;
        var sideboards = new List<DeckSideboard>();
        foreach (var board in boards)
        {
            var cards = board.Cards.ToArray();
            if (cards.Any(card => card is null)) return null;
            sideboards.Add(board with { Cards = Array.AsReadOnly(cards.Select(card => card with { }).ToArray()) });
        }
        var frozen = definition with
        {
            Heroes = Array.AsReadOnly(heroes), Cards = Array.AsReadOnly(main.Select(card => card with { }).ToArray()),
            Sideboards = sideboards.AsReadOnly()
        };
        try { _ = DeckCode.Encode(frozen); }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException) { return null; }
        return frozen;
    }

    private static bool KnownBuild(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9]{1,12}\z", RegexOptions.CultureInvariant);
    private static bool Identifier(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}\z", RegexOptions.CultureInvariant) && !value.Contains("://", StringComparison.Ordinal);
}
