namespace UltraLauncher.Core.Common;

/// <summary>
/// Где лаунчер хранит свои данные. Исходный код и релиз лежат отдельно,
/// игровые файлы — в папке игры из настроек.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    /// <summary>%LOCALAPPDATA%\UltraLauncher (или переопределение через ULTRALAUNCHER_DATA).</summary>
    public static AppPaths Default()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("ULTRALAUNCHER_DATA");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return new AppPaths(overrideRoot);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "UltraLauncher"));
    }

    /// <summary>Папка игры по умолчанию: %APPDATA%\.ultralauncher (не трогаем официальный .minecraft).</summary>
    public static string DefaultGameDirectory()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, ".ultralauncher");
    }

    public string DataRoot { get; }
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string AccountsFile => Path.Combine(DataRoot, "accounts.dat");
    public string MsalCacheFileName => "msal_cache.dat";
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string GameLogsDirectory => Path.Combine(DataRoot, "logs", "game");
    public string CacheDirectory => Path.Combine(DataRoot, "cache");
    public string RuntimesDirectory => Path.Combine(DataRoot, "runtimes");
    public string ManifestCacheFile => Path.Combine(CacheDirectory, "version_manifest_v2.json");
    public string VerificationCacheFile => Path.Combine(CacheDirectory, "verified-files.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(GameLogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RuntimesDirectory);
    }
}

/// <summary>Раскладка папки игры (как у официального лаунчера).</summary>
public sealed class GameDirectoryLayout
{
    public GameDirectoryLayout(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string VersionsDirectory => Path.Combine(Root, "versions");
    public string LibrariesDirectory => Path.Combine(Root, "libraries");
    public string AssetsDirectory => Path.Combine(Root, "assets");
    public string AssetIndexesDirectory => Path.Combine(AssetsDirectory, "indexes");
    public string AssetObjectsDirectory => Path.Combine(AssetsDirectory, "objects");
    public string LogConfigsDirectory => Path.Combine(AssetsDirectory, "log_configs");

    public string VersionDirectory(string id) => Path.Combine(VersionsDirectory, id);
    public string VersionJson(string id) => Path.Combine(VersionDirectory(id), id + ".json");
    public string VersionJar(string id) => Path.Combine(VersionDirectory(id), id + ".jar");
    public string NativesDirectory(string id) => Path.Combine(VersionDirectory(id), "natives");
    public string InstallMarker(string id) => Path.Combine(VersionDirectory(id), ".ultralauncher-installed.json");
    public string AssetIndexFile(string indexId) => Path.Combine(AssetIndexesDirectory, indexId + ".json");
    public string LibraryFile(string relativePath) => Path.Combine(LibrariesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    public string AssetObject(string hash) => Path.Combine(AssetObjectsDirectory, hash[..2], hash);

    /// <summary>Пользовательские данные, которые лаунчер никогда не удаляет.</summary>
    public static readonly IReadOnlyList<string> UserDataEntries =
    [
        "saves", "screenshots", "resourcepacks", "shaderpacks", "texturepacks",
        "options.txt", "servers.dat", "crash-reports", "logs", "config", "mods",
    ];
}
