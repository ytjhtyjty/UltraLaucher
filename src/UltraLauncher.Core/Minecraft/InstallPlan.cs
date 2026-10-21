using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;

namespace UltraLauncher.Core.Minecraft;

/// <summary>Всё, что должно лежать на диске для запуска версии.</summary>
public sealed class InstallPlan
{
    public required VersionDefinition Version { get; init; }
    public required string ClientJarPath { get; init; }
    public required IReadOnlyList<ResolvedLibrary> Libraries { get; init; }
    public required IReadOnlyList<DownloadItem> Files { get; init; }
    public string? LogConfigPath { get; init; }

    public IEnumerable<string> ClasspathEntries =>
        Libraries.Where(l => l.OnClasspath).Select(l => l.File.Path).Append(ClientJarPath);

    public IEnumerable<ResolvedLibrary> NativeArchives => Libraries.Where(l => l.ExtractNatives);
}

public static class InstallPlanBuilder
{
    /// <summary>Строит план по уже объединённой (с учётом наследования) версии и разобранному индексу ассетов.</summary>
    public static InstallPlan Build(VersionDefinition version, AssetIndex? assets, GameDirectoryLayout layout, PlatformInfo platform)
    {
        var files = new List<DownloadItem>();

        var jarId = version.JarVersionId ?? version.Id;
        var clientJar = layout.VersionJar(jarId);
        if (version.ClientDownload is { } client)
            files.Add(new DownloadItem(client.Url, clientJar, client.Sha1, client.Size, DownloadCategory.Client));
        else if (!File.Exists(clientJar))
            throw new LauncherException(ErrorCode.Integrity, $"В метаданных версии {version.Id} нет ссылки на клиент.",
                "Эта версия не может быть установлена автоматически.");

        var libraries = LibraryResolver.Resolve(version, layout, platform);
        files.AddRange(libraries.Select(l => l.File));

        string? logConfig = null;
        if (version.Logging is { } logging)
        {
            logConfig = Path.Combine(layout.LogConfigsDirectory, logging.FileId);
            files.Add(new DownloadItem(logging.File.Url, logConfig, logging.File.Sha1, logging.File.Size, DownloadCategory.LogConfig));
        }

        if (assets is not null)
            files.AddRange(assets.ToDownloads(layout));

        return new InstallPlan
        {
            Version = version,
            ClientJarPath = clientJar,
            Libraries = libraries,
            Files = files,
            LogConfigPath = logConfig,
        };
    }
}
