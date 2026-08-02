using Elsie.Pipelines;
using Elsie.Routing;

namespace Elsie;

/// <summary>
/// Host-agnostic route dispatch: match → pipelines → handler → result.
/// Order: app.Before → module.Before → handler → module.After → app.After.
/// Short-circuits still run afters. After hooks may replace the result.
/// Errors: options.MapException → module.OnError → ExceptionHandler → rethrow.
/// After-hook exceptions re-enter the same error chain; remaining afters continue.
/// </summary>
public sealed class ElsieDispatcher
{
    private readonly RouteTable _routes;
    private readonly ElsiePipelines _applicationPipelines;
    private readonly ElsieOptions _options;

    public ElsieDispatcher(RouteTable routes, ElsiePipelines applicationPipelines, ElsieOptions options)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _applicationPipelines = applicationPipelines ?? throw new ArgumentNullException(nameof(applicationPipelines));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ElsieDispatchResult> DispatchAsync(ElsieRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lookup = _routes.Lookup(request.Method, request.Path);
        if (lookup.Status == RouteLookupStatus.NotFound)
        {
            return ElsieDispatchResult.NotFound();
        }

        if (lookup.Status == RouteLookupStatus.MethodNotAllowed)
        {
            return ElsieDispatchResult.MethodNotAllowed(lookup.AllowedMethods);
        }

        var match = lookup.Match!;
        var response = new ElsieResponse();
        var context = new ElsieContext(
            request,
            response,
            match.RouteValues,
            _options.JsonSerializerOptions,
            _routes,
            _options.MaxBindBodySize,
            _options.MaxFormFileBytes,
            _options.MaxFormFiles);

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
            var module = match.Route.Module;
            var modulePipelines = module?.Pipelines;

            ElsieResult result;
            try
            {
                var shortCircuit = await _applicationPipelines.InvokeBeforeAsync(context, ct).ConfigureAwait(false);
                if (shortCircuit is null && modulePipelines is not null)
                {
                    shortCircuit = await modulePipelines.InvokeBeforeAsync(context, ct).ConfigureAwait(false);
                }

                result = shortCircuit ?? await match.Route.Handler(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = await MapErrorAsync(context, module, ex, ct).ConfigureAwait(false);
            }

            result = await RunAftersAsync(context, modulePipelines, module, result, ct).ConfigureAwait(false);
            return ElsieDispatchResult.Handled(result, response);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    private async Task<ElsieResult> RunAftersAsync(
        ElsieContext context,
        ElsiePipelines? modulePipelines,
        ElsieModule? module,
        ElsieResult result,
        CancellationToken ct)
    {
        if (modulePipelines is not null)
        {
            result = await RunAfterListAsync(context, modulePipelines.After, module, result, ct).ConfigureAwait(false);
        }

        result = await RunAfterListAsync(context, _applicationPipelines.After, module, result, ct).ConfigureAwait(false);
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
