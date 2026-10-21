using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UltraLauncher.Core.Java;

public sealed record JavaInstallation(string JavaExecutable, int MajorVersion, string VersionText, bool Is64Bit, string Source)
{
    public string Home => Path.GetDirectoryName(Path.GetDirectoryName(JavaExecutable)!)!;

    /// <summary>javaw.exe рядом (без консольного окна), если есть.</summary>
    public string PreferredExecutable
    {
        get
        {
            var javaw = Path.Combine(Path.GetDirectoryName(JavaExecutable)!, "javaw.exe");
            return File.Exists(javaw) ? javaw : JavaExecutable;
        }
    }

    public string DisplayName => $"Java {MajorVersion} ({VersionText}{(Is64Bit ? ", 64-bit" : ", 32-bit")}) — {Home}";
}

public static partial class JavaVersionParser
{
    /// <summary>"1.8.0_51" → 8, "17.0.2" → 17, "25" → 25.</summary>
    public static int? ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var m = VersionPattern().Match(version);
        if (!m.Success) return null;
        var first = int.Parse(m.Groups[1].Value);
        if (first == 1 && m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
        return first;
    }

    /// <summary>Читает JAVA_VERSION из файла release в корне JDK/JRE.</summary>
    public static string? ReadReleaseFile(string javaHome)
    {
        var release = Path.Combine(javaHome, "release");
        if (!File.Exists(release)) return null;
        foreach (var line in File.ReadLines(release))
        {
            if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                return line["JAVA_VERSION=".Length..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>Парсит вывод `java -version` (первая строка: version "17.0.2" ...).</summary>
    public static string? ParseVersionOutput(string output)
    {
        var m = QuotedVersion().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? QueryByRunning(string javaExe, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return ParseVersionOutput(errTask.Result + "\n" + outTask.Result);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("version \"([^\"]+)\"")]
    private static partial Regex QuotedVersion();
}

public static class PeArchitecture
{
    /// <summary>true для x64/ARM64 исполняемого файла, false для 32-битного, null если не удалось прочитать.</summary>
    public static bool? Is64Bit(string exePath)
    {
        try
        {
            using var fs = File.OpenRead(exePath);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            fs.Seek(peOffset + 4, SeekOrigin.Begin);
            var machine = br.ReadUInt16();
            return machine switch
            {
                0x8664 or 0xAA64 => true,
                0x014C => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
