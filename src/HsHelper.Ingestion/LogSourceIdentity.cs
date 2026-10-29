using System.Security.Cryptography;
using System.Text;

namespace HsHelper.Ingestion;

public static class LogSourceIdentity
{
    public static string Create(string path, int perspectiveSelection, string? deckVersionId)
    {
        var info = new FileInfo(path);
        var material = $"replay-v2|{info.FullName.ToUpperInvariant()}|{info.CreationTimeUtc.Ticks}|{perspectiveSelection}|{deckVersionId ?? "unbound"}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24];
    }
}
