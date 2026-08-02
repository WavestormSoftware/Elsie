using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

public class StreamingScaleTests
{
    private sealed class StreamModule : ElsieModule
    {
        public StreamModule()
        {
            Post("/partial", async (ctx, ct) =>
            {
                var buf = new byte[1024];
                _ = await ctx.Request.Body.ReadAsync(buf.AsMemory(0, 1024), ct);
                return ElsieResult.Text("partial");
            });

            Post("/full", async (ctx, ct) =>
            {
                var bytes = await ctx.Request.BufferBodyAsync(ct);
                return ElsieResult.Text(bytes.Length.ToString());
            });

            Post("/stream-read", async (ctx, ct) =>
            {
                var total = 0L;
                var buf = new byte[4096];
                while (true)
                {
                    var n = await ctx.Request.Body.ReadAsync(buf, ct);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                }

                return ElsieResult.Text(total.ToString());
            });

            Post("/echo-json", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Msg>(ct);
                return bind.IsSuccess ? ctx.Json(bind.Value) : bind.Error!;
            });

            Get("/ping", _ => ElsieResult.Text("pong"));

            Get("/stream-unknown", _ => ElsieResult.Stream(
                async (s, ct) =>
                {
                    await s.WriteAsync("hello-stream"u8.ToArray(), ct);
                },
                "application/octet-stream"));
        }
    }

    private sealed record Msg(string Text);

    [Fact]
    public async Task Content_length_over_max_returns_413_without_buffering()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.MaxRequestBodyBytes = 4 * 1024 * 1024)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        const long declared = 50L * 1024 * 1024;
        var headers =
            "POST /full HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            $"Content-Length: {declared}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(headers));

        // Server should reject on declared CL before reading 50 MiB.
        var response = await ReadUntilAsync(ns, "\r\n\r\n", TimeSpan.FromSeconds(3));
        Assert.Contains("413", response, StringComparison.Ordinal);

        var after = GC.GetTotalMemory(forceFullCollection: true);
        // Must not have materialized the 50 MiB body.
        Assert.True(after - before < 8 * 1024 * 1024, $"memory delta {after - before}");
    }

    [Fact]
    public async Task Keep_alive_drains_partially_read_body()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        var body = new byte[8 * 1024];
        Random.Shared.NextBytes(body);
        var req1 =
            "POST /partial HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(req1));
        await ns.WriteAsync(body);

        var res1 = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("200", res1.Headers, StringComparison.Ordinal);
        Assert.Contains("partial", res1.Body, StringComparison.Ordinal);

        var req2 =
            "GET /ping HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(req2));
        var res2 = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("200", res2.Headers, StringComparison.Ordinal);
        Assert.Contains("pong", res2.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_body_streams_without_full_buffer_and_keep_alive_works()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        // 3 chunks totaling 11 bytes: "hello world"
        var chunked =
            "POST /stream-read HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n" +
            "5\r\nhello\r\n" +
            "1\r\n \r\n" +
            "5\r\nworld\r\n" +
            "0\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(chunked));

        var res1 = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("200", res1.Headers, StringComparison.Ordinal);
        Assert.Contains("11", res1.Body, StringComparison.Ordinal);

        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        var res2 = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("200", res2.Headers, StringComparison.Ordinal);
        Assert.Contains("pong", res2.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_chunk_size_returns_400()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /stream-read HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            "ZZ\r\n"));

        var res = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("400", res.Headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_body_over_max_returns_413()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.MaxRequestBodyBytes = 16)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        var payload = new string('x', 32);
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /stream-read HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            $"{payload.Length:X}\r\n{payload}\r\n" +
            "0\r\n\r\n"));

        var res = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("413", res.Headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindJsonAsync_still_works_with_streaming_body()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var content = new StringContent("{\"Text\":\"hi\"}", Encoding.UTF8, "application/json");
        using var res = await client.PostAsync("/echo-json", content);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("hi", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_length_body_writer_uses_chunked()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<StreamModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /stream-unknown HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

        var msg = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Contains("200", msg.Headers, StringComparison.Ordinal);
        Assert.Contains("Transfer-Encoding: chunked", msg.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello-stream", msg.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Static_file_streams_with_content_length()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-stream-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var payload = new string('Z', 200_000);
        await File.WriteAllTextAsync(Path.Combine(root, "big.txt"), payload);

        try
        {
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<StreamModule>()
                .ContentRoot(root)
                .StaticFiles(s =>
                {
                    s.Root = root;
                    s.RequestPath = "/files";
                })
                .StartAsync();

            var ep = server.Endpoints[0];
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ep.Address, ep.Port);
            await using var ns = tcp.GetStream();

            await ns.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /files/big.txt HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

            var msg = await ReadHttpMessageAsync(ns, TimeSpan.FromSeconds(5));
            Assert.Contains("200", msg.Headers, StringComparison.Ordinal);
            Assert.Contains($"Content-Length: {payload.Length}", msg.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Transfer-Encoding: chunked", msg.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(payload, msg.Body);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker, TimeSpan timeout)
    {
        var ms = new MemoryStream();
        var buf = new byte[1024];
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
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

        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task<(string Headers, string Body)> ReadHttpMessageAsync(Stream stream, TimeSpan timeout)
    {
        var headerText = await ReadUntilAsync(stream, "\r\n\r\n", timeout);
        var split = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split >= 0, "missing header terminator: " + headerText);
        var headers = headerText[..split];
        var already = headerText[(split + 4)..];

        if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            var body = new StringBuilder(already);
            // already may include chunk framing; dechunk from full remainder
            var raw = new MemoryStream(Encoding.ASCII.GetBytes(already));
            var remainingTimeout = timeout;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < remainingTimeout)
            {
                // Try parse what we have; if incomplete, read more.
                if (TryDechunk(raw.ToArray(), out var plain, out var complete) && complete)
                {
                    return (headers, plain);
                }

                var buf = new byte[4096];
                using var cts = new CancellationTokenSource(remainingTimeout - sw.Elapsed);
                int n;
                try
                {
                    n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (n == 0)
                {
                    break;
                }

                var prev = raw.ToArray();
                raw = new MemoryStream();
                raw.Write(prev);
                raw.Write(buf, 0, n);
            }

            TryDechunk(raw.ToArray(), out var fallback, out _);
            return (headers, fallback);
        }

        var cl = 0;
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
            {
                cl = n;
            }
        }

        var bodyBytes = new MemoryStream();
        if (already.Length > 0)
        {
            var pref = Encoding.UTF8.GetBytes(already);
            bodyBytes.Write(pref, 0, pref.Length);
        }

        while (bodyBytes.Length < cl)
        {
            var buf = new byte[Math.Min(8192, cl - (int)bodyBytes.Length)];
            using var cts = new CancellationTokenSource(timeout);
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
            if (n == 0)
            {
                break;
            }

            bodyBytes.Write(buf, 0, n);
        }

        var all = bodyBytes.ToArray();
        var take = Math.Min(cl, all.Length);
        return (headers, Encoding.UTF8.GetString(all, 0, take));
    }

    private static bool TryDechunk(byte[] raw, out string plain, out bool complete)
    {
        plain = string.Empty;
        complete = false;
        var ms = new MemoryStream();
        var offset = 0;
        while (offset < raw.Length)
        {
            var lineEnd = IndexOfCrlf(raw, offset);
            if (lineEnd < 0)
            {
                return false;
            }

            var sizeLine = Encoding.ASCII.GetString(raw, offset, lineEnd - offset);
            offset = lineEnd + 2;
            var semi = sizeLine.IndexOf(';');
            var hex = semi >= 0 ? sizeLine[..semi] : sizeLine;
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size))
            {
                return false;
            }

            if (size == 0)
            {
                complete = true;
                plain = Encoding.UTF8.GetString(ms.ToArray());
                return true;
            }

            if (offset + size + 2 > raw.Length)
            {
                return false;
            }

            ms.Write(raw, offset, size);
            offset += size;
            if (raw[offset] != (byte)'\r' || raw[offset + 1] != (byte)'\n')
            {
                return false;
            }

            offset += 2;
        }

        return false;
    }

    private static int IndexOfCrlf(byte[] data, int start)
    {
        for (var i = start; i < data.Length - 1; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }
}
