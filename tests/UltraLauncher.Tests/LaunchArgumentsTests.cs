using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.Tests;

public class LaunchArgumentsTests
{
    private static readonly PlatformInfo Win64 = new("windows", "x86_64", "10.0", true);

    private const string ModernJson = """
    {"id":"1.21.4","type":"release","mainClass":"net.minecraft.client.main.Main",
     "assetIndex":{"id":"19","url":"https://x/19.json","sha1":"ab","size":1,"totalSize":2},
     "downloads":{"client":{"url":"https://x/client.jar","sha1":"cd","size":3}},
     "logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","file":{"id":"client-1.21.2.xml","sha1":"ef","size":4,"url":"https://x/l.xml"}}},
     "libraries":[{"name":"a:b:1","downloads":{"artifact":{"path":"a/b/1/b-1.jar","sha1":"01","size":1,"url":"https://x/b"}}}],
     "arguments":{
       "jvm":[{"rules":[{"action":"allow","os":{"name":"osx"}}],"value":["-XstartOnFirstThread"]},
              {"rules":[{"action":"allow","os":{"name":"windows"}}],"value":"-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump"},
              {"rules":[{"action":"allow","os":{"arch":"x86"}}],"value":"-Xss1M"},
              "-Djava.library.path=${natives_directory}","-Dminecraft.launcher.brand=${launcher_name}","-cp","${classpath}"],
       "game":["--username","${auth_player_name}","--version","${version_name}","--gameDir","${game_directory}",
               "--assetsDir","${assets_root}","--assetIndex","${assets_index_name}","--uuid","${auth_uuid}",
               "--accessToken","${auth_access_token}","--userType","${user_type}","--versionType","${version_type}",
               {"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"},
               {"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}"]}]}}
    """;

    private static LaunchRequest Request(string json, string gameDir, bool demo = false, IReadOnlyList<string>? extra = null)
    {
        var layout = new GameDirectoryLayout(gameDir);
        var plan = InstallPlanBuilder.Build(VersionJsonParser.Parse(json), null, layout, Win64);
        return new LaunchRequest
        {
            Plan = plan,
            Layout = layout,
            JavaPath = @"C:\Java\bin\javaw.exe",
            Player = new PlayerIdentity("Steve", "0123456789abcdef0123456789abcdef", "secret-access-token-123456", "0", "msa", demo),
            MinMemoryMb = 1024,
            MaxMemoryMb = 4096,
            ExtraJvmArguments = extra ?? [],
        };
    }

    [Fact]
    public void Modern_BuildsExpectedOrderAndValues()
    {
        var gameDir = @"D:\Игры\Мой Minecraft";
        var args = LaunchArgumentsBuilder.Build(Request(ModernJson, gameDir, extra: ["-XX:+UseG1GC"]), Win64);

        Assert.Equal("-Xms1024M", args[0]);
        Assert.Equal("-Xmx4096M", args[1]);
        Assert.Contains("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump", args);
        Assert.DoesNotContain("-XstartOnFirstThread", args);
        Assert.DoesNotContain("-Xss1M", args);
        Assert.Contains(@"-Djava.library.path=D:\Игры\Мой Minecraft\versions\1.21.4\natives", args);
        Assert.Contains(@"-Dlog4j.configurationFile=D:\Игры\Мой Minecraft\assets\log_configs\client-1.21.2.xml", args);
        Assert.Contains("-Dminecraft.launcher.brand=UltraLauncher", args);

        var main = args.IndexOf("net.minecraft.client.main.Main");
        Assert.True(main > 0);
        Assert.True(args.IndexOf("-XX:+UseG1GC") < main, "Пользовательские JVM-аргументы должны идти до главного класса");
        Assert.True(args.IndexOf("-cp") < main);

        // Путь с пробелами и кириллицей — один аргумент.
        Assert.Equal(gameDir, args[args.IndexOf("--gameDir") + 1]);
        Assert.Equal(@"D:\Игры\Мой Minecraft\assets", args[args.IndexOf("--assetsDir") + 1]);
        Assert.Equal("19", args[args.IndexOf("--assetIndex") + 1]);
        Assert.Equal("Steve", args[args.IndexOf("--username") + 1]);
        Assert.Equal("msa", args[args.IndexOf("--userType") + 1]);
        Assert.DoesNotContain("--demo", args);
        Assert.DoesNotContain("--width", args);
        Assert.DoesNotContain(args, a => a.Contains("${"));
    }

    [Fact]
    public void Classpath_HasLibrariesThenClientJar()
    {
        var req = Request(ModernJson, @"C:\Games\MC");
        var args = LaunchArgumentsBuilder.Build(req, Win64);
        var cp = args[args.IndexOf("-cp") + 1].Split(';');
        Assert.Equal(@"C:\Games\MC\libraries\a\b\1\b-1.jar", cp[0]);
        Assert.Equal(@"C:\Games\MC\versions\1.21.4\1.21.4.jar", cp[^1]);
    }

