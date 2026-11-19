using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Middleware;

/// <summary>Options for <see cref="ElsieHttpsRedirectionMiddleware"/>.</summary>
public sealed class ElsieHttpsRedirectionOptions
{
    /// <summary>HTTPS port used when the request arrives without one. 443 (or null) omits the port.</summary>
    public int? HttpsPort { get; set; } = 443;

    /// <summary>Use 308 (method-preserving, permanent) instead of 307. Default false.</summary>
    public bool Permanent { get; set; }

    /// <summary>
    /// Headers that prove TLS terminated upstream (default <c>X-Forwarded-Proto</c>). When one of
    /// them says <c>https</c> the request is treated as already secure.
    /// </summary>
    public string ForwardedProtoHeader { get; set; } = "X-Forwarded-Proto";

    /// <summary>Paths that are never redirected (health probes, ACME challenges).</summary>
    public List<string> ExcludedPaths { get; } = [];
}

/// <summary>
/// Redirects plain-HTTP requests to HTTPS (307/308), preserving path and query. Requests that
/// already arrived over TLS — or behind a proxy that sets the forwarded-proto header to
/// <c>https</c> — pass through untouched.
/// </summary>
public sealed class ElsieHttpsRedirectionMiddleware : IElsieMiddleware
{
    private readonly ElsieHttpsRedirectionOptions _options;

    public ElsieHttpsRedirectionMiddleware(ElsieHttpsRedirectionOptions? options = null)
    {
        _options = options ?? new ElsieHttpsRedirectionOptions();
    }

    public Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (IsHttps(context.Request) || IsExcluded(context.Request.Path))
        {
            return next(context);
        }

        var host = context.Request.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            // HTTP/1.0 requests without Host cannot be redirected safely.
            context.Result = ElsieResult.BadRequest("Host header is required for HTTPS redirection.");
            return Task.CompletedTask;
        }

        var authority = StripPort(host);
        if (_options.HttpsPort is { } port && port != 443)
        {
            authority = $"{authority}:{port}";
        }

        var location = $"https://{authority}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        var status = _options.Permanent ? 308 : 307;
        context.Result = ElsieResult.Status(status).WithHeader("Location", location);
        return Task.CompletedTask;
    }

    private bool IsHttps(ElsieRequest request)
    {
        if (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var forwarded = request.GetHeader(_options.ForwardedProtoHeader);
        return !string.IsNullOrEmpty(forwarded)
            && forwarded.Contains("https", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExcluded(string path)
    {
        foreach (var excluded in _options.ExcludedPaths)
        {
            if (!string.IsNullOrEmpty(excluded)
                && path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripPort(string host)
    {
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var end = host.IndexOf(']');
            return end < 0 ? host : host[..(end + 1)];
        }

        var colon = host.LastIndexOf(':');
        return colon < 0 ? host : host[..colon];
    }
}

/// <summary>Registration for <see cref="ElsieHttpsRedirectionMiddleware"/>.</summary>
public static class ElsieHttpsRedirectionExtensions
{
    public static IServiceCollection AddElsieHttpsRedirection(
        this IServiceCollection services,
        Action<ElsieHttpsRedirectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieHttpsRedirectionOptions();
        configure?.Invoke(options);
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use(new ElsieHttpsRedirectionMiddleware(options))));
        return services;
    }
}
