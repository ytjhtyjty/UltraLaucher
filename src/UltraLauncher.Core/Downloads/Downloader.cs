using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Downloads;

public sealed class DownloaderOptions
{
    public int MaxParallel { get; init; } = 8;
    public int MaxAttempts { get; init; } = 4;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Задержка между попытками (подменяется в тестах).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Скачивает файлы параллельно. Каждый файл пишется в *.part, хеш считается на лету,
/// и только после проверки файл переносится на место. Временные сетевые ошибки и
/// несовпадение хеша повторяются с экспоненциальной паузой.
/// </summary>
public sealed class Downloader
{
    private readonly HttpClient _http;
    private readonly FileVerifier _verifier;
    private readonly ILauncherLog _log;
    private readonly DownloaderOptions _options;

    public Downloader(HttpClient http, FileVerifier verifier, ILauncherLog log, DownloaderOptions? options = null)
    {
        _http = http;
        _verifier = verifier;
        _log = log;
        _options = options ?? new DownloaderOptions();
    }

    public FileVerifier Verifier => _verifier;

    public async Task DownloadAllAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (items.Count == 0) return;

        long totalBytes = items.Sum(i => Math.Max(0, i.Size));
        long doneBytes = 0;
        int doneFiles = 0;
        var sw = Stopwatch.StartNew();
        var lastReport = 0L;
        string? current = null;

        void Report(bool force)
        {
            var now = sw.ElapsedMilliseconds;
            if (!force && now - Interlocked.Read(ref lastReport) < 100) return;
            Interlocked.Exchange(ref lastReport, now);
            var bytes = Interlocked.Read(ref doneBytes);
            var speed = sw.Elapsed.TotalSeconds > 0.2 ? bytes / sw.Elapsed.TotalSeconds : 0;
            progress?.Report(new DownloadProgress(items.Count, Volatile.Read(ref doneFiles), totalBytes, bytes, speed, current));
        }

        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallel, CancellationToken = ct },
            async (item, token) =>
            {
                current = Path.GetFileName(item.Path);
                long counted = 0;
                await DownloadOneAsync(item, delta =>
                {
                    counted += delta;
                    Interlocked.Add(ref doneBytes, delta);
                    Report(false);
                }, token).ConfigureAwait(false);

                // Сервер мог не указать размер — выравниваем счётчик под фактический.
                if (item.Size > 0 && counted != item.Size) Interlocked.Add(ref doneBytes, item.Size - counted);
                Interlocked.Increment(ref doneFiles);
                Report(false);
            }).ConfigureAwait(false);

        Report(true);
    }

    public async Task DownloadOneAsync(DownloadItem item, Action<long>? onBytes, CancellationToken ct)
    {
        var delay = _options.InitialRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            long reported = 0;
            try
            {
                await DownloadAttemptAsync(item, b => { reported += b; onBytes?.Invoke(b); }, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < _options.MaxAttempts && IsRetryable(ex))
            {
                onBytes?.Invoke(-reported);
                _log.Warn("Download", $"Попытка {attempt}/{_options.MaxAttempts} не удалась для {item.Url}: {ex.Message}. Повтор через {delay.TotalSeconds:0.#} с");
                await _options.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(20).Ticks));
            }
                catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                onBytes?.Invoke(-reported);
                throw Wrap(item, ex, attempt);
            }
        }
    }

    private async Task DownloadAttemptAsync(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(item.Path)!;
        Directory.CreateDirectory(dir);
        var part = item.Path + ".part";

        // Если данные не приходят дольше StallTimeout — считаем соединение зависшим и повторяем.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_options.StallTimeout);
        try
        {
            using var response = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while (true)
                {
                    stall.CancelAfter(_options.StallTimeout);
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    onBytes(read);
                }
            }

            if (item.Size > 0 && written != item.Size)
                throw new IntegrityException($"размер {written} байт вместо {item.Size}");
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (item.HasHash && !string.Equals(actual, item.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"SHA-1 {actual} вместо {item.Sha1}");

            File.Move(part, item.Path, overwrite: true);
            if (item.HasHash) _verifier.RememberValid(item.Path, item.Sha1!);
        }
        finally
        {
            if (File.Exists(part))
            {
                try { File.Delete(part); } catch { /* будет перезаписан при следующей попытке */ }
            }
        }
    }

    private static bool IsRetryable(Exception ex)
        => ex is IntegrityException || ErrorTranslator.IsTransient(ex)
           || ex is OperationCanceledException; // зависшее соединение (внешняя отмена отфильтрована выше)

    private static LauncherException Wrap(DownloadItem item, Exception ex, int attempts)
    {
        var name = Path.GetFileName(item.Path);
        if (ex is IntegrityException ie)
            return new LauncherException(ErrorCode.Integrity,
                $"Файл {name} повреждён после {attempts} попыток загрузки ({ie.Message}).",
                "Повторите установку. Если ошибка повторяется — проверьте антивирус/прокси, которые могут подменять файлы.", ex);
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
            return new LauncherException(ErrorCode.Network, $"Файл {name} не найден на сервере (HTTP 404).",
                "Обновите список версий; если ошибка повторяется — версия могла быть удалена с серверов Mojang.", ex);
        if (ex is OperationCanceledException)
            return new LauncherException(ErrorCode.Network, $"Превышено время ожидания при загрузке {name}.",
                "Проверьте подключение к интернету и повторите попытку — уже скачанные файлы не будут загружаться заново.", ex);
        var translated = ErrorTranslator.Translate(ex);
        if (translated.Code == ErrorCode.Network)
            return new LauncherException(ErrorCode.Network, $"Не удалось скачать {name}: {translated.UserMessage}",
                "Проверьте подключение к интернету и нажмите «Установить» ещё раз — загрузка продолжится с места остановки.", ex);
        return translated;
    }
}

public sealed class IntegrityException(string message) : Exception(message);
