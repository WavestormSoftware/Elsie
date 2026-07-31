using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.HealthChecks;

public static class ElsieHealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Registers health checks and the <see cref="ElsieHealthChecksModule"/>
    /// (<c>/healthz</c>, <c>/healthz/live</c>, <c>/healthz/ready</c>).
    /// </summary>
    public static IServiceCollection AddElsieHealthChecks(
        this IServiceCollection services,
        Action<ElsieHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElsie();

        var options = new ElsieHealthCheckOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<ElsieHealthCheckRunner>();
        services.AddElsieModule<ElsieHealthChecksModule>();
        return services;
    }
}
