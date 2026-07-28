using Elsie.AspNetCore;
using Xunit;

namespace Elsie.AspNetCore.Tests;

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
    public async Task CreateApp_handles_get()
    {
        await using var app = ElsieWeb.CreateApp<PingModule>(
            args: ["--urls", "http://127.0.0.1:0"],
            configure: o => o.ScanEntryAssembly = false);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.Equal("pong", await client.GetStringAsync("/ping"));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
