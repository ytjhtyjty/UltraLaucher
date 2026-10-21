using System.Collections.Concurrent;
using System.Text.Json;
using UltraLauncher.Core.Common;

namespace UltraLauncher.Core.Downloads;

/// <summary>
/// Запоминает файлы, хеш которых уже проверен: путь → (размер, время изменения, sha1).
/// Позволяет не пересчитывать тысячи хешей ассетов при каждом запуске.
/// Если файл изменился (размер или время), запись игнорируется и хеш считается заново.
/// </summary>
public sealed class VerificationCache
{
    private readonly string? _path;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _dirty;

    public VerificationCache(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (data is not null)
                    foreach (var (k, v) in data) _entries[k] = v;
            }
            catch (Exception)
            {
                // Повреждённый кеш просто сбрасывается: файлы будут перепроверены.
            }
        }
    }

    public sealed record Entry(long Size, long LastWriteUtcTicks, string Sha1);

    public bool IsKnownValid(FileInfo file, string expectedSha1)
        => _entries.TryGetValue(file.FullName, out var e)
           && e.Size == file.Length
           && e.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks
           && string.Equals(e.Sha1, expectedSha1, StringComparison.OrdinalIgnoreCase);

    public void Remember(FileInfo file, string sha1)
    {
        file.Refresh();
        if (!file.Exists) return;
        _entries[file.FullName] = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, sha1.ToLowerInvariant());
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Forget(string fullPath)
    {
        if (_entries.TryRemove(fullPath, out _)) Interlocked.Exchange(ref _dirty, 1);
    }

    public void Save()
    {
        if (_path is null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, Entry>(_entries)));
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _dirty, 1);
        }
    }
}
