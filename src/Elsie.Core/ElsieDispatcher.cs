using Elsie.Middleware;
using Elsie.Routing;

namespace Elsie;

/// <summary>
/// Host-agnostic route dispatch: route lookup → middleware pipeline → handler.
/// Route values are populated right after lookup so middleware can bind route parameters;
/// exceptions are mapped by the terminal <see cref="ElsieExceptionHandlerMiddleware"/>.
/// The dispatcher runs the pipeline only — no legacy before/after hooks.
/// </summary>
public sealed class ElsieDispatcher
{
    private static readonly IReadOnlyDictionary<string, string> EmptyRouteValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly RouteTable _routes;
    private readonly ElsieMiddlewarePipeline _applicationPipeline;
    private readonly ElsieOptions _options;

    public ElsieDispatcher(
        RouteTable routes,
        ElsieMiddlewarePipeline applicationPipeline,
        ElsieOptions options)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _applicationPipeline = applicationPipeline ?? throw new ArgumentNullException(nameof(applicationPipeline));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ElsieDispatchResult> DispatchAsync(
        ElsieRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = new ElsieResponse();
        var context = new ElsieContext(
            request,
            response,
            EmptyRouteValues,
            _options.JsonSerializerOptions,
            _routes,
            _options.MaxBindBodySize,
            _options.MaxFormFileBytes,
            _options.MaxFormFiles,
            _options.MultipartMemoryThresholdBytes);

        CancellationToken ct;
        CancellationTokenSource? linked = null;
        if (cancellationToken.CanBeCanceled && request.RequestAborted.CanBeCanceled)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.RequestAborted);
            ct = linked.Token;
        }
        else if (cancellationToken.CanBeCanceled)
        {
            ct = cancellationToken;
        }
        else
        {
            ct = request.RequestAborted;
        }

        context.DispatchCancellationToken = ct;

        // Route lookup happens BEFORE the pipeline so middleware sees populated RouteValues
        // (e.g. {tenant} bindings). Middleware may still short-circuit or override the outcome.
        var lookup = _routes.Lookup(request.Method, request.Path);
        var match = lookup.Status == RouteLookupStatus.Matched ? lookup.Match! : null;
        if (match is not null)
        {
            context.RouteValues = match.RouteValues;
        }

        try
        {
            await _applicationPipeline.InvokeAsync(context, TerminalAsync(context, ct, match), ct).ConfigureAwait(false);
        }
        finally
        {
            linked?.Dispose();
        }

        if (context.Result is not null)
        {
            return ElsieDispatchResult.Handled(context.Result, response);
        }

        return lookup.Status == RouteLookupStatus.MethodNotAllowed
            ? ElsieDispatchResult.MethodNotAllowed(lookup.AllowedMethods)
            : ElsieDispatchResult.NotFound();
    }

    /// <summary>
    /// Terminal pipeline step: module middleware (if any) wrapped around the route handler.
    /// Leaves <see cref="ElsieContext.Result"/> null when the route did not match (404 / 405 outcome).
    /// </summary>
    private ElsieMiddlewareDelegate TerminalAsync(ElsieContext context, CancellationToken ct, RouteMatch? match)
    {
        if (match is null)
        {
            return static _ => Task.CompletedTask;
        }

        var moduleMiddleware = match.Route.Module?.Middleware;
        var route = match.Route;

        return async ctx =>
        {
            if (moduleMiddleware is { Count: > 0 })
            {
                await moduleMiddleware.InvokeAsync(
                    ctx,
                    async handlerCtx => handlerCtx.Result = await route.Handler(handlerCtx, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
            }
            else
            {
                ctx.Result = await route.Handler(ctx, ct).ConfigureAwait(false);
            }
        };
    }
}
