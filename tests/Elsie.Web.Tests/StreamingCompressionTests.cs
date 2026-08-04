using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Testing;
using Elsie.Web.Http3;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Response compression for streaming (<c>BodyWriter</c>) responses: Brotli/GZip wrapping of
/// the outgoing stream, HTTP/1.1 chunked framing, HTTP/2/HTTP/3 DATA-framed streaming through
/// flow control, SSE staying uncompressed, and Vary/negotiation semantics.
/// </summary>
public class StreamingCompressionTests
{
    // > 64 KiB, moderately varied → gzip emits multiple blocks/chunks.
    private static readonly byte[] BigStreamPayload = BuildPayload(64 * 1024 + 137);
    // 4 MiB — well past the h2/h3 peer windows, so compressed DATA frames must respect
    // flow control to roundtrip. Compressed size stays under the source size while the
    // source stays deterministic for byte-for-byte comparison.
    private static readonly byte[] HugeStreamPayload = BuildPayload(4 * 1024 * 1024);

    private sealed class CompressionModule : ElsieModule
    {
        public CompressionModule()
        {
            Get("/stream/big", _ => ElsieResult.Stream(
                async (s, ct) => await s.WriteAsync(BigStreamPayload, ct),
                "text/plain"));

            Get("/stream/huge", _ => ElsieResult.Stream(
                async (s, ct) => await s.WriteAsync(HugeStreamPayload, ct),
                "text/plain"));

            Get("/stream/tiny", _ => ElsieResult.Stream(
                async (s, ct) => await s.WriteAsync("tiny-body"u8.ToArray(), ct),
                "text/plain"));

            // Known length below the default min-size threshold (1024 bytes).
            Get("/stream/small-known", ctx =>
            {
                ctx.Response.Headers.Set("Content-Length", "9");
                return ElsieResult.Stream(
                    async (s, ct) => await s.WriteAsync("tiny-body"u8.ToArray(), ct),
                    "text/plain");
            });

            // App already encoded the payload — the middleware must not wrap it again.
            Get("/stream/precompressed", ctx =>
            {
                ctx.Response.Headers.Set("Content-Encoding", "gzip");
                return ElsieResult.Stream(
                    async (s, ct) => await s.WriteAsync("already-gzip"u8.ToArray(), ct),
                    "text/plain");
            });

            Get("/sse", _ => ElsieResult.ServerSentEvents(async (sse, ct) =>
            {
                await sse.WriteEventAsync("one", cancellationToken: ct);
                await sse.WriteEventAsync("two", cancellationToken: ct);
            }));
        }
    }

    private static byte[] BuildPayload(int size)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(size + 96);
        var row = 0;
        while (sb.Length < size)
        {
            sb.Append("row-").Append(row.ToString("D8", CultureInfo.InvariantCulture)).Append('-');
            for (var j = 0; j < 64; j++)
            {
                sb.Append(alphabet[(row * 31 + j * 7) % alphabet.Length]);
            }

            sb.Append('\n');
            row++;
        }

