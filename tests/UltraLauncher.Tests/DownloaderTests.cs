using System.Net;
using System.Text;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Downloads;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Tests;

public class DownloaderTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("minecraft-test-payload");
    private static readonly string PayloadSha1 = FileVerifier.ComputeSha1(Payload);

    private static Downloader Create(FakeHttpHandler handler, VerificationCache? cache = null, int attempts = 4, TimeSpan? stall = null)
        => new(new HttpClient(handler), new FileVerifier(cache), new MemoryLog(), new DownloaderOptions
        {
            MaxAttempts = attempts,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            Delay = (_, _) => Task.CompletedTask,
            StallTimeout = stall ?? TimeSpan.FromSeconds(30),
        });

    private static DownloadItem Item(string dir, string name = "file.jar", string url = "https://test/file.jar")
        => new(url, Path.Combine(dir, "sub папка", name), PayloadSha1, Payload.Length, DownloadCategory.Library);

    [Fact]
    public async Task Downloads_VerifiesHash_AndReportsProgress()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Bytes(Payload));
        var reports = new List<DownloadProgress>();
        var item = Item(dir.Path);

        await Create(handler).DownloadAllAsync([item], new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(Payload, File.ReadAllBytes(item.Path));
        Assert.False(File.Exists(item.Path + ".part"));
        var last = reports.Last();
        Assert.Equal(1, last.CompletedFiles);
        Assert.Equal(Payload.Length, last.CompletedBytes);
    }

    [Fact]
    public async Task TransientErrors_AreRetried()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler((_, n) => n switch
        {
            1 => FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            2 => throw new HttpRequestException("connection reset"),
            _ => FakeHttpHandler.Bytes(Payload),
        });
        var item = Item(dir.Path);

        await Create(handler).DownloadOneAsync(item, null, CancellationToken.None);

        Assert.Equal(3, handler.CountFor(item.Url));
        Assert.Equal(Payload, File.ReadAllBytes(item.Path));
    }

    [Fact]
    public async Task CorruptedResponse_IsRetriedThenFailsWithIntegrityError_AndLeavesNoFile()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Bytes(Encoding.UTF8.GetBytes("minecraft-test-PAYLOAD")));
        var item = Item(dir.Path);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Create(handler, attempts: 3).DownloadOneAsync(item, null, CancellationToken.None));

        Assert.Equal(ErrorCode.Integrity, ex.Code);
        Assert.Equal(3, handler.CountFor(item.Url));
        Assert.False(File.Exists(item.Path));
        Assert.False(File.Exists(item.Path + ".part"));
    }

    [Fact]
    public async Task CorruptThenGood_Recovers()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler((_, n) => n == 1 ? FakeHttpHandler.Bytes(Encoding.UTF8.GetBytes("broken-broken-broken-xx")) : FakeHttpHandler.Bytes(Payload));
        var item = Item(dir.Path);
        await Create(handler).DownloadOneAsync(item, null, CancellationToken.None);
        Assert.Equal(PayloadSha1, FileVerifier.ComputeSha1(item.Path));
    }

    [Fact]
    public async Task NotFound_IsNotRetried()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Status(HttpStatusCode.NotFound));
        var item = Item(dir.Path);
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Create(handler).DownloadOneAsync(item, null, CancellationToken.None));
        Assert.Equal(ErrorCode.Network, ex.Code);
        Assert.Equal(1, handler.CountFor(item.Url));
    }

    [Fact]
    public async Task NetworkDown_FailsWithNetworkErrorAfterAllAttempts()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler((Func<HttpRequestMessage, int, HttpResponseMessage>)((_, _) => throw new HttpRequestException("No such host is known")));
        var item = Item(dir.Path);
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Create(handler, attempts: 4).DownloadOneAsync(item, null, CancellationToken.None));
        Assert.Equal(ErrorCode.Network, ex.Code);
        Assert.Equal(4, handler.CountFor(item.Url));
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public async Task StalledConnection_IsRetried()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler(async (req, n) =>
        {
            if (n == 1) await Task.Delay(TimeSpan.FromSeconds(10));
            return FakeHttpHandler.Bytes(Payload);
        });
        var item = Item(dir.Path);
        await Create(handler, stall: TimeSpan.FromMilliseconds(200)).DownloadOneAsync(item, null, CancellationToken.None);
        Assert.Equal(2, handler.CountFor(item.Url));
    }

    [Fact]
    public async Task Cancellation_StopsAndLeavesNoPartialFiles()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler(async (req, _) =>
        {
            cts.Cancel();
            await Task.Delay(50);
            return FakeHttpHandler.Bytes(Payload);
        });
        var items = Enumerable.Range(0, 5).Select(i => Item(dir.Path, $"f{i}.jar", $"https://test/f{i}")).ToList();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(handler).DownloadAllAsync(items, null, cts.Token));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task VerifiedFiles_AreNotDownloadedAgain()
    {
        using var dir = new TempDir();
        var cache = new VerificationCache(dir.Combine("cache.json"));
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Bytes(Payload));
        var downloader = Create(handler, cache);
        var item = Item(dir.Path);
        await downloader.DownloadOneAsync(item, null, CancellationToken.None);
        cache.Save();

        // Новый процесс: кеш читается с диска, файл признаётся проверенным без повторного хеширования и загрузки.
        var cache2 = new VerificationCache(dir.Combine("cache.json"));
        Assert.True(cache2.IsKnownValid(new FileInfo(item.Path), PayloadSha1));
        Assert.Equal(FileState.Valid, new FileVerifier(cache2).Verify(item));
        Assert.Equal(1, handler.TotalRequests);
    }
}

