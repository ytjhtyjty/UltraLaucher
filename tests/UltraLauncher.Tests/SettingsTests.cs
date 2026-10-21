using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.Tests;

public class SettingsStoreTests
{
    private static LauncherSettings Defaults(string dir) => new() { GameDirectory = Path.Combine(dir, "игра по умолчанию"), MinMemoryMb = 1024, MaxMemoryMb = 2048 };

    [Fact]
    public void SaveAndLoad_RoundTripsAllFields()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var store = new SettingsStore(path, new MemoryLog(), () => Defaults(dir.Path));
        var s = new LauncherSettings
        {
            GameDirectory = dir.Combine("Моя игра", "Minecraft Files"),
            JavaPath = @"C:\Program Files\Java\bin\javaw.exe",
            MinMemoryMb = 1536,
            MaxMemoryMb = 6144,
            ExtraJvmArguments = "-XX:+UseG1GC -Dfoo=\"a b\"",
            ShowSnapshots = true,
            SelectedVersionId = "1.21.4",
            SelectedAccountId = "acc-1",
            MicrosoftClientId = "11111111-2222-3333-4444-555555555555",
        };
        store.Save(s);

        var loaded = new SettingsStore(path, new MemoryLog(), () => Defaults(dir.Path)).Load();

        Assert.Null(loaded.RecoveryNotice);
        Assert.Equal(s.GameDirectory, loaded.Settings.GameDirectory);
        Assert.Equal(s.JavaPath, loaded.Settings.JavaPath);
        Assert.Equal(1536, loaded.Settings.MinMemoryMb);
        Assert.Equal(6144, loaded.Settings.MaxMemoryMb);
        Assert.Equal(s.ExtraJvmArguments, loaded.Settings.ExtraJvmArguments);
        Assert.True(loaded.Settings.ShowSnapshots);
        Assert.Equal("1.21.4", loaded.Settings.SelectedVersionId);
        Assert.Equal("acc-1", loaded.Settings.SelectedAccountId);
        Assert.Equal(s.MicrosoftClientId, loaded.Settings.MicrosoftClientId);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsWithoutNotice()
    {
        using var dir = new TempDir();
        var result = new SettingsStore(dir.Combine("settings.json"), new MemoryLog(), () => Defaults(dir.Path)).Load();
        Assert.Null(result.RecoveryNotice);
        Assert.Equal(Defaults(dir.Path).GameDirectory, result.Settings.GameDirectory);
    }

    [Fact]
    public void Load_CorruptedFile_RestoresFromBackup()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var store = new SettingsStore(path, new MemoryLog(), () => Defaults(dir.Path));
        store.Save(new LauncherSettings { GameDirectory = dir.Combine("first"), MinMemoryMb = 1024, MaxMemoryMb = 3000 });
        store.Save(new LauncherSettings { GameDirectory = dir.Combine("second"), MinMemoryMb = 1024, MaxMemoryMb = 4000 }); // первая версия ушла в .bak
        File.WriteAllText(path, "{ это не json ");

        var result = store.Load();

        Assert.NotNull(result.RecoveryNotice);
        Assert.Contains("резервной", result.RecoveryNotice);
        Assert.Equal(dir.Combine("first"), result.Settings.GameDirectory);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_CorruptedWithoutBackup_FallsBackToDefaultsAndKeepsCorruptCopy()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "\0\0\0garbage");

        var result = new SettingsStore(path, new MemoryLog(), () => Defaults(dir.Path)).Load();

        Assert.Contains("по умолчанию", result.RecoveryNotice);
        Assert.Equal(Defaults(dir.Path).GameDirectory, result.Settings.GameDirectory);
        Assert.True(File.Exists(path), "Восстановленные настройки должны быть сохранены");
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_NormalizesInvalidValues()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, """{"GameDirectory":"","MinMemoryMb":-5,"MaxMemoryMb":0,"JavaPath":"  ","ExtraJvmArguments":null}""");
        var s = new SettingsStore(path, new MemoryLog(), () => Defaults(dir.Path)).Load().Settings;
        Assert.Equal(Defaults(dir.Path).GameDirectory, s.GameDirectory);
        Assert.Equal(1024, s.MinMemoryMb);
        Assert.Equal(2048, s.MaxMemoryMb);
        Assert.Null(s.JavaPath);
        Assert.Equal("", s.ExtraJvmArguments);
    }
}

