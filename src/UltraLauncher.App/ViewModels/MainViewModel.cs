using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.App.ViewModels;

/// <summary>Оболочка: навигация, уведомления, диалог, общая операция и состояние игры.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LauncherCore _core;

    public MainViewModel(LauncherCore core)
    {
        _core = core;
        Notifications = new NotificationViewModel();
        Dialog = new DialogViewModel();
        Operation = new OperationViewModel(Notifications);
        Catalog = new VersionCatalog(core.Game, Notifications) { ShowSnapshots = core.Settings.Current.ShowSnapshots };

        Home = new HomeViewModel(core, this);
        Downloads = new DownloadsViewModel(core, this);
        Accounts = new AccountsViewModel(core, this);
        Settings = new SettingsViewModel(core, this);
        Diagnostics = new DiagnosticsViewModel(core, this);

        core.Game.SessionStarted += s => Application.Current.Dispatcher.BeginInvoke(() => OnGameStarted(s));
        _currentPage = Home;
    }

    public NotificationViewModel Notifications { get; }
    public DialogViewModel Dialog { get; }
    public OperationViewModel Operation { get; }
    public VersionCatalog Catalog { get; }

    public HomeViewModel Home { get; }
    public DownloadsViewModel Downloads { get; }
    public AccountsViewModel Accounts { get; }
    public SettingsViewModel Settings { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public string Version => _core.Version;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private string _currentKey = "home";
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string? _gameStatus;
    [ObservableProperty] private string _sidebarAccount = "Нет аккаунта";
    [ObservableProperty] private string _sidebarAccountStatus = "Войдите через Microsoft";

    public async Task InitializeAsync()
    {
        Accounts.Load();
        RefreshAccounts();
        Downloads.LoadInstalled();
        if (_core.Settings.RecoveryNotice is { } notice)
            Notifications.Show(NoticeKind.Warning, notice, "Проверьте настройки на вкладке «Настройки».");
        else if (!_core.Accounts.IsMicrosoftConfigured)
            Notifications.Show(NoticeKind.Info, "Вход Microsoft пока не настроен.", "Укажите Client ID в «Настройки → Расширенные» (инструкция в README).");
        await Catalog.RefreshAsync();
        _ = Diagnostics.RefreshAsync();
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        if (Settings.IsDirty && CurrentKey == "settings" && key != "settings")
            Notifications.Show(NoticeKind.Warning, "В настройках есть несохранённые изменения.", "Вернитесь на вкладку «Настройки», чтобы сохранить или отменить их.");
        CurrentKey = key;
        CurrentPage = key switch
        {
            "downloads" => Downloads,
            "accounts" => Accounts,
            "settings" => Settings,
            "diagnostics" => Diagnostics,
            _ => Home,
        };
        switch (key)
        {
            case "accounts": Accounts.Load(); break;
            case "settings": Settings.OnShown(); break;
            case "downloads": Downloads.LoadInstalled(); break;
            case "home": Home.Reevaluate(); break;
        }
    }

    public void RefreshAccounts()
    {
        var a = _core.Accounts.Find(_core.Settings.Current.SelectedAccountId);
        SidebarAccount = a?.DisplayName ?? "Нет аккаунта";
        SidebarAccountStatus = a is null ? "Войдите через Microsoft" : a.OwnsGame ? "Лицензия" : "Демо";
        Home.Reevaluate();
    }

    public void OnSettingsSaved()
    {
        Catalog.ShowSnapshots = _core.Settings.Current.ShowSnapshots;
        Catalog.RefreshInstallStates();
        Downloads.LoadInstalled();
        Accounts.Load();
        Home.Reevaluate();
    }

    private void OnGameStarted(GameSession session)
    {
        IsGameRunning = true;
        GameStatus = $"Minecraft {session.VersionId} запущен";
        Home.Reevaluate();
        _ = WatchGameAsync(session);
    }

    private async Task WatchGameAsync(GameSession session)
    {
        var info = await session.Exited;
        IsGameRunning = false;
        GameStatus = null;
        Home.Reevaluate();
        if (info.Crashed)
        {
            Notifications.Show(NoticeKind.Error,
                $"Minecraft {session.VersionId} завершился с ошибкой (код {info.ExitCode}).",
                info.CrashReport is not null
                    ? $"Отчёт о сбое: {info.CrashReport}. Журнал — на вкладке «Диагностика»."
                    : "Подробности — на вкладке «Диагностика» (журнал игры). Частые причины: несовместимая Java, мало памяти, устаревший видеодрайвер.");
        }
        else
        {
            Notifications.Show(NoticeKind.Info, $"Minecraft {session.VersionId} закрыт. Время в игре: {info.Duration:hh\\:mm\\:ss}.");
        }
    }

    [RelayCommand]
    private async Task StopGameAsync()
    {
        var s = _core.Game.RunningSession;
        if (s is null || s.HasExited) return;
        if (await Dialog.ConfirmAsync("Завершить Minecraft принудительно?", "Несохранённый прогресс в игре может быть потерян. Обычно игру лучше закрыть из её меню.", "Завершить", danger: true))
            s.Kill();
    }
}
