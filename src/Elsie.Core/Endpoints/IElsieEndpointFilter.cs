using Elsie.Routing;

namespace Elsie.Endpoints;

/// <summary>
/// Request/response filter that runs around an endpoint handler. Register with
/// <c>WithFilter</c>/<c>AddEndpointFilter</c> on a <see cref="RouteBuilder"/> or route group.
/// Filters run innermost-last: the last registered filter wraps the handler first.
/// Set <see cref="ElsieContext.Result"/> or return a value from a filter to short-circuit.
/// </summary>
public interface IElsieEndpointFilter
{
    /// <summary>Run the filter, optionally calling <paramref name="next"/>.</summary>
    ValueTask<object?> InvokeAsync(ElsieEndpointFilterContext context, ElsieEndpointFilterDelegate next);
}

/// <summary>Continuation handed to an <see cref="IElsieEndpointFilter"/>.</summary>
public delegate ValueTask<object?> ElsieEndpointFilterDelegate(ElsieEndpointFilterContext context);

/// <summary>Per-invocation state available to endpoint filters.</summary>
public sealed class ElsieEndpointFilterContext
{
    public ElsieEndpointFilterContext(ElsieContext context, RouteDescriptor endpoint)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>The current request context.</summary>
    public ElsieContext Context { get; }

    /// <summary>The matched endpoint descriptor.</summary>
    public RouteDescriptor Endpoint { get; }

    /// <summary>Request-scoped service provider.</summary>
    public IServiceProvider Services => Context.RequestServices;

    /// <summary>Resolve a required request-scoped service.</summary>
    public T GetRequiredService<T>()
        where T : notnull => Context.GetRequiredService<T>();

    /// <summary>Resolve an optional request-scoped service.</summary>
    public T? GetService<T>() => Context.GetService<T>();

    /// <summary>Route value lookup (null when absent).</summary>
    public string? RouteValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Context.RouteValues.TryGetValue(name, out var value) ? value : null;
    }
}

/// <summary>Runs an endpoint's filter chain, then its handler.</summary>
internal static class ElsieEndpointFilterPipeline
{
    public static async Task<ElsieResult> InvokeAsync(
        RouteDescriptor route,
        ElsieContext context,
        CancellationToken cancellationToken)
    {
        var filters = route.Filters;
        var filterContext = new ElsieEndpointFilterContext(context, route);

        ElsieEndpointFilterDelegate next = static async ctx =>
            await ctx.Endpoint.Handler(ctx.Context, ctx.Context.DispatchCancellationToken).ConfigureAwait(false);

        for (var i = filters.Count - 1; i >= 0; i--)
        {
            var current = filters[i];
            var captured = next;
            next = ctx => current.InvokeAsync(ctx, captured);
        }

        var value = await next(filterContext).ConfigureAwait(false);
        return await ElsieEndpointResultMapper.ToResultAsync(value, context).ConfigureAwait(false);
    }
}
