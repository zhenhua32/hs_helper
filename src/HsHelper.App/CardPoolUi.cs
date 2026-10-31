using System.IO;
using System.Windows;
using System.Windows.Controls;
using HsHelper.Cards;
using Microsoft.Win32;

namespace HsHelper.App;

public partial class MainWindow
{
    private async Task RefreshPoolStatusAsync()
    {
        var build = _manifest?.Build; var revision = _catalogRefreshRevision;
        bool Current() => !_closing && revision == _catalogRefreshRevision && _manifest?.Build == build;
        try
        {
            var rules = build is null ? null : await _catalog.GetPoolRulesAsync(build);
            if (!Current()) return;
            PoolStatusText.Text = rules is null ? "标准系列池尚未加载或已过期；先同步对应 build 的池证据。"
                : rules.IsComplete ? $"已加载完整规则 {rules.RuleId}，有效至 {rules.ValidUntil.ToLocalTime():MM-dd HH:mm}。"
                : $"已核对官方标准系列成员（build {rules.Build}）；临时禁用和上线证据不完整，单卡合法性保持未知。";
        }
        catch { if (Current()) PoolStatusText.Text = "标准系列池信息读取失败，请重试；不能据此判断规则是否存在。"; }
    }
    private async void SyncPoolClicked(object sender, RoutedEventArgs e) => await CatalogOperation(async () =>
    {
        if (_manifest is null) throw new InvalidOperationException("请先同步卡库。");
        StatusText.Text = "正在核对官方标准系列与固定 build 的卡牌定义…";
        var build = _manifest.Build;
        await Task.Run(() => _catalog.SyncStandardPoolAsync(_cardHttp, build));
        await RefreshPoolStatusAsync();
        await SearchCardsAsync();
        StatusText.Text = "标准系列池已同步。";
    });
    private async void ImportPoolClicked(object sender, RoutedEventArgs e) => await CatalogOperation(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "版本化卡池规则JSON|*.json" };
        if (dialog.ShowDialog() != true) return;
        using var input = File.OpenRead(dialog.FileName);
        await _catalog.ImportPoolRulesAsync(input);
        await RefreshPoolStatusAsync(); await SearchCardsAsync();
        StatusText.Text = "卡池规则已导入并保留历史版本。";
    });
    private async void CardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_clearingCardBrowsing || _closing) return;
        if (CardGrid.SelectedItem is not CardRecord card || _cardSearchBuild is not { } build)
        { ClearCardDetails(); return; }
        _cardBrowseHistory.Clear();
        await ShowCardDetailAsync(card, build);
    }
}
