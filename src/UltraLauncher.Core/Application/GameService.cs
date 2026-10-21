using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Java;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.Core.Application;

public sealed record VersionItem(
    string Id,
    string Type,
    DateTimeOffset ReleaseTime,
    bool IsSupported,
    string? UnsupportedReason,
    InstallState? InstallState,
    bool IsLatest);

public sealed record VersionListResult(IReadOnlyList<VersionItem> Versions, bool FromCache, DateTime? CachedAtUtc, string? LatestRelease);

/// <summary>
/// Сценарии «список версий → установка → запуск». Интерфейс вызывает только этот класс
/// и <see cref="AccountService"/>; сеть, сборка команды и секреты скрыты здесь.
/// </summary>
public sealed class GameService
{
    private readonly SettingsService _settings;
    private readonly VersionManifestService _manifestService;
    private readonly VersionInstaller _installer;
    private readonly InstallationInspector _inspector;
    private readonly JavaDetector _javaDetector;
    private readonly JavaRuntimeInstaller _javaInstaller;
    private readonly AccountService _accounts;
    private readonly GameLauncher _launcher;
    private readonly OperationGuard _guard;
    private readonly ILauncherLog _log;
    private readonly string _launcherVersion;
    private VersionManifest? _manifest;
    private IReadOnlyList<JavaInstallation>? _javaCache;

    public GameService(SettingsService settings, VersionManifestService manifestService, VersionInstaller installer,
        InstallationInspector inspector, JavaDetector javaDetector, JavaRuntimeInstaller javaInstaller,
        AccountService accounts, GameLauncher launcher, OperationGuard guard, ILauncherLog log, string launcherVersion)
    {
        _settings = settings;
        _manifestService = manifestService;
        _installer = installer;
        _inspector = inspector;
        _javaDetector = javaDetector;
        _javaInstaller = javaInstaller;
        _accounts = accounts;
        _launcher = launcher;
        _guard = guard;
        _log = log;
        _launcherVersion = launcherVersion;
    }

    public GameSession? RunningSession { get; private set; }

    public event Action<GameSession>? SessionStarted;

    public GameDirectoryLayout Layout => new(_settings.Current.GameDirectory);

    public bool IsBusy(string key) => _guard.IsRunning(key);

    // ---------- Версии ----------

