using Elsie.Routing;
using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

public sealed class ElsieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRouteMatcher _matcher;
    private readonly IElsieResultExecutor _executor;
    private readonly bool _terminal;

    public ElsieMiddleware(
        RequestDelegate next,
        IRouteMatcher matcher,
        IElsieResultExecutor executor,
        bool terminal = false)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _terminal = terminal;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path;
        if (!_matcher.TryMatch(context.Request.Method, path, out var match) || match is null)
        {
            if (_terminal)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        var elsieContext = new ElsieContext(context, match.RouteValues);
        var result = await match.Route.Handler(elsieContext, context.RequestAborted).ConfigureAwait(false);
        await _executor.ExecuteAsync(context, result, context.RequestAborted).ConfigureAwait(false);
    }
}
