using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Middleware;

/// <summary>
/// Registers <see cref="OutputCachingMiddleware"/> into the app middleware pipeline. Caches
/// successful GET/HEAD responses keyed by method + route + query + Accept-Encoding, honors
/// <c>Cache-Control: no-store</c>/<c>no-cache</c>, and composes with
/// <see cref="ElsieResultConditionalGetExtensions.WithETag"/> for 304.
/// </summary>
public static class ElsieOutputCachingServiceCollectionExtensions
{
    /// <summary>
    /// Add the in-memory output cache middleware with the given options (or defaults).
    /// </summary>
    public static IServiceCollection AddOutputCaching(
        this IServiceCollection services,
        Action<ElsieOutputCachingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElsie();

        var options = new ElsieOutputCachingOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<OutputCachingMiddleware>();
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use<OutputCachingMiddleware>()));

        return services;
    }
}
