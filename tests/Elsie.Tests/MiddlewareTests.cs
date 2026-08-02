using Elsie.Middleware;
using Elsie.RateLimiting;
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

    private static readonly Elsie.RateLimiting.FixedWindowStore SharedStore =
        new(permitLimit: 5, window: TimeSpan.FromMinutes(1), TimeProvider.System, maxPartitions: 100);

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

    [Fact]
    public async Task Auth_gate_factory_short_circuits_as_middleware()
    {
        var (sp, dispatcher) = Build(pipeline =>
            pipeline.Use(ElsieAuth.RequireApiKey("s3cret")));

        await using (sp)
        {
            var ok = await dispatcher.DispatchAsync(
                new ElsieRequest("GET", "/ok", headers: new Dictionary<string, string>
                {
                    ["X-Api-Key"] = "s3cret"
                }, requestServices: sp));
            Assert.Equal(200, ok.Result!.StatusCode);

            var denied = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(401, denied.Result!.StatusCode);
        }
    }

    [Fact]
    public async Task Security_headers_after_factory_applies_via_middleware()
    {
        var (sp, dispatcher) = Build(pipeline =>
            pipeline.Use(ElsieSecurityHeaders.DefaultAfter()));

        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(200, outcome.Result!.StatusCode);
            Assert.Equal("nosniff", outcome.Result!.Headers["X-Content-Type-Options"]);
        }
    }

    [Fact]
    public async Task Rate_limit_factory_short_circuits_as_middleware()
    {
        var (sp, dispatcher) = Build(pipeline =>
            pipeline.Use(ElsieRateLimit.FixedWindow(
                permitLimit: 2,
                window: TimeSpan.FromMinutes(1))));

        await using (sp)
        {
            Assert.Equal(200, (await dispatcher.DispatchAsync(Request(sp, "/ok"))).Result!.StatusCode);
            Assert.Equal(200, (await dispatcher.DispatchAsync(Request(sp, "/ok"))).Result!.StatusCode);
            var limited = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(429, limited.Result!.StatusCode);
            Assert.True(limited.Result!.Headers.Contains("Retry-After"));
        }
    }

    [Fact]
    public async Task Rate_limit_headers_attach_via_middleware()
    {
        var (sp, dispatcher) = Build(pipeline =>
        {
            pipeline.Use(ElsieRateLimit.FixedWindow(
                permitLimit: 5,
                window: TimeSpan.FromMinutes(1),
                store: SharedStore));
            pipeline.Use(ElsieRateLimitHeaders.Attach(SharedStore));
        });

        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(Request(sp, "/ok"));
            Assert.Equal(200, outcome.Result!.StatusCode);
            Assert.Equal("5", outcome.Result!.Headers["X-RateLimit-Limit"]);
            Assert.Equal("4", outcome.Result!.Headers["X-RateLimit-Remaining"]);
            Assert.True(outcome.Result!.Headers.Contains("X-RateLimit-Reset"));
        }
    }

    [Fact]
    public async Task Exception_handler_middleware_maps_downstream_errors()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.ExceptionHandler = (_, ex, _) =>
                Task.FromResult(ElsieResult.Text($"handled:{ex.Message}", statusCode: 500));
        });
        services.AddElsieModule<MwModule>();
        await using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetRequiredService<ElsieMiddlewarePipeline>();
        // Exception handler middleware must be outermost (registered first).
        pipeline.Use(new ElsieExceptionHandlerMiddleware(sp.GetRequiredService<ElsieOptions>()));
        pipeline.Use(BoomAsync);

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var outcome = await dispatcher.DispatchAsync(Request(sp, "/ok"));
        Assert.Equal(500, outcome.Result!.StatusCode);
        Assert.Equal("handled:boom", System.Text.Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
    }

    private static Task BoomAsync(ElsieContext ctx, ElsieMiddlewareDelegate next) =>
        throw new InvalidOperationException("boom");
}
