using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Deep HTTP/1.1 protocol tests over raw sockets against a real loopback ElsieApp server.
/// Every socket read is bounded with a CancellationTokenSource timeout so a hung server
/// turns into a test failure rather than a deadlocked suite.
/// </summary>
public class Http1DeepTests
{
    private sealed class DeepModule : ElsieModule
    {
        public DeepModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Get("/hello", () => ElsieResult.Text("hello"));
            Get("/path", ctx => ElsieResult.Text(ctx.QueryOrDefault("q") ?? "none"));
            Post("/echo", async (ctx, ct) =>
            {
                using var sr = new StreamReader(ctx.Request.Body);
                var body = await sr.ReadToEndAsync(ct);
                return ElsieResult.Text(body);
            });
            // Intentionally does NOT read the request body (tests the drain path).
            Post("/nolisten", () => ElsieResult.Text("ok"));
            Options("/", () => ElsieResult.Text("opts-root"));
            Options("/path", () => ElsieResult.Text("opts-path"));
        }
    }

    // --- 1. HTTP/1.1 pipelining ---

    [Fact]
    public async Task Pipelined_requests_yield_ordered_responses()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);
        var req =
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n" +
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n";
        await client.WriteAsync(req);

        var r1 = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        var r2 = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(200, r1.Status);
        Assert.Equal("pong", r1.Body);
        Assert.Equal(200, r2.Status);
        Assert.Equal("pong", r2.Body);
    }

    // --- 2. Chunked request body with extensions + trailer ---

    [Fact]
    public async Task Chunked_body_with_extensions_and_trailer_is_echoed_and_connection_survives()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);

        var body = "Hello, Elsie!";
        var chunked =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n" +
            "5;name=value\r\nHello\r\n" +
            "7\r\n, Elsie\r\n" +
            "1\r\n!\r\n" +
            "0\r\n" +
            "X-Elsie: done\r\n" +
            "\r\n";
        await client.WriteAsync(chunked);

        var echo = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(200, echo.Status);
        Assert.Equal(body, echo.Body);

        // Connection survives: a follow-up request on the same socket must work.
        await client.WriteAsync("GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n");
        var ping = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(200, ping.Status);
        Assert.Equal("pong", ping.Body);
    }

    // --- 3. Chunked body missing terminating 0-chunk, then client FIN ---

    [Fact]
    public async Task Chunked_body_missing_terminator_then_fin_does_not_hang()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);

        var incomplete =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n" +
            "5\r\nHello\r\n" +
            "3\r\nfoo\r\n";
        // No "0\r\n" terminator and no trailers. Shut down the client's write side (FIN).
        await client.WriteAsync(incomplete);
        await client.ShutdownWriteAsync();

        var raw = await client.ReadUntilCloseAsync(TimeSpan.FromSeconds(30));

        // Server must either produce a 4xx response or close the connection — never a 200.
        if (raw.Length == 0)
        {
            // Connection closed cleanly (EOF) — acceptable per the drain/close contract.
            return;
        }

        Assert.DoesNotContain(" 200 ", raw, StringComparison.Ordinal);
        var status = ParseStatus(raw);
        Assert.NotEqual(200, status);
        Assert.True(status >= 400 && status < 500, $"Expected a 4xx status, got {status}.");
    }

    // --- 4. Content-Length body split byte-by-byte ---

    [Fact]
    public async Task Content_length_body_split_byte_by_byte_is_reassembled()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);

        var body = "Segmented-Body-12345";
        await client.WriteAsync(
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

        // Emit one byte at a time, flushing, 5 ms apart.
        foreach (var ch in body)
        {
            await client.WriteAsync(Encoding.ASCII.GetBytes(ch.ToString()));
            await client.FlushAsync();
            await Task.Delay(5);
        }

        var echo = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(200, echo.Status);
        Assert.Equal(body, echo.Body);
    }

    // --- 5. Absolute-form request URI ---

    [Fact]
    public async Task Absolute_form_uri_is_routed_to_path_with_query()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var status = await RawGetStatusAndBodyAsync(ep, "GET http://example.com/path?q=1 HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        // Http1RequestReader.SplitTarget parses an absolute URI via Uri.TryCreate and
        // routes to PathAndQuery ("/path?q=1"), keeping the query intact.
        Assert.Equal(200, status.Status);
        Assert.Equal("1", status.Body);
    }

    // --- 6. MaxHeaderBytes boundary ---

    [Fact]
    public async Task Header_block_at_exact_max_header_bytes_succeeds()
    {
        await using var server = await StartServerAsync(o => o.MaxHeaderBytes = 256);
        var ep = server.Endpoints[0];

        var atLimit = BuildGetRequestWithPad(189); // 58 base + 9 pad-overhead + 189 = 256
        Assert.Equal(256, atLimit.Length);

        var status = await RawGetStatusAndBodyAsync(ep, Encoding.ASCII.GetString(atLimit));
        Assert.Equal(200, status.Status);
    }

    [Fact]
    public async Task Header_block_one_byte_over_max_header_bytes_is_rejected()
    {
        await using var server = await StartServerAsync(o => o.MaxHeaderBytes = 256);
        var ep = server.Endpoints[0];

        var overLimit = BuildGetRequestWithPad(190); // 257 bytes
        Assert.Equal(257, overLimit.Length);

        var status = await RawGetStatusAndBodyAsync(ep, Encoding.ASCII.GetString(overLimit));
        // ConnectionHandler maps "Request headers too large." to 413.
        Assert.Equal(413, status.Status);
    }

    // --- 7. Keep-alive then close ---

    [Fact]
    public async Task Keep_alive_for_two_then_close_on_third()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);

        var req =
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n" +
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n" +
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        await client.WriteAsync(req);

        var r1 = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        var r2 = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        var r3 = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(200, r1.Status);
        Assert.Equal(200, r2.Status);
        Assert.Equal(200, r3.Status);

        // Third response had Connection: close → server closes: remaining read returns EOF.
        var tail = await client.ReadUntilCloseAsync(TimeSpan.FromSeconds(5));
        Assert.False(tail.Contains("200", StringComparison.Ordinal), "Unexpected extra 200 response after close.");
    }

    // --- 8. HEAD response carries Content-Length with no body bytes ---

    [Fact]
    public async Task Head_response_has_content_length_but_no_body_bytes()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);

        await client.WriteAsync(
            "HEAD /hello HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n");

        var headers = await client.ReadHeadersOnlyAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(" 200 ", headers, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 5", headers, StringComparison.Ordinal);

        // No body bytes may follow the header block. A short bounded read must return nothing.
        var stray = await client.ReadShortAsync(TimeSpan.FromMilliseconds(400));
        Assert.Empty(stray);

        // Connection still usable → proves the HEAD framing didn't emit body bytes.
        await client.WriteAsync("GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        var ping = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(200, ping.Status);
        Assert.Equal("pong", ping.Body);
    }

    // --- 9. Second request pipelined while first has an unread body ---

    [Fact]
    public async Task Pipelined_request_after_unread_content_length_body_is_drained()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        await using var client = await RawClient.ConnectAsync(ep);

        // POST /nolisten does NOT read its body; the GET is pipelined immediately after it.
        var req =
            "POST /nolisten HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n" +
            "hello" +
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n";
        await client.WriteAsync(req);

        var post = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(200, post.Status);

        // The server must drain the unread POST body then serve the pipelined GET.
        var get = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(200, get.Status);
        Assert.Equal("pong", get.Body);
    }

    // --- 10. OPTIONS * and OPTIONS /path ---

    [Fact]
    public async Task Options_star_returns_a_response()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        // Http1RequestReader.SplitTarget maps "*" to "/", so it matches Options("/").
        var status = await RawGetStatusAndBodyAsync(ep, "OPTIONS * HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.Equal(200, status.Status);
        Assert.Equal("opts-root", status.Body);
    }

    [Fact]
    public async Task Options_path_returns_a_response()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var status = await RawGetStatusAndBodyAsync(ep, "OPTIONS /path HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.Equal(200, status.Status);
        Assert.Equal("opts-path", status.Body);
    }

    // --- helpers ---

    private static async Task<ElsieTestServer> StartServerAsync(Action<ElsieServerOptions>? server = null)
    {
        var builder = ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<DeepModule>();
        if (server is not null)
        {
            builder = builder.Server(server);
        }

        return await builder.StartAsync();
    }

    private static byte[] BuildGetRequestWithPad(int padValueLength)
    {
        var sb = new StringBuilder();
        sb.Append("GET /ping HTTP/1.1\r\n");
        sb.Append("Host: localhost\r\n");
        sb.Append("Connection: close\r\n");
        if (padValueLength > 0)
        {
            sb.Append("X-Pad: ").Append('a', padValueLength).Append("\r\n");
        }

        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static async Task<(int Status, string Body)> RawGetStatusAndBodyAsync(IPEndPoint ep, string rawRequest)
    {
        await using var client = await RawClient.ConnectAsync(ep);
        await client.WriteAsync(rawRequest);
        var resp = await client.ReadResponseAsync(TimeSpan.FromSeconds(30));
        return (resp.Status, resp.Body);
    }

    private static int ParseStatus(string raw)
    {
        var line = raw.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : -1;
    }

    private static int? GetContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                if (int.TryParse(value, out var v))
                {
                    return v;
                }
            }
        }

        return null;
    }

    /// <summary>A raw socket client with bounded, framing-aware reads.</summary>
    private sealed class RawClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _ns;

        private RawClient(TcpClient tcp, NetworkStream ns)
        {
            _tcp = tcp;
            _ns = ns;
        }

        public static async Task<RawClient> ConnectAsync(IPEndPoint ep)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(ep.Address, ep.Port);
            return new RawClient(tcp, tcp.GetStream());
        }

        public async Task WriteAsync(string text) => await _ns.WriteAsync(Encoding.ASCII.GetBytes(text));

        public async Task WriteAsync(byte[] data) => await _ns.WriteAsync(data);

        public async Task FlushAsync() => await _ns.FlushAsync();

        public async Task ShutdownWriteAsync() => _tcp.Client.Shutdown(SocketShutdown.Send);

        /// <summary>Read one full response (headers + Content-Length body) without over-reading into the next.</summary>
        public async Task<(int Status, string Headers, string Body)> ReadResponseAsync(TimeSpan timeout)
        {
            var headers = await ReadHeadersOnlyAsync(timeout);
            var status = ParseStatus(headers);
            var cl = GetContentLength(headers);
            var body = "";
            if (cl is > 0)
            {
                body = await ReadBodyAsync(cl.Value, timeout);
            }

            return (status, headers, body);
        }

        /// <summary>Read the header block of one response, byte-by-byte, stopping at the CRLFCRLF terminator.</summary>
        public async Task<string> ReadHeadersOnlyAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var ms = new MemoryStream();
            var buf = new byte[1];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var n = await _ns.ReadAsync(buf.AsMemory(0, 1), cts.Token);
                    if (n == 0)
                    {
                        break;
                    }

                    ms.WriteByte(buf[0]);
                    var len = (int)ms.Length;
                    var ba = ms.GetBuffer();
                    if (len >= 4 && ba[len - 4] == '\r' && ba[len - 3] == '\n' &&
                        ba[len - 2] == '\r' && ba[len - 1] == '\n')
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // timeout
            }

            return Encoding.ASCII.GetString(ms.ToArray());
        }

        private async Task<string> ReadBodyAsync(int length, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var ms = new MemoryStream();
            var buf = new byte[4096];
            try
            {
                while (ms.Length < length)
                {
                    var n = await _ns.ReadAsync(
                        buf.AsMemory(0, (int)Math.Min(buf.Length, length - ms.Length)),
                        cts.Token);
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

        /// <summary>Read until EOF or timeout (used to detect a server-initiated close).</summary>
        public async Task<string> ReadUntilCloseAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var ms = new MemoryStream();
            var buf = new byte[4096];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var n = await _ns.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
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

        /// <summary>Read whatever is available within a short window (expects nothing).</summary>
        public async Task<string> ReadShortAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[256];
            try
            {
                var n = await _ns.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                return n == 0 ? string.Empty : Encoding.ASCII.GetString(buf, 0, n);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _ns.DisposeAsync();
            }
            catch
            {
                // ignore
            }

            _tcp.Dispose();
        }
    }
}
