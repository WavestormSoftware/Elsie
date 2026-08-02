using System.Diagnostics.CodeAnalysis;
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
    /// Repeat calls compose <paramref name="configure"/> onto the single registered <see cref="ElsieOptions"/> instance.
    /// </summary>
    public static IServiceCollection AddElsie(this IServiceCollection services, Action<ElsieOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = GetOrCreateOptionsInstance(services);
        if (configure is not null)
        {
            configure(options);
        }

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
            var opts = sp.GetRequiredService<ElsieOptions>();
            return RouteTable.FromModules(modules, opts);
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

    public static IServiceCollection AddElsieModule<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TModule>(
        this IServiceCollection services)
        where TModule : ElsieModule
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ElsieModule, TModule>());
        services.TryAddSingleton<TModule>();
        return services;
    }

    /// <summary>
    /// Returns the single registered <see cref="ElsieOptions"/> ImplementationInstance.
    /// Registers a fresh instance when missing. Never returns a detached options object.
    /// </summary>
    private static ElsieOptions GetOrCreateOptionsInstance(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ElsieOptions))
            {
                continue;
            }

            if (descriptor.ImplementationInstance is ElsieOptions existing)
            {
                return existing;
            }

            throw new InvalidOperationException(
                "ElsieOptions is registered without an ImplementationInstance. " +
                "Call AddElsie() before custom ElsieOptions registrations, or register " +
                "ServiceDescriptor.Singleton(new ElsieOptions()).");
        }

        var options = new ElsieOptions();
        services.AddSingleton(options);
        return options;
    }

    /// <summary>
    /// Scans assemblies for <see cref="ElsieModule"/> subclasses. Assembly scanning is inherently
    /// incompatible with trimming/AOT; AOT apps should set <see cref="ElsieOptions.ScanEntryAssembly"/>
    /// to false and register modules explicitly. The suppression is scoped to this opt-in path.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly scanning is opt-in; AOT apps register modules explicitly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Assembly scanning is opt-in; AOT apps register modules explicitly.")]
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
