using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Web.Http3;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// HTTP/3 request-scope lifecycle regression tests. The per-request MS.DI scope must stay alive
/// across dispatch and response writing — <c>ctx.GetRequiredService&lt;T&gt;()</c>,
/// <c>ctx.Services</c>, and <c>Use&lt;T&gt;()</c> middleware all resolve from
/// <c>RequestServices</c>. Skipped when <c>QuicListener.IsSupported</c> is false (no libmsquic);
/// CI installs libmsquic so these run in http3.yml.
/// </summary>
public class Http3ScopeTests
{
    private sealed class RequestScopedMarker
    {
        public string Value { get; set; } = "resolved";
    }

    private sealed class ScopedModule : ElsieModule
    {
        public ScopedModule()
        {
            // Handler resolves a scoped service through the live per-request scope.
            Get("/scoped", ctx => ElsieResult.Text("scoped:" + ctx.GetRequiredService<RequestScopedMarker>().Value));
            Get("/services", ctx => ElsieResult.Text("services:" + (ctx.Services.GetService<RequestScopedMarker>()?.Value ?? "null")));
        }
    }

    [Fact]
    public async Task Request_scope_is_alive_during_h3_dispatch()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

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
            .Services(s => s.AddScoped<RequestScopedMarker>())
            .Module<ScopedModule>()
            .StartAsync();

        await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            DefaultStreamErrorCode = 0x0100,
            DefaultCloseErrorCode = 0x0100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        });

        await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);
        await control.WriteAsync(new byte[] { 0x00 }); // control stream type

        var (status, body) = await RoundTripAsync(connection, port, "/scoped");
        Assert.Equal("200", status);
        Assert.Equal("scoped:resolved", body);

        var (status2, body2) = await RoundTripAsync(connection, port, "/services");
        Assert.Equal("200", status2);
        Assert.Equal("services:resolved", body2);
    }

    /// <summary>QUIC is platform-guarded; the caller gates on <see cref="QuicListener.IsSupported"/>.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task<(string Status, string Body)> RoundTripAsync(
        QuicConnection connection,
        int port,
        string path)
    {
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        var encoder = new QpackEncoder(encoderStream: null);
        var block = encoder.EncodeFieldSection(
            [
                (":method", "GET"),
                (":scheme", "https"),
                (":path", path),
                (":authority", $"127.0.0.1:{port}")
            ],
            streamId: 0);
        await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), CancellationToken.None);
        await stream.WriteAsync(new byte[] { 0x00, 0x00 }); // empty DATA frame (end of request body)
        await stream.FlushAsync();
        stream.CompleteWrites();

        string? status = null;
        using var payload = new MemoryStream();
        while (true)
        {
            var frame = await Http3FrameReader.ReadAsync(stream, CancellationToken.None);
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
