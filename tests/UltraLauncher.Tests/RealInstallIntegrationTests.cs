using System.Diagnostics;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Java;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;
using Xunit.Abstractions;

namespace UltraLauncher.Tests;

/// <summary>
/// Интеграционные проверки с РЕАЛЬНЫМИ серверами Mojang (сотни МБ загрузок).
/// Включаются переменной окружения ULTRALAUNCHER_E2E=1, папка — ULTRALAUNCHER_E2E_DIR.
/// Запуск игры здесь — проверка конвейера запуска в официальном демо-режиме без входа Microsoft;
/// это НЕ подтверждение лицензионного входа.
/// </summary>
/// <summary>Факт, который пропускается (а не «проходит»), если интеграционные проверки не включены.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ULTRALAUNCHER_E2E") != "1")
            Skip = "Интеграционная проверка с серверами Mojang: включите ULTRALAUNCHER_E2E=1";
    }
}

[Trait("Category", "Integration")]
public class RealInstallIntegrationTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable("ULTRALAUNCHER_E2E_DIR")
                                  ?? Path.Combine(Path.GetTempPath(), "UltraLauncher E2E", "Папка игры");

    private sealed class OutputLog(ITestOutputHelper o) : ILauncherLog
    {
        public void Write(LogLevel level, string source, string message, Exception? ex = null)
        {
            try { o.WriteLine($"[{level}] {source}: {SecretRedactor.Redact(message)}{(ex is null ? "" : " " + ex.Message)}"); } catch { }
        }
    }

    [E2EFact]
    public async Task InstallReinstallRepairAndLaunch_LatestRelease()
    {
        var version = Environment.GetEnvironmentVariable("ULTRALAUNCHER_E2E_VERSION");
        var log = new OutputLog(output);
        using var http = LauncherCore.CreateHttpClient("e2e");
        var cacheFile = Path.Combine(Root, "..", "verified.json");
        var verifier = new FileVerifier(new VerificationCache(cacheFile));
        var downloader = new Downloader(http, verifier, log);
        var installer = new VersionInstaller(http, downloader, log, PlatformInfo.Current, "e2e");
        var manifest = (await new VersionManifestService(http, Path.Combine(Root, "..", "manifest.json"), log).LoadAsync(CancellationToken.None)).Manifest;
        version ??= manifest.LatestRelease!;
        var layout = new GameDirectoryLayout(Root);
        output.WriteLine($"Версия {version}, папка {layout.Root}");

        // 1. Установка.
        var sw = Stopwatch.StartNew();
        var plan = await installer.InstallAsync(layout, version, manifest, false, null, CancellationToken.None);
        output.WriteLine($"Установка: {sw.Elapsed}, файлов {plan.Files.Count}");
        Assert.True(File.Exists(plan.ClientJarPath));
        Assert.NotNull(InstallationInspector.ReadMarker(layout, version));

        // 2. Повторная установка: ничего не скачивается.
        var requestsBefore = 0;
        var counting = new CountingHandler(new SocketsHttpHandler());
        using var http2 = new HttpClient(counting);
        var installer2 = new VersionInstaller(http2, new Downloader(http2, new FileVerifier(new VerificationCache(cacheFile)), log), log, PlatformInfo.Current, "e2e");
        sw.Restart();
        await installer2.InstallAsync(layout, version, manifest, false, null, CancellationToken.None);
        output.WriteLine($"Повторная установка: {sw.Elapsed}, HTTP-запросов: {counting.Count - requestsBefore}");
        Assert.Equal(0, counting.Count);

        // 3. Повреждённый файл: портим ассет и библиотеку, «ремонт» их перекачивает.
        var asset = plan.Files.First(f => f.Category == DownloadCategory.Asset);
        var lib = plan.Files.First(f => f.Category == DownloadCategory.Library && f.Size > 1000);
        var bytes = File.ReadAllBytes(lib.Path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(lib.Path, bytes);
        File.Delete(asset.Path);
        await installer2.InstallAsync(layout, version, manifest, true, null, CancellationToken.None);
        Assert.Equal(FileState.Valid, new FileVerifier().Verify(lib, forceRehash: true));
        Assert.Equal(FileState.Valid, new FileVerifier().Verify(asset, forceRehash: true));
        output.WriteLine($"Ремонт: перекачано запросов {counting.Count}");

        // 4. Запуск игры (демо-режим, без входа Microsoft) и проверка, что клиент дошёл до инициализации.
        var java = JavaCompatibility.PickBest(new JavaDetector(Path.Combine(Root, "..", "runtimes"), log).Detect(), plan.Version.Java)
                   ?? throw new InvalidOperationException("Нет подходящей Java");
        output.WriteLine("Java: " + java.DisplayName);
        var launcher = new GameLauncher(log, Path.Combine(Root, "..", "game-logs"), PlatformInfo.Current);
        using var session = launcher.Start(new LaunchRequest
        {
            Plan = InstallationInspector.FindMissingFiles(plan).Count == 0 ? plan : throw new InvalidOperationException("Файлы отсутствуют"),
            Layout = layout,
            JavaPath = java.PreferredExecutable,
            Player = new PlayerIdentity("UltraTest", "00000000000040008000000000000001", "0", "0", "msa", IsDemo: true),
            MinMemoryMb = 1024,
            MaxMemoryMb = 2048,
        });
        var lines = new List<string>();
        session.OutputLine += l => { lock (lines) lines.Add(l); };
        var deadline = DateTime.UtcNow.AddSeconds(150);
        bool started = false;
        while (DateTime.UtcNow < deadline && !session.HasExited)
        {
            await Task.Delay(1000);
            lock (lines) started = lines.Any(l => l.Contains("Backend library: LWJGL") || l.Contains("OpenAL initialized") || l.Contains("Sound engine started") || l.Contains("Created:") && l.Contains("atlas"));
            if (started) break;
        }
        var alive = !session.HasExited;
        output.WriteLine($"Процесс жив: {alive}, признак инициализации клиента: {started}, лог: {session.LogFile}");
        lock (lines) foreach (var l in lines.TakeLast(25)) output.WriteLine("  | " + l);
        await Task.Delay(5000);
        session.Kill();
        Assert.True(started, "Клиент Minecraft не дошёл до инициализации рендера/звука");
    }

    [E2EFact]
    public async Task LegacyVersion_WithMojangJava8_InstallsAndLaunches()
    {
        var log = new OutputLog(output);
        using var http = LauncherCore.CreateHttpClient("e2e");
        var downloader = new Downloader(http, new FileVerifier(new VerificationCache(Path.Combine(Root, "..", "verified.json"))), log);
        var manifest = (await new VersionManifestService(http, Path.Combine(Root, "..", "manifest.json"), log).LoadAsync(CancellationToken.None)).Manifest;
        var layout = new GameDirectoryLayout(Root);
        var plan = await new VersionInstaller(http, downloader, log, PlatformInfo.Current, "e2e")
            .InstallAsync(layout, "1.12.2", manifest, false, null, CancellationToken.None);
        Assert.Null(plan.Version.JvmArguments.FirstOrDefault());
        Assert.NotNull(plan.Version.LegacyMinecraftArguments);
        Assert.True(Directory.GetFiles(layout.NativesDirectory("1.12.2"), "*.dll", SearchOption.AllDirectories).Length > 0, "natives не распакованы");

        // Официальная Java 8 от Mojang (jre-legacy) — тем же механизмом, что и в лаунчере.
        var runtimes = Path.Combine(Root, "..", "runtimes");
        var java = await new JavaRuntimeInstaller(http, downloader, runtimes, log, PlatformInfo.Current)
            .InstallAsync(plan.Version.Java!, null, CancellationToken.None);
        output.WriteLine("Java: " + java.DisplayName);
        Assert.Equal(8, java.MajorVersion);

        using var session = new GameLauncher(log, Path.Combine(Root, "..", "game-logs"), PlatformInfo.Current).Start(new LaunchRequest
        {
            Plan = plan,
            Layout = layout,
            JavaPath = java.PreferredExecutable,
            Player = new PlayerIdentity("UltraTest", "00000000000040008000000000000001", "0", "0", "msa", IsDemo: true),
            MinMemoryMb = 512,
            MaxMemoryMb = 1536,
        });
        var lines = new List<string>();
        session.OutputLine += l => { lock (lines) lines.Add(l); };
        var started = false;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline && !session.HasExited && !started)
        {
            await Task.Delay(1000);
            lock (lines) started = lines.Any(l => l.Contains("LWJGL Version") || l.Contains("OpenAL initialized") || l.Contains("Sound engine started"));
        }
        output.WriteLine($"Процесс жив: {!session.HasExited}, инициализация: {started}");
        lock (lines) foreach (var l in lines.TakeLast(15)) output.WriteLine("  | " + l);
        await Task.Delay(3000);
        session.Kill();
        Assert.True(started, "Minecraft 1.12.2 не дошёл до инициализации LWJGL");
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
