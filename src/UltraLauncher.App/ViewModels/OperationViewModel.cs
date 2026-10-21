using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.App.ViewModels;

/// <summary>
/// Текущая длительная операция (установка, запуск, установка Java). В интерфейсе одновременно
/// идёт только одна такая операция — кнопки повторного запуска отключаются, пока она активна.
/// </summary>
public sealed partial class OperationViewModel : ObservableObject
{
    private readonly NotificationViewModel _notifications;
    private CancellationTokenSource? _cts;

    public OperationViewModel(NotificationViewModel notifications)
    {
        _notifications = notifications;
    }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isCancelling;

    public event Action? Finished;

    /// <summary>
    /// Выполняет работу с прогрессом и отменой. Возвращает true при успехе.
    /// Ошибки показываются пользователю с подсказкой.
    /// </summary>
    public async Task<bool> RunAsync(string title, Func<IProgress<InstallProgress>, CancellationToken, Task> work, string? successMessage = null)
    {
        if (IsActive)
        {
            _notifications.Show(NoticeKind.Warning, "Уже выполняется операция: " + Title, "Дождитесь её завершения или отмените её.");
            return false;
        }

        _cts = new CancellationTokenSource();
        Title = title;
        StageText = "Подготовка";
        StatusText = "";
        DetailText = "";
        Progress = 0;
        IsIndeterminate = true;
        IsCancelling = false;
        IsActive = true;
        CancelCommand.NotifyCanExecuteChanged();

        var progress = new Progress<InstallProgress>(OnProgress); // создаётся в UI-потоке → отчёты приходят в UI-поток
        try
        {
            // Работа идёт в пуле потоков, чтобы интерфейс не подвисал даже на синхронных участках.
            await Task.Run(() => work(progress, _cts.Token), _cts.Token);
            if (successMessage is not null) _notifications.Show(NoticeKind.Success, successMessage);
            return true;
        }
        catch (OperationCanceledException)
        {
            _notifications.Show(NoticeKind.Info, $"{title}: отменено.", "Уже скачанные и проверенные файлы сохранены — при повторе загрузка продолжится.");
            return false;
        }
        catch (Exception ex)
        {
            var le = ErrorTranslator.Translate(ex);
            if (le.Code == ErrorCode.AuthCancelled) _notifications.Show(NoticeKind.Info, le.UserMessage, le.Hint);
            else _notifications.Show(NoticeKind.Error, le.UserMessage, le.Hint);
            App.Core?.Log.Write(Core.Logging.LogLevel.Error, "UI", $"{title}: {le.UserMessage}", ex is LauncherException ? ex.InnerException : ex);
            return false;
        }
        finally
        {
            IsActive = false;
            IsCancelling = false;
            _cts.Dispose();
            _cts = null;
            CancelCommand.NotifyCanExecuteChanged();
            Finished?.Invoke();
        }
    }

    private void OnProgress(InstallProgress p)
    {
        StageText = InstallProgress.StageTitle(p.Stage);
        StatusText = p.Status;
        if (p.Fraction is { } f)
        {
            IsIndeterminate = false;
            Progress = Math.Round(f * 100, 1);
        }
        else
        {
            IsIndeterminate = true;
        }

        if (p.Download is { } d)
        {
            var speed = d.BytesPerSecond > 0 ? $" · {FormatBytes(d.BytesPerSecond)}/с" : "";
            DetailText = $"{FormatBytes(d.CompletedBytes)} из {FormatBytes(d.TotalBytes)}{speed}";
        }
        else if (p.Stage != InstallStage.Downloading)
        {
            DetailText = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        StatusText = "Отмена…";
        _cts?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancel() => IsActive && !IsCancelling;

    public static string FormatBytes(double bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }
}
