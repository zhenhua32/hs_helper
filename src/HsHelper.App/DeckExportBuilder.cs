using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Storage;

namespace HsHelper.App;

public enum DeckExportFormat { Json, DeckCode }

public sealed record DeckExportDocument(int SchemaVersion, DateTimeOffset ExportedAt, string Name,
    string Code, DeckDefinition Deck, string? DeckVersionId, string? CardBuild,
    DateTimeOffset? DeckVersionCreatedAt, string? PreviousVersionId)
{
    public bool IsSavedVersion => DeckVersionId is not null;
}

/// <summary>Captures the editor content and its actual saved provenance before any file dialog.</summary>
public static class DeckExportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DeckExportDocument Create(string input, string draftName, DeckVersion? selected,
        DateTimeOffset exportedAt)
    {
        var code = DeckCode.Encode(DeckCode.Decode(input));
        // Decode again to canonicalize ordering and own every nested, read-only collection.
        var deck = DeckCode.Decode(code);
        var source = selected is not null && !string.IsNullOrWhiteSpace(selected.Id) &&
            !string.IsNullOrWhiteSpace(selected.CardBuild) && Matches(selected, code) ? selected : null;
        return new(1, exportedAt.ToUniversalTime(), source?.Name ??
            (string.IsNullOrWhiteSpace(draftName) ? "未命名套牌" : draftName.Trim()), code, deck,
            source?.Id, source?.CardBuild, source?.CreatedAt, source?.PreviousVersionId);
    }

    private static bool Matches(DeckVersion source, string code)
    {
        try
        {
            return DeckCode.Encode(DeckCode.Decode(source.Code)) == code &&
                DeckCode.Encode(source.Definition) == code;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public static string ToJson(DeckExportDocument document) => JsonSerializer.Serialize(document, JsonOptions);
    public static string ToCodeText(DeckExportDocument document) => document.Code + Environment.NewLine;
}
