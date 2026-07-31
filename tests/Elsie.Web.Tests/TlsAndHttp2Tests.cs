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
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
#else
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
#endif
    }
}
