using Elsie.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
    public void WebApplicationBuilder_AddElsie_registers_dispatcher()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddElsie(o => o.ScanEntryAssembly = false);
        builder.Services.AddElsieModule<PingModule>();
        using var app = builder.Build();

        Assert.NotNull(app.Services.GetService<ElsieDispatcher>());
        Assert.NotNull(app.Services.GetService<ElsieOptions>());
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
            var url = app.Urls.Single();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var body = await client.GetStringAsync("/ping");
            Assert.Equal("pong", body);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
