using System.Globalization;
using System.Text.RegularExpressions;

namespace HsHelper.Cards;

/// <summary>Observed text composition only. No minion statistics, costs or executable effects are projected.</summary>
public sealed record AssembledTextResolution(bool IsObserved, bool IsOffer, bool IsResolved,
    string? EffectiveText, string? TriggerCardId, string? EffectCardId, string? OfferedHalf,
    string? OfferedHalfText, IReadOnlyList<string> Unknowns)
{
    public string? BaseCardId { get; init; }
    public string? BaseCardName { get; init; }
    public int? FirstHalfDbfId { get; init; }
    public int? SecondHalfDbfId { get; init; }
}

/// <summary>
/// Build 255790's observed Assemble halves: 4572 is the first/trigger half, 4573 the second/effect half.
/// A marker of 1 identifies an offered entity, while a value greater than 1 names a donor DBF ID.
/// Primary evidence: HearthDb/Enums/Enums.cs (56a3a445ef8612816d71500bb5609c0114c35978),
/// and HDT AssembledHalves.cs / AssembledEffectCardIdTests.cs (7c0550fe8388bf45e0f3dbc5d2ff25ada2e1a3ae).
/// HDT's missing-tag GetTag convention is not evidence that a partially recorded half is zero;
/// when custom text is observed this resolver requires an explicit zero before retaining a base half.
/// </summary>
public static class AssembledCardText
{
    private const string First = "CRAFTABLE_FIRST_HALF_DBID";
    private const string Second = "CRAFTABLE_SECOND_HALF_DBID";
    private readonly record struct HalfTag(bool IsPresent, bool IsValid, int? Value);

    /// <summary>True also for malformed/contradictory tags, so callers never fall back to a native effect.</summary>
    public static bool RequiresCustomText(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var first = Read(tags, First, "4572"); var second = Read(tags, Second, "4573");
        return !first.IsValid || !second.IsValid || first.Value is > 0 || second.Value is > 0;
    }

    /// <summary>Only consistently parsed donor IDs; zero and offer markers are not database references.</summary>
    public static IReadOnlyList<int> ReferencedDbfIds(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return Array.AsReadOnly(new[] { Read(tags, First, "4572"), Read(tags, Second, "4573") }
            .Where(half => half.IsValid && half.Value is > 1).Select(half => half.Value!.Value).Distinct().ToArray());
    }

