using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Wire-level shape of conditional-request responses over real HTTP/1.1 loopback:
/// a 304 must carry no body and no Content-Length (RFC 9110 §8.6 permits one only when
/// it equals the would-be 200 payload, which the framework does not compute here).
/// </summary>
public class ResponseCachingWireTests
{
    private sealed class CacheModule : ElsieModule
    {
        public CacheModule()
        {
            Use(ElsieCaching.ConditionalGet());
            Get("/etag", () => ElsieResult.Text("hello").WithETag("\"v1\""));
        }
    }

    [Fact]
    public async Task NotModified_wire_response_has_no_body_and_no_content_length()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<CacheModule>()
            .StartAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Endpoints[0].Address, server.Endpoints[0].Port);
        await using var ns = tcp.GetStream();
        var request = "GET /etag HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: \"v1\"\r\nConnection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await ns.ReadAsync(buffer, cts.Token)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        var raw = Encoding.ASCII.GetString(ms.ToArray());
        Assert.StartsWith("HTTP/1.1 304", raw, StringComparison.Ordinal);
        Assert.Contains("ETag: \"v1\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", raw, StringComparison.OrdinalIgnoreCase);
        // No bytes after the header terminator.
        Assert.EndsWith("\r\n\r\n", raw, StringComparison.Ordinal);
    }
}
