using System.IO.Compression;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.Tests;

public class RuleEvaluatorTests
{
    private static readonly PlatformInfo Win64 = new("windows", "x86_64", "10.0", true);
    private static readonly PlatformInfo Win32 = new("windows", "x86", "10.0", false);

    private static IReadOnlyList<Rule> Rules(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return VersionJsonParser.ParseRules(doc.RootElement);
    }

    [Fact]
    public void NoRules_Allowed() => Assert.True(RuleEvaluator.IsAllowed(null, Win64));

    [Fact]
    public void AllowExceptOsx_AllowedOnWindows()
        => Assert.True(RuleEvaluator.IsAllowed(Rules("""[{"action":"allow"},{"action":"disallow","os":{"name":"osx"}}]"""), Win64));

    [Fact]
    public void OnlyOsx_NotAllowedOnWindows()
        => Assert.False(RuleEvaluator.IsAllowed(Rules("""[{"action":"allow","os":{"name":"osx"}}]"""), Win64));

    [Fact]
    public void OnlyLinux_NotAllowed_OnlyWindows_Allowed()
    {
        Assert.False(RuleEvaluator.IsAllowed(Rules("""[{"action":"allow","os":{"name":"linux"}}]"""), Win64));
        Assert.True(RuleEvaluator.IsAllowed(Rules("""[{"action":"allow","os":{"name":"windows"}}]"""), Win64));
    }

    [Fact]
    public void ArchX86Rule_OnlyFor32Bit()
    {
        var r = Rules("""[{"action":"allow","os":{"arch":"x86"}}]""");
        Assert.False(RuleEvaluator.IsAllowed(r, Win64));
        Assert.True(RuleEvaluator.IsAllowed(r, Win32));
    }

    [Fact]
    public void OsVersionRegex_IsApplied()
    {
        var r = Rules("""[{"action":"allow","os":{"name":"windows","version":"^10\\."}}]""");
        Assert.True(RuleEvaluator.IsAllowed(r, Win64));
        Assert.False(RuleEvaluator.IsAllowed(r, Win64 with { OsVersion = "6.1" }));
    }

    [Fact]
    public void Features_DemoAndResolution()
    {
        var demo = Rules("""[{"action":"allow","features":{"is_demo_user":true}}]""");
        Assert.False(RuleEvaluator.IsAllowed(demo, Win64, new LaunchFeatures()));
        Assert.True(RuleEvaluator.IsAllowed(demo, Win64, new LaunchFeatures { IsDemoUser = true }));
        var quickPlay = Rules("""[{"action":"allow","features":{"has_quick_plays_support":true}}]""");
        Assert.False(RuleEvaluator.IsAllowed(quickPlay, Win64, new LaunchFeatures { IsDemoUser = true }));
    }
}

public class LibraryResolverTests
{
    private static readonly PlatformInfo Win64 = new("windows", "x86_64", "10.0", true);

    private static VersionDefinition Version(string librariesJson)
        => VersionJsonParser.Parse($$"""{"id":"test","type":"release","mainClass":"a.B","libraries":{{librariesJson}}}""");

    [Fact]
    public void ModernNatives_TakesWindowsAndSkipsOtherArchitecturesAndOs()
    {
        var v = Version("""
        [
          {"name":"org.lwjgl:lwjgl:3.3.3","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar","sha1":"aa","size":10,"url":"https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"}}},
          {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar","sha1":"bb","size":11,"url":"https://x/1"}},"rules":[{"action":"allow","os":{"name":"windows"}}]},
          {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-arm64","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-arm64.jar","sha1":"cc","size":12,"url":"https://x/2"}},"rules":[{"action":"allow","os":{"name":"windows"}}]},
          {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-x86","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-x86.jar","sha1":"dd","size":13,"url":"https://x/3"}},"rules":[{"action":"allow","os":{"name":"windows"}}]},
          {"name":"org.lwjgl:lwjgl:3.3.3:natives-linux","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar","sha1":"ee","size":14,"url":"https://x/4"}},"rules":[{"action":"allow","os":{"name":"linux"}}]}
        ]
        """);
        using var dir = new TempDir();
        var libs = LibraryResolver.Resolve(v, new GameDirectoryLayout(dir.Path), Win64);

        Assert.Equal(2, libs.Count);
        Assert.All(libs, l => Assert.True(l.OnClasspath));
        Assert.Contains(libs, l => l.File.Path.EndsWith("lwjgl-3.3.3-natives-windows.jar"));
        Assert.DoesNotContain(libs, l => l.File.Path.Contains("arm64") || l.File.Path.Contains("x86") || l.File.Path.Contains("linux"));
    }

