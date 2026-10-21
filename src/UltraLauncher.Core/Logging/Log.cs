using System.Collections.Concurrent;
using System.Text;

namespace UltraLauncher.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-7}] {Source}: {Message}";
}

public interface ILauncherLog
{
    void Write(LogLevel level, string source, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Info(this ILauncherLog log, string source, string message) => log.Write(LogLevel.Info, source, message);
    public static void Warn(this ILauncherLog log, string source, string message, Exception? ex = null) => log.Write(LogLevel.Warning, source, message, ex);
    public static void Error(this ILauncherLog log, string source, string message, Exception? ex = null) => log.Write(LogLevel.Error, source, message, ex);
    public static void Debug(this ILauncherLog log, string source, string message) => log.Write(LogLevel.Debug, source, message);
}

/// <summary>
/// Журнал лаунчера: файл logs\launcher-ГГГГММДД.log + последние записи в памяти для экрана диагностики.
/// Всё проходит через <see cref="SecretRedactor"/>.
/// </summary>
public sealed class FileLog : ILauncherLog, IDisposable
{
    private const int MemoryCapacity = 2000;
    private const int KeepFiles = 14;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly object _fileLock = new();
    private readonly StreamWriter? _writer;

    public FileLog(string logsDirectory)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            CleanupOld(logsDirectory);
            FilePath = Path.Combine(logsDirectory, $"launcher-{DateTime.Now:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // Журнал не должен ронять приложение: остаётся журнал в памяти.
            _writer = null;
        }
    }

    public string? FilePath { get; }

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Recent => [.. _recent];

    public void Write(LogLevel level, string source, string message, Exception? ex = null)
    {
        var text = message;
        if (ex is not null) text += Environment.NewLine + ex;
        var entry = new LogEntry(DateTime.Now, level, source, SecretRedactor.Redact(text));

        _recent.Enqueue(entry);
        while (_recent.Count > MemoryCapacity && _recent.TryDequeue(out _)) { }

        if (_writer is not null)
        {
            lock (_fileLock)
            {
                try { _writer.WriteLine(entry.ToString()); } catch { /* диск недоступен — пропускаем */ }
            }
        }
        EntryAdded?.Invoke(entry);
    }

    public void Dispose()
    {
        lock (_fileLock) _writer?.Dispose();
    }

    private static void CleanupOld(string dir)
    {
        foreach (var old in new DirectoryInfo(dir).GetFiles("launcher-*.log")
                     .OrderByDescending(f => f.Name).Skip(KeepFiles))
        {
            try { old.Delete(); } catch { }
        }
    }
}

/// <summary>Журнал-заглушка для тестов.</summary>
public sealed class MemoryLog : ILauncherLog
{
    public List<LogEntry> Entries { get; } = [];

    public void Write(LogLevel level, string source, string message, Exception? ex = null)
    {
        lock (Entries) Entries.Add(new LogEntry(DateTime.Now, level, source, SecretRedactor.Redact(message + (ex is null ? "" : " " + ex.Message))));
    }
}
