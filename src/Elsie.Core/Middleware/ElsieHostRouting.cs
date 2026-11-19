using Elsie.Endpoints;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Middleware;

/// <summary>
/// Host patterns an endpoint (or group) is bound to. Patterns are exact hosts
/// (<c>api.example.com</c>), ports may be included, and <c>*.example.com</c> matches any
/// subdomain but not the apex.
/// </summary>
public sealed class ElsieHostMetadata
{
    public ElsieHostMetadata(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        Patterns = patterns.Where(static p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (Patterns.Count == 0)
        {
            throw new ArgumentException("At least one host pattern is required.", nameof(patterns));
        }
    }

    public IReadOnlyList<string> Patterns { get; }
}

/// <summary>Options for <see cref="ElsieHostFilterMiddleware"/>.</summary>
public sealed class ElsieHostRoutingOptions
{
    /// <summary>Hosts the app accepts. Empty means every host is accepted.</summary>
    public List<string> AllowedHosts { get; } = [];

    /// <summary>Reject requests without a Host header (default true for the global allow-list).</summary>
    public bool RequireHost { get; set; } = true;

    /// <summary>Status used for a rejected host. Default 421 (Misdirected Request).</summary>
    public int RejectStatusCode { get; set; } = 421;
}

/// <summary>
/// Validates the request host against <see cref="ElsieHostRoutingOptions.AllowedHosts"/> and
/// against endpoint metadata added by <c>RequireHost(...)</c>. Enables multi-tenant apps that share
/// one listener.
/// </summary>
public sealed class ElsieHostFilterMiddleware : IElsieMiddleware
{
    private readonly ElsieHostRoutingOptions _options;

    public ElsieHostFilterMiddleware(ElsieHostRoutingOptions? options = null)
    {
        _options = options ?? new ElsieHostRoutingOptions();
    }

    public Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var host = context.Request.Host ?? string.Empty;

        var endpointPatterns = context.Endpoint?
            .GetMetadataOfType<ElsieHostMetadata>()
            .SelectMany(static metadata => metadata.Patterns)
            .ToArray();
        if (endpointPatterns is { Length: > 0 })
        {
            if (!endpointPatterns.Any(pattern => HostMatcher.Matches(pattern, host)))
            {
                context.Result = ElsieResult.Problem(
                    _options.RejectStatusCode,
                    "Misdirected Request",
                    $"Endpoint is not available on host '{host}'.");
                return Task.CompletedTask;
            }

            return next(context);
        }

        if (_options.AllowedHosts.Count == 0)
        {
            return next(context);
        }

        if (string.IsNullOrEmpty(host))
        {
            if (_options.RequireHost)
            {
                context.Result = ElsieResult.Problem(400, "Bad Request", "Host header is required.");
                return Task.CompletedTask;
            }

            return next(context);
        }

        if (!_options.AllowedHosts.Any(pattern => HostMatcher.Matches(pattern, host)))
        {
            context.Result = ElsieResult.Problem(
                _options.RejectStatusCode,
                "Misdirected Request",
                $"Host '{host}' is not allowed.");
            return Task.CompletedTask;
        }

        return next(context);
    }
}

/// <summary>Host pattern matching shared by routing metadata and the filter.</summary>
public static class HostMatcher
{
    /// <summary>
    /// Match <paramref name="host"/> (optionally with a port) against <paramref name="pattern"/>.
    /// <c>*.example.com</c> matches one or more leading labels, not <c>example.com</c> itself.
    /// </summary>
    public static bool Matches(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        pattern = pattern.Trim().TrimEnd('.');
        host = host.Trim().TrimEnd('.');

        var patternPort = SplitPort(pattern, out var patternHost);
        var hostPort = SplitPort(host, out var hostOnly);
        if (patternPort is not null && !string.Equals(patternPort, hostPort, StringComparison.Ordinal))
        {
            return false;
        }

        if (patternHost.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = patternHost[1..]; // ".example.com"
            return hostOnly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && hostOnly.Length > suffix.Length;
        }

        return string.Equals(patternHost, hostOnly, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SplitPort(string value, out string host)
    {
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var end = value.IndexOf(']');
            if (end > 0 && end + 1 < value.Length && value[end + 1] == ':')
            {
                host = value[..(end + 1)];
                return value[(end + 2)..];
            }

            host = value;
            return null;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            host = value[..colon];
            return value[(colon + 1)..];
        }

        host = value;
        return null;
    }
}

/// <summary>Host conventions for endpoints and groups.</summary>
public static class ElsieHostConventionExtensions
{
    /// <summary>Only serve this endpoint on the given hosts (e.g. <c>api.example.com</c>, <c>*.example.com</c>).</summary>
    public static RouteBuilder RequireHost(this RouteBuilder builder, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddMetadata(new ElsieHostMetadata(hosts ?? []));
        return builder;
    }

    /// <summary>Only serve endpoints mapped on this builder on the given hosts.</summary>
    public static TBuilder RequireHost<TBuilder>(this TBuilder builder, params string[] hosts)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Conventions.AddMetadata(new ElsieHostMetadata(hosts ?? []));
        return builder;
    }
}

/// <summary>Registration for <see cref="ElsieHostFilterMiddleware"/>.</summary>
public static class ElsieHostRoutingExtensions
{
    public static IServiceCollection AddElsieHostRouting(
        this IServiceCollection services,
        Action<ElsieHostRoutingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieHostRoutingOptions();
        configure?.Invoke(options);
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use(new ElsieHostFilterMiddleware(options))));
        return services;
    }
}
