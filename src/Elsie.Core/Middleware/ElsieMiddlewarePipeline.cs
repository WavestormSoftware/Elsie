using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Middleware;

/// <summary>
/// Ordered middleware pipeline. Components run in registration order; each component's
/// pre-logic runs FIFO and its post-logic (after <c>await next</c>) runs LIFO.
/// Delegate components are added directly; <c>Use&lt;T&gt;</c> / factory components are
/// resolved per request from <see cref="ElsieContext.RequestServices"/> (supports scoped DI).
/// </summary>
public sealed class ElsieMiddlewarePipeline
{
    private readonly List<Func<IServiceProvider, IElsieMiddleware>> _components = [];

    /// <summary>Number of registered components.</summary>
    public int Count => _components.Count;

    /// <summary>
    /// Register an inline middleware delegate.
    /// <c>next</c> is a delegate that continues the pipeline (call it to proceed).
    /// </summary>
    public ElsieMiddlewarePipeline Use(Func<ElsieContext, ElsieMiddlewareDelegate, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _components.Add(_ => new DelegateMiddleware(middleware));
        return this;
    }

    /// <summary>Register a middleware instance directly (shared across requests).</summary>
    public ElsieMiddlewarePipeline Use(IElsieMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _components.Add(_ => middleware);
        return this;
    }

    /// <summary>
    /// Register a before-hook style gate: when it returns a non-null result the pipeline
    /// short-circuits with that result (the handler and remaining middleware are skipped).
    /// This is how <c>ElsieAuth.RequireApiKey(...)</c> / <c>ElsieRateLimit.*</c> factories
    /// plug into the middleware pipeline.
    /// </summary>
    public ElsieMiddlewarePipeline Use(Func<ElsieContext, ElsieResult?> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return Use(async (ctx, next) =>
        {
            var result = gate(ctx);
            if (result is not null)
            {
                ctx.Result = result;
                return;
            }

            await next(ctx);
        });
    }

    /// <summary>
    /// Register an async before-hook style gate (e.g. <c>ElsieAntiforgeryService.RequireAntiforgery()</c>,
    /// or any <see cref="Elsie.Pipelines.ElsieBeforeDelegate"/>).
    /// Non-null result short-circuits; null continues the pipeline.
    /// Cancellation uses the dispatcher's linked token (<see cref="ElsieContext.DispatchCancellationToken"/>),
    /// consistent with the rest of the pipeline.
    /// </summary>
    public ElsieMiddlewarePipeline Use(Elsie.Pipelines.ElsieBeforeDelegate asyncGate)
    {
        ArgumentNullException.ThrowIfNull(asyncGate);
        return Use(async (ctx, next) =>
        {
            var result = await asyncGate(ctx, ctx.DispatchCancellationToken);
            if (result is not null)
            {
                ctx.Result = result;
                return;
            }

            await next(ctx);
        });
    }

    /// <summary>
    /// Register an after-hook style transform: it runs on the way back out (after the rest of
    /// the pipeline produced a result) and may replace <see cref="ElsieContext.Result"/>.
    /// This is how <c>ElsieSecurityHeaders.DefaultAfter(...)</c> and
    /// <c>ElsieRateLimitHeaders.Attach(...)</c> plug into the middleware pipeline.
    /// </summary>
    public ElsieMiddlewarePipeline Use(Func<ElsieContext, ElsieResult, ElsieResult> after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return Use(async (ctx, next) =>
        {
            await next(ctx);
            if (ctx.Result is not null)
            {
                ctx.Result = after(ctx, ctx.Result);
            }
        });
    }

    /// <summary>Register a DI-resolved middleware component (per-request scope).</summary>
    public ElsieMiddlewarePipeline Use<TMiddleware>()
        where TMiddleware : class, IElsieMiddleware
    {
        _components.Add(sp => sp.GetRequiredService<TMiddleware>());
        return this;
    }

    /// <summary>Register a middleware component resolved by a custom factory.</summary>
    public ElsieMiddlewarePipeline Use(Func<IServiceProvider, IElsieMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _components.Add(factory);
        return this;
    }

    /// <summary>
    /// Run the pipeline against <paramref name="context"/> ending at <paramref name="terminal"/>.
    /// Components are resolved from <see cref="ElsieContext.RequestServices"/> at invocation time
    /// so scoped DI lifetimes are honored per request.
    /// </summary>
    public Task InvokeAsync(
        ElsieContext context,
        ElsieMiddlewareDelegate terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terminal);

        var services = _components.Count == 0
            ? null
            : context.RequestServices
              ?? throw new InvalidOperationException(
                  "ElsieContext.RequestServices is null; middleware resolution requires a request scope.");

        ElsieMiddlewareDelegate tail = terminal;
        for (var i = _components.Count - 1; i >= 0; i--)
        {
            var middleware = _components[i](services!);
            var next = tail;
            tail = ctx => middleware.InvokeAsync(ctx, next);
        }

        return tail(context);
    }

    private sealed class DelegateMiddleware(Func<ElsieContext, ElsieMiddlewareDelegate, Task> middleware)
        : IElsieMiddleware
    {
        public Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next) =>
            middleware(context, next);
    }
}
