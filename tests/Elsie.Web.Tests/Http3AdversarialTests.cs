using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Deep adversarial HTTP/3 integration tests (RFC 9114) using the in-process QUIC client
/// pattern shared with the other Http3* tests. These probe the server's framing resilience:
/// unexpected first frames, unknown/extension frame tolerance, truncated HEADERS payloads,
/// unknown unidirectional stream types, a second control stream, concurrent load, and
/// request-body size enforcement. Skipped when <c>QuicListener.IsSupported</c> is false (no
/// libmsquic); CI installs libmsquic so these run in http3.yml.
/// </summary>
public class Http3AdversarialTests
{
    private sealed class AdversarialModule : ElsieModule
    {
        public AdversarialModule()
        {
            Get("/ping", () => ElsieResult.Text("h3-pong"));
            Get("/hello", () => ElsieResult.Text("hello"));
            // Reads the (possibly oversized) request body so the background DATA pump runs to
            // completion and the host's IsTooLarge → 413 gate is exercised deterministically.
            Get("/echo", async (ctx, ct) =>
            {
                using var ms = new MemoryStream();
                var buf = new byte[256];
                while (true)
                {
                    var n = await ctx.Request.Body.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    ms.Write(buf, 0, n);
                }

                return ElsieResult.Bytes(ms.ToArray(), "application/octet-stream");
            });
        }
    }

