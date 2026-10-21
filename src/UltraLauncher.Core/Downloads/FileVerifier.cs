using System.Security.Cryptography;

namespace UltraLauncher.Core.Downloads;

public enum FileState { Valid, Missing, WrongSize, WrongHash }

/// <summary>Проверка целостности файлов по официальным SHA-1 и размерам.</summary>
public sealed class FileVerifier
{
    private readonly VerificationCache? _cache;

    public FileVerifier(VerificationCache? cache = null)
    {
        _cache = cache;
    }

    public static string ComputeSha1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }

    public static string ComputeSha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    /// <summary>Быстрая проверка: файл есть и размер совпадает (для офлайн-запуска и повторного запуска).</summary>
    public static FileState QuickCheck(DownloadItem item)
    {
        var info = new FileInfo(item.Path);
        if (!info.Exists) return FileState.Missing;
        if (item.Size > 0 && info.Length != item.Size) return FileState.WrongSize;
        return FileState.Valid;
    }

    /// <summary>
    /// Полная проверка. Если файл уже проверялся и не менялся (тот же размер и время записи),
    /// повторно хеш не считается.
    /// </summary>
    public FileState Verify(DownloadItem item, bool forceRehash = false)
    {
        var info = new FileInfo(item.Path);
        if (!info.Exists) return FileState.Missing;
        if (item.Size > 0 && info.Length != item.Size) return FileState.WrongSize;
        if (!item.HasHash) return FileState.Valid;

        if (!forceRehash && _cache is not null && _cache.IsKnownValid(info, item.Sha1!))
            return FileState.Valid;

        var actual = ComputeSha1(item.Path);
        if (!string.Equals(actual, item.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            _cache?.Forget(info.FullName);
            return FileState.WrongHash;
        }
        _cache?.Remember(info, item.Sha1!);
        return FileState.Valid;
    }

    public void RememberValid(string path, string sha1)
    {
        _cache?.Remember(new FileInfo(path), sha1);
    }

    public void SaveCache() => _cache?.Save();
}
