using System.Text.Json;
using UltraLauncher.Core.Common;

namespace UltraLauncher.Core.Minecraft;

/// <summary>Разбор JSON версии Mojang в <see cref="VersionDefinition"/>.</summary>
public static class VersionJsonParser
{
    public const string DefaultLibraryRepository = "https://libraries.minecraft.net/";

    public static VersionDefinition Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new LauncherException(ErrorCode.Integrity, "Файл метаданных версии повреждён.",
                "Переустановите версию — метаданные будут скачаны заново.", ex);
        }
    }

    public static VersionDefinition Parse(JsonElement root)
    {
        var id = Str(root, "id") ?? throw new LauncherException(ErrorCode.Integrity, "В метаданных версии нет id.");

        FileRef? client = null;
        if (root.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("client", out var c))
            client = ParseFile(c);

        AssetIndexRef? assetIndex = null;
        if (root.TryGetProperty("assetIndex", out var ai))
            assetIndex = new AssetIndexRef(Str(ai, "id")!, Str(ai, "url")!, Str(ai, "sha1"), Long(ai, "size"), Long(ai, "totalSize"));

        JavaRequirement? java = null;
        if (root.TryGetProperty("javaVersion", out var jv))
            java = new JavaRequirement(Str(jv, "component") ?? "jre-legacy", jv.TryGetProperty("majorVersion", out var mv) ? mv.GetInt32() : 8);

        LoggingConfig? logging = null;
        if (root.TryGetProperty("logging", out var lg) && lg.TryGetProperty("client", out var lc)
            && lc.TryGetProperty("file", out var lf))
        {
            logging = new LoggingConfig(Str(lc, "argument") ?? "-Dlog4j.configurationFile=${path}", Str(lf, "id") ?? "client-log4j.xml", ParseFile(lf));
        }

        var jvmArgs = new List<ArgumentEntry>();
        var gameArgs = new List<ArgumentEntry>();
        if (root.TryGetProperty("arguments", out var args))
        {
            if (args.TryGetProperty("jvm", out var jvm)) jvmArgs.AddRange(ParseArguments(jvm));
            if (args.TryGetProperty("game", out var game)) gameArgs.AddRange(ParseArguments(game));
        }

        var libraries = new List<Library>();
        if (root.TryGetProperty("libraries", out var libs))
            foreach (var lib in libs.EnumerateArray())
                libraries.Add(ParseLibrary(lib));

        DateTimeOffset.TryParse(Str(root, "releaseTime"), out var releaseTime);

        return new VersionDefinition
        {
            Id = id,
            InheritsFrom = Str(root, "inheritsFrom"),
            Type = Str(root, "type") ?? "release",
            MainClass = Str(root, "mainClass"),
            AssetsId = Str(root, "assets"),
            AssetIndex = assetIndex,
            ClientDownload = client,
            JarVersionId = Str(root, "jar"),
            Libraries = libraries,
            JvmArguments = jvmArgs,
            GameArguments = gameArgs,
            LegacyMinecraftArguments = Str(root, "minecraftArguments"),
            Java = java,
            Logging = logging,
            ReleaseTime = releaseTime,
        };
    }

    private static IEnumerable<ArgumentEntry> ParseArguments(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                yield return new ArgumentEntry([item.GetString()!], null);
                continue;
            }
            var rules = item.TryGetProperty("rules", out var r) ? ParseRules(r) : null;
            var values = new List<string>();
            if (item.TryGetProperty("value", out var v))
            {
                if (v.ValueKind == JsonValueKind.String) values.Add(v.GetString()!);
                else if (v.ValueKind == JsonValueKind.Array) values.AddRange(v.EnumerateArray().Select(x => x.GetString()!));
            }
            yield return new ArgumentEntry(values, rules);
        }
    }

    public static IReadOnlyList<Rule> ParseRules(JsonElement array)
    {
        var rules = new List<Rule>();
        foreach (var r in array.EnumerateArray())
        {
            var action = Str(r, "action") == "disallow" ? RuleAction.Disallow : RuleAction.Allow;
            OsCondition? os = null;
            if (r.TryGetProperty("os", out var o))
                os = new OsCondition(Str(o, "name"), Str(o, "arch"), Str(o, "version"));
            Dictionary<string, bool>? features = null;
            if (r.TryGetProperty("features", out var f))
            {
                features = [];
                foreach (var p in f.EnumerateObject())
                    features[p.Name] = p.Value.ValueKind == JsonValueKind.True;
            }
            rules.Add(new Rule(action, os, features));
        }
        return rules;
    }

    private static Library ParseLibrary(JsonElement lib)
    {
        var name = Str(lib, "name") ?? "";
        FileRef? artifact = null;
        var classifiers = new Dictionary<string, FileRef>();
        if (lib.TryGetProperty("downloads", out var d))
        {
            if (d.TryGetProperty("artifact", out var a)) artifact = ParseFile(a);
            if (d.TryGetProperty("classifiers", out var cl))
                foreach (var p in cl.EnumerateObject())
                    classifiers[p.Name] = ParseFile(p.Value);
        }

        Dictionary<string, string>? natives = null;
        if (lib.TryGetProperty("natives", out var n))
        {
            natives = [];
            foreach (var p in n.EnumerateObject()) natives[p.Name] = p.Value.GetString()!;
        }

        var exclude = new List<string>();
        if (lib.TryGetProperty("extract", out var ex) && ex.TryGetProperty("exclude", out var exl))
            exclude.AddRange(exl.EnumerateArray().Select(x => x.GetString()!));

        return new Library(
            name,
            artifact,
            classifiers,
            natives,
            lib.TryGetProperty("rules", out var rules) ? ParseRules(rules) : null,
            exclude,
            Str(lib, "url"));
    }

    private static FileRef ParseFile(JsonElement e)
        => new(Str(e, "url") ?? "", Str(e, "sha1"), Long(e, "size"), Str(e, "path"));

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
