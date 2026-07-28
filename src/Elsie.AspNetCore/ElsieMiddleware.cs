using Elsie.Pipelines;
using Elsie.Routing;
using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

public sealed class ElsieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRouteMatcher _matcher;
    private readonly IElsieResultExecutor _executor;
    private readonly ElsiePipelines _applicationPipelines;
    private readonly ElsieOptions _options;
    private readonly bool _terminal;

    public ElsieMiddleware(
        RequestDelegate next,
        IRouteMatcher matcher,
        IElsieResultExecutor executor,
        ElsiePipelines applicationPipelines,
        ElsieOptions options,
        bool terminal = false)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _applicationPipelines = applicationPipelines ?? throw new ArgumentNullException(nameof(applicationPipelines));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _terminal = terminal;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path;
        var lookup = _matcher.Lookup(context.Request.Method, path);

        if (lookup.Status == RouteLookupStatus.NotFound)
        {
            if (_terminal)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (lookup.Status == RouteLookupStatus.MethodNotAllowed)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = string.Join(", ", lookup.AllowedMethods);
            return;
        }

        var match = lookup.Match!;
        var elsieContext = new ElsieContext(context, match.RouteValues, _options.JsonSerializerOptions);
        var ct = context.RequestAborted;

        var result = await _applicationPipelines.InvokeBeforeAsync(elsieContext, ct).ConfigureAwait(false);

        var modulePipelines = match.Route.Module?.Pipelines;
        if (result is null && modulePipelines is not null)
        {
            result = await modulePipelines.InvokeBeforeAsync(elsieContext, ct).ConfigureAwait(false);
        }

        result ??= await match.Route.Handler(elsieContext, ct).ConfigureAwait(false);

        if (modulePipelines is not null)
        {
            await modulePipelines.InvokeAfterAsync(elsieContext, result, ct).ConfigureAwait(false);
        }

        await _applicationPipelines.InvokeAfterAsync(elsieContext, result, ct).ConfigureAwait(false);
        await _executor.ExecuteAsync(context, result, ct).ConfigureAwait(false);
    }
}
