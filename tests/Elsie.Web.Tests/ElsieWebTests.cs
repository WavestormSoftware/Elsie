using System.Net;
using Elsie.Web;
using Xunit;

namespace Elsie.Web.Tests;

public class ElsieWebTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
        }
    }

    [Fact]
    public async Task StartAsync_handles_get()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        Assert.Equal("pong", await client.GetStringAsync("/ping"));
    }

    [Fact]
    public async Task ElsieWeb_RunAsync_serves_module()
    {
        await using var server = await ElsieApp.Create(["--urls", "http://127.0.0.1:0"])
            .QuietConsole(false)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        Assert.Equal("pong", await client.GetStringAsync("/ping"));
    }
}
