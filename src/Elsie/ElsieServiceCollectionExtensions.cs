using System.Reflection;
using Elsie.Pipelines;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie;

public static class ElsieServiceCollectionExtensions
{
    /// <summary>
    /// Registers core Elsie services (modules, routes, dispatcher). Host packages add transport adapters.
    /// </summary>
    public static IServiceCollection AddElsie(this IServiceCollection services, Action<ElsieOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = GetOrAddOptions(services, configure);

        services.TryAddSingleton<ElsiePipelines>(sp =>
        {
            var pipelines = new ElsiePipelines();
            foreach (var setup in sp.GetServices<ElsiePipelineSetup>())
            {
                setup.Configure(pipelines);
            }

            return pipelines;
        });
        services.TryAddSingleton<RouteTable>(sp =>
        {
            var modules = sp.GetServices<ElsieModule>().ToArray();
            return RouteTable.FromModules(modules);
        });
        services.TryAddSingleton<ElsieDispatcher>();

        RegisterScannedModules(services, options);

        return services;
    }

    /// <summary>
    /// Configures application-wide before/after pipelines.
    /// Multiple calls compose in registration order onto a single <see cref="ElsiePipelines"/> instance.
    /// </summary>
    public static IServiceCollection ConfigureElsiePipelines(
        this IServiceCollection services,
        Action<ElsiePipelines> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddElsie();
        services.AddSingleton(new ElsiePipelineSetup(configure));
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

    private static ElsieOptions GetOrAddOptions(IServiceCollection services, Action<ElsieOptions>? configure)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ElsieOptions))
            {
                continue;
            }

            if (descriptor.ImplementationInstance is ElsieOptions existing)
            {
                configure?.Invoke(existing);
                ElsieJson.Configure(existing.JsonSerializerOptions);
                return existing;
            }

            if (configure is not null)
            {
                var fallback = new ElsieOptions();
                configure(fallback);
                ElsieJson.Configure(fallback.JsonSerializerOptions);
                return fallback;
            }

            return new ElsieOptions();
        }

        var options = new ElsieOptions();
        configure?.Invoke(options);
        ElsieJson.Configure(options.JsonSerializerOptions);
        services.AddSingleton(options);
        return options;
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
            IEnumerable<Type> types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null)!;
            }

            foreach (var type in types)
            {
                if (type is null || type.IsAbstract || !typeof(ElsieModule).IsAssignableFrom(type))
                {
                    continue;
                }

                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(ElsieModule), type));
                services.TryAddSingleton(type);
            }
        }
    }
}

/// <summary>Internal registration hook so pipeline configures compose on one singleton.</summary>
internal sealed class ElsiePipelineSetup
{
    public ElsiePipelineSetup(Action<ElsiePipelines> configure)
    {
        Configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public Action<ElsiePipelines> Configure { get; }
}
