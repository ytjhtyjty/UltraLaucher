using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.Core.Java;

/// <summary>
/// Устанавливает официальную Java-среду Mojang (тот же источник, что у официального лаунчера):
/// launchermeta.mojang.com → java-runtime/all.json → manifest компонента → файлы с SHA-1.
/// </summary>
public sealed class JavaRuntimeInstaller
{
    public const string RuntimeIndexUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly HttpClient _http;
    private readonly Downloader _downloader;
    private readonly string _runtimesDirectory;
    private readonly ILauncherLog _log;
    private readonly PlatformInfo _platform;

    public JavaRuntimeInstaller(HttpClient http, Downloader downloader, string runtimesDirectory, ILauncherLog log, PlatformInfo platform)
    {
        _http = http;
        _downloader = downloader;
        _runtimesDirectory = runtimesDirectory;
        _log = log;
        _platform = platform;
    }

    public string PlatformKey => _platform.Arch switch
    {
        "arm64" => "windows-arm64",
        "x86" => "windows-x86",
        _ => "windows-x64",
    };

    public string RuntimeHome(string component) => Path.Combine(_runtimesDirectory, component);

    public async Task<JavaInstallation> InstallAsync(JavaRequirement requirement, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var component = requirement.Component;
        _log.Info("Java", $"Установка Java-среды Mojang {component} (Java {requirement.MajorVersion}) для {PlatformKey}");
        progress?.Report(new InstallProgress(InstallStage.Metadata, $"Поиск Java {requirement.MajorVersion} на серверах Mojang…", null));

        string indexJson;
        try
        {
            indexJson = await _http.GetStringAsync(RuntimeIndexUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw ErrorTranslator.Translate(ex);
        }

        FileRef manifestRef;
        using (var index = JsonDocument.Parse(indexJson))
        {
            if (!index.RootElement.TryGetProperty(PlatformKey, out var plat)
                || !plat.TryGetProperty(component, out var arr) || arr.GetArrayLength() == 0)
                throw new LauncherException(ErrorCode.JavaNotFound,
                    $"Mojang не предоставляет Java «{component}» для {PlatformKey}.",
                    $"Установите Java {requirement.MajorVersion} (например, Eclipse Temurin) и укажите путь в настройках.");
            var m = arr[0].GetProperty("manifest");
            manifestRef = new FileRef(m.GetProperty("url").GetString()!, m.GetProperty("sha1").GetString(), m.GetProperty("size").GetInt64());
        }

        var home = RuntimeHome(component);
        Directory.CreateDirectory(home);
        var manifestPath = Path.Combine(home, ".manifest.json");
        await _downloader.DownloadOneAsync(new DownloadItem(manifestRef.Url, manifestPath, manifestRef.Sha1, manifestRef.Size, DownloadCategory.Runtime), null, ct).ConfigureAwait(false);

        var files = new List<DownloadItem>();
        var homeRoot = Path.GetFullPath(home) + Path.DirectorySeparatorChar;
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false)))
        {
            foreach (var f in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                var target = Path.GetFullPath(Path.Combine(home, f.Name));
                if (!target.StartsWith(homeRoot, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException(ErrorCode.Integrity, $"Небезопасный путь в манифесте Java: {f.Name}");
                var type = f.Value.GetProperty("type").GetString();
                if (type == "directory")
                {
                    Directory.CreateDirectory(target);
                }
                else if (type == "file")
                {
                    var raw = f.Value.GetProperty("downloads").GetProperty("raw");
                    files.Add(new DownloadItem(raw.GetProperty("url").GetString()!, target, raw.GetProperty("sha1").GetString(), raw.GetProperty("size").GetInt64(), DownloadCategory.Runtime));
                }
                // Ссылки (type=link) в Windows-сборках не используются.
            }
        }

        progress?.Report(new InstallProgress(InstallStage.Verifying, "Проверка файлов Java…", null));
        var missing = files.Where(f => _downloader.Verifier.Verify(f) != FileState.Valid).ToList();
        if (missing.Count > 0)
        {
            var dl = new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress(InstallStage.Downloading,
                $"Java: скачано {p.CompletedFiles} из {p.TotalFiles} файлов",
                p.TotalBytes > 0 ? (double)p.CompletedBytes / p.TotalBytes : null, p)));
            try
            {
                await _downloader.DownloadAllAsync(missing, dl, ct).ConfigureAwait(false);
            }
            finally
            {
                _downloader.Verifier.SaveCache();
            }
        }

        var javaExe = Path.Combine(home, "bin", "java.exe");
        var info = JavaDetector.Inspect(javaExe, "UltraLauncher (Mojang)")
                   ?? throw new LauncherException(ErrorCode.JavaNotFound, "Java скачана, но не запускается.", "Проверьте антивирус и повторите установку Java.");
        progress?.Report(new InstallProgress(InstallStage.Completed, $"Java {info.MajorVersion} установлена", 1));
        _log.Info("Java", "Установлена " + info.DisplayName);
        return info;
    }
}
