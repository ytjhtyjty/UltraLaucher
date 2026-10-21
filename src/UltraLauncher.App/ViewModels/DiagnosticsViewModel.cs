using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.App.Services;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;
using UltraLauncher.Core.Settings;

namespace UltraLauncher.App.ViewModels;

public sealed record InfoRow(string Name, string Value);

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private const int MaxLines = 1500;
    private readonly LauncherCore _core;
    private readonly MainViewModel _shell;
    private GameSession? _attached;

    public DiagnosticsViewModel(LauncherCore core, MainViewModel shell)
    {
        _core = core;
        _shell = shell;
        foreach (var e in core.Log.Recent.TakeLast(MaxLines)) LauncherLog.Add(e.ToString());
        core.Log.EntryAdded += e => Application.Current?.Dispatcher.BeginInvoke(() => Append(LauncherLog, e.ToString()));
        core.Game.SessionStarted += s => Application.Current?.Dispatcher.BeginInvoke(() => AttachGame(s));
    }

    public ObservableCollection<string> LauncherLog { get; } = [];
    public ObservableCollection<string> GameLog { get; } = [];
    public ObservableCollection<InfoRow> SystemInfo { get; } = [];
    public ObservableCollection<string> JavaList { get; } = [];

    [ObservableProperty] private string _gameLogTitle = "Журнал игры (игра ещё не запускалась в этом сеансе)";
    [ObservableProperty] private bool _isRefreshing;

    private static void Append(ObservableCollection<string> target, string line)
    {
        target.Add(line);
        while (target.Count > MaxLines) target.RemoveAt(0);
    }

    private void AttachGame(GameSession session)
    {
        _attached = session;
        GameLog.Clear();
        GameLogTitle = $"Журнал игры: {session.VersionId} (процесс {session.ProcessId})";
        session.OutputLine += line => Application.Current?.Dispatcher.BeginInvoke(() => { if (_attached == session) Append(GameLog, line); });
        _ = session.Exited.ContinueWith(t => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_attached == session) GameLogTitle = $"Журнал игры: {session.VersionId} — завершена с кодом {t.Result.ExitCode}";
        }));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var s = _core.Settings.Current;
            SystemInfo.Clear();
            SystemInfo.Add(new InfoRow("UltraLauncher", _core.Version));
            SystemInfo.Add(new InfoRow("Windows", $"{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})"));
            SystemInfo.Add(new InfoRow(".NET", Environment.Version.ToString()));
            SystemInfo.Add(new InfoRow("Память", $"{SystemMemory.TotalPhysicalMb()} МБ всего, {SystemMemory.AvailablePhysicalMb()} МБ свободно"));
            SystemInfo.Add(new InfoRow("Масштаб экрана", $"{GetDpiScale():P0}"));
            SystemInfo.Add(new InfoRow("Папка игры", s.GameDirectory));
            SystemInfo.Add(new InfoRow("Данные лаунчера", _core.Paths.DataRoot));
            SystemInfo.Add(new InfoRow("Журнал лаунчера", _core.Log.FilePath ?? "недоступен"));
            SystemInfo.Add(new InfoRow("Память игры", $"{s.MinMemoryMb}–{s.MaxMemoryMb} МБ"));
            SystemInfo.Add(new InfoRow("Java в настройках", s.JavaPath ?? "автоматический выбор"));
            SystemInfo.Add(new InfoRow("Вход Microsoft", _core.Accounts.IsMicrosoftConfigured ? "Client ID указан" : "не настроен (нет Client ID)"));
            SystemInfo.Add(new InfoRow("Установлено версий", _core.Game.SafeListInstalled().Count.ToString()));

            var java = await Task.Run(() => _core.Game.DetectJava(refresh: true));
            JavaList.Clear();
            foreach (var j in java) JavaList.Add($"{j.DisplayName}  [{j.Source}]");
            if (java.Count == 0) JavaList.Add("Java не найдена. При запуске игры будет скачана официальная Java от Mojang.");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static double GetDpiScale()
    {
        var w = Application.Current?.MainWindow;
        return w is null ? 1 : System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;
    }

    [RelayCommand]
    private void OpenLogsFolder() => Safe(() => ShellService.OpenFolder(_core.Paths.LogsDirectory));

    [RelayCommand]
    private void OpenGameLogsFolder() => Safe(() => ShellService.OpenFolder(_core.Paths.GameLogsDirectory));

    [RelayCommand]
    private void OpenCrashReports() => Safe(() => ShellService.OpenFolder(Path.Combine(_core.Settings.Current.GameDirectory, "crash-reports")));

    [RelayCommand]
    private void CopyReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== UltraLauncher: отчёт диагностики ===");
        foreach (var r in SystemInfo) sb.AppendLine($"{r.Name}: {r.Value}");
        sb.AppendLine().AppendLine("--- Java ---");
        foreach (var j in JavaList) sb.AppendLine(j);
        sb.AppendLine().AppendLine("--- Журнал лаунчера (последние 200 строк) ---");
        foreach (var l in LauncherLog.TakeLast(200)) sb.AppendLine(l);
        if (GameLog.Count > 0)
        {
            sb.AppendLine().AppendLine("--- Журнал игры (последние 200 строк) ---");
            foreach (var l in GameLog.TakeLast(200)) sb.AppendLine(l);
        }
        // Повторная очистка на всякий случай: в отчёт не должны попасть токены.
        ShellService.CopyText(SecretRedactor.Redact(sb.ToString()));
        _shell.Notifications.Show(NoticeKind.Success, "Отчёт скопирован в буфер обмена.", "Токены и секреты в отчёт не включаются.");
    }

    private void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { _shell.Notifications.ShowError(ex); }
    }
}
