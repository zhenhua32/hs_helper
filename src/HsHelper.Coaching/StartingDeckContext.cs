using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;
using HsHelper.Storage;

namespace HsHelper.Coaching;

/// <summary>Creates a frame-bound reference only from a host's original saved session/source deck.</summary>
public static class StartingDeckContext
{
    public static StartingDeckBinding? FromSavedDeck(GameSnapshot snapshot, string? fixedBuild,
        DeckVersion? version, string? expectedVersionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (version is null || expectedVersionId is null || version.Id != expectedVersionId) return null;
        var binding = StartingDeckBinding.FreezeForSnapshot(snapshot, fixedBuild,
            new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, fixedBuild ?? "unknown", version.Id, version.Definition));
        if (binding is null) return null;
        // A saved identity must describe this exact deck, rather than a later editor draft.
        // Its creation build can differ from the original game's fixed analysis build.
        try
        {
            return DeckCode.Encode(DeckCode.Decode(version.Code)) == DeckCode.Encode(binding.Definition) ? binding : null;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }
}
