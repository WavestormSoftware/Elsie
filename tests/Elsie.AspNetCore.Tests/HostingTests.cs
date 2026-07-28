using System.Net;
using System.Text.Json;
using Elsie.AspNetCore;
using Elsie.Pipelines;
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
            Get("/items/{id:int}", ctx => ElsieResult.Text(ctx.RouteValues["id"]));
            Get("/go", () => ElsieResult.Redirect("/hello/redirected"));
            Post("/echo", async (ctx, ct) =>
            {
                var body = await ctx.ReadJsonAsync<EchoDto>(ct);
                return ElsieResult.Json(body);
            });
        }
    }

    private sealed class GuardedModule : ElsieModule
    {
        public GuardedModule()
        {
            Before(ctx =>
                ctx.QueryOrDefault("token") == "secret"
                    ? null
                    : ElsieResult.Status(401));

            Get("/secret", () => ElsieResult.Text("ok"));
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

    [Fact]
    public async Task Int_constraint_route_works()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var ok = await host.GetAsync("/items/7");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("7", await ok.Content.ReadAsStringAsync());

        var bad = await host.GetAsync("/items/nope");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
    }

    [Fact]
    public async Task Redirect_sets_location()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var response = await host.GetAsync("/go");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/hello/redirected", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Module_before_pipeline_can_short_circuit()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<GuardedModule>());
        var denied = await host.GetAsync("/secret");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var allowed = await host.GetAsync("/secret?token=secret");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("ok", await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Application_pipeline_runs()
    {
        var sawAfter = false;
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieModule<HelloModule>();
            s.ConfigureElsiePipelines(p =>
            {
                p.AddAfter((_, _) => sawAfter = true);
            });
        });

        await host.GetAsync("/health");
        Assert.True(sawAfter);
    }
}
