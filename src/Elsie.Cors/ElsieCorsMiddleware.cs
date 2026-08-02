using Elsie.Middleware;
using Elsie.Routing;

namespace Elsie.Cors;

/// <summary>
/// CORS middleware: handles OPTIONS preflight (short-circuit 204 + allow headers) and applies
/// <c>Access-Control-Allow-*</c> headers to actual responses on the way out.
/// This is the middleware counterpart of the legacy <c>IElsieRequestFilter</c> preflight +
/// ACAO after-hook wiring; register with
/// <see cref="ElsieCorsPipelineExtensions.UseElsieCors"/> (or DI + <c>Use&lt;T&gt;</c>).
/// </summary>
public sealed class ElsieCorsMiddleware : IElsieMiddleware
{
    private readonly ElsieCorsApplier _applier;
    private readonly ElsieCorsOptions _options;
    private readonly RouteTable _routes;

    /// <summary>Create the middleware (resolved from DI; see <c>AddElsieCors</c>).</summary>
    internal ElsieCorsMiddleware(
        ElsieCorsOptions options,
        ElsieCorsConfigRelay relay,
        RouteTable routes,
        ElsieCorsApplier applier)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ = relay ?? throw new ArgumentNullException(nameof(relay)); // keep config reload forwarding live
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var origin = context.Request.GetHeader("Origin");
        var isPreflight = !string.IsNullOrEmpty(origin) &&
                          context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrEmpty(context.Request.GetHeader("Access-Control-Request-Method"));

        if (isPreflight)
        {
            context.Result = BuildPreflightResult(context, origin!);
            return;
        }

        await next(context);

        if (context.Result is not null && !string.IsNullOrEmpty(origin))
        {
            context.Result = _applier.Apply(context, context.Result);
        }
    }

    private ElsieResult BuildPreflightResult(ElsieContext ctx, string origin)
    {
        var requestMethod = ctx.Request.GetHeader("Access-Control-Request-Method");
        var requestHeaders = ctx.Request.GetHeader("Access-Control-Request-Headers");
        var policy = ResolvePreflightPolicy(ctx.Request);

        return ElsieCorsEvaluator.TryBuildPreflightHeaders(
                policy,
                origin,
                requestMethod,
                string.IsNullOrEmpty(requestHeaders) ? null : requestHeaders,
                out var headers)
            ? ElsieResult.NoContent().WithHeaders(headers)
            : ElsieResult.NoContent(); // origin/method not allowed — empty 204 (browser enforces missing ACAO)
    }

    private ElsieCorsPolicy ResolvePreflightPolicy(ElsieRequest request)
    {
        var requestMethod = request.GetHeader("Access-Control-Request-Method");
        if (!string.IsNullOrEmpty(requestMethod))
        {
            var lookup = _routes.Lookup(requestMethod!, request.Path);
            if (lookup.Status == RouteLookupStatus.Matched &&
                lookup.Match!.Route.TryGetCorsPolicyName(out var named) &&
                named is not null &&
                _options.TryGetPolicy(named, out var policy))
            {
                return policy;
            }
        }

        return _options.GetRequiredPolicy(_options.DefaultPolicy);
    }
}

/// <summary>Pipeline registration helpers for CORS middleware.</summary>
public static class ElsieCorsPipelineExtensions
{
    /// <summary>Register <see cref="ElsieCorsMiddleware"/> (resolved per request from DI).</summary>
    public static ElsieMiddlewarePipeline UseElsieCors(this ElsieMiddlewarePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Use<ElsieCorsMiddleware>();
    }
}