    [Fact]
    public void LegacyNatives_UseClassifierWithArchAndAreExtracted()
    {
        var v = Version("""
        [
          {"name":"tv.twitch:twitch-platform:6.5","natives":{"windows":"natives-windows-${arch}","osx":"natives-osx"},
           "extract":{"exclude":["META-INF/"]},
           "downloads":{"classifiers":{
              "natives-windows-64":{"path":"tv/twitch/twitch-platform/6.5/twitch-platform-6.5-natives-windows-64.jar","sha1":"11","size":5,"url":"https://x/64"},
              "natives-windows-32":{"path":"tv/twitch/twitch-platform/6.5/twitch-platform-6.5-natives-windows-32.jar","sha1":"22","size":5,"url":"https://x/32"}}}}
        ]
        """);
        using var dir = new TempDir();
        var libs = LibraryResolver.Resolve(v, new GameDirectoryLayout(dir.Path), Win64);

        var native = Assert.Single(libs);
        Assert.True(native.ExtractNatives);
        Assert.False(native.OnClasspath);
        Assert.EndsWith("natives-windows-64.jar", native.File.Path);
        Assert.Equal(DownloadCategory.Native, native.File.Category);
        Assert.Equal(["META-INF/"], native.ExtractExclude);
    }

    [Fact]
    public void LibraryWithoutDownloads_UsesMavenLayout()
    {
        var v = Version("""[{"name":"net.example:cool-lib:1.2.3","url":"https://maven.example.net/"}]""");
        using var dir = new TempDir();
        var lib = Assert.Single(LibraryResolver.Resolve(v, new GameDirectoryLayout(dir.Path), Win64));
        Assert.Equal("https://maven.example.net/net/example/cool-lib/1.2.3/cool-lib-1.2.3.jar", lib.File.Url);
        Assert.EndsWith(Path.Combine("libraries", "net", "example", "cool-lib", "1.2.3", "cool-lib-1.2.3.jar"), lib.File.Path);
        Assert.False(lib.File.HasHash);
    }

    [Fact]
    public void DuplicatePaths_AreIncludedOnce()
    {
        var art = """{"path":"a/b/1/b-1.jar","sha1":"aa","size":1,"url":"https://x/b"}""";
        var v = Version($$$"""[{"name":"a:b:1","downloads":{"artifact":{{{art}}}}},{"name":"a:b:1","downloads":{"artifact":{{{art}}}}}]""");
        using var dir = new TempDir();
        Assert.Single(LibraryResolver.Resolve(v, new GameDirectoryLayout(dir.Path), Win64));
    }

    [Fact]
    public void PathTraversal_IsRejected()
    {
        var v = Version("""[{"name":"a:b:1","downloads":{"artifact":{"path":"../../evil.jar","sha1":"aa","size":1,"url":"https://x/b"}}}]""");
        using var dir = new TempDir();
        var ex = Assert.Throws<LauncherException>(() => LibraryResolver.Resolve(v, new GameDirectoryLayout(dir.Path), Win64));
        Assert.Equal(ErrorCode.Integrity, ex.Code);
    }
}

public class VersionInheritanceTests
{
    [Fact]
    public void Merge_ChildOverridesAndAppends()
    {
        var parent = VersionJsonParser.Parse("""
        {"id":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","assets":"5",
         "assetIndex":{"id":"5","url":"https://x/5.json","sha1":"ab","size":1,"totalSize":2},
         "downloads":{"client":{"url":"https://x/client.jar","sha1":"cd","size":3}},
         "javaVersion":{"component":"java-runtime-gamma","majorVersion":17},
         "arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]},
         "libraries":[{"name":"com.google:guava:31","downloads":{"artifact":{"path":"g/31.jar","sha1":"1","size":1,"url":"https://x/g31"}}}]}
        """);
        var child = VersionJsonParser.Parse("""
        {"id":"custom-1.20.1","inheritsFrom":"1.20.1","type":"release","mainClass":"custom.Main",
         "arguments":{"game":["--extra"]},
         "libraries":[{"name":"com.google:guava:32","downloads":{"artifact":{"path":"g/32.jar","sha1":"2","size":1,"url":"https://x/g32"}}},
                      {"name":"org.custom:lib:1","downloads":{"artifact":{"path":"c/1.jar","sha1":"3","size":1,"url":"https://x/c"}}}]}
        """);

        var merged = VersionInheritance.Resolve(child, id => id == "1.20.1" ? parent : throw new InvalidOperationException());

        Assert.Equal("custom-1.20.1", merged.Id);
        Assert.Equal("custom.Main", merged.MainClass);
        Assert.Equal("5", merged.AssetIndex!.Id);
        Assert.Equal(17, merged.Java!.MajorVersion);
        Assert.Equal("1.20.1", merged.JarVersionId);
        Assert.Equal(["--username", "${auth_player_name}", "--extra"], merged.GameArguments.SelectMany(a => a.Values));
        // Наследник заменил guava 31 на 32, родительская версия не дублируется.
        Assert.Equal(["com.google:guava:32", "org.custom:lib:1"], merged.Libraries.Select(l => l.Name));
        Assert.Null(merged.InheritsFrom);
    }

    [Fact]
    public void Resolve_DetectsCycles()
    {
        var a = VersionJsonParser.Parse("""{"id":"a","inheritsFrom":"b"}""");
        var b = VersionJsonParser.Parse("""{"id":"b","inheritsFrom":"a"}""");
        Assert.Throws<LauncherException>(() => VersionInheritance.Resolve(a, id => id == "a" ? a : b));
    }
}

