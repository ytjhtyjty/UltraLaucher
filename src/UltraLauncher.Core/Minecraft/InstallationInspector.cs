using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;

namespace UltraLauncher.Core.Minecraft;

public enum InstallState { Complete, Incomplete }

public sealed record InstalledVersion(string Id, string Type, InstallState State, DateTime? InstalledUtc, long VersionFolderBytes);

/// <summary>Состояние установленных версий без обращения к сети.</summary>
public sealed class InstallationInspector
{
    private readonly PlatformInfo _platform;

    public InstallationInspector(PlatformInfo platform)
    {
        _platform = platform;
    }

    public IReadOnlyList<InstalledVersion> ListInstalled(GameDirectoryLayout layout)
    {
        var result = new List<InstalledVersion>();
        if (!Directory.Exists(layout.VersionsDirectory)) return result;
        foreach (var dir in Directory.EnumerateDirectories(layout.VersionsDirectory))
        {
            var id = Path.GetFileName(dir);
            var json = layout.VersionJson(id);
            if (!File.Exists(json)) continue;
            string type = "release";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString() ?? type;
            }
            catch (JsonException) { type = "повреждена"; }

            var marker = ReadMarker(layout, id);
            long size = 0;
            try { size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            result.Add(new InstalledVersion(id, type, marker is null ? InstallState.Incomplete : InstallState.Complete, marker?.InstalledUtc, size));
        }
        return result.OrderByDescending(v => v.InstalledUtc ?? DateTime.MinValue).ToList();
    }

    public static InstallMarker? ReadMarker(GameDirectoryLayout layout, string id)
    {
        try
        {
            var path = layout.InstallMarker(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Собирает план установки только из локальных файлов. Бросает <see cref="ErrorCode.NotInstalled"/>,
    /// если метаданные отсутствуют.
    /// </summary>
    public InstallPlan LoadLocalPlan(GameDirectoryLayout layout, string versionId)
    {
        VersionDefinition Load(string id)
        {
            var path = layout.VersionJson(id);
            if (!File.Exists(path))
                throw new LauncherException(ErrorCode.NotInstalled, $"Версия {id} не установлена на этом компьютере.",
                    "Сначала установите версию при подключении к интернету.");
            return VersionJsonParser.Parse(File.ReadAllText(path));
        }

        var version = VersionInheritance.Resolve(Load(versionId), Load);
        AssetIndex? assets = null;
        if (version.AssetIndex is { } ai)
        {
            var indexPath = layout.AssetIndexFile(ai.Id);
            if (!File.Exists(indexPath))
                throw new LauncherException(ErrorCode.NotInstalled, $"Для версии {versionId} не скачан индекс ассетов.",
                    "Завершите установку при подключении к интернету.");
            assets = AssetIndex.Parse(File.ReadAllText(indexPath));
        }
        return InstallPlanBuilder.Build(version, assets, layout, _platform);
    }

    /// <summary>Быстрая проверка готовности к запуску: все файлы на месте и нужного размера.</summary>
    public static IReadOnlyList<DownloadItem> FindMissingFiles(InstallPlan plan)
        => plan.Files.Where(f => FileVerifier.QuickCheck(f) != FileState.Valid).ToList();

    public bool IsReadyOffline(GameDirectoryLayout layout, string versionId, out InstallPlan? plan, out string? reason)
    {
        plan = null;
        if (ReadMarker(layout, versionId) is null)
        {
            reason = $"Установка версии {versionId} не завершена.";
            return false;
        }
        try
        {
            plan = LoadLocalPlan(layout, versionId);
        }
        catch (LauncherException ex)
        {
            reason = ex.UserMessage;
            return false;
        }
        var missing = FindMissingFiles(plan);
        if (missing.Count > 0)
        {
            reason = $"Не хватает {missing.Count} файлов (например, {Path.GetFileName(missing[0].Path)}).";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// Удаляет только файлы самой версии (versions/&lt;id&gt;). Миры, скриншоты, ресурспаки,
    /// настройки игры, а также общие библиотеки и ассеты не затрагиваются.
    /// </summary>
    public static void DeleteVersionFiles(GameDirectoryLayout layout, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || versionId is "." or "..")
            throw new LauncherException(ErrorCode.InvalidPath, "Некорректный идентификатор версии.");
        var dir = Path.GetFullPath(layout.VersionDirectory(versionId));
        var versionsRoot = Path.GetFullPath(layout.VersionsDirectory) + Path.DirectorySeparatorChar;
        if (!dir.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException(ErrorCode.InvalidPath, "Путь версии вне папки versions.");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
