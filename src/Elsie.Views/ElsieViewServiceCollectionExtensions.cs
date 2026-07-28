using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Views;

public static class ElsieViewServiceCollectionExtensions
{
    public static IServiceCollection AddElsieViews(
        this IServiceCollection services,
        Action<ElsieViewOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieViewOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<ElsieFileViewEngine>();
        return services;
    }
}
