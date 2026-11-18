using Elsie.Routing;

namespace Elsie.Endpoints;

/// <summary>
/// Conventions (prefix, filters, metadata) inherited by endpoints registered through a route
/// builder or route group.
/// </summary>
public sealed class ElsieEndpointConventions
{
    private readonly List<IElsieEndpointFilter> _filters = [];
    private readonly List<object> _metadata = [];

    /// <summary>Path prefix prepended to every registered template (empty for the root).</summary>
    public string Prefix { get; internal set; } = string.Empty;

    /// <summary>Filters applied to every endpoint registered under these conventions.</summary>
    public IReadOnlyList<IElsieEndpointFilter> Filters => _filters;

    /// <summary>Metadata applied to every endpoint registered under these conventions.</summary>
    public IReadOnlyList<object> Metadata => _metadata;

    internal void AddFilter(IElsieEndpointFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filters.Add(filter);
    }

    internal void AddMetadata(object metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata.Add(metadata);
    }

    internal ElsieEndpointConventions Clone()
    {
        var clone = new ElsieEndpointConventions { Prefix = Prefix };
        clone._filters.AddRange(_filters);
        clone._metadata.AddRange(_metadata);
        return clone;
    }
}

/// <summary>
/// Anything endpoints can be mapped on: the Elsie host app, a route group, or a standalone
/// registry (tests / custom hosts).
/// </summary>
public interface IElsieEndpointRouteBuilder
{
    /// <summary>Route sink the endpoints are written to.</summary>
    ElsieEndpointRegistry Registry { get; }

    /// <summary>Conventions inherited by newly mapped endpoints.</summary>
    ElsieEndpointConventions Conventions { get; }
}

/// <summary>
/// Collects minimal-API endpoint registrations. The DI <see cref="RouteTable"/> is built from
/// both <see cref="ElsieModule"/> routes and every registered registry, so modules and mapping
/// APIs can be mixed freely.
/// </summary>
public sealed class ElsieEndpointRegistry : IElsieEndpointRouteBuilder
{
    private readonly List<RouteDescriptor> _routes = [];

    /// <summary>Registered endpoint descriptors (single-method routes only).</summary>
    public IReadOnlyList<RouteDescriptor> Routes => _routes;

    /// <summary>Root conventions (prefix/filters/metadata) for endpoints mapped directly on this registry.</summary>
    public ElsieEndpointConventions Conventions { get; } = new();

    /// <inheritdoc />
    ElsieEndpointRegistry IElsieEndpointRouteBuilder.Registry => this;

    internal RouteDescriptor Add(
        string method,
        string pattern,
        Delegate handler,
        ElsieEndpointConventions conventions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(conventions);

        var template = ElsieModule.CombinePaths(conventions.Prefix, pattern);
        var descriptor = new RouteDescriptor(method, template, static (_, _) => Task.FromResult(ElsieResult.NoContent()));
        descriptor.Handler = ElsieEndpointHandlerFactory.Create(handler, descriptor.Method, descriptor);

        foreach (var metadata in conventions.Metadata)
        {
            if (metadata is ElsieTagMetadata tag)
            {
                descriptor.AddTags([tag.Tag]);
                continue;
            }

            descriptor.AddMetadata(metadata);
        }

        foreach (var filter in conventions.Filters)
        {
            descriptor.AddFilter(filter);
        }

        _routes.Add(descriptor);
        return descriptor;
    }
}
