using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.Core.Common;

namespace UltraLauncher.App.ViewModels;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>Плашка уведомления вверху окна.</summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };

    public NotificationViewModel()
    {
        _timer.Tick += (_, _) => { _timer.Stop(); IsVisible = false; };
    }

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private NoticeKind _kind;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string? _hint;

    public void Show(NoticeKind kind, string message, string? hint = null)
    {
        Kind = kind;
        Message = message;
        Hint = hint;
        IsVisible = true;
        _timer.Stop();
        // Ошибки и предупреждения висят, пока пользователь не закроет — их нельзя пропустить.
        if (kind is NoticeKind.Info or NoticeKind.Success) _timer.Start();
    }

    public void ShowError(Exception ex)
    {
        var le = ErrorTranslator.Translate(ex);
        Show(NoticeKind.Error, le.UserMessage, le.Hint);
    }

    [RelayCommand]
    private void Dismiss()
    {
        _timer.Stop();
        IsVisible = false;
    }
}

/// <summary>Модальный диалог подтверждения внутри окна.</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _tcs;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _confirmText = "OK";
    [ObservableProperty] private string _cancelText = "Отмена";
    [ObservableProperty] private bool _isDanger;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, string cancelText = "Отмена")
    {
        _tcs?.TrySetResult(false);
        _tcs = new TaskCompletionSource<bool>();
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        IsDanger = danger;
        IsOpen = true;
        return _tcs.Task;
    }

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        IsOpen = false;
        _tcs?.TrySetResult(result);
        _tcs = null;
    }
}
