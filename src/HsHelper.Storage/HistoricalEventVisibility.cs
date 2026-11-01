using System.Text.Json;
using HsHelper.Core;

namespace HsHelper.Storage;

/// <summary>Derives display events without rewriting immutable stored evidence or inferring past visibility from a later reveal.</summary>
public static class HistoricalEventVisibility
{
    public static IReadOnlyList<SessionEvent> Project(GameSnapshot snapshot, IReadOnlyList<SessionEvent> events)
    {
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        var evidence = events.Select(ReadEvidence).ToArray();
        // Delta rows may omit CARDTYPE/ATTACHED, and the current observer view may
        // have removed the source. Recognizing its category does not establish
        // visibility for any of its past identities.
        var attachments = snapshot.Entities.Values.Where(AttachmentVisibility.IsAttachment).Select(entity => entity.Id).ToHashSet();
        for (var index = 0; index < events.Count; index++)
            if (events[index].EventSequence <= snapshot.EventSequence && evidence[index].Attachment && events[index].EntityId is { } id) attachments.Add(id);
        return Array.AsReadOnly(events.Select((entry, index) =>
        {
            var permitted = evidence[index].Proof switch
            {
                EventIdentityVisibility.Public => true,
                EventIdentityVisibility.PlayerOne => local == 1,
                EventIdentityVisibility.PlayerTwo => local == 2,
                null => entry.CardId is null && entry.EntityId is null || entry.EntityId is { } id &&
                    !attachments.Contains(id) && AttachmentVisibility.HasKnownOrdinaryCategory(snapshot, id),
                _ => false
            };
            return permitted ? entry : entry with { CardId = null, Summary = "身份未公开的实体事件", DataJson = "{}" };
        }).ToArray());
    }

    private static (EventIdentityVisibility? Proof, bool Attachment) ReadEvidence(SessionEvent entry)
    {
        EventIdentityVisibility? proof = null;
        var attachment = false;
        try
        {
            using var data = JsonDocument.Parse(entry.DataJson);
            if (data.RootElement.ValueKind != JsonValueKind.Object) return (proof, attachment);
            if (data.RootElement.TryGetProperty("IdentityVisibility", out var field))
            {
                proof = EventIdentityVisibility.Unobserved;
                if (field.ValueKind == JsonValueKind.String && Enum.TryParse<EventIdentityVisibility>(field.GetString(), out var parsed) && Enum.IsDefined(parsed)) proof = parsed;
                else if (field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) && Enum.IsDefined(typeof(EventIdentityVisibility), number)) proof = (EventIdentityVisibility)number;
            }
            foreach (var key in new[] { "Tags", "ChangedTags" })
                if (data.RootElement.TryGetProperty(key, out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    foreach (var tag in new[] { "CARDTYPE", "202" })
                        attachment |= tags.TryGetProperty(tag, out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "ENCHANTMENT" or "6";
                    foreach (var tag in new[] { "ATTACHED", "40" })
                        attachment |= tags.TryGetProperty(tag, out var attached) && attached.ValueKind == JsonValueKind.String && attached.GetString() is not (null or "0");
                }
        }
        catch (JsonException) { /* A known attachment without valid proof remains masked by the caller. */ }
        return (proof, attachment);
    }
}
