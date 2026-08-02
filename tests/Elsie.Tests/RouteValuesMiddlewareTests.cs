using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class RouteValuesMiddlewareTests
{
    private sealed class TenantModule : ElsieModule
    {
        public TenantModule()
        {
            Use(ctx =>
            {
                ctx.Response.Headers["X-Mod-Tenant"] = ctx.RouteOrDefault("tenant") ?? "none";
                return null;
            });
            Get("/{tenant}/items", ctx => ElsieResult.Text($"tenant={ctx.RouteOrDefault("tenant")}"));
        }
    }

    [Fact]
    public async Task App_middleware_sees_route_values_before_handler_runs()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<TenantModule>();
        services.AddElsieMiddleware(p =>
        {
            p.Use(ctx =>
            {
                // RouteValues are populated by the dispatcher before the pipeline runs.
                ctx.Response.Headers["X-Tenant-Seen"] = ctx.RouteOrDefault("tenant") ?? "none";
                return null;
            });
        });
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/acme/items"));
        Assert.Equal(200, outcome.Result!.StatusCode);
        Assert.Equal("acme", outcome.Response!.Headers["X-Tenant-Seen"]);
        Assert.Equal("tenant=acme", System.Text.Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
    }

    [Fact]
    public async Task Module_middleware_can_bind_route_values()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<TenantModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/corp/items"));
        Assert.Equal(200, outcome.Result!.StatusCode);
        Assert.Equal("corp", outcome.Response!.Headers["X-Mod-Tenant"]);
        Assert.Equal("tenant=corp", System.Text.Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
    }
}
