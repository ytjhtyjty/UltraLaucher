namespace UltraLauncher.Core.Settings;

/// <summary>Настройки лаунчера. Секретов здесь нет — только пути и параметры.</summary>
public sealed class LauncherSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Папка игры (versions, libraries, assets, saves…).</summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>Путь к java.exe/javaw.exe. Пусто — выбирать автоматически.</summary>
    public string? JavaPath { get; set; }

    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>Дополнительные аргументы JVM (расширенные настройки).</summary>
    public string ExtraJvmArguments { get; set; } = string.Empty;

    public bool ShowSnapshots { get; set; }

    public string? SelectedVersionId { get; set; }
    public string? SelectedAccountId { get; set; }

    /// <summary>
    /// Client ID приложения в Microsoft Entra (Azure). Реального значения в поставке нет —
    /// его нужно зарегистрировать самостоятельно (см. README).
    /// </summary>
    public string? MicrosoftClientId { get; set; }

    public LauncherSettings Clone() => (LauncherSettings)MemberwiseClone();
}