    [Fact]
    public void Demo_AddsDemoFlag()
    {
        var args = LaunchArgumentsBuilder.Build(Request(ModernJson, @"C:\g", demo: true), Win64);
        Assert.Contains("--demo", args);
    }

    [Fact]
    public void Legacy_MinecraftArgumentsAndDefaultJvm()
    {
        var json = """
        {"id":"1.12.2","type":"release","mainClass":"net.minecraft.client.main.Main","assets":"1.12",
         "assetIndex":{"id":"1.12","url":"https://x/1.12.json","sha1":"ab","size":1,"totalSize":2},
         "downloads":{"client":{"url":"https://x/client.jar","sha1":"cd","size":3}},
         "minecraftArguments":"--username ${auth_player_name} --gameDir ${game_directory} --accessToken ${auth_access_token} --userType ${user_type} --userProperties ${user_properties}",
         "libraries":[]}
        """;
        var args = LaunchArgumentsBuilder.Build(Request(json, @"C:\Мои игры\MC 1"), Win64);

        Assert.Contains(@"-Djava.library.path=C:\Мои игры\MC 1\versions\1.12.2\natives", args);
        Assert.Contains("-cp", args);
        Assert.Equal(@"C:\Мои игры\MC 1", args[args.IndexOf("--gameDir") + 1]);
        Assert.Equal("{}", args[args.IndexOf("--userProperties") + 1]);
        Assert.Equal("net.minecraft.client.main.Main", args[args.IndexOf("--username") - 1]);
    }

    [Fact]
    public void LegacyDemo_AppendsDemoFlag()
    {
        var json = """{"id":"1.8.9","mainClass":"M","downloads":{"client":{"url":"u","sha1":"a","size":1}},"minecraftArguments":"--username ${auth_player_name}"}""";
        Assert.Equal("--demo", LaunchArgumentsBuilder.Build(Request(json, @"C:\g", demo: true), Win64)[^1]);
    }

    [Fact]
    public void LoggedCommand_DoesNotContainToken()
    {
        var req = Request(ModernJson, @"C:\g");
        SecretRedactor.Register(req.Player.AccessToken);
        var line = SecretRedactor.Redact(CommandLine.Join(LaunchArgumentsBuilder.Build(req, Win64)));
        Assert.DoesNotContain("secret-access-token-123456", line);
        Assert.Contains("--accessToken ***", line);
    }

    [Fact]
    public void Substitute_LeavesUnknownPlaceholders()
    {
        Assert.Equal("a-1-${unknown}", LaunchArgumentsBuilder.Substitute("a-${x}-${unknown}", new Dictionary<string, string> { ["x"] = "1" }));
    }
}

public class CommandLineTests
{
    [Fact]
    public void Split_RespectsQuotes()
    {
        Assert.Equal(["-Xss2M", "-Dname=a b c", "-XX:+UseG1GC", ""], CommandLine.Split("-Xss2M  -Dname=\"a b c\" -XX:+UseG1GC \"\""));
        Assert.Empty(CommandLine.Split("   "));
        Assert.Empty(CommandLine.Split(null));
    }

    [Fact]
    public void Join_QuotesArgumentsWithSpaces()
        => Assert.Equal("java -cp \"C:\\Мои игры\\a.jar\" Main", CommandLine.Join(["java", "-cp", @"C:\Мои игры\a.jar", "Main"]));
}

public class Log4jEventParserTests
{
    [Fact]
    public void ParsesXmlEventsAndPassesPlainLines()
    {
        var p = new Log4jEventParser();
        Assert.Equal(["plain jvm line"], p.Feed("plain jvm line"));
        Assert.Empty(p.Feed("<log4j:Event logger=\"net.minecraft.client.Minecraft\" timestamp=\"1700000000000\" level=\"INFO\" thread=\"Render thread\">"));
        Assert.Empty(p.Feed("  <log4j:Message><![CDATA[Setting user: Steve]]></log4j:Message>"));
        var lines = p.Feed("</log4j:Event>").ToList();
        var line = Assert.Single(lines);
        Assert.Contains("[Render thread/INFO] Minecraft: Setting user: Steve", line);

        p.Feed("<log4j:Event logger=\"x.Y\" timestamp=\"1700000000000\" level=\"ERROR\" thread=\"main\">");
        p.Feed("<log4j:Message><![CDATA[Boom]]></log4j:Message>");
        p.Feed("<log4j:Throwable><![CDATA[java.lang.RuntimeException: bad");
        p.Feed("\tat a.b.C(C.java:1)");
        p.Feed("]]></log4j:Throwable>");
        var err = p.Feed("</log4j:Event>").ToList();
        Assert.Equal(3, err.Count);
        Assert.Contains("[main/ERROR] Y: Boom", err[0]);
        Assert.Equal("java.lang.RuntimeException: bad", err[1]);
    }
}
