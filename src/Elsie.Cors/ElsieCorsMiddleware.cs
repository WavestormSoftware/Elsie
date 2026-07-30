using Elsie.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Cors;

/// <summary>
/// Handles CORS preflight (OPTIONS) before Elsie dispatch.
/// Actual-response ACAO headers are applied by the Elsie after-hook (response already started after MapElsie).
/// </summary>
internal sealed class ElsieCorsMiddleware
{
    private readonly RequestDelegate _next;

    public ElsieCorsMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext http, ElsieCorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        var origin = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) ||
            !HttpMethods.IsOptions(http.Request.Method) ||
            !http.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            await _next(http).ConfigureAwait(false);
            return;
        }

        var policy = ResolvePreflightPolicy(http, options);
        var requestMethod = http.Request.Headers["Access-Control-Request-Method"].ToString();
        var requestHeaders = http.Request.Headers["Access-Control-Request-Headers"].ToString();

        if (ElsieCorsEvaluator.TryBuildPreflightHeaders(
                policy,
                origin,
                requestMethod,
                string.IsNullOrEmpty(requestHeaders) ? null : requestHeaders,
                out var headers))
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            foreach (var (name, value) in headers)
            {
                http.Response.Headers.Append(name, value);
            }

            return;
        }

        // Origin/method not allowed — empty 204 (browser enforces missing ACAO).
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static ElsieCorsPolicy ResolvePreflightPolicy(HttpContext http, ElsieCorsOptions options)
    {
        var path = http.Request.Path.Value ?? "/";
        var requestMethod = http.Request.Headers["Access-Control-Request-Method"].ToString();
        if (!string.IsNullOrEmpty(requestMethod))
        {
            var routes = http.RequestServices.GetService<RouteTable>();
            if (routes is not null)
            {
                var lookup = routes.Lookup(requestMethod, path);
                if (lookup.Status == RouteLookupStatus.Matched &&
                    lookup.Match!.Route.TryGetCorsPolicyName(out var named) &&
                    named is not null &&
                    options.TryGetPolicy(named, out var policy))
                {
                    return policy;
                }
            }
        }

        return options.GetRequiredPolicy(options.DefaultPolicy);
    }
}
