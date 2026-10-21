using Microsoft.Win32;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Java;

/// <summary>Ищет установленные Java: среды Mojang, JAVA_HOME, PATH, реестр и типовые папки.</summary>
public sealed class JavaDetector
{
    private static readonly string[] VendorFolders =
    [
        "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Microsoft", "Zulu",
        "BellSoft", "Amazon Corretto", "Semeru", "OpenJDK", "RedHat", "GraalVM",
    ];

    private readonly string _runtimesDirectory;
    private readonly ILauncherLog _log;

    public JavaDetector(string runtimesDirectory, ILauncherLog log)
    {
        _runtimesDirectory = runtimesDirectory;
        _log = log;
    }

    public IReadOnlyList<JavaInstallation> Detect()
    {
        var candidates = new List<(string exe, string source)>();

        if (Directory.Exists(_runtimesDirectory))
            foreach (var dir in Directory.EnumerateDirectories(_runtimesDirectory))
                candidates.Add((Path.Combine(dir, "bin", "java.exe"), "UltraLauncher (Mojang)"));

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            candidates.Add((Path.Combine(javaHome.Trim('"'), "bin", "java.exe"), "JAVA_HOME"));

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add((Path.Combine(p.Trim('"'), "java.exe"), "PATH")); } catch (ArgumentException) { }
        }

        foreach (var home in RegistryHomes())
            candidates.Add((Path.Combine(home, "bin", "java.exe"), "Реестр"));

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                     .Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            foreach (var vendor in VendorFolders)
            {
                var vdir = Path.Combine(root, vendor);
                if (!Directory.Exists(vdir)) continue;
                foreach (var jdk in SafeDirs(vdir))
                    candidates.Add((Path.Combine(jdk, "bin", "java.exe"), vendor));
            }
        }

        // Java официального лаунчера Minecraft.
        var mcRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
        if (Directory.Exists(mcRuntime))
            foreach (var comp in SafeDirs(mcRuntime))
                foreach (var plat in SafeDirs(comp))
                    candidates.Add((Path.Combine(plat, Path.GetFileName(comp), "bin", "java.exe"), "Minecraft Launcher"));

        var result = new List<JavaInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, source) in candidates)
        {
            string full;
            try { full = Path.GetFullPath(exe); } catch { continue; }
            if (!File.Exists(full) || !seen.Add(full)) continue;
            var info = Inspect(full, source);
            if (info is not null) result.Add(info);
        }
        _log.Info("Java", $"Найдено Java: {result.Count}" + string.Concat(result.Select(j => Environment.NewLine + "  " + j.DisplayName + " [" + j.Source + "]")));
        return result.OrderByDescending(j => j.MajorVersion).ToList();
    }

    /// <summary>Определяет версию и разрядность конкретного java.exe/javaw.exe.</summary>
    public static JavaInstallation? Inspect(string javaExe, string source = "Указано вручную")
    {
        if (!File.Exists(javaExe)) return null;
        var bin = Path.GetDirectoryName(javaExe)!;
        var home = Path.GetDirectoryName(bin)!;
        var java = Path.Combine(bin, "java.exe");
        if (!File.Exists(java)) java = javaExe;

        var version = JavaVersionParser.ReadReleaseFile(home) ?? JavaVersionParser.QueryByRunning(java, TimeSpan.FromSeconds(10));
        var major = JavaVersionParser.ParseMajor(version);
        if (major is null) return null;
        var is64 = PeArchitecture.Is64Bit(java) ?? true;
        return new JavaInstallation(java, major.Value, version!, is64, source);
    }

    private static IEnumerable<string> RegistryHomes()
    {
        var keys = new[]
        {
            @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\Eclipse Adoptium\JDK", @"SOFTWARE\Eclipse Adoptium\JRE",
            @"SOFTWARE\Microsoft\JDK", @"SOFTWARE\Azul Systems\Zulu", @"SOFTWARE\BellSoft\Liberica",
        };
        var homes = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var keyPath in keys)
            {
                using var key = hklm.OpenSubKey(keyPath);
                if (key is null) continue;
                Collect(key, homes, 0);
            }
        }
        return homes;
    }

    private static void Collect(RegistryKey key, List<string> homes, int depth)
    {
        foreach (var name in new[] { "JavaHome", "Path", "InstallationPath" })
            if (key.GetValue(name) is string v && Directory.Exists(v))
                homes.Add(v);
        if (depth >= 3) return;
        foreach (var sub in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(sub);
            if (child is not null) Collect(child, homes, depth + 1);
        }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); } catch { return []; }
    }
}
