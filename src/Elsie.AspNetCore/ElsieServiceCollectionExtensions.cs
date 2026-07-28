using System.Reflection;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.AspNetCore;

public static class ElsieServiceCollectionExtensions
{
    public static IServiceCollection AddElsie(this IServiceCollection services, Action<ElsieOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IElsieResultExecutor, ElsieResultExecutor>();
        services.TryAddSingleton<RouteTable>(sp =>
        {
            var modules = sp.GetServices<ElsieModule>().ToArray();
            return RouteTable.FromModules(modules);
        });
        services.TryAddSingleton<IRouteMatcher>(sp => new RouteMatcher(sp.GetRequiredService<RouteTable>()));

        RegisterScannedModules(services, options);

        return services;
    }

    public static IServiceCollection AddElsieModule<TModule>(this IServiceCollection services)
        where TModule : ElsieModule
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ElsieModule, TModule>());
        services.TryAddSingleton<TModule>();
        return services;
    }

    private static void RegisterScannedModules(IServiceCollection services, ElsieOptions options)
    {
        var assemblies = new List<Assembly>(options.AssembliesToScan);
        if (options.ScanEntryAssembly)
        {
            var entry = Assembly.GetEntryAssembly();
            if (entry is not null && assemblies.All(a => a != entry))
            {
                assemblies.Add(entry);
            }
        }

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(ElsieModule).IsAssignableFrom(type))
                {
                    continue;
                }

                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(ElsieModule), type));
                services.TryAddSingleton(type);
            }
        }
    }
}
