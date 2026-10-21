using System.IO.Compression;
using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Minecraft;

public enum InstallStage { Metadata, Verifying, Downloading, Natives, Finalizing, Completed }

public sealed record InstallProgress(InstallStage Stage, string Status, double? Fraction, DownloadProgress? Download = null)
{
    public static string StageTitle(InstallStage s) => s switch
    {
        InstallStage.Metadata => "Получение метаданных",
        InstallStage.Verifying => "Проверка файлов",
        InstallStage.Downloading => "Загрузка файлов",
        InstallStage.Natives => "Распаковка native-библиотек",
        InstallStage.Finalizing => "Завершение установки",
        InstallStage.Completed => "Готово",
        _ => s.ToString(),
    };
}

public sealed record InstallMarker(string VersionId, string? VersionJsonSha1, DateTime InstalledUtc, string LauncherVersion);

/// <summary>
/// Установка vanilla-версии. Повторный запуск продолжает с места остановки: уже проверенные
/// файлы не скачиваются, недокачанные (*.part) перезаписываются. Пользовательские данные не трогаются.
/// </summary>
public sealed class VersionInstaller
{
    private readonly HttpClient _http;
    private readonly Downloader _downloader;
    private readonly ILauncherLog _log;
    private readonly PlatformInfo _platform;
    private readonly string _launcherVersion;

    public VersionInstaller(HttpClient http, Downloader downloader, ILauncherLog log, PlatformInfo platform, string launcherVersion)
    {
        _http = http;
        _downloader = downloader;
        _log = log;
        _platform = platform;
        _launcherVersion = launcherVersion;
    }

    public async Task<InstallPlan> InstallAsync(
        GameDirectoryLayout layout,
        string versionId,
        VersionManifest? manifest,
        bool forceRehash,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        _log.Info("Install", $"Установка {versionId} в {layout.Root} (полная проверка хешей: {forceRehash})");
        progress?.Report(new InstallProgress(InstallStage.Metadata, "Загрузка описания версии…", null));

        var root = await FetchVersionAsync(layout, versionId, manifest, ct).ConfigureAwait(false);
        var version = VersionInheritance.Resolve(root, parentId =>
            FetchVersionAsync(layout, parentId, manifest, ct).GetAwaiter().GetResult());

        if (version.MainClass is null)
            throw new LauncherException(ErrorCode.Integrity, $"В описании версии {versionId} нет главного класса.");

        AssetIndex? assets = null;
        if (version.AssetIndex is { } aiRef)
        {
            progress?.Report(new InstallProgress(InstallStage.Metadata, "Загрузка индекса ассетов…", null));
            var indexPath = layout.AssetIndexFile(aiRef.Id);
            await EnsureFileAsync(new DownloadItem(aiRef.Url, indexPath, aiRef.Sha1, aiRef.Size, DownloadCategory.Metadata), forceRehash, ct).ConfigureAwait(false);
            assets = AssetIndex.Parse(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));
            if (assets.IsLegacyLayout)
                throw new LauncherException(ErrorCode.UnsupportedVersion, $"Версия {versionId} использует устаревший формат ассетов.",
                    VersionSupport.RangeDescription);
        }

        var plan = InstallPlanBuilder.Build(version, assets, layout, _platform);

        // Проверка уже имеющихся файлов (хеши, с кешем проверенных файлов).
        var missing = await VerifyAsync(plan.Files, forceRehash, progress, ct).ConfigureAwait(false);
        _log.Info("Install", $"{versionId}: файлов в плане {plan.Files.Count}, требуется скачать {missing.Count}");

        if (missing.Count > 0)
        {
            var totalMb = missing.Sum(m => m.Size) / 1024d / 1024d;
            progress?.Report(new InstallProgress(InstallStage.Downloading, $"Скачивание {missing.Count} файлов ({totalMb:0.#} МБ)…", 0));
            var dlProgress = new Progress<DownloadProgress>(p =>
            {
                double? fraction = p.TotalBytes > 0 ? (double)p.CompletedBytes / p.TotalBytes : (double)p.CompletedFiles / Math.Max(1, p.TotalFiles);
                progress?.Report(new InstallProgress(InstallStage.Downloading,
                    $"Скачано {p.CompletedFiles} из {p.TotalFiles} файлов", Math.Clamp(fraction ?? 0, 0, 1), p));
            });
            try
            {
                await _downloader.DownloadAllAsync(missing, dlProgress, ct).ConfigureAwait(false);
            }
            finally
            {
                _downloader.Verifier.SaveCache();
            }
        }

