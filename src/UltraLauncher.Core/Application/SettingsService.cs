using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.Core.Application;

/// <summary>Текущие настройки + сохранение с проверкой.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly ILauncherLog _log;
    private LauncherSettings _current;

    public SettingsService(SettingsStore store, ILauncherLog log)
    {
        _store = store;
        _log = log;
        var loaded = store.Load();
        _current = loaded.Settings;
        RecoveryNotice = loaded.RecoveryNotice;
        if (RecoveryNotice is not null) log.Warn("Settings", RecoveryNotice);
    }

    /// <summary>Сообщение о восстановлении повреждённых настроек (показывается один раз при запуске).</summary>
    public string? RecoveryNotice { get; }

    /// <summary>Копия текущих настроек (изменения применяются только через <see cref="Save"/>).</summary>
    public LauncherSettings Current => _current.Clone();

    public event Action<LauncherSettings>? Changed;

    public IReadOnlyList<SettingsIssue> Validate(LauncherSettings s) => SettingsValidator.Validate(s, SystemMemory.TotalPhysicalMb());

    public IReadOnlyList<SettingsIssue> Save(LauncherSettings s)
    {
        var issues = Validate(s);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            throw new LauncherException(ErrorCode.InvalidSettings, "Настройки содержат ошибки и не сохранены.",
                string.Join(Environment.NewLine, issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
        s.GameDirectory = Path.GetFullPath(s.GameDirectory);
        var writable = SettingsValidator.CheckWritable(s.GameDirectory);
        if (writable is not null)
            throw new LauncherException(ErrorCode.AccessDenied, writable, "Выберите другую папку игры.");

        _store.Save(s);
        _current = s.Clone();
        _log.Info("Settings", $"Настройки сохранены: папка игры {s.GameDirectory}, RAM {s.MinMemoryMb}-{s.MaxMemoryMb} МБ, Java {(s.JavaPath ?? "авто")}");
        Changed?.Invoke(Current);
        return issues;
    }

    /// <summary>Небольшие изменения без полной проверки (выбранная версия/аккаунт).</summary>
    public void Update(Action<LauncherSettings> change)
    {
        var copy = _current.Clone();
        change(copy);
        try
        {
            _store.Save(copy);
        }
        catch (Exception ex)
        {
            _log.Warn("Settings", "Не удалось сохранить настройки", ex);
        }
        _current = copy;
        Changed?.Invoke(Current);
    }
}
