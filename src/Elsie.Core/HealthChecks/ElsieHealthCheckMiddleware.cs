using Elsie.Middleware;

namespace Elsie.HealthChecks;

/// <summary>
/// Health-check middleware serving <c>/healthz</c>, <c>/healthz/live</c> and
/// <c>/healthz/ready</c> (prefix configurable via <see cref="ElsieHealthCheckOptions.PathPrefix"/>).
/// Matched probe paths short-circuit; anything else continues down the pipeline.
/// This is the middleware replacement for the legacy <c>ElsieHealthChecksModule</c> routes.
/// </summary>
public sealed class ElsieHealthCheckMiddleware : IElsieMiddleware
{
    private readonly ElsieHealthCheckRunner _runner;

    /// <summary>Create the middleware over the shared health-check runner.</summary>
    public ElsieHealthCheckMiddleware(ElsieHealthCheckRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var prefix = NormalizePrefix(_runner.Options.PathPrefix);
        if (!IsHealthPath(context.Request.Path, prefix, out var endpoint))
        {
            await next(context);
            return;
        }

        if (!context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !context.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = ElsieResult.Problem(
                405,
                "Method Not Allowed",
                "Allowed: GET")
                .WithHeader("Allow", "GET");
            return;
        }

        var report = endpoint switch
        {
            "live" => await _runner
                .RunAsync(context.Services, r => r.HasTag(ElsieHealthCheckTags.Live), context.DispatchCancellationToken)
                .ConfigureAwait(false),
            "ready" => await _runner
                .RunAsync(context.Services, r => r.HasTag(ElsieHealthCheckTags.Ready), context.DispatchCancellationToken)
                .ConfigureAwait(false),
            _ => await _runner
                .RunAsync(context.Services, predicate: null, context.DispatchCancellationToken)
                .ConfigureAwait(false)
        };

        context.Result = ElsieHealthCheckRunner.ToResult(report);
    }

    private static bool IsHealthPath(string path, string prefix, out string endpoint)
    {
        endpoint = string.Empty;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == prefix.Length)
        {
            return true; // bare prefix → all checks
        }

        if (path.Length > prefix.Length && path[prefix.Length] != '/')
        {
            return false;
        }

        endpoint = path[(prefix.Length + 1)..].Trim('/');
        return endpoint is "live" or "ready";
    }

    private static string NormalizePrefix(string? pathPrefix) =>
        string.IsNullOrWhiteSpace(pathPrefix) ? "/healthz" : "/" + pathPrefix.Trim('/');
}
