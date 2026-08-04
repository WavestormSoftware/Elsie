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
/// HTTP/3 hardening tests: SETTINGS_MAX_FIELD_SECTION_SIZE enforcement (H3_EXCESSIVE_LOAD) and
/// the request-body idle timeout on QUIC bodies (408, mirroring the HTTP/1.1 path). Skipped
/// when <c>QuicListener.IsSupported</c> is false (no libmsquic); CI installs it (http3.yml).
/// </summary>
public class Http3HardeningTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            Post("/echo", async (ctx, ct) =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ct);
                return ElsieResult.Bytes(ms.ToArray(), "application/octet-stream");
            });
        }
    }

    /// <summary>QUIC is platform-guarded; the test gates on <see cref="QuicListener.IsSupported"/>.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Oversized_field_section_closes_connection_with_0x107()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.EnableHttp3 = true;
                })
                .Server(o => o.Http3MaxFieldSectionBytes = 256)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<EchoModule>()
                .StartAsync();

            var port = server.Endpoints[0].Port;
            await using var connection = await ConnectAsync(port, ct);

            // HEADERS block far above the advertised 256-byte field-section limit.
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "GET"),
                    (":scheme", "https"),
                    (":path", "/echo"),
                    (":authority", $"127.0.0.1:{port}"),
                    ("x-padding", new string('a', 1024))
                ],
                streamId: 0);
            Assert.True(block.Length > 256);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);
            await stream.FlushAsync(ct);
            stream.CompleteWrites();

            // Reading surfaces the peer's connection close (H3_EXCESSIVE_LOAD).
            var ex = await Assert.ThrowsAsync<QuicException>(async () =>
            {
                var buffer = new byte[1024];
#pragma warning disable CA2022 // partial reads fine — the loop exists only to observe the close
                while (true)
                {
                    await stream.ReadAsync(buffer, ct);
                }
#pragma warning restore CA2022
            });
            Assert.Equal(QuicError.ConnectionAborted, ex.QuicError);
            Assert.Equal(0x107, ex.ApplicationErrorCode);
        });
    }

    /// <summary>QUIC is platform-guarded; the test gates on <see cref="QuicListener.IsSupported"/>.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Stalled_request_body_returns_408()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.EnableHttp3 = true;
                })
                .Server(o => o.RequestBodyIdleTimeout = TimeSpan.FromMilliseconds(300))
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<EchoModule>()
                .StartAsync();

            var port = server.Endpoints[0].Port;
            await using var connection = await ConnectAsync(port, ct);

            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "POST"),
                    (":scheme", "https"),
                    (":path", "/echo"),
                    (":authority", $"127.0.0.1:{port}"),
                    ("content-type", "application/octet-stream"),
                    ("content-length", "10")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);
            // Two body bytes, then the client stalls — the remaining 8 never arrive.
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, new byte[] { 65, 66 }), ct);
            await stream.FlushAsync(ct);

            string? status = null;
            while (true)
            {
                var frame = await Http3FrameReader.ReadAsync(stream, ct);
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
            }

            Assert.Equal("408", status);
        });
    }

    /// <summary>QUIC is platform-guarded; the test gates on <see cref="QuicListener.IsSupported"/>.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Shutdown_with_inflight_stream_returns_within_connection_drain_timeout()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.EnableHttp3 = true;
                })
                .Server(o => o.ConnectionDrainTimeout = TimeSpan.FromMilliseconds(500))
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<EchoModule>()
                .StartAsync();

            var port = server.Endpoints[0].Port;
            await using var connection = await ConnectAsync(port, ct);

            // Open a request stream and send HEADERS but never complete the request — an
            // in-flight stream that would otherwise hold shutdown open.
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "POST"),
                    (":scheme", "https"),
                    (":path", "/echo"),
                    (":authority", $"127.0.0.1:{port}"),
                    ("content-length", "10")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);
            await stream.FlushAsync(ct);

            // Stop the server; the h3 connection drain must return within the configured
            // ConnectionDrainTimeout (500 ms) even though a stream is in flight.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await server.DisposeAsync().ConfigureAwait(false);
            sw.Stop();

            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(5),
                $"h3 drain was not bounded by ConnectionDrainTimeout; took {sw.Elapsed}.");
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

    private static X509Certificate2 CreateSelfSigned()
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
