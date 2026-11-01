using System.Text;
using HsHelper.Cards;

namespace HsHelper.App;

internal static class DeckConstructionPresentation
{
    public static string Format(DeckDefinition deck, DeckDescription description)
    {
        var actual = deck.Cards.Sum(card => (long)card.Count);
        var text = new StringBuilder($"固定卡库 build {description.Build}\n{description.HeroClass ?? "未知职业"} · 主牌 {actual} 张\n");
        if (description.MainDeckSize is not { } size)
            text.AppendLine($"主牌构筑张数：普通要求 30 张；{(actual == 30 ? "满足" : "不满足")}普通张数要求。");
        else
        {
            text.AppendLine(size.IsKnown && size.RequiredCards is { } required
                ? $"主牌构筑张数：当前 {size.ActualCards} 张；要求 {required} 张；{(size.IsSatisfied == true ? "满足" : size.IsSatisfied == false ? "不满足" : "是否满足未知")}。"
                : $"主牌构筑张数：当前 {size.ActualCards} 张；要求未知或存在冲突，不能认定满足。");
            foreach (var source in size.Sources)
                text.AppendLine($"张数来源：{source.OwnerName}（{source.OwnerCardId}）要求 {source.RequiredCards} 张；证据 {source.EvidenceId} · {source.EvidenceSource}");
            foreach (var unknown in size.Unknowns) text.AppendLine("张数未知项：" + unknown);
        }
        text.AppendLine("以上仅检查主牌构筑张数，不是开局复制或其它效果结算后的牌库数量；不代替完整构筑检查。");
        text.AppendLine("完整构筑检查：" + (description.Legality switch { CardLegality.Legal => "合法", CardLegality.Illegal => "不合法", _ => "未知" }));
        foreach (var card in description.Cards.OrderBy(card => card.Cost).ThenBy(card => card.Name))
            text.AppendLine($"{card.Count}x ({card.Cost?.ToString() ?? "?"}) {card.Name}");
        foreach (var sideboard in description.Sideboards)
        {
            text.AppendLine($"\n{sideboard.OwnerName} 的边栏：");
            foreach (var card in sideboard.Cards) text.AppendLine($"  {card.Count}x ({card.Cost?.ToString() ?? "?"}) {card.Name}");
        }
        text.AppendLine("\n费用曲线：" + string.Join("  ", description.ManaCurve.Select(bucket => $"{bucket.Cost}费 {bucket.Count}张")));
        foreach (var issue in description.Issues) text.AppendLine(issue);
        return text.ToString();
    }
}

public partial class MainWindow
{
    private long _deckDescriptionRevision;
    private CancellationTokenSource? _deckDescriptionCancellation;
    private Func<DeckDescription, CancellationToken, Task>? _deckDescriptionReadCompleted = null;

    private void CancelDeckDescription(string? message = null)
    {
        _deckDescriptionRevision++;
        var previous = _deckDescriptionCancellation; _deckDescriptionCancellation = null;
        try { previous?.Cancel(); } finally { previous?.Dispose(); }
        if (message is not null) DeckResultBox.Text = message;
    }

    private Task RefreshDeckDescriptionAsync() => _deck is { } deck
        ? DescribeDeckAsync(deck, _selectedDeckVersion?.CardBuild) : Task.CompletedTask;

    private async Task DescribeDeckAsync(DeckDefinition deck, string? build = null)
    {
        if (_closing || !ReferenceEquals(_deck, deck)) return;
        CancelDeckDescription("正在读取固定版本套牌资料，主牌张数要求尚未确认…");
        build ??= _manifest?.Build;
        if (string.IsNullOrWhiteSpace(build) || build == "unknown")
        { DeckResultBox.Text = $"套牌已解析 · 主牌 {deck.Cards.Sum(card => (long)card.Count)} 张\n卡库版本未知；主牌构筑张数要求未知，加载对应卡库后重试。"; return; }
        var frozen = new DeckDefinition(deck.Format, Array.AsReadOnly(deck.Heroes.ToArray()),
            Array.AsReadOnly(deck.Cards.ToArray()), Array.AsReadOnly(deck.Sideboards.Select(sideboard =>
                new DeckSideboard(sideboard.OwnerDbfId, Array.AsReadOnly(sideboard.Cards.ToArray()))).ToArray()));
        var revision = _deckDescriptionRevision; var catalogRevision = _catalogRefreshRevision;
        var versionId = _selectedDeckVersion?.Id; var input = DeckBox.Text;
        var cancellation = new CancellationTokenSource(); _deckDescriptionCancellation = cancellation;
        var completed = _deckDescriptionReadCompleted;
        bool Current() => !_closing && revision == _deckDescriptionRevision && catalogRevision == _catalogRefreshRevision &&
            ReferenceEquals(_deck, deck) && _selectedDeckVersion?.Id == versionId && DeckBox.Text == input;
        try
        {
            var description = await _catalog.DescribeDeckAsync(frozen, build, cancellationToken: cancellation.Token);
            if (completed is not null) await completed(description, cancellation.Token);
            if (Current()) DeckResultBox.Text = DeckConstructionPresentation.Format(frozen, description);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !Current()) { }
        catch (Exception) when (!Current()) { }
        catch
        { DeckResultBox.Text = "套牌资料读取失败；主牌构筑张数要求尚未确认，请重试。"; throw; }
        finally
        {
            if (ReferenceEquals(_deckDescriptionCancellation, cancellation)) _deckDescriptionCancellation = null;
            cancellation.Dispose();
        }
    }
}