    public async Task<VersionListResult> GetVersionsAsync(CancellationToken ct)
    {
        var installed = SafeListInstalled().ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = await _manifestService.LoadAsync(ct).ConfigureAwait(false);
            _manifest = result.Manifest;
            var items = result.Manifest.Versions.Select(v =>
            {
                var supported = VersionSupport.IsSupported(v, out var reason);
                return new VersionItem(v.Id, v.Type, v.ReleaseTime, supported, reason,
                    installed.TryGetValue(v.Id, out var iv) ? iv.State : null,
                    v.Id == result.Manifest.LatestRelease || v.Id == result.Manifest.LatestSnapshot);
            }).ToList();
            // Локальные версии, которых нет в манифесте (например, удалённые снапшоты).
            foreach (var iv in installed.Values.Where(i => items.All(x => !x.Id.Equals(i.Id, StringComparison.OrdinalIgnoreCase))))
                items.Add(new VersionItem(iv.Id, iv.Type, default, true, null, iv.State, false));
            return new VersionListResult(items, result.FromCache, result.CachedAtUtc, result.Manifest.LatestRelease);
        }
        catch (LauncherException ex) when (ex.Code == ErrorCode.Network && installed.Count > 0)
        {
            // Без сети и без кеша — показываем хотя бы установленные версии.
            var items = installed.Values.Select(iv => new VersionItem(iv.Id, iv.Type, default, true, null, iv.State, false)).ToList();
            return new VersionListResult(items, true, null, null);
        }
    }

    public IReadOnlyList<InstalledVersion> SafeListInstalled()
    {
        try { return _inspector.ListInstalled(Layout); }
        catch (Exception ex) { _log.Warn("Game", "Не удалось прочитать папку версий", ex); return []; }
    }

    // ---------- Установка ----------

    public async Task<InstallPlan> InstallAsync(string versionId, bool repair, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var operation = _guard.Begin("install:" + versionId, $"Версия {versionId} уже устанавливается.");
        EnsureSettingsValid();
        var layout = Layout;
        var writable = SettingsValidator.CheckWritable(layout.Root);
        if (writable is not null) throw new LauncherException(ErrorCode.AccessDenied, writable, "Выберите другую папку игры в настройках.");
        if (_manifest is null)
        {
            try { _manifest = (await _manifestService.LoadAsync(ct).ConfigureAwait(false)).Manifest; }
            catch (LauncherException) when (File.Exists(layout.VersionJson(versionId))) { /* обойдёмся локальным описанием */ }
        }
        return await _installer.InstallAsync(layout, versionId, _manifest, repair, progress, ct).ConfigureAwait(false);
    }

    public void DeleteVersion(string versionId)
    {
        if (_guard.IsRunning("install:" + versionId))
            throw new LauncherException(ErrorCode.OperationInProgress, "Версия сейчас устанавливается.", "Отмените установку или дождитесь её окончания.");
        if (RunningSession is { HasExited: false } s && s.VersionId == versionId)
            throw new LauncherException(ErrorCode.OperationInProgress, "Эта версия сейчас запущена.", "Закройте игру и повторите.");
        InstallationInspector.DeleteVersionFiles(Layout, versionId);
        _log.Info("Game", $"Удалены файлы версии {versionId} (пользовательские данные не затронуты)");
    }

    public bool IsReadyOffline(string versionId, out string? reason) => _inspector.IsReadyOffline(Layout, versionId, out _, out reason);

    // ---------- Java ----------

    public IReadOnlyList<JavaInstallation> DetectJava(bool refresh = false)
    {
        if (refresh || _javaCache is null) _javaCache = _javaDetector.Detect();
        return _javaCache;
    }

    /// <summary>Требование к Java для установленной версии (null — версия не установлена).</summary>
    public JavaRequirement? GetJavaRequirement(string versionId)
    {
        try { return _inspector.LoadLocalPlan(Layout, versionId).Version.Java ?? JavaCompatibility.LegacyDefault; }
        catch (LauncherException) { return null; }
    }

    public async Task<JavaInstallation> InstallJavaAsync(JavaRequirement requirement, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var operation = _guard.Begin("java:" + requirement.Component, "Java уже устанавливается.");
        var java = await _javaInstaller.InstallAsync(requirement, progress, ct).ConfigureAwait(false);
        _javaCache = null;
        return java;
    }

    private async Task<JavaInstallation> ResolveJavaAsync(JavaRequirement? requirement, bool allowDownload, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var req = requirement ?? JavaCompatibility.LegacyDefault;
        var settings = _settings.Current;
        if (settings.JavaPath is not null)
        {
            var java = JavaDetector.Inspect(settings.JavaPath)
                       ?? throw new LauncherException(ErrorCode.JavaNotFound, "Указанная в настройках Java не найдена или не запускается.",
                           "Проверьте путь к Java в настройках или включите автоматический выбор.");
            var compat = JavaCompatibility.Check(java, req);
            if (compat.Level == CompatibilityLevel.Incompatible)
                throw new LauncherException(ErrorCode.JavaIncompatible, compat.Message,
                    $"Выберите Java {req.MajorVersion}+ в настройках или включите автоматический выбор Java.");
            if (compat.Level == CompatibilityLevel.Warning) _log.Warn("Java", compat.Message);
            return java;
        }

        var best = JavaCompatibility.PickBest(DetectJava(), req);
        if (best is not null && JavaCompatibility.Check(best, req).Level == CompatibilityLevel.Compatible) return best;

        if (allowDownload)
        {
            _log.Info("Java", $"Подходящая Java {req.MajorVersion} не найдена — установка среды Mojang {req.Component}");
            return await InstallJavaAsync(req, progress, ct).ConfigureAwait(false);
        }
        if (best is not null) return best;
        throw new LauncherException(ErrorCode.JavaNotFound, $"Не найдена Java {req.MajorVersion} или новее.",
            "Подключитесь к интернету — лаунчер скачает Java автоматически, или установите её вручную и укажите путь в настройках.");
    }

    // ---------- Запуск ----------

    public async Task<GameSession> PlayAsync(string versionId, bool offline, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var operation = _guard.Begin("play", "Запуск уже выполняется.");
        if (RunningSession is { HasExited: false })
            throw new LauncherException(ErrorCode.OperationInProgress, "Игра уже запущена.", "Закройте текущую игру, чтобы запустить другую.");

        EnsureSettingsValid();
        var settings = _settings.Current;
        var layout = Layout;

        // Аккаунт проверяем до загрузок, чтобы не качать гигабайты без возможности запуска.
        if (_accounts.Find(settings.SelectedAccountId) is null)
            throw new LauncherException(ErrorCode.AccountRequired, "Для запуска нужен аккаунт Microsoft.", "Войдите на вкладке «Аккаунты».");

        InstallPlan plan;
        if (offline)
        {
            if (!_inspector.IsReadyOffline(layout, versionId, out var offlinePlan, out var reason))
                throw new LauncherException(ErrorCode.NotInstalled, $"Версию {versionId} нельзя запустить офлайн: {reason}",
                    "Офлайн-запуск возможен только для полностью установленной версии. Установите её при подключении к интернету.");
            plan = offlinePlan!;
        }
        else if (_inspector.IsReadyOffline(layout, versionId, out var localPlan, out _))
        {
            _log.Info("Game", $"{versionId} уже установлена — загрузка не требуется");
            plan = localPlan!;
        }
        else
        {
            plan = await InstallAsync(versionId, repair: false, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress(InstallStage.Finalizing, "Подготовка Java…", null));
        var java = await ResolveJavaAsync(plan.Version.Java, allowDownload: !offline, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress(InstallStage.Finalizing, "Проверка аккаунта…", null));
        var player = await _accounts.GetPlayerAsync(settings.SelectedAccountId, offline, ct).ConfigureAwait(false);

        // Natives старого формата могли не распаковаться, если установка прерывалась.
        VersionInstaller.ExtractNatives(plan, layout.NativesDirectory(plan.Version.Id));

        progress?.Report(new InstallProgress(InstallStage.Finalizing, "Запуск игры…", null));
        var session = _launcher.Start(new LaunchRequest
        {
            Plan = plan,
            Layout = layout,
            JavaPath = java.PreferredExecutable,
            Player = player,
            MinMemoryMb = settings.MinMemoryMb,
            MaxMemoryMb = settings.MaxMemoryMb,
            ExtraJvmArguments = CommandLine.Split(settings.ExtraJvmArguments),
            LauncherVersion = _launcherVersion,
            ClientId = settings.MicrosoftClientId,
        });
        RunningSession = session;
        SessionStarted?.Invoke(session);
        progress?.Report(new InstallProgress(InstallStage.Completed, "Игра запущена", 1));
        return session;
    }

    private void EnsureSettingsValid()
    {
        var issues = _settings.Validate(_settings.Current).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (issues.Count > 0)
            throw new LauncherException(ErrorCode.InvalidSettings, "Исправьте настройки: " + issues[0].Message, "Откройте вкладку «Настройки».");
    }
}
