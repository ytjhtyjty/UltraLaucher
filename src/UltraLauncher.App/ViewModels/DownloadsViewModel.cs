using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.App.Services;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.App.ViewModels;

public sealed class InstalledVersionRow
{
    public required InstalledVersion Info { get; init; }
    public string Id => Info.Id;
    public bool IsComplete => Info.State == InstallState.Complete;
    public string StateText => IsComplete ? "Установлена" : "Установка не завершена";
    public string Details => $"{(Info.Type == "snapshot" ? "снапшот" : Info.Type == "release" ? "релиз" : Info.Type)} · {OperationViewModel.FormatBytes(Info.VersionFolderBytes)} в папке версии"
                             + (Info.InstalledUtc is { } t ? $" · установлена {t.ToLocalTime():dd.MM.yyyy HH:mm}" : "");
}

public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly LauncherCore _core;
    private readonly MainViewModel _shell;

    public DownloadsViewModel(LauncherCore core, MainViewModel shell)
    {
        _core = core;
        _shell = shell;
        Catalog.Reloaded += () =>
        {
            // По умолчанию — версия, выбранная на главной, иначе последний релиз.
            var preferred = _core.Settings.Current.SelectedVersionId ?? SelectedToInstall?.Id;
            SelectedToInstall = Catalog.Visible.FirstOrDefault(v => v.Id == preferred)
                                ?? Catalog.Visible.FirstOrDefault(v => v.Id == Catalog.LatestRelease)
                                ?? Catalog.Visible.FirstOrDefault();
            LoadInstalled();
        };
        Operation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.IsActive)) NotifyCommands();
        };
    }

    public VersionCatalog Catalog => _shell.Catalog;
    public OperationViewModel Operation => _shell.Operation;

    public ObservableCollection<InstalledVersionRow> Installed { get; } = [];

    [ObservableProperty] private VersionOption? _selectedToInstall;
    [ObservableProperty] private string _gameDirectory = "";

    public string BusyReason => Operation.IsActive ? "Дождитесь завершения текущей операции." : "";

    partial void OnSelectedToInstallChanged(VersionOption? value) => NotifyCommands();

    public void LoadInstalled()
    {
        GameDirectory = _core.Settings.Current.GameDirectory;
        Installed.Clear();
        foreach (var v in _core.Game.SafeListInstalled()) Installed.Add(new InstalledVersionRow { Info = v });
    }

    private void NotifyCommands()
    {
        InstallCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BusyReason));
    }

    private bool CanInstall() => !Operation.IsActive && SelectedToInstall is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var id = SelectedToInstall!.Id;
        await Operation.RunAsync($"Установка {id}", (p, ct) => _core.Game.InstallAsync(id, repair: false, p, ct), $"Версия {id} установлена.");
        AfterChange();
    }

    // Не зависит от параметра: WPF может спросить CanExecute до привязки CommandParameter.
    private bool CanModify(InstalledVersionRow? row) => !Operation.IsActive;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RepairAsync(InstalledVersionRow? row)
    {
        if (row is null) return;
        await Operation.RunAsync($"Проверка и восстановление {row.Id}", (p, ct) => _core.Game.InstallAsync(row.Id, repair: true, p, ct),
            $"Версия {row.Id} проверена: повреждённые и отсутствующие файлы восстановлены.");
        AfterChange();
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync(InstalledVersionRow? row)
    {
        if (row is null) return;
        var ok = await _shell.Dialog.ConfirmAsync(
            $"Удалить файлы версии {row.Id}?",
            $"Будет удалена только папка versions\\{row.Id}. Миры, скриншоты, ресурспаки, настройки игры, а также общие библиотеки и ассеты останутся на месте. Версию можно установить заново.",
            "Удалить", danger: true);
        if (!ok) return;
        try
        {
            _core.Game.DeleteVersion(row.Id);
            _shell.Notifications.Show(NoticeKind.Success, $"Файлы версии {row.Id} удалены. Пользовательские данные не затронуты.");
        }
        catch (Exception ex)
        {
            _shell.Notifications.ShowError(ex);
        }
        AfterChange();
    }

    [RelayCommand]
    private void OpenVersionFolder(InstalledVersionRow? row)
    {
        if (row is null) return;
        try { ShellService.OpenFolder(_core.Game.Layout.VersionDirectory(row.Id)); } catch (Exception ex) { _shell.Notifications.ShowError(ex); }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        try { ShellService.OpenFolder(_core.Settings.Current.GameDirectory); } catch (Exception ex) { _shell.Notifications.ShowError(ex); }
    }

    private void AfterChange()
    {
        Catalog.RefreshInstallStates();
        LoadInstalled();
    }
}
