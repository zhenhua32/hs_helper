using HsHelper.Core;

namespace HsHelper.App;

/// <summary>A continuously observed historical public identity, never a current card projection.</summary>
public sealed record BattleHandMemoryView(string CardId, string Name, string PublicZone,
    long PublicAtSequence, long? EnteredHandAtSequence)
{
    public PublicHandDirectRevealEvidence? DirectReveal { get; init; }
    public string? SourceName { get; init; }
    public string EvidenceKind => DirectReveal is null ? "公开回手" : "历史展示确认";
    public string PublicSource => DirectReveal is { } reveal
        ? $"历史展示确认 · 展示 #{reveal.ShowAtSequence} · 确认 #{reveal.ConfirmedAtSequence}"
        : $"公开回手 · {PublicZone} #{PublicAtSequence}";
    public string Continuity => DirectReveal is { } reveal
        ? $"公开来源：{SourceName}（{ZoneName(reveal.SourceZone)} #{reveal.SourcePublicAtSequence}，来源 #{reveal.SourceEntityId}，结算块 #{reveal.BlockStartedAtSequence}）；同一实体连续观测"
        : $"#{EnteredHandAtSequence} 回手；同一实体连续观测";
    public string DisplayText => $"{Name} · {PublicSource} → {Continuity}（仅身份记忆）";

    private static string ZoneName(string zone) => zone == "PLAY" ? "公开场上" : "公开墓地";
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
                entry.DirectReveal is not null ? "手牌历史展示" : entry.FromZone == "PLAY" ? "公开场上" : "公开墓地",
                entry.PublicAtSequence, entry.EnteredHandAtSequence)
            { DirectReveal = entry.DirectReveal, SourceName = entry.DirectReveal is { } reveal ? name(reveal.SourceCardId) : null }).ToArray();
        var status = !memory.IsComplete
            ? "记录可能有遗漏，仅列连续公开证据仍可靠的身份；未列出的手牌保持未知。"
            : entries.Length == 0 ? "当前没有连续公开证据支持的手牌身份记忆；不代表对手没有其他牌。" : IdentityOnly;
        if (!memory.IsComplete) status += " " + IdentityOnly;
        return new(Array.AsReadOnly(entries), status);
    }

    public static IEnumerable<string> CardIds(GameSnapshot snapshot, string? fixedBuild) =>
        HasMatchingBuild(snapshot, fixedBuild)
            ? PublicHandMemoryVisibility.KnownCardIds(AttachmentVisibility.Project(snapshot)) : [];

    public static bool SameEvidence(GameSnapshot current, PublicHandMemorySnapshot? captured)
    {
        var actual = PublicHandMemoryVisibility.Confirmed(AttachmentVisibility.Project(current));
        return captured is null ? actual is null : actual is not null &&
            actual.MatchId == captured.MatchId && actual.StateVersion == captured.StateVersion && actual.EventSequence == captured.EventSequence &&
            actual.LocalPlayerId == captured.LocalPlayerId && actual.PerspectiveSource == captured.PerspectiveSource &&
            actual.IsComplete == captured.IsComplete && actual.IsSettled == captured.IsSettled && actual.ContinuityPolicy == captured.ContinuityPolicy &&
            actual.Warnings.SequenceEqual(captured.Warnings) && actual.Entries.Count == captured.Entries.Count &&
            actual.Entries.Zip(captured.Entries).All(pair => SameEntry(pair.First, pair.Second));
    }

    private static bool SameEntry(PublicHandMemoryEntry left, PublicHandMemoryEntry right) =>
        left.EntityId == right.EntityId && left.CardId == right.CardId && left.ControllerId == right.ControllerId && left.FromZone == right.FromZone &&
        left.PublicAtSequence == right.PublicAtSequence && left.EnteredHandAtSequence == right.EnteredHandAtSequence &&
        Equals(left.DirectReveal, right.DirectReveal);

    public static bool HasMatchingBuild(GameSnapshot snapshot, string? fixedBuild) =>
        !string.IsNullOrWhiteSpace(fixedBuild) && fixedBuild != "unknown" &&
        (snapshot.ClientBuild is null || snapshot.ClientBuild == fixedBuild);
}
