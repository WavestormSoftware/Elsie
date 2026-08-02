using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Elsie.Web.Tests;

public class TlsAndHttp2Tests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Post("/echo", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Echo>(ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                return ctx.Json(bind.Value);
            });
        }
    }

    private sealed record Echo(string Message);

    [Fact]
    public async Task Https_http1_ping()
    {
        using var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        var port = server.Endpoints[0].Port;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        Assert.Equal("pong", await client.GetStringAsync("/ping"));
    }

    [Fact]
    public async Task Https_http2_ping()
    {
        using var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        var port = server.Endpoints[0].Port;
        using var handler = CreateHttp2Handler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        using var res = await client.GetAsync("/ping");
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        Assert.Equal("pong", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Https_http2_post_json()
    {
        using var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        var port = server.Endpoints[0].Port;
        using var handler = CreateHttp2Handler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var res = await client.PostAsJsonAsync("/echo", new Echo("hi"), ElsieJson.DefaultOptions);
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("hi", body, StringComparison.Ordinal);
    }

    private sealed class TrailersModule : ElsieModule
    {
        public TrailersModule()
        {
            Get("/trailers", ctx =>
            {
                ctx.Response.AddTrailer("grpc-status", "0");
                ctx.Response.AddTrailer("grpc-message", "ok");
                return ElsieResult.Text("payload");
            });
            Get("/trailers-empty", ctx =>
            {
                ctx.Response.AddTrailer("x-tail", "1");
                return ElsieResult.Text("");
            });
        }
    }

    [Fact]
    public async Task Https_http2_response_trailers()
    {
        using var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<TrailersModule>()
            .StartAsync();

        var port = server.Endpoints[0].Port;
        using var handler = CreateHttp2Handler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var res = await client.GetAsync("/trailers");
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        Assert.Equal("payload", await res.Content.ReadAsStringAsync());
        Assert.True(res.TrailingHeaders.TryGetValues("grpc-status", out var status));
        Assert.Equal("0", Assert.Single(status));
        Assert.True(res.TrailingHeaders.TryGetValues("grpc-message", out var message));
        Assert.Equal("ok", Assert.Single(message));
    }

    [Fact]
    public async Task Https_http2_trailers_with_empty_body()
    {
        using var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<TrailersModule>()
            .StartAsync();

        var port = server.Endpoints[0].Port;
        using var handler = CreateHttp2Handler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var res = await client.GetAsync("/trailers-empty");
        res.EnsureSuccessStatusCode();
        Assert.Equal(string.Empty, await res.Content.ReadAsStringAsync());
        Assert.True(res.TrailingHeaders.TryGetValues("x-tail", out var tail));
        Assert.Equal("1", Assert.Single(tail));
    }

    private static SocketsHttpHandler CreateHttp2Handler()
    {
        var ssl = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        };
        ssl.ApplicationProtocols = new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http2,
            SslApplicationProtocol.Http11
        };
        return new SocketsHttpHandler
        {
            SslOptions = ssl,
            EnableMultipleHttp2Connections = true
        };
    }

    [Fact]
    public async Task Server_limits_reject_huge_body()
    {
        await using var host = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.MaxRequestBodyBytes = 64)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        using var client = host.CreateClient();
        var content = new StringContent(new string('x', 200));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var res = await client.PostAsync("/echo", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    internal static X509Certificate2 CreateSelfSignedForTests() => CreateSelfSigned();

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
    }
}

public class Http3ServerTests
{
    [Fact]
    public async Task Https3_ping_when_supported()
    {
        if (!System.Net.Quic.QuicListener.IsSupported)
        {
            return; // libmsquic absent (e.g. local dev) — exercised in CI http3.yml
        }

        using var cert = TlsAndHttp2Tests.CreateSelfSignedForTests();
        // Fixed free port so the TCP and UDP (h3) listeners share one port.
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
            .Module<PingModule2>()
            .StartAsync();
        var quic = new System.Net.Quic.QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            ClientAuthenticationOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http3],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            },
            DefaultStreamErrorCode = 0x0100,
            DefaultCloseErrorCode = 0x0100
        };

        await using var connection = await System.Net.Quic.QuicConnection.ConnectAsync(quic);
        await using var control = await connection.OpenOutboundStreamAsync(System.Net.Quic.QuicStreamType.Unidirectional);
        await control.WriteAsync(new byte[] { 0x00 });
        await using var request = await connection.OpenOutboundStreamAsync(System.Net.Quic.QuicStreamType.Bidirectional);
        // Minimal request: HEADERS frame with QPACK-encoded :method GET / :scheme https / :path /ping.
        await request.WriteAsync(BuildH3RequestHeaders());
        await request.WriteAsync(new byte[] { 0x00, 0x00 }); // empty DATA frame
        await request.FlushAsync();
        request.CompleteWrites();

        // Read response frames until a DATA frame arrives.
        var payload = new MemoryStream();
        while (true)
        {
            var frame = await Elsie.Web.Http3.Http3FrameReader.ReadAsync(request, CancellationToken.None);
            if (frame is null)
            {
                break;
            }

            if (frame.Value.Type == Elsie.Web.Http3.Http3FrameType.Data)
            {
                payload.Write(frame.Value.Payload.Span);
            }

            if (frame.Value.Type == Elsie.Web.Http3.Http3FrameType.Headers &&
                payload.Length > 0)
            {
                break;
            }
        }

        var body = System.Text.Encoding.UTF8.GetString(payload.ToArray());
        Assert.Equal("h3-pong", body);
    }

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static byte[] BuildH3RequestHeaders()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00); // required insert count
        ms.WriteByte(0x00); // delta base
        // :method GET → indexed (QPACK static 1)
        ms.WriteByte(0x81);
        // :scheme https → indexed (QPACK static 10)
        ms.WriteByte(0x8A);
        // :path /ping → literal name ref (:path QPACK static 3) + value "/ping"
        ms.WriteByte(0x23);
        ms.WriteByte(0x05);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("/ping"));
        return ms.ToArray();
    }

    private sealed class PingModule2 : ElsieModule
    {
        public PingModule2()
        {
            Get("/ping", () => ElsieResult.Text("h3-pong"));
        }
    }
}
