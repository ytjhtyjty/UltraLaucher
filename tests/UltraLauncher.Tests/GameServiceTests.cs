using System.Net;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Java;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.Tests;

/// <summary>
/// Сценарии «запуск» на уровне GameService без сети: офлайн-запуск, неверная/несовместимая Java,
/// отсутствие аккаунта. Любой HTTP-запрос считается ошибкой — так проверяется, что офлайн-путь не ходит в сеть.
/// </summary>
public class GameServiceTests
{
    private sealed class Env : IDisposable
    {
        public TempDir Dir { get; } = new("GameService тест");
        public AppPaths Paths { get; }
        public GameService Game { get; }
        public SettingsService Settings { get; }
        public AccountStore Store { get; }
        public FakeHttpHandler Http { get; }
        public GameDirectoryLayout Layout { get; }

        public Env(int requiredJava = 25)
        {
            Paths = new AppPaths(Dir.Combine("данные"));
            Paths.EnsureCreated();
            var log = new MemoryLog();
            Http = new FakeHttpHandler((Func<HttpRequestMessage, int, HttpResponseMessage>)((r, _) => throw new InvalidOperationException("Неожиданный сетевой запрос: " + r.RequestUri)));
            var http = new HttpClient(Http);
            Layout = new GameDirectoryLayout(Dir.Combine("Папка игры"));
            Settings = new SettingsService(new SettingsStore(Paths.SettingsFile, log, () => new LauncherSettings
            {
                GameDirectory = Layout.Root, MinMemoryMb = 512, MaxMemoryMb = 1024,
            }), log);
            Store = new AccountStore(Paths.AccountsFile, log);
            var guard = new OperationGuard();
            var accounts = new AccountService(() => throw new InvalidOperationException("MSAL не должен вызываться"),
                new XboxMinecraftClient(http, log), Store, log, guard);
            var downloader = new Downloader(http, new FileVerifier(), log);
            Game = new GameService(Settings, new VersionManifestService(http, Paths.ManifestCacheFile, log),
                new VersionInstaller(http, downloader, log, PlatformInfo.Current, "test"),
                new InstallationInspector(PlatformInfo.Current),
                new JavaDetector(Paths.RuntimesDirectory, log),
                new JavaRuntimeInstaller(http, downloader, Paths.RuntimesDirectory, log, PlatformInfo.Current),
                accounts, new GameLauncher(log, Paths.GameLogsDirectory, PlatformInfo.Current), guard, log, "test");

            // Минимальная «установленная» версия: описание, клиент нужного размера и маркер завершения.
            Directory.CreateDirectory(Layout.VersionDirectory("fake-1"));
            File.WriteAllText(Layout.VersionJson("fake-1"), $$$"""
                {"id":"fake-1","type":"release","mainClass":"net.minecraft.client.main.Main",
                 "javaVersion":{"component":"java-runtime-epsilon","majorVersion":{{{requiredJava}}}},
                 "downloads":{"client":{"url":"https://example.invalid/c.jar","sha1":"00","size":4}},
                 "arguments":{"game":["--username","${auth_player_name}","--accessToken","${auth_access_token}"],"jvm":["-cp","${classpath}"]},
                 "libraries":[]}
                """);
            File.WriteAllText(Layout.VersionJar("fake-1"), "jar!");
            File.WriteAllText(Layout.InstallMarker("fake-1"), """{"VersionId":"fake-1","VersionJsonSha1":null,"InstalledUtc":"2026-01-01T00:00:00Z","LauncherVersion":"t"}""");
        }

        public void AddLicensedAccount()
        {
            Store.Upsert(new StoredAccount
            {
                Id = "acc", MinecraftName = "Steve", MinecraftUuid = "0123456789abcdef0123456789abcdef", OwnsGame = true,
                OwnershipVerifiedUtc = DateTime.UtcNow.AddDays(-3), MinecraftAccessToken = "cached-token-123456789",
                MinecraftTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(-2), // сессия истекла — офлайн это допустимо
            });
            Settings.Update(s => s.SelectedAccountId = "acc");
        }

        public void SetJava(string? path) => Settings.Update(s => s.JavaPath = path);

        public void Dispose() => Dir.Dispose();
    }

