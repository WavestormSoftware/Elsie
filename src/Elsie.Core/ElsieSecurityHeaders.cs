using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie;

/// <summary>Response headers that harden browser behaviour.</summary>
public sealed class ElsieSecurityHeadersOptions
{
    /// <summary>Emit <c>X-Content-Type-Options: nosniff</c>. Default true.</summary>
    public bool NoSniff { get; set; } = true;

    /// <summary><c>X-Frame-Options</c> value; null to omit. Default <c>DENY</c>.</summary>
    public string? FrameOptions { get; set; } = "DENY";

    /// <summary><c>Referrer-Policy</c>; null to omit. Default <c>no-referrer</c>.</summary>
    public string? ReferrerPolicy { get; set; } = "no-referrer";

    /// <summary><c>Permissions-Policy</c>; null to omit. Default disables geolocation/microphone/camera.</summary>
    public string? PermissionsPolicy { get; set; } = "geolocation=(), microphone=(), camera=()";

    /// <summary><c>Content-Security-Policy</c>; null (default) to omit.</summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary><c>Cross-Origin-Opener-Policy</c>; null to omit. Default <c>same-origin</c>.</summary>
    public string? CrossOriginOpenerPolicy { get; set; } = "same-origin";

    /// <summary><c>Cross-Origin-Resource-Policy</c>; null to omit. Default <c>same-origin</c>.</summary>
    public string? CrossOriginResourcePolicy { get; set; } = "same-origin";

    /// <summary><c>Cross-Origin-Embedder-Policy</c>; null (default) to omit (it breaks most embeds).</summary>
    public string? CrossOriginEmbedderPolicy { get; set; }

    /// <summary>
    /// <c>Strict-Transport-Security</c> value; only sent for HTTPS requests. Null to omit.
    /// Default one year with subdomains.
    /// </summary>
    public string? StrictTransportSecurity { get; set; } = "max-age=31536000; includeSubDomains";

    /// <summary>Append <c>preload</c> to the HSTS value.</summary>
    public bool HstsPreload { get; set; }

    /// <summary>Also add <c>Cache-Control: no-store</c> to error responses (4xx/5xx). Default false.</summary>
    public bool NoStoreErrors { get; set; }
}

/// <summary>Middleware that applies <see cref="ElsieSecurityHeadersOptions"/> to every response.</summary>
public sealed class ElsieSecurityHeadersMiddleware : IElsieMiddleware
{
    private readonly ElsieSecurityHeadersOptions _options;

    public ElsieSecurityHeadersMiddleware(ElsieSecurityHeadersOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await next(context).ConfigureAwait(false);
        if (context.Result is { } result)
        {
            context.Result = ElsieSecurityHeaders.Apply(result, _options, IsHttps(context.Request));
        }
    }

    internal static bool IsHttps(ElsieRequest request) =>
        string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Helpers for common browser security response headers. Use
/// <see cref="ElsieSecurityHeadersServiceCollectionExtensions.AddElsieSecurityHeaders"/> (middleware)
/// or <see cref="DefaultAfter"/> (after-hook).
/// </summary>
public static class ElsieSecurityHeaders
{
    /// <summary>
    /// Sets baseline headers on every response via after-hook:
    /// X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy (none).
    /// </summary>
    public static Func<ElsieContext, ElsieResult, ElsieResult> DefaultAfter(
        string frameOptions = "DENY",
        string referrerPolicy = "no-referrer",
        string? contentSecurityPolicy = null)
    {
        return (_, result) =>
        {
            result = result
                .WithHeader("X-Content-Type-Options", "nosniff")
                .WithHeader("X-Frame-Options", frameOptions)
                .WithHeader("Referrer-Policy", referrerPolicy)
                .WithHeader("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
            if (!string.IsNullOrWhiteSpace(contentSecurityPolicy))
            {
                result = result.WithHeader("Content-Security-Policy", contentSecurityPolicy);
            }

            return result;
        };
    }

    /// <summary>Apply the configured security headers to <paramref name="result"/>.</summary>
    public static ElsieResult Apply(ElsieResult result, ElsieSecurityHeadersOptions options, bool isHttps)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        if (options.NoSniff)
        {
            result = result.WithHeader("X-Content-Type-Options", "nosniff");
        }

        result = WithOptional(result, "X-Frame-Options", options.FrameOptions);
        result = WithOptional(result, "Referrer-Policy", options.ReferrerPolicy);
        result = WithOptional(result, "Permissions-Policy", options.PermissionsPolicy);
        result = WithOptional(result, "Content-Security-Policy", options.ContentSecurityPolicy);
        result = WithOptional(result, "Cross-Origin-Opener-Policy", options.CrossOriginOpenerPolicy);
        result = WithOptional(result, "Cross-Origin-Resource-Policy", options.CrossOriginResourcePolicy);
        result = WithOptional(result, "Cross-Origin-Embedder-Policy", options.CrossOriginEmbedderPolicy);

        if (isHttps && !string.IsNullOrWhiteSpace(options.StrictTransportSecurity))
        {
            var hsts = options.HstsPreload && !options.StrictTransportSecurity.Contains("preload", StringComparison.OrdinalIgnoreCase)
                ? options.StrictTransportSecurity + "; preload"
                : options.StrictTransportSecurity;
            result = result.WithHeader("Strict-Transport-Security", hsts);
        }

        if (options.NoStoreErrors && result.StatusCode >= 400)
        {
            result = result.WithHeader("Cache-Control", "no-store");
        }

        return result;
    }

    private static ElsieResult WithOptional(ElsieResult result, string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? result : result.WithHeader(name, value);
}

/// <summary>DI registration for security headers.</summary>
public static class ElsieSecurityHeadersServiceCollectionExtensions
{
    /// <summary>
    /// Add <see cref="ElsieSecurityHeadersMiddleware"/> to the application pipeline. Defaults are
    /// conservative; set <see cref="ElsieSecurityHeadersOptions.ContentSecurityPolicy"/> when the
    /// app serves HTML.
    /// </summary>
    public static IServiceCollection AddElsieSecurityHeaders(
        this IServiceCollection services,
        Action<ElsieSecurityHeadersOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieSecurityHeadersOptions();
        configure?.Invoke(options);
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use(new ElsieSecurityHeadersMiddleware(options))));
        return services;
    }
}
