using Elsie.Pipelines;
using Elsie.Routing;

namespace Elsie;

/// <summary>
/// Host-agnostic route dispatch: match → pipelines → handler → result.
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
            _routes);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.RequestAborted);
        var ct = linked.Token;
        var modulePipelines = match.Route.Module?.Pipelines;

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
        catch (Exception ex) when (_options.ExceptionHandler is not null && ex is not OperationCanceledException)
        {
            result = await _options.ExceptionHandler(context, ex, ct).ConfigureAwait(false);
        }

        if (modulePipelines is not null)
        {
            await modulePipelines.InvokeAfterAsync(context, result, ct).ConfigureAwait(false);
        }

        await _applicationPipelines.InvokeAfterAsync(context, result, ct).ConfigureAwait(false);
        return ElsieDispatchResult.Handled(result, response);
    }
}
