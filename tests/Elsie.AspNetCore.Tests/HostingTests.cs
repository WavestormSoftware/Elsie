using System.Net;
using System.Text.Json;
using Elsie.AspNetCore;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.AspNetCore.Tests;

public class HostingTests
{
    private sealed class HelloModule : ElsieModule
    {
        public HelloModule()
        {
            Get("/hello/{name}", ctx => ElsieResult.Text($"Hello {ctx.RouteValues["name"]}"));
            Get("/health", () => ElsieResult.Json(new { status = "ok" }));
            Post("/echo", async (ctx, ct) =>
            {
                var body = await ctx.ReadJsonAsync<EchoDto>(ct);
                return ElsieResult.Json(body);
            });
        }
    }

    private sealed record EchoDto(string Message);

    [Fact]
    public async Task Get_hello_returns_text()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var response = await host.GetAsync("/hello/Ada");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello Ada", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_health_returns_json()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var response = await host.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Post_echo_roundtrips_json()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var response = await host.PostJsonAsync("/echo", new EchoDto("ping"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ping", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_route_returns_404_when_mapped_terminal()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var response = await host.GetAsync("/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