        progress?.Report(new InstallProgress(InstallStage.Natives, "Распаковка native-библиотек…", null));
        ExtractNatives(plan, layout.NativesDirectory(versionId));

        progress?.Report(new InstallProgress(InstallStage.Finalizing, "Запись сведений об установке…", null));
        var jsonSha = FileVerifier.ComputeSha1(layout.VersionJson(versionId));
        AtomicFile.WriteAllText(layout.InstallMarker(versionId),
            JsonSerializer.Serialize(new InstallMarker(versionId, jsonSha, DateTime.UtcNow, _launcherVersion)));
        _downloader.Verifier.SaveCache();

        progress?.Report(new InstallProgress(InstallStage.Completed, $"Версия {versionId} установлена", 1));
        _log.Info("Install", $"Установка {versionId} завершена");
        return plan;
    }

    private async Task<VersionDefinition> FetchVersionAsync(GameDirectoryLayout layout, string id, VersionManifest? manifest, CancellationToken ct)
    {
        var path = layout.VersionJson(id);
        var entry = manifest?.Versions.FirstOrDefault(v => v.Id == id);
        if (entry is not null)
        {
            if (!VersionSupport.IsSupported(entry, out var reason))
                throw new LauncherException(ErrorCode.UnsupportedVersion, $"Версия {id} не поддерживается: {reason}", VersionSupport.RangeDescription);
            await EnsureFileAsync(new DownloadItem(entry.Url, path, entry.Sha1, 0, DownloadCategory.Metadata), false, ct).ConfigureAwait(false);
        }
        else if (!File.Exists(path))
        {
            throw new LauncherException(ErrorCode.VersionNotFound, $"Версия {id} не найдена ни в официальном списке, ни на диске.",
                "Обновите список версий (нужен интернет).");
        }
        return VersionJsonParser.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }

    private async Task EnsureFileAsync(DownloadItem item, bool forceRehash, CancellationToken ct)
    {
        if (_downloader.Verifier.Verify(item, forceRehash) == FileState.Valid) return;
        await _downloader.DownloadOneAsync(item, null, ct).ConfigureAwait(false);
    }

    private async Task<List<DownloadItem>> VerifyAsync(IReadOnlyList<DownloadItem> files, bool forceRehash, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var missing = new System.Collections.Concurrent.ConcurrentBag<DownloadItem>();
        var done = 0;
        progress?.Report(new InstallProgress(InstallStage.Verifying, $"Проверка {files.Count} файлов…", 0));
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, (item, _) =>
        {
            var state = _downloader.Verifier.Verify(item, forceRehash);
            if (state != FileState.Valid)
            {
                if (state is FileState.WrongHash or FileState.WrongSize)
                    _log.Warn("Install", $"Файл повреждён ({state}), будет скачан заново: {item.Path}");
                missing.Add(item);
            }
            var n = Interlocked.Increment(ref done);
            if (n % 200 == 0 || n == files.Count)
                progress?.Report(new InstallProgress(InstallStage.Verifying, $"Проверено {n} из {files.Count} файлов", (double)n / files.Count));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        _downloader.Verifier.SaveCache();
        return [.. missing.DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Распаковывает natives старого формата. Новый формат LWJGL извлекает сам из classpath.</summary>
    public static void ExtractNatives(InstallPlan plan, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);
        var fullRoot = Path.GetFullPath(nativesDir) + Path.DirectorySeparatorChar;
        foreach (var lib in plan.NativeArchives)
        {
            using var zip = ZipFile.OpenRead(lib.File.Path);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // каталог
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                if (lib.ExtractExclude.Any(ex => entry.FullName.StartsWith(ex, StringComparison.Ordinal))) continue;

                var target = Path.GetFullPath(Path.Combine(nativesDir, entry.FullName));
                if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException(ErrorCode.Integrity, $"Небезопасный путь в архиве {Path.GetFileName(lib.File.Path)}: {entry.FullName}");
                var existing = new FileInfo(target);
                if (existing.Exists && existing.Length == entry.Length) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    entry.ExtractToFile(target, overwrite: true);
                }
                catch (IOException) when (existing.Exists)
                {
                    // DLL может быть занята запущенной игрой — оставляем существующую.
                }
            }
        }
    }
}
