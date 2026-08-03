using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// QPACK error-handling regression tests (RFC 9114 §8.1 / RFC 9204 §2.2): a QPACK violation on
/// the encoder/decoder instruction stream or in a field section must terminate the connection
/// with H3_QPACK_ENCODER_STREAM_ERROR (0x201), H3_QPACK_DECODER_STREAM_ERROR (0x202), or
/// H3_QPACK_DECOMPRESSION_FAILED (0x200) — not be swallowed (a poisoned decoder buffer would
/// fail every later stream). Skipped when <c>QuicListener.IsSupported</c> is false (no
/// libmsquic); CI installs libmsquic so these run in http3.yml.
/// </summary>
public class Http3QpackErrorTests
{
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Qpack_encoder_stream_violation_closes_connection_with_0x201()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
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
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // A QPACK encoder stream (type 0x02) carrying Set Dynamic Table Capacity = 5000 —
            // above the server's advertised 4096-byte maximum → the decoder must reject it and the
            // server must close the connection with H3_QPACK_ENCODER_STREAM_ERROR.
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await stream.WriteAsync(new byte[] { 0x02, 0x3F, 0xE9, 0x26 }, ct);
            stream.CompleteWrites();

            await AssertConnectionClosedWithErrorAsync(connection, 0x201, port, ct);
        });
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Qpack_decompression_failure_closes_connection_with_0x200()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
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
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // A request HEADERS frame whose QPACK block encodes Required Insert Count = 1 while the
            // decoder has zero inserts: reconstruction must fail → H3_QPACK_DECOMPRESSION_FAILED.
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            await Http3FrameWriter.WriteAsync(
                stream,
                new Http3Frame(Http3FrameType.Headers, new byte[] { 0x01, 0x00 }),
                ct);
            await stream.FlushAsync(ct);
            stream.CompleteWrites();

            // Reading on the request stream surfaces the peer's connection close (0x200).
            var ex = await Assert.ThrowsAsync<QuicException>(async () =>
                await ReadUntilConnectionClosedAsync(stream, new byte[4096], ct));
            Assert.Equal(QuicError.ConnectionAborted, ex.QuicError);
            Assert.Equal(0x200, ex.ApplicationErrorCode);
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
            $"The HTTP/3 connection did not close with QPACK error code 0x{expectedErrorCode:X}.");
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

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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