    /// <summary>
    /// A request stream whose first frame is DATA (not HEADERS) must terminate the connection
    /// with H3_FRAME_UNEXPECTED (0x105) — RFC 9114 §4.1.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Request_stream_starting_with_data_closes_connection_with_0x105()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
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
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // First frame is DATA (not HEADERS) → the server must detect the framing violation
            // and close the connection with H3_FRAME_UNEXPECTED (0x105).
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, new byte[] { 0xAA }), ct);
            await stream.FlushAsync(ct);
            stream.CompleteWrites();

            await AssertConnectionClosedWithErrorAsync(connection, 0x105, port, ct);
        });
    }

    /// <summary>
    /// An unknown/extension frame type (0x1F) before HEADERS must be tolerated (RFC 9114 §9):
    /// the subsequent valid HEADERS request still gets a 200.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Unknown_frame_before_headers_is_tolerated_and_request_succeeds()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
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
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            // Unknown extension frame type 0x1F with a tiny payload — MUST be ignored (RFC 9114 §9).
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame((Http3FrameType)0x1F, new byte[] { 0x00 }), ct);
            // Then a valid HEADERS request follows.
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "GET"),
                    (":scheme", "https"),
                    (":path", "/ping"),
                    (":authority", $"127.0.0.1:{port}")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);
            await stream.WriteAsync(new byte[] { 0x00, 0x00 }, ct); // empty DATA frame
            await stream.FlushAsync(ct);
            stream.CompleteWrites();

            var (status, body) = await ReadResponseAsync(stream, ct);
            Assert.Equal("200", status);
            Assert.Equal("h3-pong", body);
        });
    }

    /// <summary>
    /// A HEADERS frame whose declared payload length exceeds what is actually sent, followed by
    /// stream FIN, must not hang the server: the frame-reader treats the truncation as
    /// end-of-stream. A second valid request on the same connection still gets a 200 (or the
    /// connection closes with an RFC error code — both are acceptable).
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Truncated_headers_payload_followed_by_fin_does_not_hang_server()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
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
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // HEADERS frame declaring a 64-byte payload but only 2 bytes are sent, then FIN.
            await using var badStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            await badStream.WriteAsync(new byte[] { 0x01, 0x40, 0xAB, 0xCD }, ct); // type=0x01, len=0x40(64), 2 payload bytes
            await badStream.FlushAsync(ct);
            badStream.CompleteWrites();

            // Give the server a moment to process the truncated stream (it must not hang).
            await Task.Delay(200, ct);

            // A second valid request on the SAME connection must still succeed (or the
            // connection closed with an RFC error code — both acceptable).
            (string status, string body) = await RoundTripAsync(connection, port, "/ping", ct)
                .ConfigureAwait(false);
            Assert.Equal("200", status);
            Assert.Equal("h3-pong", body);
        });
    }

    /// <summary>
    /// A reserved/unknown unidirectional stream type (0x1F) carrying garbage payload must be
    /// drained and ignored (RFC 9114 §6.2) — a valid request afterwards still gets a 200.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Unknown_unidirectional_stream_is_drained_and_ignored()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
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
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // Unknown unidirectional stream type 0x1F with garbage payload, then FIN.
            await using var junk = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await junk.WriteAsync(new byte[] { 0x1F, 0xDE, 0xAD, 0xBE, 0xEF }, ct);
            await junk.FlushAsync(ct);
            junk.CompleteWrites();

            var (status, body) = await RoundTripAsync(connection, port, "/ping", ct).ConfigureAwait(false);
            Assert.Equal("200", status);
            Assert.Equal("h3-pong", body);
        });
    }

    /// <summary>
    /// A second client control stream (type 0x00) must be rejected: RFC 9114 §6.2.1 permits
    /// exactly one control stream per endpoint, so the connection is closed with H3_ID_ERROR
    /// (0x108) — never tolerated.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Second_client_control_stream_closes_connection_with_0x108()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
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
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // First client control stream (type 0x00).
            await using var firstControl = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await firstControl.WriteAsync(new byte[] { 0x00 }, ct);
            await firstControl.FlushAsync(ct);

            // Second client control stream (type 0x00) — must terminate the connection with
            // H3_ID_ERROR (0x108) instead of being tolerated.
            await using var secondControl = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await secondControl.WriteAsync(new byte[] { 0x00 }, ct);
            await secondControl.FlushAsync(ct);

            await AssertConnectionClosedWithErrorAsync(connection, 0x108, port, ct);
        });
    }

    /// <summary>
    /// Concurrent stress-lite: 20 sequential + 10 parallel requests on one connection all return
    /// 200 with correct bodies.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Concurrent_stress_sequential_and_parallel_requests_all_200()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
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
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // 20 sequential requests.
            for (var i = 0; i < 20; i++)
            {
                var (status, body) = await RoundTripAsync(connection, port, "/hello", ct).ConfigureAwait(false);
                Assert.Equal("200", status);
                Assert.Equal("hello", body);
            }

            // 10 parallel requests on the same connection.
            var tasks = Enumerable.Range(0, 10).Select(_ => RoundTripAsync(connection, port, "/ping", ct));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var (status, body) in results)
            {
                Assert.Equal("200", status);
                Assert.Equal("h3-pong", body);
            }
        });
    }

    /// <summary>
    /// A GET with a request body larger than the MaxRequestBodyBytes override (64) returns 413.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Oversized_request_body_returns_413()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
            var port = FindFreePort();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, port, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.EnableHttp3 = true;
                })
                .Server(o => o.MaxRequestBodyBytes = 64)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<AdversarialModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // GET with a 200-byte body — far above the 64-byte limit.
            var body = new byte[200];
            for (var i = 0; i < body.Length; i++)
            {
                body[i] = (byte)(i % 251);
            }

            var (status, _) = await RoundTripAsync(connection, port, "/echo", ct, body).ConfigureAwait(false);
            Assert.Equal("413", status);
        });
    }

    /// <summary>QUIC is platform-guarded; the callers gate on <see cref="QuicListener.IsSupported"/>.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task<QuicConnection> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        return await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            DefaultStreamErrorCode = 0x0100,
            DefaultCloseErrorCode = 0x0100,
            // Zero inbound credit (the default) would starve the server's control/QPACK streams.
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        }, cancellationToken);
    }

    /// <summary>Probes with valid requests until the peer's connection close surfaces as a
    /// <see cref="QuicException"/> carrying <paramref name="expectedErrorCode"/> (the BCL exposes
    /// no connection-closed event). Transient pre-close errors (e.g. stream limits) are retried.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task AssertConnectionClosedWithErrorAsync(
        QuicConnection connection,
        long expectedErrorCode,
        int port,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var probe = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
                var encoder = new QpackEncoder(encoderStream: null);
                var block = encoder.EncodeFieldSection(
                    [
                        (":method", "GET"),
                        (":scheme", "https"),
                        (":path", "/"),
                        (":authority", $"127.0.0.1:{port}")
                    ],
                    streamId: 0);
                await Http3FrameWriter.WriteAsync(probe, new Http3Frame(Http3FrameType.Headers, block), cancellationToken);
                await probe.FlushAsync(cancellationToken);
                probe.CompleteWrites();

                await ReadUntilConnectionClosedAsync(probe, new byte[4096], cancellationToken);
            }
            catch (QuicException ex) when (ex.ApplicationErrorCode == expectedErrorCode)
            {
                Assert.Equal(QuicError.ConnectionAborted, ex.QuicError);
                return;
            }
            catch (QuicException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"The HTTP/3 connection did not close with error code 0x{expectedErrorCode:X}.");
    }

    /// <summary>Loops on reads until the peer's connection close surfaces as a
    /// <see cref="QuicException"/>; partial reads are irrelevant to this loop.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task ReadUntilConnectionClosedAsync(QuicStream stream, byte[] buffer, CancellationToken ct)
    {
#pragma warning disable CA2022 // partial reads are fine — the loop exists only to observe the close
        while (true)
        {
            await stream.ReadAsync(buffer, ct);
        }
#pragma warning restore CA2022
    }

    /// <summary>Performs one GET request (optionally with a body) and returns the :status and
    /// the concatenated DATA payload.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task<(string Status, string Body)> RoundTripAsync(
        QuicConnection connection,
        int port,
        string path,
        CancellationToken cancellationToken,
        byte[]? body = null)
    {
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
        var encoder = new QpackEncoder(encoderStream: null);
        var block = encoder.EncodeFieldSection(
            [
                (":method", "GET"),
                (":scheme", "https"),
                (":path", path),
                (":authority", $"127.0.0.1:{port}")
            ],
            streamId: 0);
        await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), cancellationToken);
        if (body is { Length: > 0 })
        {
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, body), cancellationToken);
        }
        else
        {
            await stream.WriteAsync(new byte[] { 0x00, 0x00 }, cancellationToken); // empty DATA frame
        }

        await stream.FlushAsync(cancellationToken);
        stream.CompleteWrites();

        return await ReadResponseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads response frames until the stream ends, returning :status and the DATA body.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task<(string Status, string Body)> ReadResponseAsync(
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        string? status = null;
        using var payload = new MemoryStream();
        while (true)
        {
            var frame = await Http3FrameReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }

            if (frame.Value.Type == Http3FrameType.Headers && status is null)
            {
                var decoder = new QpackDecoder(maxCapacity: 0, decoderStream: null);
                var fields = decoder.DecodeHeaderBlock(frame.Value.Payload.Span).Fields!;
                status = fields.FirstOrDefault(f => f.Item1 == ":status").Item2;
            }
            else if (frame.Value.Type == Http3FrameType.Data)
            {
                payload.Write(frame.Value.Payload.Span);
            }
        }

        return (status ?? "none", Encoding.UTF8.GetString(payload.ToArray()));
    }

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
    }
}
