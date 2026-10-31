using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace HsHelper.App;

public partial class MainWindow
{
    private Func<DeckExportFormat, string?>? _chooseDeckExportPath = null;
    private Func<string, string, Task>? _writeDeckExportText = null;
    private bool _deckExportBusy;
    private Task? _deckExportWrite;

    private async void SaveDeckClicked(object sender, RoutedEventArgs e) => await Run(() => ExportDeckAsync(DeckExportFormat.Json));
    private async void ExportDeckCodeClicked(object sender, RoutedEventArgs e) => await Run(() => ExportDeckAsync(DeckExportFormat.DeckCode));

    private async Task ExportDeckAsync(DeckExportFormat format)
    {
        if (_closing || _deckExportBusy) return;
        _deckExportBusy = true; RefreshDeckExportControls();
        try
        {
            var document = DeckExportBuilder.Create(DeckBox.Text, DeckNameBox.Text, _selectedDeckVersion, DateTimeOffset.UtcNow);
            var text = format == DeckExportFormat.Json ? DeckExportBuilder.ToJson(document) : DeckExportBuilder.ToCodeText(document);
            var path = _chooseDeckExportPath is { } choose ? choose(format) : ChooseDeckExportPath(format);
            if (_closing) return;
            if (string.IsNullOrWhiteSpace(path)) { DeckExportStatusText.Text = "已取消套牌导出。"; return; }
            DeckExportStatusText.Text = "正在保存套牌导出文件…";
            _deckExportWrite = _writeDeckExportText is { } write ? write(path, text)
                : File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            await _deckExportWrite;
            DeckExportStatusText.Text = format == DeckExportFormat.DeckCode
                ? "牌码已导出（包含边栏）；纯牌码不携带套牌版本或卡库信息。"
                : document.IsSavedVersion ? $"已导出套牌版本 {document.DeckVersionId} · 卡库 {document.CardBuild}。"
                : "已导出未保存草稿；来源版本和卡库未知。";
        }
        catch
        {
            DeckExportStatusText.Text = "套牌导出未完成，请检查牌码或目标文件后重试。";
            throw;
        }
        finally { _deckExportWrite = null; _deckExportBusy = false; RefreshDeckExportControls(); }
    }

    private string? ChooseDeckExportPath(DeckExportFormat format)
    {
        var dialog = new SaveFileDialog
        {
            Filter = format == DeckExportFormat.Json ? "套牌JSON|*.json" : "套牌代码文本|*.txt",
            FileName = format == DeckExportFormat.Json ? "hunter-deck.json" : "hunter-deck.txt",
            DefaultExt = format == DeckExportFormat.Json ? ".json" : ".txt"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void RefreshDeckExportControls()
    { ExportDeckJsonButton.IsEnabled = ExportDeckCodeButton.IsEnabled = !_closing && !_deckExportBusy; }

    private Task WaitForDeckExportAsync() => _deckExportWrite ?? Task.CompletedTask;
}
