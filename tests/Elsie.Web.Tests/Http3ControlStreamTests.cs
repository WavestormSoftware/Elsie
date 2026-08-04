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
/// HTTP/3 client control-stream tests (RFC 9114 §6.2.1 + §9): unknown frame types on the
/// client control stream must be ignored (not treated as an abort), and the connection must
/// stay usable for a subsequent valid request. Skipped when <c>QuicListener.IsSupported</c> is
/// false (no libmsquic); CI installs libmsquic so these run in http3.yml.
/// </summary>
public class Http3ControlStreamTests
{
    private sealed class ControlModule : ElsieModule
    {
        public ControlModule()
        {
            Get("/ping", () => ElsieResult.Text("h3-pong"));
        }
    }

    /// <summary>
    /// An unknown frame type (0x1F) on the client control stream must be ignored (RFC 9114 §9):
    /// the connection stays open and a subsequent valid request still gets a 200.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Unknown_control_stream_frame_is_ignored_and_request_succeeds()
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
                .Module<ControlModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // Client control stream (type 0x00) carrying an unknown extension frame (0x1F)
            // with a tiny payload. The server MUST ignore it, not abort the connection.
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await control.WriteAsync(new byte[] { 0x00 }, ct);
            await Http3FrameWriter.WriteAsync(control, new Http3Frame((Http3FrameType)0x1F, new byte[] { 0xAA, 0xBB }), ct);
            await control.FlushAsync(ct);
            // Send the request promptly after the control bytes (avoids the in-process
            // spurious-EOF window where a pending control read returns 0 when a new stream opens).
            var (status, body) = await RoundTripAsync(connection, port, "/ping", ct).ConfigureAwait(false);
            Assert.Equal("200", status);
            Assert.Equal("h3-pong", body);
        });
    }

    /// <summary>A known-but-unsupported control frame (GOAWAY 0x07) is also ignored.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task GoAway_control_stream_frame_is_ignored_and_request_succeeds()
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
                .Module<ControlModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // Client control stream with a GOAWAY frame (0x07) — ignored by the server.
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await control.WriteAsync(new byte[] { 0x00 }, ct);
            await Http3FrameWriter.WriteAsync(control, new Http3Frame(Http3FrameType.GoAway, new byte[] { 0x00 }), ct);
            await control.FlushAsync(ct);

            var (status, body) = await RoundTripAsync(connection, port, "/ping", ct).ConfigureAwait(false);
            Assert.Equal("200", status);
            Assert.Equal("h3-pong", body);
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

    /// <summary>Performs one GET request and returns the :status and the concatenated DATA payload.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task<(string Status, string Body)> RoundTripAsync(
        QuicConnection connection,
        int port,
        string path,
        CancellationToken cancellationToken)
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
        await stream.WriteAsync(new byte[] { 0x00, 0x00 }, cancellationToken); // empty DATA frame
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