public class InstallPlanTests
{
    [Fact]
    public void Plan_ContainsClientLibrariesLogConfigAndDedupedAssets()
    {
        var v = VersionJsonParser.Parse("""
        {"id":"1.21","type":"release","mainClass":"M",
         "downloads":{"client":{"url":"https://x/client.jar","sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":100}},
         "logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","file":{"id":"client-1.12.xml","sha1":"bb","size":5,"url":"https://x/log.xml"}}},
         "libraries":[{"name":"a:b:1","downloads":{"artifact":{"path":"a/b/1/b-1.jar","sha1":"cc","size":1,"url":"https://x/b"}}}]}
        """);
        var assets = AssetIndex.Parse("""
        {"objects":{
          "minecraft/sounds/a.ogg":{"hash":"0123456789abcdef0123456789abcdef01234567","size":10},
          "minecraft/sounds/b.ogg":{"hash":"0123456789abcdef0123456789abcdef01234567","size":10},
          "icons/icon.png":{"hash":"ffffffffffffffffffffffffffffffffffffffff","size":20}}}
        """);
        using var dir = new TempDir();
        var layout = new GameDirectoryLayout(dir.Path);

        var plan = InstallPlanBuilder.Build(v, assets, layout, PlatformInfo.Current);

        Assert.Equal(1 + 1 + 1 + 2, plan.Files.Count);
        Assert.Equal(layout.VersionJar("1.21"), plan.ClientJarPath);
        Assert.Equal(plan.ClientJarPath, plan.ClasspathEntries.Last());
        var asset = plan.Files.Single(f => f.Sha1 == "ffffffffffffffffffffffffffffffffffffffff");
        Assert.Equal("https://resources.download.minecraft.net/ff/ffffffffffffffffffffffffffffffffffffffff", asset.Url);
        Assert.Equal(layout.AssetObject("ffffffffffffffffffffffffffffffffffffffff"), asset.Path);
        Assert.Equal(Path.Combine(layout.LogConfigsDirectory, "client-1.12.xml"), plan.LogConfigPath);
    }

    [Fact]
    public void LegacyAssetIndex_IsFlagged()
    {
        Assert.True(AssetIndex.Parse("""{"virtual":true,"objects":{}}""").IsLegacyLayout);
        Assert.True(AssetIndex.Parse("""{"map_to_resources":true,"objects":{}}""").IsLegacyLayout);
        Assert.False(AssetIndex.Parse("""{"objects":{}}""").IsLegacyLayout);
    }

    [Fact]
    public void AssetIndex_RejectsBadHash()
    {
        Assert.Throws<LauncherException>(() => AssetIndex.Parse("""{"objects":{"x":{"hash":"../../evil","size":1}}}"""));
    }

    [Fact]
    public void VersionSupport_Range()
    {
        var at = (string type, int year) => new ManifestVersion("v", type, "u", null, new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(VersionSupport.IsSupported(at("release", 2024), out _));
        Assert.True(VersionSupport.IsSupported(at("snapshot", 2026), out _));
        Assert.False(VersionSupport.IsSupported(at("release", 2013), out var reason));
        Assert.NotNull(reason);
        Assert.False(VersionSupport.IsSupported(at("old_beta", 2011), out _));
        Assert.False(VersionSupport.IsSupported(at("old_alpha", 2010), out _));
    }

    [Fact]
    public void ExtractNatives_SkipsMetaInfAndExcludes_AndBlocksZipSlip()
    {
        using var dir = new TempDir();
        var jar = dir.Combine("natives.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            void Add(string name) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write("dll"); }
            Add("lwjgl64.dll");
            Add("META-INF/MANIFEST.MF");
            Add("skip/me.txt");
        }
        var plan = new InstallPlan
        {
            Version = VersionJsonParser.Parse("""{"id":"x"}"""),
            ClientJarPath = "x.jar",
            Libraries = [new ResolvedLibrary("n", new DownloadItem("u", jar, null, 0, DownloadCategory.Native), false, true, ["skip/"])],
            Files = [],
        };
        var natives = dir.Combine("natives");
        VersionInstaller.ExtractNatives(plan, natives);
        Assert.True(File.Exists(Path.Combine(natives, "lwjgl64.dll")));
        Assert.False(Directory.Exists(Path.Combine(natives, "META-INF")));
        Assert.False(Directory.Exists(Path.Combine(natives, "skip")));

        var evil = dir.Combine("evil.jar");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("../outside.dll").Open())) w.Write("x");
        var evilPlan = new InstallPlan
        {
            Version = plan.Version,
            ClientJarPath = "x.jar",
            Libraries = [new ResolvedLibrary("n", new DownloadItem("u", evil, null, 0, DownloadCategory.Native), false, true, [])],
            Files = [],
        };
        Assert.Throws<LauncherException>(() => VersionInstaller.ExtractNatives(evilPlan, natives));
        Assert.False(File.Exists(dir.Combine("outside.dll")));
    }
}
