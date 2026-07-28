using System.Net;
using Elsie.AspNetCore;
using Elsie.Testing;
using Xunit;

namespace Elsie.Testing.Tests;

public class TestHostSmokeTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule() => Get("/ping", () => ElsieResult.Text("pong"));
    }

    [Fact]
    public async Task Test_host_disposes_cleanly()
    {
        var host = ElsieTestHost.Create(s => s.AddElsieModule<PingModule>());
        var response = await host.GetAsync("/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.DisposeAsync();
    }
}