public class SettingsValidatorTests
{
    private static LauncherSettings Valid(string dir) => new() { GameDirectory = dir, MinMemoryMb = 1024, MaxMemoryMb = 2048 };

    [Fact]
    public void ValidSettings_WithCyrillicAndSpaces_HaveNoErrors()
    {
        using var dir = new TempDir("Мой Майнкрафт");
        var issues = SettingsValidator.Validate(Valid(dir.Combine("Игровая папка")), 16384);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void MaxLessThanMin_IsError()
    {
        using var dir = new TempDir();
        var s = Valid(dir.Path);
        s.MinMemoryMb = 4096;
        s.MaxMemoryMb = 2048;
        Assert.Contains(SettingsValidator.Validate(s, 16384), i => i.Field == nameof(s.MaxMemoryMb) && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void MaxAbovePhysicalMemory_IsError()
    {
        using var dir = new TempDir();
        var s = Valid(dir.Path);
        s.MaxMemoryMb = 16000;
        Assert.Contains(SettingsValidator.Validate(s, 8192), i => i.Field == nameof(s.MaxMemoryMb) && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void MaxAboveThreeQuarters_IsWarning()
    {
        using var dir = new TempDir();
        var s = Valid(dir.Path);
        s.MaxMemoryMb = 7000;
        var issues = SettingsValidator.Validate(s, 8192);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void TooLittleMinMemory_IsError()
    {
        using var dir = new TempDir();
        var s = Valid(dir.Path);
        s.MinMemoryMb = 128;
        Assert.Contains(SettingsValidator.Validate(s, 16384), i => i.Field == nameof(s.MinMemoryMb));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\folder")]
    [InlineData("Q:\\несуществующий диск\\игра")]
    public void BadGameDirectory_IsError(string path)
    {
        if (path.StartsWith("Q:") && Directory.Exists("Q:\\")) return; // на машине есть диск Q
        var s = Valid(path);
        Assert.Contains(SettingsValidator.Validate(s, 16384), i => i.Field == nameof(s.GameDirectory) && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void GameDirectoryPointingToFile_IsError()
    {
        using var dir = new TempDir();
        var file = dir.Combine("file.txt");
        File.WriteAllText(file, "x");
        Assert.NotNull(SettingsValidator.ValidateDirectory(file));
    }

    [Fact]
    public void JavaPath_MissingFile_IsError()
    {
        using var dir = new TempDir();
        var s = Valid(dir.Path);
        s.JavaPath = dir.Combine("нет", "java.exe");
        Assert.Contains(SettingsValidator.Validate(s, 16384), i => i.Field == nameof(s.JavaPath));
    }

    [Fact]
    public void JavaPath_NotJavaExecutable_IsError()
    {
        using var dir = new TempDir();
        var fake = dir.Combine("notepad.exe");
        File.WriteAllText(fake, "MZ");
        Assert.NotNull(SettingsValidator.ValidateJavaPath(fake));
    }

    [Fact]
    public void ExtraArgs_OverridingClasspath_IsError_AndXmxIsWarning()
    {
        using var dir = new TempDir();
        var s = Valid(dir.Path);
        s.ExtraJvmArguments = "-cp foo.jar";
        Assert.Contains(SettingsValidator.Validate(s, 16384), i => i.Field == nameof(s.ExtraJvmArguments) && i.Severity == IssueSeverity.Error);
        s.ExtraJvmArguments = "-Xmx8G";
        Assert.Contains(SettingsValidator.Validate(s, 16384), i => i.Field == nameof(s.ExtraJvmArguments) && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void CheckWritable_CreatesDirectory()
    {
        using var dir = new TempDir();
        var target = dir.Combine("новая папка", "игра");
        Assert.Null(SettingsValidator.CheckWritable(target));
        Assert.True(Directory.Exists(target));
    }
}
