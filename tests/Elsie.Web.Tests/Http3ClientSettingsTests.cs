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
/// Soak-found repro: an h3 client whose control stream advertises
/// SETTINGS_QPACK_MAX_TABLE_CAPACITY=0 (a legal setting, RFC 9204) was reported to stall
/// every request on the connection. A well-behaved client control stream must not affect
/// request dispatch at all.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("windows")]
public class Http3ClientSettingsTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("h3-pong"));
        }
    }

    [Fact]
    public async Task Client_control_stream_with_qpack_capacity_zero_does_not_stall_requests()
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
                .Module<PingModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // Client control stream: type 0x00 + SETTINGS { QPACK_MAX_TABLE_CAPACITY (0x1) = 0 }.
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await control.WriteAsync(new byte[] { 0x00, 0x04, 0x02, 0x01, 0x00 }, ct);
            await control.FlushAsync(ct);

            // Several sequential requests on separate bidi streams must all dispatch.
            for (var i = 0; i < 3; i++)
            {
                await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
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
                await stream.FlushAsync(ct);
                stream.CompleteWrites();

                var (status, body) = await ReadResponseAsync(stream, ct);
                Assert.Equal("200", status);
                Assert.Equal("h3-pong", body);
            }
        });
    }

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
