using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class PipelineErrorTests
{
    private sealed class PipeModule : ElsieModule
    {
        public PipeModule()
        {
            Before(ctx =>
            {
                ctx.Response.Headers["X-Mod-Before"] = "1";
                return null;
            });
            After((ctx, result) =>
            {
                ctx.Response.Headers["X-Mod-After"] = "1";
                return result.WithHeader("X-Wrapped", "m");
            });
            OnError((ctx, ex) => ElsieResult.Text($"mod:{ex.GetType().Name}", statusCode: 418));

            Get("/ok", () => ElsieResult.Text("ok"));
            Get("/boom", _ => throw new InvalidOperationException("x"));
            Get("/key", _ => throw new KeyNotFoundException("missing"));
            Get("/short", () => ElsieResult.Text("never"));
            Before(ctx => ctx.Request.Path == "/short" ? ElsieResult.Text("short") : null);
        }
    }

    private sealed class AfterThrowModule : ElsieModule
    {
        public AfterThrowModule()
        {
            Get("/a", () => ElsieResult.Text("a"));
            After((_, _) => throw new InvalidOperationException("after-boom"));
        }
    }

    private sealed class BareBoomModule : ElsieModule
    {
        public BareBoomModule()
        {
            Get("/boom", _ => throw new InvalidOperationException("secret-leak"));
        }
    }

    [Fact]
    public async Task After_can_transform_result()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PipeModule>();
        services.ConfigureElsiePipelines(p =>
        {
            p.AddBefore(ctx =>
            {
                ctx.Response.Headers["X-App-Before"] = "1";
                return null;
            });
            p.AddAfter((ctx, result) =>
            {
                ctx.Response.Headers["X-App-After"] = "1";
                return result.WithHeader("X-App-Wrap", "1");
            });
        });
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/ok"));
        Assert.Equal(200, outcome.Result!.StatusCode);
        Assert.Equal("1", outcome.Response!.Headers["X-App-Before"]);
        Assert.Equal("1", outcome.Response.Headers["X-Mod-Before"]);
        Assert.Equal("1", outcome.Response.Headers["X-Mod-After"]);
        Assert.Equal("1", outcome.Response.Headers["X-App-After"]);
        Assert.Equal("m", outcome.Result.Headers["X-Wrapped"]);
        Assert.Equal("1", outcome.Result.Headers["X-App-Wrap"]);
    }

    [Fact]
    public async Task Short_circuit_still_runs_afters()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<PipeModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/short"));
        Assert.Equal("short", Encoding.UTF8.GetString(outcome.Result!.Body!.Value.Span));
        Assert.Equal("1", outcome.Response!.Headers["X-Mod-After"]);
        Assert.Equal("m", outcome.Result.Headers["X-Wrapped"]);
    }

    [Fact]
    public async Task MapException_then_module_OnError()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.MapException<KeyNotFoundException>((_, ex) =>
                ElsieResult.NotFound(ex.Message));
        });
        services.AddElsieModule<PipeModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var mapped = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/key"));
        Assert.Equal(404, mapped.Result!.StatusCode);
        Assert.Equal("1", mapped.Response!.Headers["X-Mod-After"]); // afters run for error results

        var onError = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/boom"));
        Assert.Equal(418, onError.Result!.StatusCode);
        Assert.Equal("mod:InvalidOperationException", Encoding.UTF8.GetString(onError.Result.Body!.Value.Span));
    }


    [Fact]
    public async Task Default_exception_handler_hides_message()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<BareBoomModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/boom"));
        Assert.Equal(500, outcome.Result!.StatusCode);
        var body = Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span);
        Assert.Contains("Internal Server Error", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-leak", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_exception_handler_rethrows()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.ExceptionHandler = null;
        });
        services.AddElsieModule<BareBoomModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(new ElsieRequest("GET", "/boom")));
    }

    [Fact]
    public async Task After_exception_reenters_error_chain()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.ExceptionHandler = (_, ex, _) =>
                Task.FromResult(ElsieResult.Text($"eh:{ex.Message}", statusCode: 500));
        });
        services.AddElsieModule<AfterThrowModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/a"));
        Assert.Equal(500, outcome.Result!.StatusCode);
        Assert.Equal("eh:after-boom", Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
    }

    [Fact]
    public async Task Map_and_MapMethods()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<MapModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var get = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/x"));
        var put = await dispatcher.DispatchAsync(new ElsieRequest("PUT", "/x"));
        var patch = await dispatcher.DispatchAsync(new ElsieRequest("PATCH", "/custom"));
        Assert.Equal("multi", Encoding.UTF8.GetString(get.Result!.Body!.Value.Span));
        Assert.Equal("multi", Encoding.UTF8.GetString(put.Result!.Body!.Value.Span));
        Assert.Equal("mapped", Encoding.UTF8.GetString(patch.Result!.Body!.Value.Span));
    }

    private sealed class MapModule : ElsieModule
    {
        public MapModule()
        {
            MapMethods(["GET", "PUT"], "/x", () => ElsieResult.Text("multi"));
            Map("PATCH", "/custom", () => ElsieResult.Text("mapped"));
        }
    }
}
