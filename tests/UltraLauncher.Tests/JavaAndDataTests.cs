using UltraLauncher.Core.Common;
using UltraLauncher.Core.Java;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.Tests;

public class JavaTests
{
    [Theory]
    [InlineData("1.8.0_51", 8)]
    [InlineData("17.0.15", 17)]
    [InlineData("21", 21)]
    [InlineData("25.0.4", 25)]
    [InlineData("1.7.0", 7)]
    public void ParseMajor(string version, int expected) => Assert.Equal(expected, JavaVersionParser.ParseMajor(version));

    [Fact]
    public void ParseMajor_Garbage_IsNull() => Assert.Null(JavaVersionParser.ParseMajor("abc"));

    [Fact]
    public void ParseVersionOutput()
        => Assert.Equal("21.0.3", JavaVersionParser.ParseVersionOutput("openjdk version \"21.0.3\" 2024-04-16 LTS\nOpenJDK Runtime"));

    [Fact]
    public void ReadReleaseFile()
    {
        using var dir = new TempDir("Java Домашняя");
        File.WriteAllText(dir.Combine("release"), "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"17.0.9\"\n");
        Assert.Equal("17.0.9", JavaVersionParser.ReadReleaseFile(dir.Path));
    }

    private static JavaInstallation J(int major, bool x64 = true, string source = "test") => new($@"C:\j{major}\bin\java.exe", major, major + ".0", x64, source);

    [Fact]
    public void Compatibility_Rules()
    {
        var req21 = new JavaRequirement("java-runtime-delta", 21);
        Assert.Equal(CompatibilityLevel.Incompatible, JavaCompatibility.Check(J(17), req21).Level);
        Assert.Equal(CompatibilityLevel.Compatible, JavaCompatibility.Check(J(21), req21).Level);
        Assert.Equal(CompatibilityLevel.Compatible, JavaCompatibility.Check(J(25), req21).Level);
        Assert.Equal(CompatibilityLevel.Warning, JavaCompatibility.Check(J(21, x64: false), req21).Level);

        var legacy = new JavaRequirement("jre-legacy", 8);
        Assert.Equal(CompatibilityLevel.Compatible, JavaCompatibility.Check(J(8), legacy).Level);
        Assert.Equal(CompatibilityLevel.Warning, JavaCompatibility.Check(J(17), legacy).Level);
        Assert.Equal(CompatibilityLevel.Compatible, JavaCompatibility.Check(J(8), null).Level); // без javaVersion — Java 8
    }

    [Fact]
    public void PickBest_PrefersClosestCompatible()
    {
        var all = new[] { J(8), J(17), J(21), J(25) };
        Assert.Equal(21, JavaCompatibility.PickBest(all, new JavaRequirement("x", 21))!.MajorVersion);
        Assert.Equal(25, JavaCompatibility.PickBest(all, new JavaRequirement("x", 25))!.MajorVersion);
        Assert.Equal(8, JavaCompatibility.PickBest(all, new JavaRequirement("x", 8))!.MajorVersion);
        Assert.Null(JavaCompatibility.PickBest([J(8), J(17)], new JavaRequirement("x", 21)));
    }

    [Fact]
    public void Inspect_RealJavaIfPresent()
    {
        // Проверка на реальной Java этой машины (если установлена).
        var java = Environment.GetEnvironmentVariable("JAVA_HOME") is { } home ? Path.Combine(home, "bin", "java.exe") : null;
        if (java is null || !File.Exists(java)) return;
        var info = JavaDetector.Inspect(java);
        Assert.NotNull(info);
        Assert.True(info!.MajorVersion >= 8);
    }

    [Fact]
    public void Inspect_NonJavaFile_ReturnsNull()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("bin"));
        File.WriteAllText(dir.Combine("bin", "java.exe"), "not an exe");
        Assert.Null(JavaDetector.Inspect(dir.Combine("bin", "java.exe")));
    }
}

public class UserDataSafetyTests
{
    [Fact]
    public void DeleteVersion_KeepsWorldsAndSharedFiles()
    {
        using var dir = new TempDir("Игровая папка");
        var layout = new GameDirectoryLayout(dir.Path);
        Directory.CreateDirectory(layout.VersionDirectory("1.21.4"));
        File.WriteAllText(layout.VersionJson("1.21.4"), "{}");
        Directory.CreateDirectory(dir.Combine("saves", "Мой мир"));
        File.WriteAllText(dir.Combine("saves", "Мой мир", "level.dat"), "world");
        Directory.CreateDirectory(dir.Combine("screenshots"));
        File.WriteAllText(dir.Combine("screenshots", "1.png"), "png");
        Directory.CreateDirectory(dir.Combine("resourcepacks"));
        File.WriteAllText(dir.Combine("options.txt"), "fov:90");
        Directory.CreateDirectory(layout.LibrariesDirectory);
        File.WriteAllText(Path.Combine(layout.LibrariesDirectory, "shared.jar"), "lib");

        InstallationInspector.DeleteVersionFiles(layout, "1.21.4");

        Assert.False(Directory.Exists(layout.VersionDirectory("1.21.4")));
        Assert.True(File.Exists(dir.Combine("saves", "Мой мир", "level.dat")));
        Assert.True(File.Exists(dir.Combine("screenshots", "1.png")));
        Assert.True(Directory.Exists(dir.Combine("resourcepacks")));
        Assert.True(File.Exists(dir.Combine("options.txt")));
        Assert.True(File.Exists(Path.Combine(layout.LibrariesDirectory, "shared.jar")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../saves")]
    [InlineData("")]
    public void DeleteVersion_RejectsDangerousIds(string id)
    {
        using var dir = new TempDir();
        Assert.Throws<LauncherException>(() => InstallationInspector.DeleteVersionFiles(new GameDirectoryLayout(dir.Path), id));
    }

    [Fact]
    public void OfflineReadiness_RequiresMarkerAndFiles()
    {
        using var dir = new TempDir();
        var layout = new GameDirectoryLayout(dir.Path);
        var inspector = new InstallationInspector(PlatformInfo.Current);

        Assert.False(inspector.IsReadyOffline(layout, "1.21", out _, out var reason));
        Assert.Contains("не завершена", reason);

        Directory.CreateDirectory(layout.VersionDirectory("1.21"));
        File.WriteAllText(layout.VersionJson("1.21"), """{"id":"1.21","mainClass":"M","downloads":{"client":{"url":"u","sha1":"a","size":4}},"libraries":[]}""");
        File.WriteAllText(layout.InstallMarker("1.21"), """{"VersionId":"1.21","VersionJsonSha1":null,"InstalledUtc":"2026-01-01T00:00:00Z","LauncherVersion":"1"}""");
        Assert.False(inspector.IsReadyOffline(layout, "1.21", out _, out reason));
        Assert.Contains("Не хватает", reason);

        File.WriteAllText(layout.VersionJar("1.21"), "jar!");
        Assert.True(inspector.IsReadyOffline(layout, "1.21", out var plan, out _));
        Assert.NotNull(plan);
        Assert.Single(inspector.ListInstalled(layout), v => v.State == InstallState.Complete);
    }
}
