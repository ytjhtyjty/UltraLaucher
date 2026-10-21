using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Minecraft;

public sealed record ManifestVersion(string Id, string Type, string Url, string? Sha1, DateTimeOffset ReleaseTime);

public sealed record VersionManifest(string? LatestRelease, string? LatestSnapshot, IReadOnlyList<ManifestVersion> Versions)
{
    public static VersionManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? latestRelease = null, latestSnapshot = null;
        if (root.TryGetProperty("latest", out var latest))
        {
            latestRelease = latest.TryGetProperty("release", out var r) ? r.GetString() : null;
            latestSnapshot = latest.TryGetProperty("snapshot", out var s) ? s.GetString() : null;
        }
        var list = new List<ManifestVersion>();
        foreach (var v in root.GetProperty("versions").EnumerateArray())
        {
            DateTimeOffset.TryParse(v.GetProperty("releaseTime").GetString(), out var rt);
            list.Add(new ManifestVersion(
                v.GetProperty("id").GetString()!,
                v.GetProperty("type").GetString()!,
                v.GetProperty("url").GetString()!,
                v.TryGetProperty("sha1", out var sha) ? sha.GetString() : null,
                rt));
        }
        return new VersionManifest(latestRelease, latestSnapshot, list);
    }
}

public sealed record ManifestLoadResult(VersionManifest Manifest, bool FromCache, DateTime? CachedAtUtc);

/// <summary>
/// Загружает официальный манифест версий Mojang. Последний успешный ответ кешируется,
/// чтобы без сети показывать список и запускать установленные версии.
/// </summary>
public sealed class VersionManifestService
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly ILauncherLog _log;

    public VersionManifestService(HttpClient http, string cacheFile, ILauncherLog log)
    {
        _http = http;
        _cacheFile = cacheFile;
        _log = log;
    }

    public async Task<ManifestLoadResult> LoadAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var json = await _http.GetStringAsync(ManifestUrl, cts.Token).ConfigureAwait(false);
            var manifest = VersionManifest.Parse(json);
            try { AtomicFile.WriteAllText(_cacheFile, json); } catch (Exception ex) { _log.Warn("Manifest", "Не удалось сохранить кеш манифеста", ex); }
            _log.Info("Manifest", $"Манифест загружен: {manifest.Versions.Count} версий, последний релиз {manifest.LatestRelease}");
            return new ManifestLoadResult(manifest, false, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ErrorTranslator.IsTransient(ex) || ex is OperationCanceledException or JsonException))
        {
            _log.Warn("Manifest", "Манифест недоступен, используется кеш: " + ex.Message);
            if (File.Exists(_cacheFile))
            {
                try
                {
                    var cached = VersionManifest.Parse(await File.ReadAllTextAsync(_cacheFile, ct).ConfigureAwait(false));
                    return new ManifestLoadResult(cached, true, File.GetLastWriteTimeUtc(_cacheFile));
                }
                catch (JsonException) { /* кеш повреждён */ }
            }
            throw new LauncherException(ErrorCode.Network, "Не удалось получить список версий Minecraft.",
                "Проверьте подключение к интернету. Установленные версии всё равно можно запустить офлайн.", ex);
        }
    }
}

/// <summary>Какие версии UltraLauncher умеет устанавливать и запускать.</summary>
public static class VersionSupport
{
    /// <summary>Дата выхода 1.8 — с неё используется современная раскладка ассетов.</summary>
    public static readonly DateTimeOffset MinimumReleaseTime = new(2014, 9, 2, 0, 0, 0, TimeSpan.Zero);

    public const string RangeDescription = "Minecraft Java Edition 1.8 и новее (релизы и снапшоты). " +
                                           "Alpha, Beta и версии до 1.8 не поддерживаются.";

    public static bool IsSupported(ManifestVersion v, out string? reason)
    {
        if (v.Type is "old_alpha" or "old_beta")
        {
            reason = "Alpha/Beta версии не поддерживаются.";
            return false;
        }
        if (v.Type is not ("release" or "snapshot"))
        {
            reason = $"Тип версии «{v.Type}» не поддерживается.";
            return false;
        }
        if (v.ReleaseTime < MinimumReleaseTime)
        {
            reason = "Версии старше 1.8 не поддерживаются (устаревший формат ассетов).";
            return false;
        }
        reason = null;
        return true;
    }
}
