using System.Net;
using System.Reflection;
using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Java;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.Core.Application;

/// <summary>Сборка всех сервисов ядра (без DI-контейнера — зависимости видны явно).</summary>
public sealed class LauncherCore : IDisposable
{
    private IMicrosoftIdentity? _identity;
    private string? _identityClientId;

    public LauncherCore(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureCreated();
        Version = typeof(LauncherCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

        Log = new FileLog(paths.LogsDirectory);
        Log.Info("App", $"UltraLauncher {Version} запущен. Данные: {paths.DataRoot}; ОС: {Environment.OSVersion}; .NET {Environment.Version}");

        Http = CreateHttpClient(Version);
        Guard = new OperationGuard();
        Settings = new SettingsService(new SettingsStore(paths.SettingsFile, Log), Log);

        var verifier = new FileVerifier(new VerificationCache(paths.VerificationCacheFile));
        var downloader = new Downloader(Http, verifier, Log);
        var platform = PlatformInfo.Current;

        Accounts = new AccountService(GetIdentity, new XboxMinecraftClient(Http, Log), new AccountStore(paths.AccountsFile, Log), Log, Guard);
        Game = new GameService(
            Settings,
            new VersionManifestService(Http, paths.ManifestCacheFile, Log),
            new VersionInstaller(Http, downloader, Log, platform, Version),
            new InstallationInspector(platform),
            new JavaDetector(paths.RuntimesDirectory, Log),
            new JavaRuntimeInstaller(Http, downloader, paths.RuntimesDirectory, Log, platform),
            Accounts,
            new GameLauncher(Log, paths.GameLogsDirectory, platform),
            Guard,
            Log,
            Version);
    }

    public string Version { get; }
    public AppPaths Paths { get; }
    public FileLog Log { get; }
    public HttpClient Http { get; }
    public OperationGuard Guard { get; }
    public SettingsService Settings { get; }
    public AccountService Accounts { get; }
    public GameService Game { get; }

    /// <summary>Клиент MSAL пересоздаётся, если в настройках поменяли Client ID.</summary>
    private IMicrosoftIdentity GetIdentity()
    {
        var clientId = Settings.Current.MicrosoftClientId;
        if (_identity is null || _identityClientId != clientId)
        {
            _identity = new MsalMicrosoftIdentity(clientId, Paths.DataRoot, Paths.MsalCacheFileName, Log);
            _identityClientId = clientId;
        }
        return _identity;
    }

    public static HttpClient CreateHttpClient(string version)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 16,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // таймауты — через отмену и контроль зависания
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"UltraLauncher/{version}");
        return http;
    }

    public void Dispose()
    {
        Http.Dispose();
        Log.Dispose();
    }
}
