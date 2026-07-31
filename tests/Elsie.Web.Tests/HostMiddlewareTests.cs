using System.Net;
using Elsie.Testing;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>Host-level features previously covered via ASP.NET middleware.</summary>
public class HostMiddlewareTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            Get("/ok", () => ElsieResult.Text("ok"));
        }
    }

    [Fact]
    public async Task Static_files_served_under_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-static-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "app.css"), "body{color:red}");

        try
        {
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<EchoModule>()
                .ContentRoot(root)
                .StaticFiles(s =>
                {
                    s.Root = root;
                    s.RequestPath = "/assets";
                })
                .StartAsync();

            using var client = server.CreateClient();
            var css = await client.GetStringAsync("/assets/app.css");
            Assert.Equal("body{color:red}", css);
            Assert.Equal("ok", await client.GetStringAsync("/ok"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Method_not_allowed_returns_405()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<EchoModule>());
        var res = await host.Client.PostAsync("/ok", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.True(res.Content.Headers.Contains("Allow") || res.Headers.Contains("Allow"));
    }
}
