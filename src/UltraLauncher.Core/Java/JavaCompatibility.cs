using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.Core.Java;

public enum CompatibilityLevel { Compatible, Warning, Incompatible }

public sealed record CompatibilityResult(CompatibilityLevel Level, string Message);

/// <summary>Совместимость Java и версии Minecraft (по полю javaVersion из метаданных).</summary>
public static class JavaCompatibility
{
    /// <summary>Требование по умолчанию для старых версий без javaVersion.</summary>
    public static readonly JavaRequirement LegacyDefault = new("jre-legacy", 8);

    public static CompatibilityResult Check(JavaInstallation java, JavaRequirement? requirement)
    {
        var req = requirement ?? LegacyDefault;
        if (!java.Is64Bit)
            return new CompatibilityResult(CompatibilityLevel.Warning,
                "32-битная Java ограничивает память ~1,5 ГБ. Рекомендуется 64-битная Java.");
        if (java.MajorVersion < req.MajorVersion)
            return new CompatibilityResult(CompatibilityLevel.Incompatible,
                $"Эта версия Minecraft требует Java {req.MajorVersion} или новее, выбрана Java {java.MajorVersion}.");
        if (req.MajorVersion == 8 && java.MajorVersion > 8)
            return new CompatibilityResult(CompatibilityLevel.Warning,
                $"Версия рассчитана на Java 8; с Java {java.MajorVersion} она может не запуститься. Рекомендуется Java 8.");
        if (java.MajorVersion > req.MajorVersion + 4)
            return new CompatibilityResult(CompatibilityLevel.Warning,
                $"Рекомендуется Java {req.MajorVersion}; Java {java.MajorVersion} значительно новее и не проверялась Mojang для этой версии.");
        return new CompatibilityResult(CompatibilityLevel.Compatible, $"Java {java.MajorVersion} подходит.");
    }

    /// <summary>Лучшая Java из найденных: совместимая, ближайшая к требуемой версии, 64-битная.</summary>
    public static JavaInstallation? PickBest(IEnumerable<JavaInstallation> installations, JavaRequirement? requirement)
    {
        var req = requirement ?? LegacyDefault;
        return installations
            .Select(j => (j, r: Check(j, req)))
            .Where(x => x.r.Level != CompatibilityLevel.Incompatible)
            .OrderBy(x => x.r.Level)
            .ThenBy(x => Math.Abs(x.j.MajorVersion - req.MajorVersion))
            .ThenByDescending(x => x.j.Source.StartsWith("UltraLauncher", StringComparison.Ordinal))
            .Select(x => x.j)
            .FirstOrDefault();
    }
}
