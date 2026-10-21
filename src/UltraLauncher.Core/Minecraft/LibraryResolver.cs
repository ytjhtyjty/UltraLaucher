using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;

namespace UltraLauncher.Core.Minecraft;

/// <summary>Библиотека, применимая к текущей платформе.</summary>
public sealed record ResolvedLibrary(
    string Name,
    DownloadItem File,
    bool OnClasspath,
    bool ExtractNatives,
    IReadOnlyList<string> ExtractExclude);

/// <summary>
/// Определяет, какие библиотеки нужны на этой платформе, где они лежат и откуда скачиваются.
/// Поддерживает оба формата natives: старый (поле natives + classifiers) и новый
/// (отдельная библиотека с классификатором natives-windows и правилами ОС).
/// </summary>
public static class LibraryResolver
{
    public static IReadOnlyList<ResolvedLibrary> Resolve(VersionDefinition version, GameDirectoryLayout layout, PlatformInfo platform)
    {
        var result = new List<ResolvedLibrary>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, platform)) continue;

            var coords = MavenCoordinate.TryParse(lib.Name);

            // Новый формат: natives — обычная библиотека с классификатором. Чужую архитектуру пропускаем.
            if (coords?.Classifier is { } cls && cls.StartsWith("natives-", StringComparison.Ordinal)
                && !NativeClassifierMatches(cls, platform))
                continue;

            // Обычный артефакт для classpath.
            var artifact = lib.Artifact ?? (lib.Natives is null && coords is not null ? FromMaven(coords, lib.RepositoryUrl) : null);
            if (artifact is not null && !string.IsNullOrEmpty(artifact.Url))
            {
                var item = ToItem(artifact, coords, layout, DownloadCategory.Library);
                if (item is not null && seenPaths.Add(item.Path))
                    result.Add(new ResolvedLibrary(lib.Name, item, OnClasspath: true, ExtractNatives: false, []));
            }

            // Старый формат: natives через classifiers, распаковываются в папку natives.
            if (lib.Natives is not null && lib.Natives.TryGetValue(platform.OsName, out var classifierTemplate))
            {
                var classifier = classifierTemplate.Replace("${arch}", platform.ArchBits, StringComparison.Ordinal);
                FileRef? native = lib.Classifiers.TryGetValue(classifier, out var nref) ? nref : null;
                if (native is null && coords is not null)
                    native = FromMaven(coords with { Classifier = classifier }, lib.RepositoryUrl);
                if (native is not null)
                {
                    var item = ToItem(native, coords is null ? null : coords with { Classifier = classifier }, layout, DownloadCategory.Native);
                    if (item is not null && seenPaths.Add(item.Path))
                        result.Add(new ResolvedLibrary(lib.Name, item, OnClasspath: false, ExtractNatives: true, lib.ExtractExclude));
                }
            }
        }
        return result;
    }

    /// <summary>natives-windows подходит всем; natives-windows-arm64 / -x86 — только своей архитектуре.</summary>
    internal static bool NativeClassifierMatches(string classifier, PlatformInfo platform)
    {
        if (classifier.EndsWith("-arm64", StringComparison.Ordinal)) return platform.Arch == "arm64";
        if (classifier.EndsWith("-x86", StringComparison.Ordinal)) return platform.Arch == "x86";
        return true;
    }

    private static FileRef FromMaven(MavenCoordinate c, string? repo)
    {
        var baseUrl = string.IsNullOrEmpty(repo) ? VersionJsonParser.DefaultLibraryRepository : repo;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return new FileRef(baseUrl + c.RelativePath, null, 0, c.RelativePath);
    }

    private static DownloadItem? ToItem(FileRef file, MavenCoordinate? coords, GameDirectoryLayout layout, DownloadCategory category)
    {
        var relative = file.Path ?? coords?.RelativePath;
        if (relative is null) return null;
        if (relative.Contains("..", StringComparison.Ordinal))
            throw new LauncherException(ErrorCode.Integrity, $"Недопустимый путь библиотеки: {relative}");
        return new DownloadItem(file.Url, layout.LibraryFile(relative), file.Sha1, file.Size, category);
    }
}

/// <summary>Координата Maven group:artifact:version[:classifier][@ext].</summary>
public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static MavenCoordinate? TryParse(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }
        var p = name.Split(':');
        if (p.Length < 3) return null;
        return new MavenCoordinate(p[0], p[1], p[2], p.Length > 3 ? p[3] : null, ext);
    }

    public string RelativePath
    {
        get
        {
            var file = $"{Artifact}-{Version}{(Classifier is null ? "" : "-" + Classifier)}.{Extension}";
            return $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{file}";
        }
    }
}
