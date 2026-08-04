using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.RequestDecompression;

/// <summary>
/// Registers <see cref="ElsieRequestDecompressionMiddleware"/> into the app middleware pipeline.
/// Decodes <c>gzip</c>/<c>deflate</c>/<c>br</c> request bodies (stacked codings decoded in reverse
/// application order), rejects unsupported codings with <c>415</c>, and caps decompressed size with
/// <c>413</c> (see <see cref="ElsieRequestDecompressionOptions"/>).
/// </summary>
public static class ElsieRequestDecompressionServiceCollectionExtensions
{
    /// <summary>
    /// Add inbound request body decompression with the given options (or defaults).
    /// </summary>
    public static IServiceCollection AddRequestDecompression(
        this IServiceCollection services,
        Action<ElsieRequestDecompressionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElsie();

        var options = new ElsieRequestDecompressionOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<ElsieRequestDecompressionMiddleware>();
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use<ElsieRequestDecompressionMiddleware>()));

        return services;
    }
}
