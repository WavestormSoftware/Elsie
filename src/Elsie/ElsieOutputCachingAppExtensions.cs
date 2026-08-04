using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web;

/// <summary>App-level registration for the in-memory output cache middleware.</summary>
public static class ElsieOutputCachingAppExtensions
{
    /// <summary>
    /// Enable the in-memory output cache: successful GET/HEAD responses are cached keyed by
    /// method + route + query + <c>Accept-Encoding</c> (default LRU: 1024 entries / 64 MiB).
    /// <c>Cache-Control: no-store</c>/<c>no-cache</c> on the request or response opts out, and
    /// a cached ETag honors <c>If-None-Match</c> → <c>304 Not Modified</c>.
    /// </summary>
    public static ElsieApp UseOutputCaching(
        this ElsieApp app,
        Action<ElsieOutputCachingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services(s => s.AddOutputCaching(configure));
    }
}
