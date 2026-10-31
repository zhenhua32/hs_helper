namespace HsHelper.App;

/// <summary>Inclusive UTC bounds for calendar dates in the viewer's local time zone.</summary>
public sealed record HistoryDateRange(DateTimeOffset? Since, DateTimeOffset? Until)
{
    public static HistoryDateRange FromDates(DateTime? since, DateTime? until, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (since?.Date > until?.Date) throw new ArgumentException("开始日期不能晚于结束日期。");
        var first = since is { } start ? Boundary(start.Date, timeZone, first: true) : (DateTimeOffset?)null;
        var last = until is { } end ? Boundary(end.Date == DateTime.MaxValue.Date ? DateTime.MaxValue
            : end.Date.AddDays(1).AddTicks(-1), timeZone, first: false) : (DateTimeOffset?)null;
        return new(first, last);
    }

    public static (DateTime Since, DateTime Until) RecentDates(DateTime localToday, int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var today = DateTime.SpecifyKind(localToday.Date, DateTimeKind.Unspecified);
        return (today.AddDays(1d - days), today);
    }

    private static DateTimeOffset Boundary(DateTime value, TimeZoneInfo timeZone, bool first)
    {
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
            throw new ArgumentException("所选日期的日界在本机时区不存在，请调整日期。");
        var offset = timeZone.IsAmbiguousTime(local)
            ? first ? timeZone.GetAmbiguousTimeOffsets(local).Max() : timeZone.GetAmbiguousTimeOffsets(local).Min()
            : timeZone.GetUtcOffset(local);
        try { return new DateTimeOffset(local, offset).ToUniversalTime(); }
        catch (ArgumentException error) { throw new ArgumentException("所选日期超出本机时区支持的范围，请调整日期。", error); }
    }
}
