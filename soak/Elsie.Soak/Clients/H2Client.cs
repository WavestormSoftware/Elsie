using System.Net;
using System.Net.Http.Json;
using System.Net.Security;

namespace Elsie.Soak.Clients;

/// <summary>
/// HTTP/2 client over TLS (ALPN h2 + h11). One handler instance = one pooled connection when
/// <see cref="allowMultipleConnections"/> is false, so concurrent requests multiplex onto a
/// single h2 connection — exactly what the stream-limit phase needs.
/// </summary>
internal sealed class H2Client : IDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;
    private readonly bool _ownsHandler;

    public H2Client(int port, bool allowMultipleConnections = false)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11
            ]
        };
        _handler = new SocketsHttpHandler
        {
            SslOptions = ssl,
            EnableMultipleHttp2Connections = allowMultipleConnections,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        _ownsHandler = true;
        _client = new HttpClient(_handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public H2Client(int port, SocketsHttpHandler handler)
    {
        _handler = handler;
        _ownsHandler = false;
        _client = new HttpClient(_handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public HttpClient Inner => _client;

    /// <summary>Verifies the negotiated version is really HTTP/2; returns the body text.</summary>
    public async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var res = await _client.GetAsync(path, ct).ConfigureAwait(false);
        CheckVersion(res);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>POSTs a body to /echo and returns the echoed bytes (validates both legs).</summary>
    public async Task<byte[]> EchoAsync(byte[] body, byte[]? expected = null, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var res = await _client.PostAsync("/echo", content, ct).ConfigureAwait(false);
        CheckVersion(res);
        res.EnsureSuccessStatusCode();
        var echoed = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (expected is { } exp && echoed.Length != exp.Length)
        {
            throw new InvalidDataException($"Echo returned {echoed.Length} bytes, expected {exp.Length}.");
        }

        return echoed;
    }

    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        var res = await _client.GetAsync(path, ct).ConfigureAwait(false);
        CheckVersion(res);
        return res;
    }

    private static void CheckVersion(HttpResponseMessage res)
    {
        if (res.Version != HttpVersion.Version20)
        {
            throw new InvalidOperationException($"Expected HTTP/2 response, got {res.Version}.");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }
}