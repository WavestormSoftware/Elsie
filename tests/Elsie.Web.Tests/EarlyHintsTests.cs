using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// 103 Early Hints (RFC 9118) tests: a handler calling <see cref="ElsieContext.SendEarlyHints"/>
/// produces a 103 with Link headers before the final response on HTTP/1.1, HTTP/2, and HTTP/3.
/// Repeatable and a no-op after the response has started.
/// </summary>
public class EarlyHintsTests
{
    private sealed class HintsModule : ElsieModule
    {
        public HintsModule()
        {
            Get("/hints", ctx =>
            {
                ctx.SendEarlyHints("</app.css>; rel=preload; as=style", "</app.js>; rel=preload; as=script");
                return ElsieResult.Text("body-ok");
            });

            Get("/hints-double", ctx =>
            {
                ctx.SendEarlyHints("</a.css>; rel=preload; as=style");
                ctx.SendEarlyHints("</b.js>; rel=preload; as=script");
                return ElsieResult.Text("double-ok");
            });

            Get("/no-hints", ctx => ElsieResult.Text("plain"));
        }
    }

    // ------------------------------------------------------------------ HTTP/1.1

    [Fact]
    public async Task H1_early_hints_precede_200()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /hints HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), cts.Token);

        var raw = await ReadAllAsync(ns, cts.Token);
        Assert.Contains("HTTP/1.1 103 Early Hints", raw, StringComparison.Ordinal);
        Assert.Contains("Link: </app.css>; rel=preload; as=style", raw, StringComparison.Ordinal);
        Assert.Contains("Link: </app.js>; rel=preload; as=script", raw, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200", raw, StringComparison.Ordinal);
        Assert.Contains("body-ok", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_double_call_emits_both_hints()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /hints-double HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), cts.Token);

        var raw = await ReadAllAsync(ns, cts.Token);
        Assert.Contains("Link: </a.css>; rel=preload; as=style", raw, StringComparison.Ordinal);
        Assert.Contains("Link: </b.js>; rel=preload; as=script", raw, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_no_hints_omits_103()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /no-hints HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), cts.Token);

        var raw = await ReadAllAsync(ns, cts.Token);
        Assert.DoesNotContain("103 Early Hints", raw, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ HTTP/2

    [Fact]
    public async Task H2_early_hints_precede_200()
    {
        using var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<HintsModule>()
            .StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var raw = await RawH2Client.ConnectAsync(server.Endpoints[0].Port, cts.Token);
        var (statuses, links) = await raw.SendSingleRequestCollectAsync(
            [(":method", "GET"), (":scheme", "https"), (":path", "/hints"), (":authority", "localhost")],
            cts.Token);

        Assert.Contains(103, statuses);
        Assert.Contains(200, statuses);
        Assert.Contains(links, l => l.Contains("</app.css>; rel=preload; as=style", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ HTTP/3

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task H3_early_hints_precede_200()
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
                .Module<HintsModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "GET"),
                    (":scheme", "https"),
                    (":path", "/hints"),
                    (":authority", $"127.0.0.1:{port}")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);
            await stream.WriteAsync(new byte[] { 0x00, 0x00 }, ct); // empty DATA
            await stream.FlushAsync(ct);
            stream.CompleteWrites();

            var statuses = new List<string>();
            var links = new List<string>();
            while (true)
            {
                var frame = await Http3FrameReader.ReadAsync(stream, ct);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Type == Http3FrameType.Headers)
                {
                    var decoder = new QpackDecoder(maxCapacity: 0, decoderStream: null);
                    var fields = decoder.DecodeHeaderBlock(frame.Value.Payload.Span).Fields!;
                    var status = fields.FirstOrDefault(f => f.Item1 == ":status").Item2;
                    statuses.Add(status);
                    foreach (var (name, value) in fields)
                    {
                        if (name.Equals("link", StringComparison.OrdinalIgnoreCase))
                        {
                            links.Add(value);
                        }
                    }
                }
            }

            Assert.Contains("103", statuses);
            Assert.Contains("200", statuses);
            Assert.Contains(links, l => l.Contains("</app.css>; rel=preload; as=style", StringComparison.Ordinal));
        });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<ElsieTestServer> StartServerAsync()
    {
        return await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<HintsModule>()
            .StartAsync();
    }

    private static async Task<string> ReadAllAsync(NetworkStream ns, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await ns.ReadAsync(buffer, ct);
                if (n == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, n);
            }
        }
        catch (IOException)
        {
            // peer closed — return what we have
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

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
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        }, cancellationToken);
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
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
