using System.Collections.ObjectModel;

namespace HsHelper.App;

/// <summary>Copies every entry so background producers cannot mutate a visible overlay state.</summary>
public sealed record OverlayState
{
    public string Title { get; }
    public IReadOnlyList<string> Hand { get; }
    public IReadOnlyList<string> Remaining { get; }
    public IReadOnlyList<string> OpponentPlayed { get; }
    public IReadOnlyList<string> OpponentHandMemory { get; }
    public string OpponentHandMemoryStatus { get; }
    public string Advice { get; }
    public string Quality { get; }
    public bool IsComplete { get; }
    public bool IsStable { get; }

    public OverlayState(string title, IEnumerable<string> hand, IEnumerable<string> remaining,
        IEnumerable<string> opponentPlayed, string advice, string quality, bool isComplete, bool isStable,
        IEnumerable<string>? opponentHandMemory = null, string? opponentHandMemoryStatus = null)
    {
        Title = Text(title, 120);
        Hand = Copy(hand, 80);
        Remaining = Copy(remaining, 120);
        OpponentPlayed = Copy(opponentPlayed, 100);
        OpponentHandMemory = Copy(opponentHandMemory, 100);
        OpponentHandMemoryStatus = Text(opponentHandMemoryStatus ?? "公开手牌记忆尚未确认。", 300);
        Advice = Text(advice, 500);
        Quality = Text(quality, 600);
        IsComplete = isComplete;
        IsStable = isStable;
    }

    public static OverlayState Empty { get; } = new("猎人天梯助手", [], [], [], "等待局面或 AI 建议。",
        "尚未连接日志", false, false);

    private static ReadOnlyCollection<string> Copy(IEnumerable<string>? values, int maximum)
    {
        var copy = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Take(maximum + 1)
            .Select(value => Text(value, 240)).ToList();
        if (copy.Count > maximum) copy[maximum] = "… 更多条目请在主窗口查看";
        return copy.AsReadOnly();
    }

    private static string Text(string? text, int maximum)
    {
        var value = (text ?? "").Replace('\0', ' ').Trim();
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }
}

public sealed record OverlayPlacement(double Left, double Top, double Width, double Height);

/// <summary>Pure layout rules, independent of monitor APIs and window handles.</summary>
public static class OverlayLayout
{
    public static double ClampScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 0.65, 1.8) : 1;

    public static OverlayPlacement Fit(double left, double top, double width, double height,
        double areaLeft, double areaTop, double areaWidth, double areaHeight)
    {
        if (!double.IsFinite(areaLeft) || !double.IsFinite(areaTop) || !double.IsFinite(areaWidth) ||
            !double.IsFinite(areaHeight) || areaWidth <= 0 || areaHeight <= 0)
            throw new ArgumentException("屏幕工作区必须为有限的正尺寸。");
        width = Math.Clamp(double.IsFinite(width) ? width : 360, Math.Min(180, areaWidth), areaWidth);
        height = Math.Clamp(double.IsFinite(height) ? height : 620, Math.Min(220, areaHeight), areaHeight);
        left = Math.Clamp(double.IsFinite(left) ? left : areaLeft, areaLeft, areaLeft + areaWidth - width);
        top = Math.Clamp(double.IsFinite(top) ? top : areaTop, areaTop, areaTop + areaHeight - height);
        return new(left, top, width, height);
    }
}