    public static AssembledTextResolution Resolve(string? build, string? baseCardId,
        IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, CardRecord> cards)
    {
        ArgumentNullException.ThrowIfNull(tags); ArgumentNullException.ThrowIfNull(cards);
        var first = Read(tags, First, "4572"); var second = Read(tags, Second, "4573");
        var observed = first.IsPresent || second.IsPresent;
        var offer = first.Value == 1 || second.Value == 1;
        var unknowns = new List<string>();
        string? text = null, triggerId = null, effectId = null, offeredHalf = null, offeredText = null, baseName = null;
        var resolved = false;
        if (build != "255790")
        { unknowns.Add("拼装半段文字仅审计了 build 255790，当前版本不能复用其标签语义。"); return Result(); }
        if (!first.IsValid || !second.IsValid)
        { unknowns.Add("拼装半段标签包含非法数值或命名/数字别名冲突，不能回退为原卡文字。"); return Result(); }
        if (string.IsNullOrWhiteSpace(baseCardId) || !cards.TryGetValue(baseCardId, out var baseCard) ||
            baseCard is null || baseCard.Id != baseCardId || baseCard.Text is null)
        { unknowns.Add("固定版本卡库未确认当前实体的基础身份和文字。"); return Result(); }
        baseName = string.IsNullOrWhiteSpace(baseCard.Name) ? null : baseCard.Name;
        if (offer)
        {
            if (first.Value == 1 && second.Value == 1)
            { unknowns.Add("同一候选同时标记两个未提供半段，不能确认所提供的半段。"); return Result(); }
            var provided = first.Value == 1 ? second : first;
            if (provided.Value is > 1 && provided.Value != baseCard.DbfId)
            { unknowns.Add("候选所提供半段的 DBF 标签与候选自身身份冲突。"); return Result(); }
            offeredHalf = first.Value == 1 ? "Effect" : "Trigger";
            if (!TryHalf(baseCard, offeredHalf == "Trigger" ? 0 : 1, out offeredText))
                unknowns.Add("候选固定文字缺少唯一的 & 分段边界，未猜测所提供的文字片段。");
            unknowns.Add("这是已提供的半段按钮，不是已拼装成品；主随从及替换后的完整文字尚未绑定。");
            return Result();
        }
        if (first.Value is null or 0 && second.Value is null or 0)
        {
            // Native text is not a replacement. Preserve its exact formatting and any separator.
            resolved = true; text = baseCard.Text; triggerId = baseCard.Id; effectId = baseCard.Id;
            return Result();
        }
        if (!first.Value.HasValue || !second.Value.HasValue)
        { unknowns.Add("已观察到替换半段，但另一半段标签缺失；缺失不能当作显式零来保留原半段。"); return Result(); }
        var trigger = CardForHalf(first.Value.Value, baseCard);
        var effect = CardForHalf(second.Value.Value, baseCard);
        if (trigger is null || effect is null) return Result();
        if (!TryHalf(trigger, 0, out var firstText) || !TryHalf(effect, 1, out var secondText))
        { unknowns.Add("基础卡或供体文字缺少唯一的 & 分段边界，未回退到原卡效果文字。"); return Result(); }
        triggerId = trigger.Id; effectId = effect.Id;
        text = (firstText + " " + secondText).Trim(); resolved = true;
        return Result();

        CardRecord? CardForHalf(int dbf, CardRecord original)
        {
            if (dbf == 0) return original;
            var matching = cards.Values.Where(card => card is not null && card.DbfId == dbf).Take(2).ToArray();
            if (matching.Length != 1 || string.IsNullOrWhiteSpace(matching[0].Id) || matching[0].Text is null ||
                !cards.TryGetValue(matching[0].Id, out var indexed) || indexed != matching[0])
            { unknowns.Add("供体 DBF 未唯一映射到固定版本卡库中的卡牌和文字，不能回退使用基础卡。"); return null; }
            return matching[0];
        }
        AssembledTextResolution Result() => new(observed, offer, resolved, text, triggerId, effectId,
            offeredHalf, offeredText, Array.AsReadOnly(unknowns.Distinct(StringComparer.Ordinal).ToArray()))
        { BaseCardId = baseCardId, BaseCardName = baseName, FirstHalfDbfId = first.IsValid ? first.Value : null,
            SecondHalfDbfId = second.IsValid ? second.Value : null };
    }

    private static bool TryHalf(CardRecord card, int half, out string? text)
    {
        text = null;
        if (card.Text is null) return false;
        var separator = card.Text.IndexOf('&');
        if (separator < 0 || card.Text.IndexOf('&', separator + 1) >= 0) return false;
        var part = half == 0 ? card.Text[..separator] : card.Text[(separator + 1)..];
        try
        {
            text = Regex.Replace(part, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
            return true;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static HalfTag Read(IReadOnlyDictionary<string, string> tags, string name, string numeric)
    {
        var hasName = tags.TryGetValue(name, out var named); var hasNumber = tags.TryGetValue(numeric, out var numbered);
        if (!hasName && !hasNumber) return new(false, true, null);
        var nameOk = int.TryParse(named, NumberStyles.None, CultureInfo.InvariantCulture, out var nameValue);
        var numberOk = int.TryParse(numbered, NumberStyles.None, CultureInfo.InvariantCulture, out var numberValue);
        if (hasName && !nameOk || hasNumber && !numberOk || hasName && hasNumber && nameValue != numberValue)
            return new(true, false, null);
        return new(true, true, hasName ? nameValue : numberValue);
    }
}
