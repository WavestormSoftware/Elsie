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
            Get("/todo/{id}", ctx => ElsieResult.Text(ctx.UrlFor("self", new { id = ctx.RouteValues["id"] })))
                .Named("self");
            Get("/cookie", ctx =>
            {
                ctx.Response.SetCookie("sid", "abc", new ElsieCookieOptions { HttpOnly = true, Path = "/" });
                return ElsieResult.Text("ok");
            });
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

    [Fact]
    public async Task Materialize_merges_hook_then_result_headers()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PingModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/hdr"));
        var baked = ElsieHttpResponse.FromDispatch(outcome);
        Assert.NotNull(baked);
        Assert.Equal(200, baked!.StatusCode);
        Assert.Equal("1", baked.Headers["X-Core"]);
        Assert.Equal("x", System.Text.Encoding.UTF8.GetString(await baked.BufferBodyAsync()));
    }

    [Fact]
    public void Materialize_not_found_is_null_for_fallthrough()
    {
        Assert.Null(ElsieHttpResponse.FromDispatch(ElsieDispatchResult.NotFound()));
    }

    [Fact]
    public void Materialize_method_not_allowed_sets_allow_and_problem_body()
    {
        var baked = ElsieHttpResponse.FromDispatch(ElsieDispatchResult.MethodNotAllowed(["GET", "POST"]));
        Assert.NotNull(baked);
        Assert.Equal(405, baked!.StatusCode);
        Assert.Equal("GET, POST", baked.Headers["Allow"]);
        Assert.Equal("application/problem+json; charset=utf-8", baked.ContentType);
        Assert.True(baked.Body is { Length: > 0 });
    }

    [Fact]
    public async Task UrlFor_expands_named_route()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PingModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/todo/9"));
        var body = System.Text.Encoding.UTF8.GetString(outcome.Result!.Body!.Value.Span);
        Assert.Equal("/todo/9", body);
    }

    [Fact]
    public async Task SetCookie_appended_at_bake()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PingModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/cookie"));
        var baked = ElsieHttpResponse.FromDispatch(outcome)!;
        var cookies = baked.Headers.GetValues("Set-Cookie");
        Assert.Single(cookies);
        Assert.Contains("sid=abc", cookies[0], StringComparison.Ordinal);
        Assert.Contains("HttpOnly", cookies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linked_cancellation_honors_request_aborted()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<SlowModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        using var cts = new CancellationTokenSource();
        var request = new ElsieRequest("GET", "/slow", requestAborted: cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(request, CancellationToken.None));
    }

    private sealed class SlowModule : ElsieModule
    {
        public SlowModule()
        {
            Get("/slow", async (ctx, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ElsieResult.Text("nope");
            });
        }
    }
}
