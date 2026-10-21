namespace UltraLauncher.Core.Downloads;

public enum DownloadCategory { Metadata, Client, Library, Native, Asset, LogConfig, Runtime }

/// <summary>Файл, который должен лежать по <see cref="Path"/> с указанным хешем.</summary>
public sealed record DownloadItem(
    string Url,
    string Path,
    string? Sha1,
    long Size,
    DownloadCategory Category)
{
    /// <summary>Режим "только ссылка": файл не скачивается, если хеша нет и файл уже есть.</summary>
    public bool HasHash => !string.IsNullOrEmpty(Sha1);
}

public sealed record DownloadProgress(
    int TotalFiles,
    int CompletedFiles,
    long TotalBytes,
    long CompletedBytes,
    double BytesPerSecond,
    string? CurrentFile);
