using Elsie.Middleware;
using Elsie.Pipelines;
using Elsie.Routing;

namespace Elsie;

/// <summary>
/// Host-agnostic route dispatch: match → pipelines → handler → result.
/// Order: app before hooks → app middleware → module before hooks → module middleware → handler
/// → module after hooks → app after hooks. Short-circuits still run afters.
/// Errors: options.MapException → module.OnError → ExceptionHandler → rethrow.
/// After-hook exceptions re-enter the same error chain; remaining afters continue.
/// </summary>
public sealed class ElsieDispatcher
{
    private static readonly IReadOnlyDictionary<string, string> EmptyRouteValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly RouteTable _routes;
    private readonly ElsiePipelines _applicationPipelines;
    private readonly ElsieMiddlewarePipeline _applicationPipeline;
    private readonly ElsieOptions _options;

    public ElsieDispatcher(
        RouteTable routes,
        ElsiePipelines applicationPipelines,
        ElsieMiddlewarePipeline applicationPipeline,
        ElsieOptions options)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _applicationPipelines = applicationPipelines ?? throw new ArgumentNullException(nameof(applicationPipelines));
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

        try
        {
            // Legacy application before hooks (phase-D migration keeps them working).
            var shortCircuit = await _applicationPipelines.InvokeBeforeAsync(context, ct).ConfigureAwait(false);
            if (shortCircuit is not null)
            {
                context.Result = shortCircuit;
            }
            else
            {
                await _applicationPipeline.InvokeAsync(context, TerminalAsync(context, ct), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Result = await MapErrorAsync(context, module: null, ex, ct).ConfigureAwait(false);
        }
        finally
        {
            linked?.Dispose();
        }

        if (context.Result is null)
        {
            return ElsieDispatchResult.NotFound();
        }

        var result = await RunAftersAsync(context, context.Result, ct).ConfigureAwait(false);
        return ElsieDispatchResult.Handled(result, response);
    }

    /// <summary>
    /// Terminal pipeline step: route lookup, module hooks/middleware, and the handler.
    /// Leaves <see cref="ElsieContext.Result"/> null for an unmatched route (404).
    /// </summary>
    private ElsieMiddlewareDelegate TerminalAsync(ElsieContext context, CancellationToken ct)
    {
        var routes = _routes;
        var options = _options;
        var dispatcher = this;

        return async ctx =>
        {
            var lookup = routes.Lookup(ctx.Request.Method, ctx.Request.Path);
            if (lookup.Status == RouteLookupStatus.NotFound)
            {
                return; // ctx.Result stays null → NotFound dispatch outcome
            }

            if (lookup.Status == RouteLookupStatus.MethodNotAllowed)
            {
                ctx.Response.Headers.Set("Allow", string.Join(", ", lookup.AllowedMethods));
                ctx.Result = ElsieResult.Problem(
                    405,
                    "Method Not Allowed",
                    $"Allowed: {string.Join(", ", lookup.AllowedMethods)}");
                return;
            }

            var match = lookup.Match!;
            var module = match.Route.Module;
            ctx.RouteValues = match.RouteValues;

            var modulePipelines = module?.Pipelines;
            var moduleMiddleware = module?.Middleware;

            try
            {
                var shortCircuit = modulePipelines is null
                    ? null
                    : await modulePipelines.InvokeBeforeAsync(ctx, ct).ConfigureAwait(false);
                if (shortCircuit is not null)
                {
                    ctx.Result = shortCircuit;
                }
                else if (moduleMiddleware is { Count: > 0 })
                {
                    await moduleMiddleware.InvokeAsync(
                        ctx,
                        async handlerCtx => handlerCtx.Result = await match.Route.Handler(handlerCtx, ct).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                }
                else
                {
                    ctx.Result = await match.Route.Handler(ctx, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Result = await dispatcher.MapErrorAsync(ctx, module, ex, ct).ConfigureAwait(false);
            }

            if (modulePipelines is { After.Count: > 0 })
            {
                ctx.Result = await dispatcher.RunAfterListAsync(
                    ctx,
                    modulePipelines.After,
                    module,
                    ctx.Result!,
                    ct).ConfigureAwait(false);
            }
        };
    }

    private async Task<ElsieResult> RunAftersAsync(
        ElsieContext context,
        ElsieResult result,
        CancellationToken ct)
    {
        // Module after hooks run inside the terminal (module known there); the application
        // after hooks run here so they wrap the entire dispatch.
        result = await RunAfterListAsync(context, _applicationPipelines.After, module: null, result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<ElsieResult> RunAfterListAsync(
        ElsieContext context,
        IReadOnlyList<ElsieAfterDelegate> hooks,
        ElsieModule? module,
        ElsieResult result,
        CancellationToken ct)
    {
        for (var i = 0; i < hooks.Count; i++)
        {
            try
            {
                result = await hooks[i](context, result, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("After hook returned a null ElsieResult.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = await MapErrorAsync(context, module, ex, ct).ConfigureAwait(false);
                // continue remaining after hooks with the error-mapped result
            }
        }

        return result;
    }

    private async Task<ElsieResult> MapErrorAsync(
        ElsieContext context,
        ElsieModule? module,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ElsieRequestException protocol)
        {
            return ElsieResult.Problem(protocol.StatusCode, protocol.Title, protocol.Message);
        }

        var mapped = await _options.TryMapExceptionAsync(context, exception, cancellationToken).ConfigureAwait(false);
        if (mapped is not null)
        {
            return mapped;
        }

        if (module?.OnErrorHandler is not null)
        {
            try
            {
                return await module.OnErrorHandler(context, exception, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rethrown) when (rethrown is not OperationCanceledException)
            {
                // OnError threw — fall through to global handler / rethrow original? use rethrown
                exception = rethrown;
            }
        }

        if (_options.ExceptionHandler is not null)
        {
            return await _options.ExceptionHandler(context, exception, cancellationToken).ConfigureAwait(false);
        }

        throw exception;
    }
}
