using System.Net;
using System.Net.Sockets;
using System.Text;
using Elsie.Web.Http;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Phase 1 security hardening specs. Skipped until each S-task lands; unskip with the fix.
/// </summary>
public class ParserAdversarialTests
{
    private sealed class PathModule : ElsieModule
    {
        public PathModule()
        {
            Get("/admin", () => ElsieResult.Text("admin"));
            Get("/a/b", () => ElsieResult.Text("ab"));
            Post("/echo", async (ctx, ct) =>
            {
                using var sr = new StreamReader(ctx.Request.Body);
                var body = await sr.ReadToEndAsync(ct);
                return ElsieResult.Text(body);
            });
            Get("/slow", async (ctx, ct) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // expected on disconnect
                }

                return ElsieResult.Text(ctx.RequestAborted.IsCancellationRequested ? "aborted" : "ok");
            });
            Get("/ping", () => ElsieResult.Text("pong"));
            Get("/empty", () => ElsieResult.NoContent());
        }
    }

    // --- S1: smuggling defenses ---

    [Fact]
    public async Task Rejects_content_length_and_transfer_encoding()
    {
        var body = "5\r\nhello\r\n0\r\n\r\n";
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 5\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            body;
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
        Assert.Contains("smuggling", ex.Message, StringComparison.OrdinalIgnoreCase);
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Rejects_differing_duplicate_content_length()
    {
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 5\r\n" +
            "Content-Length: 6\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            "hello!";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Accepts_equal_duplicate_content_length()
    {
        var body = "hello";
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 5\r\n" +
            "Content-Length: 5\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            body;
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        var req = await reader.ReadAsync(CancellationToken.None);
        Assert.NotNull(req);
        Assert.Equal(5, req!.ContentLength);
        using var sr = new StreamReader(req.Body);
        Assert.Equal(body, await sr.ReadToEndAsync());
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Rejects_non_chunked_transfer_encoding()
    {
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: gzip, chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            "5\r\nhello\r\n0\r\n\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Rejects_giant_chunk_size_line()
    {
        var giant = new string('A', 9 * 1024);
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            giant + "\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
        reader.DisposeBuffer();
    }

    // --- S2: Expect 100-continue ---

    [Fact]
    public async Task Sends_100_continue_when_expect_header_present()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        var body = "{\"ok\":true}";
        var headers =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Expect: 100-continue\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(headers));

        var prelude = await ReadUntilAsync(ns, "\r\n\r\n", TimeSpan.FromSeconds(2));
        Assert.StartsWith("HTTP/1.1 100 Continue", prelude, StringComparison.Ordinal);

        await ns.WriteAsync(Encoding.ASCII.GetBytes(body));
        var response = await ReadUntilAsync(ns, "\r\n\r\n", TimeSpan.FromSeconds(2));
        Assert.Contains("200", response, StringComparison.Ordinal);
    }

    // --- S3: path canonicalization ---

    [Theory]
    [InlineData("//admin")]
    [InlineData("/a/../admin")]
    [InlineData("/./admin")]
    public async Task Canonicalizes_path_forms(string path)
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var status = await RawGetStatusAsync(server.Endpoints[0], path);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Rejects_path_escaping_root()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var status = await RawGetStatusAsync(server.Endpoints[0], "/../admin");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Percent_2f_stays_opaque()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        // /a%2Fb is not /a/b
        var status = await RawGetStatusAsync(server.Endpoints[0], "/a%2Fb");
        Assert.Equal(404, status);
    }

    // --- S4: client disconnect abort ---

    [Fact]
    public async Task Client_disconnect_cancels_request_aborted()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        var ns = tcp.GetStream();
        var req =
            "GET /slow HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(req));
        await Task.Delay(200);
        tcp.Close();

        // Server should not hang forever; next ping still works.
        await Task.Delay(1500);
        var status = await RawGetStatusAsync(ep, "/ping");
        Assert.Equal(200, status);
    }

    // --- S5: Date header ---

    [Fact]
    public async Task Response_includes_date_header()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        var response = await ReadUntilAsync(ns, "\r\n\r\n", TimeSpan.FromSeconds(2));
        Assert.Matches(@"(?im)^Date:\s+.+$", response);
    }

    // --- S6: body idle timeout (placeholder; needs trickle client) ---

    [Fact]
    public async Task Body_idle_timeout_returns_408()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.RequestBodyIdleTimeout = TimeSpan.FromMilliseconds(200))
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 10\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            "ab")); // incomplete body; wait for idle timeout
        await Task.Delay(800);
        var response = await ReadAvailableAsync(ns, TimeSpan.FromSeconds(2));
        Assert.Contains("408", response, StringComparison.Ordinal);
    }

    // --- S7: shutdown aborts open sockets ---

    [Fact]
    public async Task Shutdown_aborts_open_connections()
    {
        var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.ConnectionDrainTimeout = TimeSpan.FromMilliseconds(200))
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PathModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        // open socket, never finish request
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /slow HTTP/1.1\r\nHost: localhost\r\n"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await server.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }

    private static async Task<int> RawGetStatusAsync(IPEndPoint ep, string path)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        var response = await ReadUntilAsync(ns, "\r\n\r\n", TimeSpan.FromSeconds(2));
        var line = response.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : -1;
    }

    private static async Task<string> ReadUntilAsync(NetworkStream ns, string marker, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var ms = new MemoryStream();
        var buf = new byte[1024];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var n = await ns.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                if (n == 0)
                {
                    break;
                }

                ms.Write(buf, 0, n);
                var text = Encoding.ASCII.GetString(ms.ToArray());
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    return text;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // timeout
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task<string> ReadAvailableAsync(NetworkStream ns, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var ms = new MemoryStream();
        var buf = new byte[1024];
        try
        {
            // First byte may block until server responds (or timeout).
            var n = await ns.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
            if (n == 0)
            {
                return string.Empty;
            }

            ms.Write(buf, 0, n);
            while (ns.DataAvailable && !cts.IsCancellationRequested)
            {
                n = await ns.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                if (n == 0)
                {
                    break;
                }

                ms.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // timeout
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
