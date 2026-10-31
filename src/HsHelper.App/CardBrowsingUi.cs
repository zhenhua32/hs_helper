using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HsHelper.Cards;

namespace HsHelper.App;

public sealed record CardBrowseRelatedRow(string CardId, string Name, bool IsMissing);

public partial class MainWindow
{
    private CancellationTokenSource? _cardSearchCancellation;
    private CancellationTokenSource? _cardRelatedCancellation;
    private CancellationTokenSource? _cardImageCancellation;
    private long _cardSearchRevision, _cardDetailRevision, _cardRelatedRevision, _cardImageRevision, _catalogRefreshRevision;
    private Task _cardSearchTask = Task.CompletedTask, _cardDetailTask = Task.CompletedTask, _cardImageTask = Task.CompletedTask;
    private Func<CardSearchQuery, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>? _catalogSearchOverride = null;
    private Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<CardRecord>>>? _relatedCardsOverride = null;
    private string? _cardSearchBuild, _cardDetailBuild;
    private CardRecord? _cardDetailCard;
    private IReadOnlyList<string> _relatedCardIds = [];
    private IReadOnlyDictionary<string, CardRecord> _relatedCardDefinitions = new Dictionary<string, CardRecord>();
    private int _relatedCardPage = 1;
    private bool _updatingCardFilters, _clearingCardBrowsing;
    private readonly List<CardBrowseHistoryEntry> _cardBrowseHistory = [];
    private sealed record CardBrowseHistoryEntry(CardRecord Card, string Build, int RelatedPage);

    private static void CancelCardRead(ref CancellationTokenSource? source)
    {
        var previous = source; source = null;
        try { previous?.Cancel(); } finally { previous?.Dispose(); }
    }

    private void CancelCardBrowsing()
    {
        _cardSearchRevision++; CancelCardRead(ref _cardSearchCancellation);
        _cardSearchBuild = null;
        if (CardGrid is not null)
        {
            _clearingCardBrowsing = true;
            try { CardGrid.ItemsSource = null; } finally { _clearingCardBrowsing = false; }
        }
        ClearCardDetails();
    }

    private void ClearCardDetails()
    {
        _cardDetailRevision++; _cardRelatedRevision++; _cardImageRevision++;
        CancelCardRead(ref _cardRelatedCancellation); CancelCardRead(ref _cardImageCancellation);
        _cardDetailCard = null; _cardDetailBuild = null; _relatedCardIds = []; _relatedCardPage = 1;
        _relatedCardDefinitions = new Dictionary<string, CardRecord>(); _cardBrowseHistory.Clear();
        if (CardDetailText is null) return;
        CardDetailText.Text = "选择卡牌查看固定版本文字及直接关联牌。";
        RelatedCardsGrid.ItemsSource = null; RelatedScopeText.Text = "关联记录不是随机生成全池或概率。";
        RelatedPageText.Text = "尚未选择卡牌"; CardBackButton.IsEnabled = false;
        RelatedPrevPageButton.IsEnabled = RelatedNextPageButton.IsEnabled = RelatedReloadButton.IsEnabled = OpenRelatedCardButton.IsEnabled = false;
        CardImage.Source = null; CardImageText.Text = "图片仅用于浏览；牌效以所选 build 的文字为准。";
    }

