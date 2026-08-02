using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class MiddlewareTests
{
    private sealed class MwModule : ElsieModule
    {
        public MwModule()
        {
            Get("/ok", () => ElsieResult.Text("ok"));
            Get("/mod-mw", () => ElsieResult.Text("handler"));
            Get("/short", () => ElsieResult.Text("never-reached"));
        }
    }

    private sealed class DiMiddleware : IElsieMiddleware
    {
        public Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
        {
            context.Response.Headers["X-Di-Mw"] = "1";
            return next(context);
        }
    }

    private static (ServiceProvider Services, ElsieDispatcher Dispatcher) Build(
        Action<ElsieMiddlewarePipeline>? appMiddleware = null,
        Action<ElsieModule>? module = null)
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<MwModule>();
        services.AddSingleton<DiMiddleware>();
        var sp = services.BuildServiceProvider();

        var pipeline = sp.GetRequiredService<ElsieMiddlewarePipeline>();
        appMiddleware?.Invoke(pipeline);
        module?.Invoke(sp.GetServices<ElsieModule>().OfType<MwModule>().Single());
        return (sp, sp.GetRequiredService<ElsieDispatcher>());
    }

    private static ElsieRequest Request(ServiceProvider sp, string path) =>
        new("GET", path, requestServices: sp);

    [Fact]
    public async Task Inline_middleware_runs_fifo_pre_lifo_post()
    {
        var order = new List<string>();
        var (sp, dispatcher) = Build(pipeline =>
        {
            pipeline.Use(async (ctx, next) =>
            {
                order.Add("a-pre");
                await next(ctx);
                order.Add("a-post");
            });
            pipeline.Use(async (ctx, next) =>
            {
                order.Add("b-pre");
                await next(ctx);
                order.Add("b-post");
            });
        });

        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(200, outcome.Result!.StatusCode);
        }

        Assert.Equal(["a-pre", "b-pre", "b-post", "a-post"], order);
    }

    [Fact]
    public async Task Middleware_can_short_circuit_with_a_result()
    {
        var (sp, dispatcher) = Build(pipeline =>
        {
            pipeline.Use((ctx, next) =>
            {
                ctx.Result = ElsieResult.Text("blocked", statusCode: 403);
                return Task.CompletedTask;
            });
        });

        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(403, outcome.Result!.StatusCode);
            Assert.Equal("blocked", System.Text.Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
        }
    }

    [Fact]
    public async Task Di_middleware_is_resolved_from_request_scope()
    {
        var (sp, dispatcher) = Build(pipeline => pipeline.Use<DiMiddleware>());

        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(200, outcome.Result!.StatusCode);
            Assert.Equal("1", outcome.Response!.Headers["X-Di-Mw"]);
        }
    }

    [Fact]
    public async Task Module_middleware_runs_only_for_module_routes()
    {
        var (sp, dispatcher) = Build(
            module: m => m.Middleware.Use((ctx, next) =>
            {
                ctx.Response.Headers["X-Mod-Mw"] = "1";
                return next(ctx);
            }));

        await using (sp)
        {
            var matched = await dispatcher.DispatchAsync(Request(sp, "/mod-mw"));
            Assert.Equal(200, matched.Result!.StatusCode);
            Assert.Equal("1", matched.Response!.Headers["X-Mod-Mw"]);

            // Unmatched route → NotFound; module middleware never runs.
            var notFound = await dispatcher.DispatchAsync(Request(sp, "/nope"));
            Assert.Equal(ElsieDispatchStatus.NotFound, notFound.Status);
        }
    }

    [Fact]
    public async Task Unmatched_route_is_not_found_after_middleware()
    {
        var ran = false;
        var (sp, dispatcher) = Build(pipeline =>
        {
            pipeline.Use((ctx, next) =>
            {
                ran = true;
                return next(ctx);
            });
        });

        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(Request(sp, "/missing"));
            Assert.Equal(ElsieDispatchStatus.NotFound, outcome.Status);
        }

        Assert.True(ran);
    }
}
