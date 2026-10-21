using System.Text.RegularExpressions;
using UltraLauncher.Core.Common;

namespace UltraLauncher.Core.Minecraft;

/// <summary>Данные игрока для запуска. AccessToken — секрет, в журнал не попадает.</summary>
public sealed record PlayerIdentity(string Name, string Uuid, string AccessToken, string Xuid, string UserType, bool IsDemo)
{
    public override string ToString() => $"{Name} ({Uuid}, {UserType}{(IsDemo ? ", demo" : "")})";
}

public sealed class LaunchRequest
{
    public required InstallPlan Plan { get; init; }
    public required GameDirectoryLayout Layout { get; init; }
    public required string JavaPath { get; init; }
    public required PlayerIdentity Player { get; init; }
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 2048;
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
    public string LauncherName { get; init; } = "UltraLauncher";
    public string LauncherVersion { get; init; } = "1.0.0";
    public string? ClientId { get; init; }
}

/// <summary>
/// Формирует командную строку Java для запуска Minecraft. Каждый аргумент — отдельный элемент списка,
/// поэтому пути с пробелами и кириллицей передаются без ручного экранирования.
/// </summary>
public static partial class LaunchArgumentsBuilder
{
    /// <summary>JVM-аргументы для старых версий без блока arguments.jvm (как у официального лаунчера).</summary>
    private static readonly string[] LegacyJvmTemplate =
    [
        "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump",
        "-Djava.library.path=${natives_directory}",
        "-Dminecraft.launcher.brand=${launcher_name}",
        "-Dminecraft.launcher.version=${launcher_version}",
        "-cp",
        "${classpath}",
    ];

    public static List<string> Build(LaunchRequest request, PlatformInfo platform)
    {
        var plan = request.Plan;
        var version = plan.Version;
        var features = new LaunchFeatures { IsDemoUser = request.Player.IsDemo };
        var values = BuildPlaceholders(request);

        var args = new List<string>
        {
            $"-Xms{request.MinMemoryMb}M",
            $"-Xmx{request.MaxMemoryMb}M",
        };

        if (version.JvmArguments.Count > 0)
            args.AddRange(Expand(version.JvmArguments, platform, features, values));
        else
            args.AddRange(LegacyJvmTemplate.Select(a => Substitute(a, values)));

        if (version.Logging is { } logging && plan.LogConfigPath is not null)
            args.Add(logging.Argument.Replace("${path}", plan.LogConfigPath, StringComparison.Ordinal));

        // Дополнительные аргументы пользователя — перед главным классом, после стандартных.
        args.AddRange(request.ExtraJvmArguments);

        args.Add(version.MainClass ?? throw new LauncherException(ErrorCode.Integrity, "Не указан главный класс игры."));

        if (version.GameArguments.Count > 0)
            args.AddRange(Expand(version.GameArguments, platform, features, values));
        else if (version.LegacyMinecraftArguments is { } legacy)
        {
            // Сначала разбиваем шаблон по пробелам, затем подставляем — значения с пробелами остаются цельными.
            args.AddRange(legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => Substitute(a, values)));
            if (request.Player.IsDemo) args.Add("--demo");
        }

        return args;
    }

    public static string BuildClasspath(InstallPlan plan)
        => string.Join(Path.PathSeparator, plan.ClasspathEntries.Distinct(StringComparer.OrdinalIgnoreCase));

    internal static Dictionary<string, string> BuildPlaceholders(LaunchRequest r)
    {
        var plan = r.Plan;
        var v = plan.Version;
        var natives = r.Layout.NativesDirectory(v.Id);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = r.Player.Name,
            ["auth_uuid"] = r.Player.Uuid,
            ["auth_access_token"] = r.Player.AccessToken,
            ["auth_session"] = r.Player.AccessToken,
            ["auth_xuid"] = r.Player.Xuid,
            ["user_type"] = r.Player.UserType,
            ["user_properties"] = "{}",
            ["clientid"] = r.ClientId ?? "",
            ["version_name"] = v.Id,
            ["version_type"] = v.Type,
            ["game_directory"] = r.Layout.Root,
            ["assets_root"] = r.Layout.AssetsDirectory,
            ["game_assets"] = r.Layout.AssetsDirectory,
            ["assets_index_name"] = v.AssetIndex?.Id ?? v.AssetsId ?? "legacy",
            ["natives_directory"] = natives,
            ["library_directory"] = r.Layout.LibrariesDirectory,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath"] = BuildClasspath(plan),
            ["launcher_name"] = r.LauncherName,
            ["launcher_version"] = r.LauncherVersion,
        };
    }

    private static IEnumerable<string> Expand(IEnumerable<ArgumentEntry> entries, PlatformInfo platform, LaunchFeatures features, Dictionary<string, string> values)
    {
        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, platform, features)) continue;
            foreach (var raw in entry.Values)
                yield return Substitute(raw, values);
        }
    }

    internal static string Substitute(string template, IReadOnlyDictionary<string, string> values)
        => Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    [GeneratedRegex(@"\$\{([a-zA-Z0-9_]+)\}")]
    private static partial Regex Placeholder();
}
