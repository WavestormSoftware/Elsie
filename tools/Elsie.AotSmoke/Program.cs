using System.Net;
using Elsie;
using Elsie.Web;

// Native-AOT smoke: publish with PublishAot/PublishTrimmed, then run this binary.
// Boots the real host (HTTP/1.1 loopback), dispatches requests in-process, and exits
// non-zero on any failure so CI can assert the trimmed binary actually works.

var app = ElsieApp.Create()
    .QuietConsole(false)
    .Listen(IPAddress.Loopback, 0)
    .Configure(o => o.ScanEntryAssembly = false)
    .Services(s => s.AddElsieModule<AotModule>());

await using var server = await app.StartAsync();
using var client = server.CreateClient();

var res = await client.GetAsync("/");
if (res.StatusCode != HttpStatusCode.OK)
{
    Console.Error.WriteLine($"AOT smoke FAILED: GET / -> {res.StatusCode}");
    return 1;
}

var body = await res.Content.ReadAsStringAsync();
if (body != "aot-ok")
{
    Console.Error.WriteLine($"AOT smoke FAILED: unexpected body '{body}'");
    return 2;
}

// Reflection-based ElsieResult.Json uses runtime STJ metadata; under AOT it fails by design
// (documented limitation — see docs/hosting-and-aot.md). Report the status but do not fail the
// gate; the host itself must stay healthy.
try
{
    var jsonRes = await client.GetAsync("/json");
    Console.WriteLine($"info: GET /json -> {(int)jsonRes.StatusCode} (reflection STJ under AOT is a known limitation)");
}
catch (HttpRequestException)
{
    Console.WriteLine("info: GET /json failed (expected under AOT — reflection-based STJ limitation)");
}

Console.WriteLine("AOT smoke OK.");
return 0;

public sealed class AotModule : ElsieModule
{
    public AotModule()
    {
        Get("/", () => ElsieResult.Text("aot-ok"));
        Get("/json", () => ElsieResult.Json(new { status = "ok" }));
    }
}
