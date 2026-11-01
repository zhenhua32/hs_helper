namespace HsHelper.Core;

public sealed partial class PowerLogStateEngine
{
    // A header is one atomic observation. Apply its explicit pose before assigning/publishing the
    // identity; a stale public zone or our old ownership must not authorize a private new CardID.
    private void ApplyIdentityHeaderPose(Entity entity, string descriptor)
    {
        var oldZone = entity.Tags.GetValueOrDefault("ZONE");
        var oldController = entity.Tags.GetValueOrDefault("CONTROLLER");
        ApplyIdentityHeaderField(entity, descriptor, "player", "CONTROLLER", numeric: true);
        ApplyIdentityHeaderField(entity, descriptor, "zone", "ZONE", numeric: false);
        var positionDeclared = Pattern(@"\bzonePos=").IsMatch(descriptor);
        if (positionDeclared) ApplyIdentityHeaderField(entity, descriptor, "zonePos", "ZONE_POSITION", numeric: true);
        else if (oldZone != entity.Tags.GetValueOrDefault("ZONE") || oldController != entity.Tags.GetValueOrDefault("CONTROLLER"))
            ForgetIdentityPose(entity, "ZONE_POSITION");
    }

    private void ApplyIdentityHeaderField(Entity entity, string descriptor, string field, string tag, bool numeric)
    {
        var fields = Pattern(@"\b" + field + @"=([^\s\]]*)").Matches(descriptor);
        if (fields.Count == 0) return;
        if (fields.Count != 1 || fields[0].Groups[1].Value.Length == 0 ||
            numeric && (!int.TryParse(fields[0].Groups[1].Value, out var number) || number < 0 || tag == "CONTROLLER" && number == 0))
        { ForgetIdentityPose(entity, tag); return; }
        ApplyTag(entity, tag, fields[0].Groups[1].Value, recordEvent: false);
    }

    private void ForgetIdentityPose(Entity entity, string tag)
    {
        if (!entity.Tags.Remove(tag)) return;
        entity.Revision++;
        if (tag == "ZONE_POSITION") return;
        _publicHandMemory.Invalidate(entity.Id);
        InvalidateHistoryEntity(entity.Id);
    }
}
