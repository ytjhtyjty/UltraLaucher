using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.App.Services;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Common;

namespace UltraLauncher.App.ViewModels;

public sealed class AccountRow
{
    public required StoredAccount Account { get; init; }
    public required bool IsSelected { get; init; }
    public string Id => Account.Id;
    public string Name => Account.DisplayName;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string? MicrosoftUser => Account.MicrosoftUsername;
    public bool IsLicensed => !Account.IsLocal && Account.OwnsGame;
    public bool CanRefresh => !Account.IsLocal;
    public string StatusText => Account.IsLocal ? "Офлайн-аккаунт — без Microsoft" : Account.OwnsGame ? "Лицензия подтверждена" : "Игра не куплена — только демо";
    public string OfflineText => Account.IsLocal ? "Для игры без сети сначала установите нужную версию и Java." : Account.OwnsGame
        ? $"Офлайн-запуск доступен (лицензия проверена {Account.OwnershipVerifiedUtc?.ToLocalTime():dd.MM.yyyy HH:mm})"
        : "Офлайн-запуск недоступен";
    public string SessionText => Account.MinecraftTokenExpiresAt is { } exp
        ? exp > DateTimeOffset.UtcNow ? $"Сессия Minecraft действует до {exp.ToLocalTime():dd.MM HH:mm}, затем обновится автоматически" : "Сессия Minecraft истекла — обновится при следующем онлайн-запуске"
        : "";
}

public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly LauncherCore _core;
    private readonly MainViewModel _shell;
    private CancellationTokenSource? _signInCts;

    public AccountsViewModel(LauncherCore core, MainViewModel shell)
    {
        _core = core;
        _shell = shell;
        _core.Settings.Changed += _ => Load();
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    [ObservableProperty] private string _localName = "";

    public string LocalNameHint => string.IsNullOrWhiteSpace(LocalName)
        ? "Введите ник: 3–16 латинских букв, цифр или символов _."
        : AccountService.IsValidLocalName(LocalName) ? "" : "Ник должен содержать 3–16 латинских букв, цифр или символов _.";

    partial void OnLocalNameChanged(string value)
    {
        OnPropertyChanged(nameof(LocalNameHint));
        CreateLocalAccountCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreateLocalAccount() => AccountService.IsValidLocalName(LocalName);

    [RelayCommand(CanExecute = nameof(CanCreateLocalAccount))]
    private void CreateLocalAccount()
    {
        try
        {
            var account = _core.Accounts.CreateLocalAccount(LocalName);
            _core.Settings.Update(s => s.SelectedAccountId = account.Id);
            LocalName = "";
            Load();
            _shell.RefreshAccounts();
            _shell.Notifications.Show(NoticeKind.Success, $"Офлайн-аккаунт {account.DisplayName} выбран.",
                "Вход Microsoft не нужен. Realms и серверы с проверкой лицензии недоступны.");
        }
        catch (Exception ex) { _shell.Notifications.ShowError(ex); }
    }

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasAccounts;

    public string SignInBlockReason => !IsConfigured
        ? "Вход недоступен: не указан Client ID приложения Microsoft (Настройки → Расширенные)."
        : IsSigningIn ? "Вход уже выполняется." : "";

    public void Load()
    {
        IsConfigured = _core.Accounts.IsMicrosoftConfigured;
        var selected = _core.Settings.Current.SelectedAccountId;
        Accounts.Clear();
        foreach (var a in _core.Accounts.Accounts)
            Accounts.Add(new AccountRow { Account = a, IsSelected = a.Id == selected });
        HasAccounts = Accounts.Count > 0;
        OnPropertyChanged(nameof(SignInBlockReason));
        SignInCommand.NotifyCanExecuteChanged();
    }

    private bool CanSignIn() => IsConfigured && !IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsSigningIn = true;
        OnPropertyChanged(nameof(SignInBlockReason));
        SignInCommand.NotifyCanExecuteChanged();
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var account = await Task.Run(() => _core.Accounts.SignInAsync(_signInCts.Token));
            _core.Settings.Update(s => s.SelectedAccountId = account.Id);
            _shell.Notifications.Show(account.OwnsGame ? NoticeKind.Success : NoticeKind.Warning,
                account.OwnsGame ? $"Вход выполнен: {account.DisplayName}." : $"Вход выполнен, но на аккаунте {account.DisplayName} нет Minecraft: Java Edition.",
                account.OwnsGame ? null : "Доступна только демо-версия. Купить игру можно на minecraft.net.");
        }
        catch (Exception ex)
        {
            var le = ErrorTranslator.Translate(ex);
            _shell.Notifications.Show(le.Code == ErrorCode.AuthCancelled ? NoticeKind.Info : NoticeKind.Error, le.UserMessage, le.Hint);
            _core.Log.Write(Core.Logging.LogLevel.Warning, "UI", "Вход не выполнен: " + le.UserMessage);
        }
        finally
        {
            _signInCts?.Dispose();
            _signInCts = null;
            IsSigningIn = false;
            Load();
            _shell.RefreshAccounts();
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    [RelayCommand]
    private void Select(AccountRow? row)
    {
        if (row is null) return;
        _core.Settings.Update(s => s.SelectedAccountId = row.Id);
        _shell.RefreshAccounts();
    }

    [RelayCommand]
    private async Task RefreshAsync(AccountRow? row)
    {
        if (row is null || !row.CanRefresh || IsBusy) return;
        IsBusy = true;
        try
        {
            var a = await Task.Run(() => _core.Accounts.RefreshAsync(row.Id, CancellationToken.None));
            _shell.Notifications.Show(NoticeKind.Success, $"Аккаунт {a.DisplayName} проверен.", a.OwnsGame ? null : "Игра на аккаунте не найдена — доступна только демо-версия.");
        }
        catch (Exception ex)
        {
            _shell.Notifications.ShowError(ex);
        }
        finally
        {
            IsBusy = false;
            Load();
            _shell.RefreshAccounts();
        }
    }

    [RelayCommand]
    private async Task SignOutAsync(AccountRow? row)
    {
        if (row is null) return;
        var ok = await _shell.Dialog.ConfirmAsync($"Выйти из аккаунта {row.Name}?",
            "Токены и сохранённые данные авторизации этого аккаунта будут удалены с компьютера. Игровые файлы и миры не затрагиваются.",
            "Выйти", danger: true);
        if (!ok) return;
        await _core.Accounts.SignOutAsync(row.Id);
        if (_core.Settings.Current.SelectedAccountId == row.Id)
            _core.Settings.Update(s => s.SelectedAccountId = _core.Accounts.Accounts.FirstOrDefault()?.Id);
        _shell.Notifications.Show(NoticeKind.Info, $"Выполнен выход из {row.Name}. Локальные данные авторизации удалены.");
        Load();
        _shell.RefreshAccounts();
    }

    [RelayCommand]
    private void OpenSettings() => _shell.Navigate("settings");

    [RelayCommand]
    private void OpenAzureGuide() => ShellService.OpenUrl("https://learn.microsoft.com/entra/identity-platform/quickstart-register-app");
}