    private void CardSearchInputChanged(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _closing || _updatingCardFilters || CardCollectibleBox is null || CardGrid is null || CardDetailText is null) return;
        UpdateCardCollectibleFilter();
        CancelCardBrowsing();
        CardSearchStatusText.Text = "筛选已变更，请点击搜索或按回车。";
    }

    private void UpdateCardCollectibleFilter()
    {
        _updatingCardFilters = true;
        try
        {
            var noncollectible = CardCollectibleBox.SelectedIndex == 2;
            if (noncollectible) StandardPoolBox.IsChecked = false;
            StandardPoolBox.IsEnabled = !noncollectible;
            StandardPoolBox.ToolTip = noncollectible ? "标准系列成员仅包含可收藏牌；查看不可收藏牌时已关闭此筛选。" : "只列可收藏的标准系列成员；不表示完整天梯合法性已确认。";
        }
        finally { _updatingCardFilters = false; }
    }

    private Task SearchCardsAsync()
    {
        if (_closing || _catalog is null || CardCollectibleBox is null) return Task.CompletedTask;
        UpdateCardCollectibleFilter(); CancelCardBrowsing();
        var build = _manifest?.Build;
        if (string.IsNullOrWhiteSpace(build))
        { CardSearchStatusText.Text = "尚无固定版本卡库，请先导入或同步。"; return _cardSearchTask = Task.CompletedTask; }
        var query = new CardSearchQuery(Text: SearchBox.Text, Collectible: CardCollectibleBox.SelectedIndex switch { 1 => null, 2 => false, _ => true },
            Limit: 201, Keyword: KeywordBox.Text.Trim(), StandardPoolOnly: StandardPoolBox.IsChecked == true);
        var hunterOnly = HunterOnlyBox.IsChecked == true;
        var source = _cardSearchCancellation = new CancellationTokenSource();
        var revision = _cardSearchRevision;
        CardSearchStatusText.Text = $"正在搜索 build {build}…";
        return _cardSearchTask = SearchCardsCoreAsync(query, hunterOnly, build, revision, source.Token);
    }

    private async Task SearchCardsCoreAsync(CardSearchQuery query, bool hunterOnly, string build, long revision, CancellationToken token)
    {
        var read = _catalogSearchOverride;
        Task<IReadOnlyList<CardRecord>> Search(CardSearchQuery filter) => read is null
            ? _catalog.SearchAsync(filter, build, cancellationToken: token) : read(filter, build, token);
        bool Current() => !_closing && !token.IsCancellationRequested && revision == _cardSearchRevision && _manifest?.Build == build;
        try
        {
            var cards = await Search(query with { CardClass = hunterOnly ? "HUNTER" : null });
            if (hunterOnly)
            {
                var neutral = new List<CardRecord>(); var offset = 0;
                while (neutral.Count < 201)
                {
                    token.ThrowIfCancellationRequested();
                    var page = await Search(query with { CardClass = "NEUTRAL", Offset = offset });
                    neutral.AddRange(page.Where(card => card.Classes.Count == 0 || card.Classes.Contains("HUNTER")));
                    if (page.Count < query.Limit) break;
                    offset = checked(offset + query.Limit);
                }
                cards = cards.Concat(neutral).DistinctBy(card => card.Id).OrderBy(card => card.Cost).ThenBy(card => card.Name).ThenBy(card => card.Id).Take(201).ToArray();
            }
            if (!Current()) return;
            _cardSearchBuild = build;
            CardGrid.ItemsSource = cards.Take(200).ToArray();
            var collection = query.Collectible switch { true => "可收藏", false => "不可收藏", _ => "全部收藏属性" };
            CardSearchStatusText.Text = $"build {build} · {collection} · {Math.Min(cards.Count, 200)} 张" +
                (cards.Count > 200 ? "；结果超过 200 张，仅显示前 200 张，请缩小筛选。" : "。") +
                (query.StandardPoolOnly ? " 标准系列筛选仅列可收藏成员。" : query.Collectible == false ? " 不可收藏牌不用于标准系列成员筛选。" : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current()) CardSearchStatusText.Text = $"build {build} 搜索失败，请重试。";
        }
    }

    private async void SearchClicked(object sender, RoutedEventArgs e) => await SearchCardsAsync();
    private async void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SearchCardsAsync(); }

    private Task ShowCardDetailAsync(CardRecord card, string build, bool rememberCurrent = false)
    {
        if (_closing) return Task.CompletedTask;
        if (rememberCurrent && _cardDetailCard is { } previous && _cardDetailBuild is { } previousBuild)
        {
            if (_cardBrowseHistory.Count == 32) _cardBrowseHistory.RemoveAt(0);
            _cardBrowseHistory.Add(new(previous, previousBuild, _relatedCardPage));
        }
        _cardDetailRevision++; _cardRelatedRevision++; _cardImageRevision++;
        CancelCardRead(ref _cardRelatedCancellation); CancelCardRead(ref _cardImageCancellation);
        _cardDetailCard = card with { Classes = Array.AsReadOnly(card.Classes.ToArray()), Entourage = Array.AsReadOnly(card.Entourage.ToArray()) };
        _cardDetailBuild = build;
        _relatedCardIds = Array.AsReadOnly(card.Entourage.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray());
        _relatedCardDefinitions = new Dictionary<string, CardRecord>();
        CardDetailText.Text = $"{card.Name}\n卡库 build {build} · CardId {card.Id} · DBF {card.DbfId?.ToString() ?? "未知"}\n" +
            $"{(card.Collectible ? "可收藏" : "不可收藏")} · {(string.IsNullOrWhiteSpace(card.CardClass) ? "职业未知" : card.CardClass)} · {(string.IsNullOrWhiteSpace(card.Type) ? "类型未知" : card.Type)} · 基础费用 {card.Cost?.ToString() ?? "未知"}\n\n卡面效果：\n" +
            (card.Text.Length == 0 ? "此版本卡库未提供效果文字。" : CardBrowseText(card.Text));
        CardBackButton.IsEnabled = _cardBrowseHistory.Count > 0;
        RelatedScopeText.Text = $"仅列此卡在 build {build} 的直接关联记录，不代表生成全池或概率；不受搜索筛选限制。";
        CardImage.Source = null; CardImageText.Text = "正在读取图片；图片仅用于浏览，固定版本牌效以上方文字为准。";
        var image = _cardImageCancellation = new CancellationTokenSource();
        _cardImageTask = LoadBrowseImageAsync(card.Id, build, _cardDetailRevision, _cardImageRevision, image.Token);
        return _cardDetailTask = LoadRelatedCardPageAsync(1);
    }

    private static string CardBrowseText(string text)
    {
        // Literal formatting tags need no pattern evaluation or regex timeout in a UI event.
        foreach (var tag in new[] { "<br>", "<br/>", "<br />" }) text = text.Replace(tag, "\n", StringComparison.OrdinalIgnoreCase);
        foreach (var tag in new[] { "<b>", "</b>", "<i>", "</i>" }) text = text.Replace(tag, "", StringComparison.OrdinalIgnoreCase);
        return WebUtility.HtmlDecode(text);
    }

    private async Task LoadRelatedCardPageAsync(int page)
    {
        if (_closing || _cardDetailCard is null || _cardDetailBuild is not { } build) return;
        var pages = Math.Max(1, (_relatedCardIds.Count + 99) / 100);
        if (page < 1 || page > pages) return;
        _cardRelatedRevision++; CancelCardRead(ref _cardRelatedCancellation);
        var source = _cardRelatedCancellation = new CancellationTokenSource(); var token = source.Token;
        var revision = _cardRelatedRevision; var detailRevision = _cardDetailRevision;
        var ids = _relatedCardIds.Skip((page - 1) * 100).Take(100).ToArray();
        var read = _relatedCardsOverride; _relatedCardPage = page;
        RelatedCardsGrid.ItemsSource = null; _relatedCardDefinitions = new Dictionary<string, CardRecord>();
        OpenRelatedCardButton.IsEnabled = RelatedPrevPageButton.IsEnabled = RelatedNextPageButton.IsEnabled = RelatedReloadButton.IsEnabled = false;
        RelatedPageText.Text = $"正在读取关联第 {page} 页…";
        bool Current() => !_closing && !token.IsCancellationRequested && detailRevision == _cardDetailRevision && revision == _cardRelatedRevision && _cardDetailBuild == build;
        try
        {
            var cards = ids.Length == 0 ? [] : read is null ? await _catalog.GetByIdsAsync(ids, build, cancellationToken: token) : await read(ids, build, token);
            if (!Current()) return;
            _relatedCardDefinitions = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
            RelatedCardsGrid.ItemsSource = ids.Select(id => _relatedCardDefinitions.TryGetValue(id, out var related)
                ? new CardBrowseRelatedRow(id, related.Name, false) : new CardBrowseRelatedRow(id, "该版本缺少定义", true)).ToArray();
            RelatedPageText.Text = _relatedCardIds.Count == 0 ? "此版本卡库未提供关联记录，不代表没有衍生牌。" : $"关联第 {page}/{pages} 页 · 共 {_relatedCardIds.Count} 项（缺项保留）";
            RelatedPrevPageButton.IsEnabled = page > 1; RelatedNextPageButton.IsEnabled = page < pages;
            RelatedReloadButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!Current()) return;
            RelatedPageText.Text = $"关联第 {page} 页读取失败，请重试本页。";
            RelatedReloadButton.IsEnabled = true;
        }
    }

    private async Task LoadBrowseImageAsync(string id, string build, long detailRevision, long revision, CancellationToken token)
    {
        bool Current() => !_closing && !token.IsCancellationRequested && detailRevision == _cardDetailRevision && revision == _cardImageRevision &&
            _cardDetailCard?.Id == id && _cardDetailBuild == build;
        try
        {
            var path = await new CardImageCache(Path.Combine(_dataRoot, "images")).GetAsync(_cardHttp, id, cancellationToken: token);
            if (!Current()) return;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
            if (!Current()) return;
            CardImage.Source = bitmap;
            CardImageText.Text = "图片为最新浏览辅助，不是历史图片；牌效以所选 build 的文字为准。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current()) CardImageText.Text = "图片暂不可用；固定版本卡面文字仍可查看。图片仅用于浏览。";
        }
    }

    private void RelatedCardSelected(object sender, SelectionChangedEventArgs e) => OpenRelatedCardButton.IsEnabled = !_closing &&
        RelatedCardsGrid.SelectedItem is CardBrowseRelatedRow { IsMissing: false } row && _relatedCardDefinitions.ContainsKey(row.CardId);
    private async void OpenRelatedCardClicked(object sender, RoutedEventArgs e)
    {
        if (!_closing && RelatedCardsGrid.SelectedItem is CardBrowseRelatedRow { IsMissing: false } row && _cardDetailBuild is { } build &&
            _relatedCardDefinitions.TryGetValue(row.CardId, out var card)) await ShowCardDetailAsync(card, build, true);
    }
    private async void CardBackClicked(object sender, RoutedEventArgs e)
    {
        if (_closing || _cardBrowseHistory.Count == 0) return;
        var previous = _cardBrowseHistory[^1]; _cardBrowseHistory.RemoveAt(_cardBrowseHistory.Count - 1);
        var operation = ShowCardDetailAsync(previous.Card, previous.Build); var revision = _cardDetailRevision;
        await operation;
        if (!_closing && revision == _cardDetailRevision && previous.RelatedPage != 1)
            await (_cardDetailTask = LoadRelatedCardPageAsync(previous.RelatedPage));
    }
    private async void RelatedPrevPageClicked(object sender, RoutedEventArgs e)
    { if (RelatedPrevPageButton.IsEnabled) await (_cardDetailTask = LoadRelatedCardPageAsync(_relatedCardPage - 1)); }
    private async void RelatedNextPageClicked(object sender, RoutedEventArgs e)
    { if (RelatedNextPageButton.IsEnabled) await (_cardDetailTask = LoadRelatedCardPageAsync(_relatedCardPage + 1)); }
    private async void RelatedReloadClicked(object sender, RoutedEventArgs e)
    { if (RelatedReloadButton.IsEnabled) await (_cardDetailTask = LoadRelatedCardPageAsync(_relatedCardPage)); }
}
