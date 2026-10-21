using UltraLauncher.Core.Common;

namespace UltraLauncher.Core.Settings;

public enum IssueSeverity { Warning, Error }

public sealed record SettingsIssue(string Field, IssueSeverity Severity, string Message);

/// <summary>Проверка настроек до сохранения и перед запуском игры.</summary>
public static class SettingsValidator
{
    public const int MinAllowedMemoryMb = 512;
    public const int ReservedForSystemMb = 1024;

    public static IReadOnlyList<SettingsIssue> Validate(LauncherSettings s, int totalPhysicalMb)
    {
        var issues = new List<SettingsIssue>();

        var dirError = ValidateDirectory(s.GameDirectory);
        if (dirError is not null) issues.Add(new SettingsIssue(nameof(s.GameDirectory), IssueSeverity.Error, dirError));

        if (s.JavaPath is not null)
        {
            var javaError = ValidateJavaPath(s.JavaPath);
            if (javaError is not null) issues.Add(new SettingsIssue(nameof(s.JavaPath), IssueSeverity.Error, javaError));
        }

        if (s.MinMemoryMb < MinAllowedMemoryMb)
            issues.Add(new SettingsIssue(nameof(s.MinMemoryMb), IssueSeverity.Error, $"Минимальная память должна быть не меньше {MinAllowedMemoryMb} МБ."));
        if (s.MaxMemoryMb < s.MinMemoryMb)
            issues.Add(new SettingsIssue(nameof(s.MaxMemoryMb), IssueSeverity.Error, "Максимальная память не может быть меньше минимальной."));
        if (totalPhysicalMb > 0)
        {
            if (s.MaxMemoryMb > totalPhysicalMb - ReservedForSystemMb)
                issues.Add(new SettingsIssue(nameof(s.MaxMemoryMb), IssueSeverity.Error,
                    $"На компьютере {totalPhysicalMb} МБ памяти. Оставьте системе хотя бы {ReservedForSystemMb} МБ: максимум — {Math.Max(MinAllowedMemoryMb, totalPhysicalMb - ReservedForSystemMb)} МБ."));
            else if (s.MaxMemoryMb > totalPhysicalMb * 3 / 4)
                issues.Add(new SettingsIssue(nameof(s.MaxMemoryMb), IssueSeverity.Warning,
                    "Выделено больше 75% памяти компьютера — система и другие программы могут замедлиться."));
        }

        var extra = CommandLine.Split(s.ExtraJvmArguments);
        if (extra.Any(a => a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)))
            issues.Add(new SettingsIssue(nameof(s.ExtraJvmArguments), IssueSeverity.Warning,
                "Память задаётся ползунками выше; -Xmx/-Xms в дополнительных аргументах переопределят их."));
        if (extra.Any(a => a is "-cp" or "-classpath" || a.StartsWith("-Djava.library.path", StringComparison.Ordinal)))
            issues.Add(new SettingsIssue(nameof(s.ExtraJvmArguments), IssueSeverity.Error,
                "Нельзя переопределять -cp/-classpath и java.library.path — лаунчер задаёт их сам."));

        return issues;
    }

    public static string? ValidateDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Укажите папку игры.";
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "Путь содержит недопустимые символы.";
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "Укажите полный путь, например D:\\Games\\Minecraft.";
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return "Некорректный путь к папке.";
        }

        var root = Path.GetPathRoot(full);
        if (root is null || !Directory.Exists(root)) return $"Диск {root} недоступен.";
        if (File.Exists(full)) return "По этому пути находится файл, а не папка.";
        return null;
    }

    public static string? ValidateJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Укажите путь к java.exe или javaw.exe.";
        if (!File.Exists(path)) return "Файл Java не найден по указанному пути.";
        var name = Path.GetFileName(path);
        if (!name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            return "Выберите файл java.exe или javaw.exe (обычно в папке bin установленной Java).";
        return null;
    }

    /// <summary>Проверяет, что в папку можно писать (создаёт её при необходимости).</summary>
    public static string? CheckWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".ul-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Нет прав на запись в папку игры: " + ex.Message;
        }
    }
}
