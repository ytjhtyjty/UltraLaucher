using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Settings;

public sealed record SettingsLoadResult(LauncherSettings Settings, string? RecoveryNotice);

/// <summary>
/// Хранит settings.json. Запись атомарная, предыдущая версия сохраняется в settings.json.bak.
/// Если файл повреждён — он переименовывается, а настройки восстанавливаются из резервной копии или по умолчанию.
/// </summary>
public sealed class SettingsStore
{
    // Кириллица в путях пишется как есть, чтобы файл можно было читать и править вручную.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly string _path;
    private readonly ILauncherLog _log;
    private readonly Func<LauncherSettings> _defaults;

    public SettingsStore(string path, ILauncherLog log, Func<LauncherSettings>? defaults = null)
    {
        _path = path;
        _log = log;
        _defaults = defaults ?? CreateDefaults;
    }

    public string BackupPath => _path + ".bak";

    public static LauncherSettings CreateDefaults()
    {
        var totalMb = SystemMemory.TotalPhysicalMb();
        var max = totalMb > 0 ? Math.Clamp(totalMb / 4, 2048, 4096) : 2048;
        return new LauncherSettings
        {
            GameDirectory = AppPaths.DefaultGameDirectory(),
            MinMemoryMb = 1024,
            MaxMemoryMb = max,
        };
    }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(_path) && !File.Exists(BackupPath))
            return new SettingsLoadResult(Normalize(_defaults()), null);

        if (TryRead(_path, out var settings, out var error))
            return new SettingsLoadResult(Normalize(settings!), null);

        string? movedTo = null;
        if (File.Exists(_path))
        {
            movedTo = _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(_path, movedTo, overwrite: true); } catch (Exception ex) { _log.Warn("Settings", "Не удалось переименовать повреждённый файл настроек", ex); }
            _log.Warn("Settings", $"Файл настроек повреждён ({error}). Сохранён как {movedTo}");
        }

        if (TryRead(BackupPath, out var backup, out _))
        {
            Save(backup!);
            return new SettingsLoadResult(Normalize(backup!),
                "Файл настроек был повреждён — восстановлены настройки из резервной копии.");
        }

        var defaults = Normalize(_defaults());
        Save(defaults);
        return new SettingsLoadResult(defaults,
            "Файл настроек был повреждён — восстановлены настройки по умолчанию." +
            (movedTo is null ? "" : $" Повреждённая копия: {movedTo}"));
    }

    public void Save(LauncherSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_path, json, BackupPath);
    }

    private LauncherSettings Normalize(LauncherSettings s)
    {
        var d = _defaults();
        if (string.IsNullOrWhiteSpace(s.GameDirectory)) s.GameDirectory = d.GameDirectory;
        if (s.MinMemoryMb <= 0) s.MinMemoryMb = d.MinMemoryMb;
        if (s.MaxMemoryMb <= 0) s.MaxMemoryMb = d.MaxMemoryMb;
        s.ExtraJvmArguments ??= string.Empty;
        if (string.IsNullOrWhiteSpace(s.JavaPath)) s.JavaPath = null;
        if (string.IsNullOrWhiteSpace(s.MicrosoftClientId)) s.MicrosoftClientId = null;
        s.SchemaVersion = LauncherSettings.CurrentSchemaVersion;
        return s;
    }

    private static bool TryRead(string path, out LauncherSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            if (!File.Exists(path)) { error = "файл отсутствует"; return false; }
            var text = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<LauncherSettings>(text, JsonOptions);
            if (settings is null) { error = "пустой файл"; return false; }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
