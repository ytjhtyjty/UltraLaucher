using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;

namespace UltraLauncher.Core.Minecraft;

public sealed record AssetObject(string Name, string Hash, long Size);

/// <summary>Индекс ассетов (assets/indexes/&lt;id&gt;.json).</summary>
public sealed class AssetIndex
{
    public const string ResourcesBaseUrl = "https://resources.download.minecraft.net/";

    public required IReadOnlyList<AssetObject> Objects { get; init; }

    /// <summary>Старые индексы (до 1.7.10 включительно) требуют virtual/resources-раскладки — не поддерживаются.</summary>
    public bool IsLegacyLayout { get; init; }

    public static AssetIndex Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var legacy = (root.TryGetProperty("virtual", out var v) && v.ValueKind == JsonValueKind.True)
                         || (root.TryGetProperty("map_to_resources", out var m) && m.ValueKind == JsonValueKind.True);
            var list = new List<AssetObject>();
            foreach (var p in root.GetProperty("objects").EnumerateObject())
            {
                var hash = p.Value.GetProperty("hash").GetString()!;
                if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
                    throw new LauncherException(ErrorCode.Integrity, $"Некорректный хеш ассета {p.Name}.");
                list.Add(new AssetObject(p.Name, hash.ToLowerInvariant(), p.Value.GetProperty("size").GetInt64()));
            }
            return new AssetIndex { Objects = list, IsLegacyLayout = legacy };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LauncherException(ErrorCode.Integrity, "Индекс ассетов повреждён.",
                "Переустановите версию — индекс будет скачан заново.", ex);
        }
    }

    public IEnumerable<DownloadItem> ToDownloads(GameDirectoryLayout layout)
    {
        // Один и тот же объект может встречаться под разными именами — качаем один раз.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Objects)
        {
            if (!seen.Add(o.Hash)) continue;
            yield return new DownloadItem($"{ResourcesBaseUrl}{o.Hash[..2]}/{o.Hash}", layout.AssetObject(o.Hash), o.Hash, o.Size, DownloadCategory.Asset);
        }
    }
}
