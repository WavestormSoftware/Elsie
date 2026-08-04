using System.Net;
using System.Net.Sockets;
using System.Text;
using Elsie.Testing;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Connection governance tests: over <see cref="ElsieServerOptions.MaxConcurrentConnections"/>
/// a TCP connection receives a graceful 503 (was a silent dispose); a per-IP cap
/// (<see cref="ElsieServerOptions.MaxConnectionsPerIp"/>) rejects further connections from the
/// same source IP; and the HTTP/1.1 keep-alive cap
/// (<see cref="ElsieServerOptions.KeepAliveMaxRequests"/>) sends <c>Connection: close</c> after
/// the configured number of requests.
/// </summary>
public class ConnectionGovernanceTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
        }
    }

    private static async Task<ElsieTestServer> StartServerAsync(Action<ElsieServerOptions> serverOptions)
    {
        return await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(serverOptions)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();
    }

    /// <summary>The first request on a socket used to test connection limits.</summary>
    private static async Task<string> RoundTripAsync(TcpClient tcp, IPEndPoint ep, CancellationToken ct)
    {
        var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), ct);
        return await ReadAllAsync(ns, ct);
    }

    private static async Task<string> ReadAllAsync(NetworkStream ns, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await ns.ReadAsync(buffer, ct);
                if (n == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, n);
            }
        }
        catch (IOException)
        {
            // peer closed — return what we have
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int FirstStatus(string raw)
    {
        var idx = raw.IndexOf("HTTP/1.", StringComparison.Ordinal);
        if (idx < 0)
        {
            return -1;
        }

        var start = idx + "HTTP/1.".Length;
        if (start < raw.Length && char.IsAsciiDigit(raw[start]))
        {
            start++;
        }

        if (start < raw.Length && raw[start] == ' ')
        {
            start++;
        }

        var end = raw.IndexOf(' ', start);
        if (end > start && int.TryParse(raw.AsSpan(start, end - start), out var code))
        {
            return code;
        }

        return -1;
    }

    /// <summary>Over the concurrent-connection limit, a new TCP connection is rejected with 503.</summary>
    [Fact]
    public async Task Over_max_concurrent_connections_returns_503()
    {
        await using var server = await StartServerAsync(o => o.MaxConcurrentConnections = 2);
        var ep = server.Endpoints[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Occupy the two allowed slots.
        using var tcp1 = new TcpClient();
        await tcp1.ConnectAsync(ep.Address, ep.Port, cts.Token);
        using var tcp2 = new TcpClient();
        await tcp2.ConnectAsync(ep.Address, ep.Port, cts.Token);

        // Give the server a moment to register the connections.
        await Task.Delay(200, cts.Token);

        // The third connection is over the limit → graceful 503, not a silent drop.
        using var tcp3 = new TcpClient();
        await tcp3.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var raw = await RoundTripAsync(tcp3, ep, cts.Token);
        Assert.Equal(503, FirstStatus(raw));
        Assert.Contains("Service Unavailable", raw, StringComparison.Ordinal);
    }

    /// <summary>Per-IP cap: connections from one source IP beyond the cap are rejected with 503.</summary>
    [Fact]
    public async Task Over_per_ip_connection_limit_returns_503()
    {
        await using var server = await StartServerAsync(o => o.MaxConnectionsPerIp = 2);
        var ep = server.Endpoints[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp1 = new TcpClient();
        await tcp1.ConnectAsync(ep.Address, ep.Port, cts.Token);
        using var tcp2 = new TcpClient();
        await tcp2.ConnectAsync(ep.Address, ep.Port, cts.Token);

        await Task.Delay(200, cts.Token);

        using var tcp3 = new TcpClient();
        await tcp3.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var raw = await RoundTripAsync(tcp3, ep, cts.Token);
        Assert.Equal(503, FirstStatus(raw));
    }

    /// <summary>After KeepAliveMaxRequests requests on one connection, the response carries
    /// <c>Connection: close</c> and the connection closes.</summary>
    [Fact]
    public async Task Keepalive_cap_sends_connection_close_after_cap()
    {
        await using var server = await StartServerAsync(o => o.KeepAliveMaxRequests = 2);
        var ep = server.Endpoints[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var ns = tcp.GetStream();

        // First request — keep-alive (below cap).
        var first = await SendAndReadOneAsync(ns, "GET /ping HTTP/1.1\r\nHost: localhost\r\n\r\n", cts.Token);
        Assert.Equal(200, FirstStatus(first));
        Assert.Contains("keep-alive", first, StringComparison.OrdinalIgnoreCase);

        // Second request hits the cap (2) → Connection: close.
        var second = await SendAndReadOneAsync(ns, "GET /ping HTTP/1.1\r\nHost: localhost\r\n\r\n", cts.Token);
        Assert.Equal(200, FirstStatus(second));
        var secondHeaders = second[..second.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.Contains("Connection: close", secondHeaders, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads exactly one HTTP/1.x response (headers + Content-Length body).</summary>
    private static async Task<string> SendAndReadOneAsync(NetworkStream ns, string request, CancellationToken ct)
    {
        await ns.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await ns.ReadAsync(buffer, ct);
            if (n == 0)
            {
                break;
            }

            ms.Write(buffer, 0, n);
            headerEnd = IndexOf(ms.ToArray(), (int)ms.Length, "\r\n\r\n"u8);
        }

        if (headerEnd < 0)
        {
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        var bytes = ms.ToArray();
        var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var contentLength = 0;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out contentLength);
            }
        }

        var bodyStart = headerEnd + 4;
        var totalNeeded = bodyStart + contentLength;
        while (ms.Length < totalNeeded)
        {
            var n = await ns.ReadAsync(buffer, ct);
            if (n == 0)
            {
                break;
            }

            ms.Write(buffer, 0, n);
        }

        return Encoding.UTF8.GetString(ms.ToArray(), 0, (int)Math.Min(totalNeeded, ms.Length));
    }

    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
