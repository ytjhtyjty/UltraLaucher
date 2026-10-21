using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.App.Services;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Java;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.App.ViewModels;

public sealed record IssueRow(string Message, bool IsError);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherCore _core;
    private readonly MainViewModel _shell;
    private bool _loading;

    public SettingsViewModel(LauncherCore core, MainViewModel shell)
    {
        _core = core;
        _shell = shell;
        TotalMemoryMb = SystemMemory.TotalPhysicalMb();
        Operation.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OperationViewModel.IsActive)) InstallJavaCommand.NotifyCanExecuteChanged(); };
        LoadFromSettings();
    }

    public OperationViewModel Operation => _shell.Operation;
    public int TotalMemoryMb { get; }
    /// <summary>
    /// Верх шкалы ползунков памяти. Статический, чтобы он был известен до привязки значения:
    /// иначе ползунок с максимумом по умолчанию «обрезал» бы сохранённое значение.
    /// </summary>
    public static double SliderMaximumMb { get; } = Math.Max(2048, SystemMemory.TotalPhysicalMb() - SettingsValidator.ReservedForSystemMb);
    public string MemoryInfo => TotalMemoryMb > 0
        ? $"Оперативная память компьютера: {TotalMemoryMb / 1024.0:0.#} ГБ, сейчас свободно {SystemMemory.AvailablePhysicalMb() / 1024.0:0.#} ГБ."
        : "Не удалось определить объём памяти.";

    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<JavaInstallation> DetectedJava { get; } = [];

    [ObservableProperty] private string _gameDirectory = "";
    [ObservableProperty] private bool _javaAuto = true;
    [ObservableProperty] private string _javaPath = "";
    [ObservableProperty] private JavaInstallation? _selectedDetectedJava;
    [ObservableProperty] private int _minMemoryMb;
    [ObservableProperty] private int _maxMemoryMb;
    [ObservableProperty] private string _extraJvmArguments = "";
    [ObservableProperty] private bool _showSnapshots;
    [ObservableProperty] private string _microsoftClientId = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private string? _javaCheckText;
    [ObservableProperty] private bool _javaCheckIsError;
    [ObservableProperty] private bool _isDetectingJava;
    [ObservableProperty] private string? _gameDirectoryError;
    [ObservableProperty] private string? _javaPathError;
    [ObservableProperty] private string? _memoryError;
    [ObservableProperty] private string? _jvmArgsError;

    public string? JavaRequirementText
    {
        get
        {
            var req = SelectedVersionRequirement();
            return req is null
                ? "Требуемая версия Java станет известна после установки выбранной версии Minecraft."
                : $"Для Minecraft {_core.Settings.Current.SelectedVersionId} нужна Java {req.MajorVersion}+.";
        }
    }

    partial void OnGameDirectoryChanged(string value) => Changed();
    partial void OnJavaAutoChanged(bool value) => Changed();
    partial void OnJavaPathChanged(string value) => Changed();
    partial void OnMinMemoryMbChanged(int value) => Changed();
    partial void OnMaxMemoryMbChanged(int value) => Changed();
    partial void OnExtraJvmArgumentsChanged(string value) => Changed();
    partial void OnShowSnapshotsChanged(bool value) => Changed();
    partial void OnMicrosoftClientIdChanged(string value) => Changed();

    partial void OnSelectedDetectedJavaChanged(JavaInstallation? value)
    {
        if (value is null || _loading) return;
        JavaAuto = false;
        JavaPath = value.PreferredExecutable;
        CheckJava();
    }

    private void Changed()
    {
        if (_loading) return;
        IsDirty = true;
        Validate();
    }

    private LauncherSettings Build()
    {
        var s = _core.Settings.Current;
        s.GameDirectory = GameDirectory.Trim();
        s.JavaPath = JavaAuto || string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim().Trim('"');
        s.MinMemoryMb = MinMemoryMb;
        s.MaxMemoryMb = MaxMemoryMb;
        s.ExtraJvmArguments = ExtraJvmArguments ?? "";
        s.ShowSnapshots = ShowSnapshots;
        s.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim();
        return s;
    }

    private void Validate()
    {
        var s = Build();
        var issues = _core.Settings.Validate(s).ToList();
        if (!JavaAuto && string.IsNullOrWhiteSpace(JavaPath))
            issues.Add(new SettingsIssue(nameof(LauncherSettings.JavaPath), IssueSeverity.Error, "Укажите путь к Java или включите автоматический выбор."));
        if (s.MicrosoftClientId is { } id && !Guid.TryParse(id, out _))
            issues.Add(new SettingsIssue(nameof(LauncherSettings.MicrosoftClientId), IssueSeverity.Error, "Client ID должен быть GUID вида 00000000-0000-0000-0000-000000000000."));

        Issues.Clear();
        foreach (var i in issues) Issues.Add(new IssueRow(i.Message, i.Severity == IssueSeverity.Error));
        string? First(string field) => issues.FirstOrDefault(i => i.Field == field)?.Message;
        GameDirectoryError = First(nameof(LauncherSettings.GameDirectory));
        JavaPathError = First(nameof(LauncherSettings.JavaPath));
        MemoryError = First(nameof(LauncherSettings.MaxMemoryMb)) ?? First(nameof(LauncherSettings.MinMemoryMb));
        JvmArgsError = First(nameof(LauncherSettings.ExtraJvmArguments));
        HasErrors = issues.Any(i => i.Severity == IssueSeverity.Error);
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = _core.Settings.Current;
            GameDirectory = s.GameDirectory;
            JavaAuto = s.JavaPath is null;
            JavaPath = s.JavaPath ?? "";
            SelectedDetectedJava = null;
            MinMemoryMb = s.MinMemoryMb;
            MaxMemoryMb = s.MaxMemoryMb;
            ExtraJvmArguments = s.ExtraJvmArguments;
            ShowSnapshots = s.ShowSnapshots;
            MicrosoftClientId = s.MicrosoftClientId ?? "";
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
        Validate();
        OnPropertyChanged(nameof(JavaRequirementText));
    }

    private bool CanSave() => IsDirty && !HasErrors;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var oldDir = _core.Settings.Current.GameDirectory;
        try
        {
            _core.Settings.Save(Build());
            IsDirty = false;
            var dirChanged = !string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(GameDirectory.Trim()), StringComparison.OrdinalIgnoreCase);
            _shell.Notifications.Show(NoticeKind.Success, "Настройки сохранены.",
                dirChanged ? "Папка игры изменена. Файлы из старой папки не переносятся и не удаляются." : null);
            _shell.OnSettingsSaved();
            LoadFromSettings();
        }
        catch (Exception ex)
        {
            _shell.Notifications.ShowError(ex);
        }
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Revert()
    {
        LoadFromSettings();
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        if (!await _shell.Dialog.ConfirmAsync("Сбросить настройки?", "Будут восстановлены значения по умолчанию (папка игры, память, Java, аргументы). Изменения вступят в силу после сохранения.", "Сбросить"))
            return;
        var d = SettingsStore.CreateDefaults();
        GameDirectory = d.GameDirectory;
        JavaAuto = true;
        JavaPath = "";
        MinMemoryMb = d.MinMemoryMb;
        MaxMemoryMb = d.MaxMemoryMb;
        ExtraJvmArguments = "";
        ShowSnapshots = false;
    }

    [RelayCommand]
    private void BrowseGameDirectory()
    {
        var picked = ShellService.PickFolder(GameDirectory, "Выберите папку для игровых файлов Minecraft");
        if (picked is not null) GameDirectory = picked;
    }

    [RelayCommand]
    private void OpenGameDirectory()
    {
        try { ShellService.OpenFolder(GameDirectory); } catch (Exception ex) { _shell.Notifications.ShowError(ex); }
    }

    [RelayCommand]
    private void BrowseJava()
    {
        var picked = ShellService.PickJava(JavaPath);
        if (picked is null) return;
        JavaAuto = false;
        JavaPath = picked;
        CheckJava();
    }

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        IsDetectingJava = true;
        try
        {
            var found = await Task.Run(() => _core.Game.DetectJava(refresh: true));
            DetectedJava.Clear();
            foreach (var j in found) DetectedJava.Add(j);
            if (found.Count == 0)
                JavaCheckText = "Java не найдена. Лаунчер скачает официальную Java от Mojang при первом запуске (нужен интернет), либо нажмите «Установить Java».";
        }
        finally
        {
            IsDetectingJava = false;
        }
    }

    [RelayCommand]
    private void CheckJava()
    {
        var req = SelectedVersionRequirement();
        if (JavaAuto)
        {
            var best = JavaCompatibility.PickBest(_core.Game.DetectJava(), req ?? JavaCompatibility.LegacyDefault);
            JavaCheckIsError = best is null;
            JavaCheckText = best is null
                ? $"Подходящая Java {(req?.MajorVersion ?? 8)}+ не найдена — при запуске будет скачана официальная Java от Mojang."
                : $"Будет использована: {best.DisplayName}";
            return;
        }

        var pathError = SettingsValidator.ValidateJavaPath(JavaPath.Trim().Trim('"'));
        if (pathError is not null)
        {
            JavaCheckIsError = true;
            JavaCheckText = pathError;
            return;
        }
        var java = JavaDetector.Inspect(JavaPath.Trim().Trim('"'));
        if (java is null)
        {
            JavaCheckIsError = true;
            JavaCheckText = "Файл не запускается как Java или версия не определяется.";
            return;
        }
        if (req is null)
        {
            JavaCheckIsError = false;
            JavaCheckText = $"{java.DisplayName}. Совместимость проверится после установки выбранной версии Minecraft.";
            return;
        }
        var c = JavaCompatibility.Check(java, req);
        JavaCheckIsError = c.Level == CompatibilityLevel.Incompatible;
        JavaCheckText = $"{java.DisplayName}. {c.Message}";
    }

    private bool CanInstallJava() => !Operation.IsActive;

    [RelayCommand(CanExecute = nameof(CanInstallJava))]
    private async Task InstallJavaAsync()
    {
        var req = SelectedVersionRequirement() ?? new Core.Minecraft.JavaRequirement("java-runtime-epsilon", 25);
        await Operation.RunAsync($"Установка Java {req.MajorVersion}", async (p, ct) => { await _core.Game.InstallJavaAsync(req, p, ct); },
            $"Java {req.MajorVersion} от Mojang установлена и будет выбираться автоматически.");
        await DetectJavaAsync();
        CheckJava();
    }

    private Core.Minecraft.JavaRequirement? SelectedVersionRequirement()
    {
        var id = _core.Settings.Current.SelectedVersionId;
        return id is null ? null : _core.Game.GetJavaRequirement(id);
    }

    public void OnShown()
    {
        OnPropertyChanged(nameof(JavaRequirementText));
        OnPropertyChanged(nameof(MemoryInfo));
        if (DetectedJava.Count == 0 && !IsDetectingJava) _ = DetectJavaAsync();
    }
}
