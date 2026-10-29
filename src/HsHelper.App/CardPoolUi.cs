using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HsHelper.Cards;
using Microsoft.Win32;

namespace HsHelper.App;

public partial class MainWindow
{
    private async Task RefreshPoolStatusAsync()
    {
        var rules = _manifest is null ? null : await _catalog.GetPoolRulesAsync(_manifest.Build);
        PoolStatusText.Text = rules is null ? "标准系列池尚未加载或已过期；先同步对应 build 的池证据。"
            : rules.IsComplete ? $"已加载完整规则 {rules.RuleId}，有效至 {rules.ValidUntil.ToLocalTime():MM-dd HH:mm}。"
            : $"已核对官方标准系列成员（build {rules.Build}）；临时禁用和上线证据不完整，单卡合法性保持未知。";
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
    private async void CardSelected(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        CardImage.Source = null;
        if (CardGrid.SelectedItem is not CardRecord card) return;
        CardImageText.Text = "正在读取图片缓存…";
        var path = await new CardImageCache(Path.Combine(_dataRoot, "images")).GetAsync(_cardHttp, card.Id);
        if ((CardGrid.SelectedItem as CardRecord)?.Id != card.Id) return;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        CardImage.Source = bitmap;
        CardImageText.Text = "图片用于浏览，历史牌效以对应 build 的文字为准。";
    });
}
