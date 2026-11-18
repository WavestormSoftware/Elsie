using Elsie.Routing;

namespace Elsie.Endpoints;

/// <summary>
/// Minimal-endpoint mapping for <see cref="IElsieEndpointRouteBuilder"/> (the app host, a route
/// group, or a standalone registry). Handlers may take bound parameters and return
/// <see cref="ElsieResult"/>, <see cref="IElsieResult"/>, a POCO (JSON), a string, or a task of any
/// of those.
/// </summary>
public static class ElsieEndpointRouteBuilderExtensions
{
    public static RouteBuilder MapGet(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("GET", pattern, handler);

    public static RouteBuilder MapPost(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("POST", pattern, handler);

    public static RouteBuilder MapPut(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("PUT", pattern, handler);

    public static RouteBuilder MapPatch(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("PATCH", pattern, handler);

    public static RouteBuilder MapDelete(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("DELETE", pattern, handler);

    public static RouteBuilder MapHead(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("HEAD", pattern, handler);

    public static RouteBuilder MapOptions(this IElsieEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.Map("OPTIONS", pattern, handler);

    /// <summary>Register a handler for an arbitrary HTTP method.</summary>
    public static RouteBuilder Map(this IElsieEndpointRouteBuilder routes, string method, string pattern, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var descriptor = routes.Registry.Add(method, pattern, handler, routes.Conventions);
        return new RouteBuilder(descriptor);
    }

    /// <summary>Register the same handler for multiple HTTP methods.</summary>
    public static RouteBuilder[] MapMethods(
        this IElsieEndpointRouteBuilder routes,
        IEnumerable<string> methods,
        string pattern,
        Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(methods);
        return methods.Select(method => routes.Map(method, pattern, handler)).ToArray();
    }

    /// <summary>
    /// Create a route group: a path prefix plus conventions (filters, metadata, auth requirements)
    /// inherited by every endpoint mapped on it. Groups nest.
    /// </summary>
    public static ElsieRouteGroup MapGroup(this IElsieEndpointRouteBuilder routes, string prefix)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var conventions = routes.Conventions.Clone();
        conventions.Prefix = ElsieModule.CombinePaths(conventions.Prefix, prefix);
        return new ElsieRouteGroup(routes.Registry, conventions);
    }
}

/// <summary>
/// A path-prefixed endpoint group. Endpoints mapped on the group inherit its filters and metadata;
/// group conventions are copied into child groups, so changes made after a child group is created
/// do not affect it.
/// </summary>
public sealed class ElsieRouteGroup : IElsieEndpointRouteBuilder
{
    internal ElsieRouteGroup(ElsieEndpointRegistry registry, ElsieEndpointConventions conventions)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Conventions = conventions ?? throw new ArgumentNullException(nameof(conventions));
    }

    /// <inheritdoc />
    public ElsieEndpointRegistry Registry { get; }

    /// <inheritdoc />
    public ElsieEndpointConventions Conventions { get; }

    /// <summary>Path prefix of this group.</summary>
    public string Prefix => Conventions.Prefix;
}
