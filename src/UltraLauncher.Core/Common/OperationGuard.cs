namespace UltraLauncher.Core.Common;

/// <summary>
/// Не даёт запустить одну и ту же операцию дважды (две установки одной версии,
/// два входа, два запуска игры).
/// </summary>
public sealed class OperationGuard
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool IsRunning(string key)
    {
        lock (_lock) return _running.Contains(key);
    }

    public IDisposable Begin(string key, string busyMessage)
    {
        lock (_lock)
        {
            if (!_running.Add(key))
                throw new LauncherException(ErrorCode.OperationInProgress, busyMessage, "Дождитесь завершения текущей операции.");
        }
        return new Releaser(this, key);
    }

    private sealed class Releaser(OperationGuard owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (owner._lock) owner._running.Remove(key);
        }
    }
}