        return Encoding.UTF8.GetBytes(sb.ToString(0, size));
    }

    [Fact]
    public async Task H1_streamed_large_body_is_gzip_chunked_and_roundtrips()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        var ep = host.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /stream/big HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Accept-Encoding: gzip\r\n" +
            "Connection: close\r\n" +
            "\r\n"));

        var raw = await ReadAllAsync(ns, TimeSpan.FromSeconds(20));
        var (headers, body) = SplitHeaders(raw);

        Assert.Contains("200", headers, StringComparison.Ordinal);
        Assert.Contains("Content-Encoding: gzip", headers, StringComparison.OrdinalIgnoreCase);
        // Unknown-length compressed stream: Content-Length stripped, chunked framing used.
        Assert.Contains("Transfer-Encoding: chunked", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Length", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vary: Accept-Encoding", headers, StringComparison.OrdinalIgnoreCase);

        var decompressed = Gunzip(Dechunk(body));
        Assert.Equal(BigStreamPayload, decompressed);
    }

    [Fact]
    public async Task H1_streamed_body_identity_accept_is_passthrough_with_vary()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/stream/big");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var res = await host.Client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        Assert.Empty(res.Content.Headers.ContentEncoding);
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(BigStreamPayload, body);
        Assert.True(res.Headers.TryGetValues("Vary", out var vary), "Vary header expected");
        Assert.Contains(vary, v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_length_stream_compresses_below_min_size_threshold()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/stream/tiny");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var res = await host.Client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        // Unknown length → compress whenever negotiated, even under the min-size threshold.
        Assert.Equal("gzip", Assert.Single(res.Content.Headers.ContentEncoding));
        Assert.Equal("tiny-body"u8.ToArray(), Gunzip(await res.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Known_length_small_stream_respects_min_size_threshold()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/stream/small-known");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var res = await host.Client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        Assert.Empty(res.Content.Headers.ContentEncoding);
        Assert.Equal("tiny-body", await res.Content.ReadAsStringAsync());
        Assert.Equal(9, res.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Stream_already_content_encoded_is_not_wrapped_again()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/stream/precompressed");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var res = await host.Client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        // Single Content-Encoding (the app's own) — the raw writer output must pass through
        // unwrapped, so double compression cannot corrupt the payload.
        Assert.Equal("gzip", Assert.Single(res.Content.Headers.ContentEncoding));
        Assert.Equal("already-gzip", await res.Content.ReadAsStringAsync());
        Assert.True(res.Headers.TryGetValues("Vary", out var vary), "Vary header expected");
        Assert.Contains(vary, v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Brotli_is_preferred_for_streaming_when_offered()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/stream/big");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        using var res = await host.Client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        Assert.Equal("br", Assert.Single(res.Content.Headers.ContentEncoding));
        var compressed = await res.Content.ReadAsByteArrayAsync();
        await using var input = new MemoryStream(compressed);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await br.CopyToAsync(output);
        Assert.Equal(BigStreamPayload, output.ToArray());
    }

    [Fact]
    public async Task Sse_stays_uncompressed_with_incremental_payload()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CompressionModule>(),
            o => o.EnableResponseCompression = true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/sse");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        using var res = await host.Client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        // The compression middleware must leave `text/event-stream` untouched so per-event
        // flushes reach the client incrementally.
        Assert.Empty(res.Content.Headers.ContentEncoding);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("data: one", body, StringComparison.Ordinal);
        Assert.Contains("data: two", body, StringComparison.Ordinal);
        Assert.True(res.Headers.TryGetValues("Vary", out var vary), "Vary header expected");
        Assert.Contains(vary, v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task H2_streamed_large_body_gzip_roundtrips_through_flow_control()
    {
        using var cert = TlsAndHttp2Tests.CreateSelfSignedForTests();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Server(o => o.EnableResponseCompression = true)
            .Module<CompressionModule>()
            .StartAsync();

        using var client = CreateH2Client(server.Endpoints[0].Port);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/stream/huge")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var res = await client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        // >64 KiB of gzip DATA frames must stream through the h2 send-side flow control
        // without a FLOW_CONTROL_ERROR — byte-for-byte roundtrip is the regression probe.
        Assert.Equal("gzip", Assert.Single(res.Content.Headers.ContentEncoding));
        Assert.Equal(HugeStreamPayload, Gunzip(await res.Content.ReadAsByteArrayAsync()));
        Assert.True(res.Headers.TryGetValues("Vary", out var vary), "Vary header expected");
        Assert.Contains(vary, v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task H3_streamed_large_body_gzip_roundtrips()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — exercised in CI (http3.yml installs it).
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = TlsAndHttp2Tests.CreateSelfSignedForTests();
            var port = FindFreePort();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, port, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.EnableHttp3 = true;
                })
                .Configure(o => o.ScanEntryAssembly = false)
                .Server(o => o.EnableResponseCompression = true)
                .Module<CompressionModule>()
                .StartAsync();

            var quic = new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [SslApplicationProtocol.Http3],
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true
                },
                DefaultStreamErrorCode = 0x0100,
                DefaultCloseErrorCode = 0x0100,
                // Advertise inbound stream credit: the default (0) would starve the server's
                // control/QPACK streams (RFC 9114 requires them).
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 100
            };

            await using var connection = await QuicConnection.ConnectAsync(quic, ct);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await control.WriteAsync(new byte[] { 0x00 }, ct);
            await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);

            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "GET"),
                    (":scheme", "https"),
                    (":path", "/stream/huge"),
                    (":authority", $"127.0.0.1:{port}"),
                    ("accept-encoding", "gzip")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(
                request,
                new Http3Frame(Http3FrameType.Headers, block),
                ct);
            await request.WriteAsync(new byte[] { 0x00, 0x00 }, ct); // empty DATA frame
            await request.FlushAsync(ct);
            request.CompleteWrites();

            string? status = null;
            string? contentEncoding = null;
            using var payload = new MemoryStream();
            var decoder = new QpackDecoder(maxCapacity: 0, decoderStream: null);
            while (true)
            {
                var frame = await Http3FrameReader.ReadAsync(request, ct);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Type == Http3FrameType.Headers && status is null)
                {
                    var fields = decoder.DecodeHeaderBlock(frame.Value.Payload.Span).Fields!;
                    status = fields.FirstOrDefault(f => f.Item1 == ":status").Item2;
                    contentEncoding = fields.FirstOrDefault(f => f.Item1 == "content-encoding").Item2;
                }
                else if (frame.Value.Type == Http3FrameType.Data)
                {
                    payload.Write(frame.Value.Payload.Span);
                }
            }

            Assert.Equal("200", status);
            Assert.Equal("gzip", contentEncoding);
            Assert.Equal(HugeStreamPayload, Gunzip(payload.ToArray()));
        });
    }

    private static HttpClient CreateH2Client(int port)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
        };
        var handler = new SocketsHttpHandler
        {
            SslOptions = ssl,
            EnableMultipleHttp2Connections = true
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, TimeSpan timeout)
    {
        using var output = new MemoryStream();
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                if (n == 0)
                {
                    break;
                }

                output.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return output.ToArray();
    }

    private static (string Headers, byte[] Body) SplitHeaders(byte[] raw)
    {
        for (var i = 0; i < raw.Length - 3; i++)
        {
            if (raw[i] == (byte)'\r' &&
                raw[i + 1] == (byte)'\n' &&
                raw[i + 2] == (byte)'\r' &&
                raw[i + 3] == (byte)'\n')
            {
                var headers = Encoding.ASCII.GetString(raw.AsSpan(0, i));
                return (headers, raw.AsSpan(i + 4).ToArray());
            }
        }

        return (string.Empty, raw);
    }

    private static byte[] Dechunk(byte[] raw)
    {
        using var output = new MemoryStream();
        var offset = 0;
        while (offset < raw.Length)
        {
            var lineEnd = IndexOfCrlf(raw, offset);
            if (lineEnd < 0)
            {
                throw new InvalidOperationException("Truncated chunk-size line.");
            }

            var sizeLine = Encoding.ASCII.GetString(raw, offset, lineEnd - offset);
            var semi = sizeLine.IndexOf(';');
            var hex = semi >= 0 ? sizeLine[..semi] : sizeLine;
            var size = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            offset = lineEnd + 2;
            if (size == 0)
            {
                break;
            }

            if (offset + size + 2 > raw.Length)
            {
                throw new InvalidOperationException("Truncated chunk payload.");
            }

            output.Write(raw, offset, size);
            offset += size + 2;
        }

        return output.ToArray();
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

    private static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
