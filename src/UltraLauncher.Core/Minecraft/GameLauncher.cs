using System.Diagnostics;
using System.Text;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Minecraft;

public sealed record GameExitInfo(int ExitCode, TimeSpan Duration, string LogFile, string? CrashReport)
{
    public bool Crashed => ExitCode != 0;
}

/// <summary>Запущенный процесс Minecraft: вывод (без секретов), завершение, журнал.</summary>
public sealed class GameSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _logWriter;
    private readonly object _writeLock = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly TaskCompletionSource<GameExitInfo> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _gameDirectory;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Log4jEventParser _parser = new();
    private const int MaxRecentLines = 3000;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _recent = new();

    internal GameSession(Process process, string versionId, string logFile, string gameDirectory)
    {
        _process = process;
        VersionId = versionId;
        LogFile = logFile;
        _gameDirectory = gameDirectory;
        _logWriter = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string VersionId { get; }
    public string LogFile { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _exit.Task.IsCompleted;
    public Task<GameExitInfo> Exited => _exit.Task;

    public event Action<string>? OutputLine;

    internal void Attach()
    {
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.Exited += (_, _) => OnExited();
        _process.EnableRaisingEvents = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        if (_process.HasExited) OnExited();
    }

    internal void WriteHeader(string text)
    {
        lock (_writeLock) _logWriter.WriteLine(SecretRedactor.Redact(text));
    }

    private void OnLine(string? line)
    {
        if (line is null) return;
        List<string> ready;
        lock (_writeLock)
        {
            ready = [.. _parser.Feed(SecretRedactor.Redact(line))];
            foreach (var l in ready)
            {
                try { _logWriter.WriteLine(l); } catch (ObjectDisposedException) { }
            }
        }
        foreach (var l in ready)
        {
            _recent.Enqueue(l);
            while (_recent.Count > MaxRecentLines && _recent.TryDequeue(out _)) { }
            OutputLine?.Invoke(l);
        }
    }

    /// <summary>Последние строки вывода игры (без секретов).</summary>
    public IReadOnlyList<string> RecentLines => [.. _recent];

    private void OnExited()
    {
        if (_exit.Task.IsCompleted) return;
        // Дочитываем оставшийся вывод.
        try { _process.WaitForExit(); } catch { }
        var code = 0;
        try { code = _process.ExitCode; } catch { }
        lock (_writeLock)
        {
            try
            {
                _logWriter.WriteLine($"[UltraLauncher] Процесс завершён с кодом {code} через {_uptime.Elapsed:hh\\:mm\\:ss}");
                _logWriter.Dispose();
            }
            catch (ObjectDisposedException) { }
        }
        _exit.TrySetResult(new GameExitInfo(code, _uptime.Elapsed, LogFile, FindCrashReport()));
    }

    private string? FindCrashReport()
    {
        var dir = Path.Combine(_gameDirectory, "crash-reports");
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).GetFiles("crash-*.txt")
            .Where(f => f.LastWriteTime >= _startedAt)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault()?.FullName;
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        _process.Dispose();
        lock (_writeLock)
        {
            try { _logWriter.Dispose(); } catch { }
        }
    }
}

/// <summary>Запускает Java с аргументами Minecraft.</summary>
public sealed class GameLauncher
{
    private readonly ILauncherLog _log;
    private readonly string _gameLogsDirectory;
    private readonly PlatformInfo _platform;

    public GameLauncher(ILauncherLog log, string gameLogsDirectory, PlatformInfo platform)
    {
        _log = log;
        _gameLogsDirectory = gameLogsDirectory;
        _platform = platform;
    }

    public GameSession Start(LaunchRequest request)
    {
        SecretRedactor.Register(request.Player.AccessToken);
        var args = LaunchArgumentsBuilder.Build(request, _platform);
        Directory.CreateDirectory(request.Layout.Root);
        Directory.CreateDirectory(request.Layout.NativesDirectory(request.Plan.Version.Id));
        Directory.CreateDirectory(_gameLogsDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = request.JavaPath,
            WorkingDirectory = request.Layout.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var commandForLog = SecretRedactor.Redact(CommandLine.Join(args.Prepend(request.JavaPath)));
        _log.Info("Launch", $"Запуск {request.Plan.Version.Id} для {request.Player}: {commandForLog}");

        var logFile = Path.Combine(_gameLogsDirectory, $"{Sanitize(request.Plan.Version.Id)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new LauncherException(ErrorCode.GameStartFailed, "Процесс Java не запустился.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new LauncherException(ErrorCode.GameStartFailed, "Не удалось запустить Java: " + ex.Message,
                "Проверьте путь к Java в настройках.", ex);
        }

        var session = new GameSession(process, request.Plan.Version.Id, logFile, request.Layout.Root);
        session.WriteHeader($"[UltraLauncher] {DateTime.Now:yyyy-MM-dd HH:mm:ss} Запуск {request.Plan.Version.Id}");
        session.WriteHeader("[UltraLauncher] " + commandForLog);
        session.Attach();
        _ = session.Exited.ContinueWith(t =>
        {
            var info = t.Result;
            if (info.Crashed)
                _log.Warn("Launch", $"Игра {request.Plan.Version.Id} завершилась с кодом {info.ExitCode}. Журнал: {info.LogFile}" + (info.CrashReport is null ? "" : $", отчёт: {info.CrashReport}"));
            else
                _log.Info("Launch", $"Игра {request.Plan.Version.Id} закрыта (время в игре {info.Duration:hh\\:mm\\:ss})");
        }, TaskScheduler.Default);
        return session;
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