public class FileVerifierTests
{
    [Fact]
    public void DetectsMissingWrongSizeAndWrongHash()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.bin");
        var data = Encoding.UTF8.GetBytes("hello world");
        var good = new DownloadItem("u", path, FileVerifier.ComputeSha1(data), data.Length, DownloadCategory.Asset);
        var verifier = new FileVerifier();

        Assert.Equal(FileState.Missing, verifier.Verify(good));
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello"));
        Assert.Equal(FileState.WrongSize, verifier.Verify(good));
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("HELLO WORLD"));
        Assert.Equal(FileState.WrongHash, verifier.Verify(good));
        File.WriteAllBytes(path, data);
        Assert.Equal(FileState.Valid, verifier.Verify(good));
    }

    [Fact]
    public void Cache_IsInvalidatedWhenFileChanges()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.bin");
        var data = Encoding.UTF8.GetBytes("0123456789");
        File.WriteAllBytes(path, data);
        var item = new DownloadItem("u", path, FileVerifier.ComputeSha1(data), data.Length, DownloadCategory.Asset);
        var cache = new VerificationCache(null);
        var verifier = new FileVerifier(cache);
        Assert.Equal(FileState.Valid, verifier.Verify(item));

        // Повреждение того же размера: время изменения другое → хеш пересчитывается.
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("9876543210"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(FileState.WrongHash, verifier.Verify(item));
    }

    [Fact]
    public void QuickCheck_UsesSizeOnly()
    {
        using var dir = new TempDir();
        var path = dir.Combine("q.bin");
        File.WriteAllBytes(path, new byte[5]);
        Assert.Equal(FileState.Valid, FileVerifier.QuickCheck(new DownloadItem("u", path, "x", 5, DownloadCategory.Asset)));
        Assert.Equal(FileState.WrongSize, FileVerifier.QuickCheck(new DownloadItem("u", path, "x", 6, DownloadCategory.Asset)));
    }

    [Fact]
    public void CorruptCacheFile_IsIgnored()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("c.json"), "{broken");
        var cache = new VerificationCache(dir.Combine("c.json"));
        Assert.False(cache.IsKnownValid(new FileInfo(dir.Combine("c.json")), "aa"));
    }
}

/// <summary>IProgress без SynchronizationContext — отчёты приходят синхронно.</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly object _lock = new();

    public void Report(T value)
    {
        lock (_lock) handler(value);
    }
}
