using HsHelper.Core;

namespace HsHelper.App;

/// <summary>A public identity remembered from a continuous return to hand, never a current card projection.</summary>
public sealed record BattleHandMemoryView(string CardId, string Name, string PublicZone,
    long PublicAtSequence, long EnteredHandAtSequence)
{
    public string PublicSource => $"{PublicZone} #{PublicAtSequence}";
    public string Continuity => $"#{EnteredHandAtSequence} 回手；同一实体连续观测";
    public string DisplayText => $"{Name} · {PublicSource} → {Continuity}（仅身份记忆）";
}

internal sealed record PublicHandMemoryPresentation(IReadOnlyList<BattleHandMemoryView> Entries, string Status)
{
    private const string IdentityOnly = "仅记住曾公开的基础身份；当前费用、增益、位置及效果未知，不用于动作验证。";

    public static PublicHandMemoryPresentation Create(GameSnapshot snapshot, Func<string, string> name)
    {
        snapshot = AttachmentVisibility.Project(snapshot);
        var memory = PublicHandMemoryVisibility.Confirmed(snapshot);
        if (memory is null) return new(Array.Empty<BattleHandMemoryView>(),
            "公开手牌记忆未确认或连续证据已失效；不推断对手隐藏手牌。");
        var entries = memory.Entries.OrderBy(entry => entry.PublicAtSequence).ThenBy(entry => entry.EnteredHandAtSequence)
            .ThenBy(entry => entry.CardId, StringComparer.Ordinal)
            .Select(entry => new BattleHandMemoryView(entry.CardId, name(entry.CardId),
                entry.FromZone == "PLAY" ? "公开场上" : "公开墓地", entry.PublicAtSequence, entry.EnteredHandAtSequence)).ToArray();
        var status = !memory.IsComplete
            ? "记录可能有遗漏，仅列连续公开证据仍可靠的身份；未列出的手牌保持未知。"
            : entries.Length == 0 ? "当前没有连续证据支持的公开回手身份；不代表对手没有其他牌。" : IdentityOnly;
        if (!memory.IsComplete) status += " " + IdentityOnly;
        return new(Array.AsReadOnly(entries), status);
    }

    public static IEnumerable<string> CardIds(GameSnapshot snapshot, string? fixedBuild) =>
        HasMatchingBuild(snapshot, fixedBuild) && PublicHandMemoryVisibility.Confirmed(AttachmentVisibility.Project(snapshot)) is { } memory
            ? memory.Entries.Select(entry => entry.CardId).Distinct(StringComparer.Ordinal) : [];

    public static bool SameEvidence(GameSnapshot current, PublicHandMemorySnapshot? captured)
    {
        var actual = PublicHandMemoryVisibility.Confirmed(AttachmentVisibility.Project(current));
        return captured is null ? actual is null : actual is not null && actual.IsComplete == captured.IsComplete && actual.Entries.SequenceEqual(captured.Entries);
    }

    public static bool HasMatchingBuild(GameSnapshot snapshot, string? fixedBuild) =>
        !string.IsNullOrWhiteSpace(fixedBuild) && fixedBuild != "unknown" &&
        (snapshot.ClientBuild is null || snapshot.ClientBuild == fixedBuild);
}
