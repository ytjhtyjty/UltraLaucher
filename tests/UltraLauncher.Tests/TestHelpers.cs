using System.Net;
using System.Text;

namespace UltraLauncher.Tests;

/// <summary>Временная папка с пробелами и кириллицей в пути — заодно проверяем такие пути везде.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UltraLauncher Тесты", (name ?? "папка теста") + " " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>Подставной HTTP-обработчик: отвечает по сценарию, считает запросы.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _respond;
    private readonly Dictionary<string, int> _counts = new();

    public FakeHttpHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public FakeHttpHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : this((r, n) => Task.FromResult(respond(r, n)))
    {
    }

    public int TotalRequests { get; private set; }

    public int CountFor(string url) => _counts.TryGetValue(url, out var c) ? c : 0;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!.ToString();
        lock (_counts)
        {
            _counts[url] = CountFor(url) + 1;
            TotalRequests++;
        }
        return _respond(request, CountFor(url));
    }

    public static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new ByteArrayContent([]) };
}
