using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Elsie.Auth.Tests;

/// <summary>
/// Minimal loopback HTTP server for JWKS/OIDC metadata tests. Serves canned JSON per path,
/// one request per connection (<c>Connection: close</c>), so <see cref="HttpClient"/> cannot
/// reuse stale keep-alive sockets across re-maps.
/// </summary>
internal sealed class FakeIdpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (int Status, string Body)> _routes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private volatile bool _down;
    private Task _loop = Task.CompletedTask;

    public FakeIdpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public void Map(string path, string json, int statusCode = 200)
    {
        lock (_gate)
        {
            _routes[path] = (statusCode, json);
        }
    }

    public void SetDown(bool down) => _down = down;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // cancellation shutdown
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var requestLine = await ReadLineAsync(stream, _cts.Token).ConfigureAwait(false);
                var path = requestLine is null ? "/" : requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";

                string? line;
                do
                {
                    line = await ReadLineAsync(stream, _cts.Token).ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(line));

                (int Status, string Body) route;
                lock (_gate)
                {
                    route = _down || !_routes.TryGetValue(path, out var r) ? (404, "{}") : r;
                }

                var statusText = route.Status switch
                {
                    200 => "200 OK",
                    404 => "404 Not Found",
                    _ => $"{route.Status} Server Error"
                };
                var body = Encoding.UTF8.GetBytes(route.Body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {statusText}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head, _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // client disconnect / shutdown
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (sb.Length < 8192)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }

            var c = (char)buffer[0];
            if (c == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
