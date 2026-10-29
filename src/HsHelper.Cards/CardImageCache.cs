using System.Text.RegularExpressions;

namespace HsHelper.Cards;

/// <summary>Latest card renders are a display aid, not historical card-rule evidence.</summary>
public sealed class CardImageCache
{
    private readonly string _root;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    public CardImageCache(string directory)
    {
        _root = Path.GetFullPath(directory);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> GetAsync(HttpClient http, string cardId, string locale = "zhCN", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cardId) || !Regex.IsMatch(cardId, "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant) ||
            string.IsNullOrEmpty(locale) || !Regex.IsMatch(locale, "^[a-z]{2}[A-Z]{2}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("图片卡牌或语言标识无效。");
        var path = Path.Combine(_root, $"{locale}-{cardId}.png");
        if (File.Exists(path) && await IsPngAsync(path, cancellationToken)) return path;
        var uri = new Uri($"https://art.hearthstonejson.com/v1/render/latest/{locale}/256x/{cardId}.png");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != uri) throw new InvalidDataException("卡牌图片来源路径不匹配。");
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("卡牌图片过大。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 8 * 1024 * 1024) throw new InvalidDataException("卡牌图片过大。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (!await IsPngAsync(temporary, cancellationToken)) throw new InvalidDataException("图片响应不是PNG。");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> IsPngAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        if (file.Length < 24 || file.Length > 8 * 1024 * 1024) return false;
        var header = new byte[8];
        await file.ReadExactlyAsync(header, token);
        return header.AsSpan().SequenceEqual(PngSignature);
    }
}
