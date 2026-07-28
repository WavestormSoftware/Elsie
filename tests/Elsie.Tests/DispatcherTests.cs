using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class DispatcherTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Post("/echo", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Echo>(ct);
                return bind.IsSuccess ? ctx.Json(bind.Value) : bind.Error!;
            });
            Get("/hdr", () => ElsieResult.Text("x"));
            After((ctx, _) => ctx.Response.Headers["X-Core"] = "1");
        }
    }

    private sealed record Echo(string Message);

    [Fact]
    public async Task Dispatcher_handles_get_without_aspnet()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PingModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/ping"));
        Assert.Equal(ElsieDispatchStatus.Handled, outcome.Status);
        Assert.Equal(200, outcome.Result!.StatusCode);
        Assert.Equal("1", outcome.Response!.Headers["X-Core"]);
    }

    [Fact]
    public async Task Dispatcher_not_found()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PingModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/nope"));
        Assert.Equal(ElsieDispatchStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void Request_items_bag_is_mutable()
    {
        var request = new ElsieRequest("GET", "/");
        var key = new object();
        request.Items[key] = "value";
        Assert.Equal("value", request.Items[key]);
    }
}
