using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Elsie;
using Elsie.Web;

// HTTP/3 interop sample: serves https+quic on the given port (default 8443).
//   dotnet run -- --urls https://127.0.0.1:8443
//   curl --http3 -k https://127.0.0.1:8443/ping
// Requires libmsquic (see docs/http3.md). Falls back to TCP-only otherwise.

var port = 8443;
for (var i = 0; i < args.Length; i++)
{
    if ((args[i] == "--urls" || args[i] == "--url") &&
        i + 1 < args.Length &&
        Uri.TryCreate(args[i + 1], UriKind.Absolute, out var url) &&
        url.Port > 0)
    {
        port = url.Port;
    }
}

using var cert = CreateSelfSigned();
ElsieApp.Create(args)
    .QuietConsole(false)
    .Listen(IPAddress.Any, port, o =>
    {
        o.UseHttps = true;
        o.Certificate = cert;
        o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
        o.EnableHttp3 = true;
    })
    .Configure(o => o.ScanEntryAssembly = false)
    .Module<AppModule>()
    .Run();

static X509Certificate2 CreateSelfSigned()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    san.AddIpAddress(IPAddress.Any);
    req.CertificateExtensions.Add(san.Build());
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
}

public sealed class AppModule : ElsieModule
{
    public AppModule()
    {
        Get("/ping", () => ElsieResult.Json(new { status = "ok", protocol = "h3" }));
        Get("/", () => ElsieResult.Text("Elsie HTTP/3 sample — try /ping with curl --http3"));
    }
}
