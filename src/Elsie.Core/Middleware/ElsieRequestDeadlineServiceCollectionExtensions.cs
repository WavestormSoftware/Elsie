using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Middleware;

/// <summary>
/// Registers <see cref="RequestDeadlineMiddleware"/> into the app middleware pipeline.
/// Each request is aborted with <c>408 Request Timeout</c> if its handler exceeds the configured
/// <see cref="ElsieRequestDeadlineOptions.Deadline"/>; WebSocket / streaming (SSE) responses are
/// exempt.
/// </summary>
public static class ElsieRequestDeadlineServiceCollectionExtensions
{
    /// <summary>
    /// Add the per-request deadline middleware with the given options (or defaults).
    /// </summary>
    public static IServiceCollection AddRequestDeadline(
        this IServiceCollection services,
        Action<ElsieRequestDeadlineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElsie();

        var options = new ElsieRequestDeadlineOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<RequestDeadlineMiddleware>();
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use<RequestDeadlineMiddleware>()));

        return services;
    }
}
