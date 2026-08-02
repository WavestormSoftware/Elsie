using System.Net;
using System.Security.Cryptography.X509Certificates;
using Elsie;
using Elsie.Web;

var port = int.TryParse(Environment.GetEnvironmentVariable("ELSIE_H2_PORT"), out var p) ? p : 9443;
var pfx = Environment.GetEnvironmentVariable("ELSIE_H2_PFX")
          ?? throw new InvalidOperationException("ELSIE_H2_PFX required");
var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, password: null);

await ElsieApp.Create()
    .QuietConsole(true)
    .Listen(IPAddress.Loopback, port, o =>
    {
        o.UseHttps = true;
        o.Certificate = cert;
        o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
    })
    .Configure(o => o.ScanEntryAssembly = false)
    .Module<App>()
    .RunAsync();

sealed class App : ElsieModule
{
    public App() => Get("/", () => ElsieResult.Text("ok"));
}
