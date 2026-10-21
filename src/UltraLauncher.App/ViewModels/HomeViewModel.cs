using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly LauncherCore _core;
    private readonly MainViewModel _shell;
    private bool _restoring;

    public HomeViewModel(LauncherCore core, MainViewModel shell)
    {
        _core = core;
        _shell = shell;
        Catalog.Reloaded += RestoreSelection;
        Operation.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OperationViewModel.IsActive)) Reevaluate(); };
    }

    public VersionCatalog Catalog => _shell.Catalog;
    public OperationViewModel Operation => _shell.Operation;
    public MainViewModel Shell => _shell;

    [ObservableProperty] private VersionOption? _selectedVersion;
    [ObservableProperty] private bool _offlineMode;
    [ObservableProperty] private bool _canUseOffline;
    [ObservableProperty] private string? _offlineReason;
    [ObservableProperty] private string _playText = "Играть";
    [ObservableProperty] private string? _playBlockReason;
    [ObservableProperty] private string? _playNote;
    [ObservableProperty] private string _accountName = "Аккаунт не выбран";
    [ObservableProperty] private string _accountStatus = "Войдите через Microsoft на вкладке «Аккаунты»";
    [ObservableProperty] private bool _hasAccount;

    public string SupportedRange => VersionSupport.RangeDescription;

    partial void OnSelectedVersionChanged(VersionOption? value)
    {
        if (!_restoring && value is not null)
            _core.Settings.Update(s => s.SelectedVersionId = value.Id);
        Reevaluate();
    }

    partial void OnOfflineModeChanged(bool value) => Reevaluate();

    private void RestoreSelection()
    {
        _restoring = true;
        try
        {
            var wanted = _core.Settings.Current.SelectedVersionId ?? SelectedVersion?.Id ?? Catalog.LatestRelease;
            SelectedVersion = Catalog.Visible.FirstOrDefault(v => v.Id == wanted)
                              ?? Catalog.Visible.FirstOrDefault(v => v.Id == Catalog.LatestRelease)
                              ?? Catalog.Visible.FirstOrDefault();
        }
        finally
        {
            _restoring = false;
        }
        Reevaluate();
    }

    /// <summary>Пересчитывает состояние кнопки «Играть» и офлайн-режима.</summary>
    public void Reevaluate()
    {
        var account = _core.Accounts.Find(_core.Settings.Current.SelectedAccountId);
        HasAccount = account is not null;
        if (account is null)
        {
            AccountName = "Аккаунт не выбран";
            AccountStatus = "Войдите через Microsoft на вкладке «Аккаунты»";
        }
        else
        {
            AccountName = account.DisplayName;
            AccountStatus = AccountService.StatusOf(account) == UltraLauncher.Core.Auth.AccountStatus.Licensed
                ? "Лицензия Minecraft: Java Edition подтверждена"
                : "Игра не куплена — доступна только демо-версия";
        }

        var version = SelectedVersion;
        // Офлайн-режим: только для подтверждённой лицензии и полностью установленной версии.
        string? offlineReason = null;
        if (account is null) offlineReason = "Нужен аккаунт, который ранее входил онлайн.";
        else if (!account.OwnsGame) offlineReason = "Офлайн-режим доступен только при подтверждённой покупке игры.";
        else if (version is null) offlineReason = "Выберите версию.";
        else if (!_core.Game.IsReadyOffline(version.Id, out var reason)) offlineReason = reason + " Сначала установите версию онлайн.";
        CanUseOffline = offlineReason is null;
        OfflineReason = offlineReason ?? "Запуск без сети: мультиплеер, Realms и скины могут быть недоступны.";
        if (!CanUseOffline && OfflineMode) OfflineMode = false;

        string? block = null;
        if (Operation.IsActive) block = "Дождитесь завершения текущей операции.";
        else if (_shell.IsGameRunning) block = "Игра уже запущена.";
        else if (version is null) block = Catalog.IsLoading ? "Загрузка списка версий…" : "Выберите версию.";
        else if (account is null) block = "Войдите в аккаунт Microsoft на вкладке «Аккаунты».";
        PlayBlockReason = block;

        PlayText = _shell.IsGameRunning ? "Игра запущена"
            : Operation.IsActive ? "Подождите…"
            : version is { IsInstalled: false } && !OfflineMode ? "Установить и играть"
            : account is { OwnsGame: false } ? "Играть (демо)"
            : "Играть";

        PlayNote = version is null ? null
            : version.IsInstalled ? $"Версия {version.Id} установлена — запуск без повторной загрузки."
            : version.IsIncomplete ? $"Установка {version.Id} будет продолжена с места остановки."
            : $"Перед запуском будут скачаны файлы версии {version.Id} (несколько сотен МБ).";

        PlayCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlay() => PlayBlockReason is null;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        var version = SelectedVersion;
        if (version is null) return;
        var offline = OfflineMode;
        await Operation.RunAsync(offline ? $"Запуск {version.Id} (офлайн)" : $"Запуск {version.Id}",
            async (progress, ct) => await _core.Game.PlayAsync(version.Id, offline, progress, ct));
        Catalog.RefreshInstallStates();
        _shell.RefreshAccounts();
    }

    [RelayCommand]
    private void GoToAccounts() => _shell.Navigate("accounts");
}