    private static string? FindJdk(int major)
    {
        var root = @"C:\Program Files\Eclipse Adoptium";
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, $"jdk-{major}*").Select(d => Path.Combine(d, "bin", "javaw.exe")).FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task NoAccount_LaunchIsRefused()
    {
        using var env = new Env();
        var ex = await Assert.ThrowsAsync<LauncherException>(() => env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None));
        Assert.Equal(ErrorCode.AccountRequired, ex.Code);
    }

    [Fact]
    public async Task Offline_NotInstalledVersion_ExplainsOnlineInstallIsNeeded()
    {
        using var env = new Env();
        env.AddLicensedAccount();
        var ex = await Assert.ThrowsAsync<LauncherException>(() => env.Game.PlayAsync("1.21.4", offline: true, null, CancellationToken.None));
        Assert.Equal(ErrorCode.NotInstalled, ex.Code);
        Assert.Contains("интернет", ex.Hint);
        Assert.Equal(0, env.Http.TotalRequests);
    }

    [Fact]
    public async Task Offline_MissingFile_IsNotReady()
    {
        using var env = new Env();
        env.AddLicensedAccount();
        File.Delete(env.Layout.VersionJar("fake-1"));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None));
        Assert.Equal(ErrorCode.NotInstalled, ex.Code);
    }

    [Fact]
    public async Task WrongJavaPath_IsReported()
    {
        using var env = new Env();
        env.AddLicensedAccount();
        env.SetJava(env.Dir.Combine("нет", "java.exe"));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None));
        Assert.Equal(ErrorCode.InvalidSettings, ex.Code); // путь отсекается ещё проверкой настроек
    }

    [Fact]
    public async Task IncompatibleJava_IsRefused_WithHint()
    {
        var jdk21 = FindJdk(21);
        if (jdk21 is null) return; // на машине нет JDK 21 — проверка неприменима
        using var env = new Env(requiredJava: 25);
        env.AddLicensedAccount();
        env.SetJava(jdk21);
        var ex = await Assert.ThrowsAsync<LauncherException>(() => env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None));
        Assert.Equal(ErrorCode.JavaIncompatible, ex.Code);
        Assert.Contains("25", ex.UserMessage);
    }

    [Fact]
    public async Task Offline_LicensedAccount_StartsJavaWithoutNetwork()
    {
        var jdk = FindJdk(25) ?? FindJdk(21);
        if (jdk is null) return;
        var major = JavaDetector.Inspect(jdk)!.MajorVersion;
        using var env = new Env(requiredJava: major);
        env.AddLicensedAccount();
        env.SetJava(jdk);

        using var session = await env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None);
        var exit = await session.Exited.WaitAsync(TimeSpan.FromSeconds(30));

        // Клиент поддельный, поэтому Java завершится с ошибкой — важно, что процесс был запущен без сети
        // и офлайн-токен не попал в журнал игры.
        Assert.Equal(0, env.Http.TotalRequests);
        Assert.NotEqual(0, exit.ExitCode);
        var log = File.ReadAllText(session.LogFile);
        Assert.Contains("--accessToken ***", log);
        Assert.DoesNotContain("cached-token-123456789", log);
    }

    [Fact]
    public async Task Online_InstalledVersion_LaunchesWithoutAnyDownload()
    {
        var jdk = FindJdk(25) ?? FindJdk(21);
        if (jdk is null) return;
        var major = JavaDetector.Inspect(jdk)!.MajorVersion;
        using var env = new Env(requiredJava: major);
        env.AddLicensedAccount();
        var acc = env.Store.Find("acc")!;
        acc.MinecraftTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(12); // действующая сессия — обновление не нужно
        env.Store.Upsert(acc);
        env.SetJava(jdk);

        using var session = await env.Game.PlayAsync("fake-1", offline: false, null, CancellationToken.None);
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, env.Http.TotalRequests);
    }

    [Fact]
    public async Task SecondLaunch_WhileRunning_IsRefused()
    {
        var jdk = FindJdk(25) ?? FindJdk(21);
        if (jdk is null) return;
        var major = JavaDetector.Inspect(jdk)!.MajorVersion;
        using var env = new Env(requiredJava: major);
        env.AddLicensedAccount();
        env.SetJava(jdk);
        // JVM-аргумент, который держит процесс живым достаточно долго не нужен: проверяем охрану «play» параллельным вызовом.
        var first = env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None);
        var second = env.Game.PlayAsync("fake-1", offline: true, null, CancellationToken.None);
        var results = await Task.WhenAll(first.ContinueWith(t => t), second.ContinueWith(t => t));
        Assert.Contains(results, t => t.IsFaulted && t.Exception!.InnerException is LauncherException { Code: ErrorCode.OperationInProgress });
        foreach (var t in results.Where(t => t.IsCompletedSuccessfully)) (await t).Dispose();
    }
}
