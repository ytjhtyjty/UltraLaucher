using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace UltraLauncher.Core.Minecraft;

public enum RuleAction { Allow, Disallow }

public sealed record OsCondition(string? Name, string? Arch, string? VersionPattern);

public sealed record Rule(RuleAction Action, OsCondition? Os, IReadOnlyDictionary<string, bool>? Features);

/// <summary>Платформа, для которой оцениваются правила (подменяется в тестах).</summary>
public sealed record PlatformInfo(string OsName, string Arch, string OsVersion, bool Is64Bit)
{
    /// <summary>Текущая Windows. Имена совпадают с теми, что использует Mojang: windows / x86 / arm64.</summary>
    public static PlatformInfo Current { get; } = new(
        "windows",
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x86_64",
        },
        Environment.OSVersion.Version.ToString(2),
        Environment.Is64BitOperatingSystem);

    /// <summary>Суффикс natives для ${arch} в старых версиях: 32 или 64.</summary>
    public string ArchBits => Is64Bit ? "64" : "32";
}

/// <summary>Флаги возможностей лаунчера для правил аргументов (features).</summary>
public sealed class LaunchFeatures
{
    public bool IsDemoUser { get; init; }
    public bool HasCustomResolution { get; init; }

    public bool Get(string name) => name switch
    {
        "is_demo_user" => IsDemoUser,
        "has_custom_resolution" => HasCustomResolution,
        _ => false, // quick play и прочее не поддерживаются — правило не срабатывает
    };
}

/// <summary>
/// Оценка правил Mojang: без правил — разрешено; иначе по умолчанию запрещено,
/// и каждое подходящее правило по порядку задаёт результат.
/// </summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, PlatformInfo platform, LaunchFeatures? features = null)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule, platform, features ?? new LaunchFeatures()))
                allowed = rule.Action == RuleAction.Allow;
        }
        return allowed;
    }

    private static bool Matches(Rule rule, PlatformInfo platform, LaunchFeatures features)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !string.Equals(os.Name, platform.OsName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.Arch is not null && !ArchMatches(os.Arch, platform))
                return false;
            if (os.VersionPattern is not null)
            {
                try
                {
                    if (!Regex.IsMatch(platform.OsVersion, os.VersionPattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                        return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        if (rule.Features is not null)
        {
            foreach (var (name, expected) in rule.Features)
                if (features.Get(name) != expected) return false;
        }
        return true;
    }

    private static bool ArchMatches(string arch, PlatformInfo platform) => arch.ToLowerInvariant() switch
    {
        "x86" => platform.Arch == "x86",
        "x86_64" or "amd64" or "x64" => platform.Arch == "x86_64",
        "arm64" or "aarch64" => platform.Arch == "arm64",
        _ => string.Equals(arch, platform.Arch, StringComparison.OrdinalIgnoreCase),
    };
}
