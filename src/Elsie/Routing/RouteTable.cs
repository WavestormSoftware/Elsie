namespace Elsie.Routing;

/// <summary>
/// Immutable collection of routes built at application startup.
/// </summary>
public sealed class RouteTable
{
    private readonly IReadOnlyList<RouteDescriptor> _routes;

    public RouteTable(IEnumerable<RouteDescriptor> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var list = routes.ToList();
        EnsureNoConflicts(list);
        _routes = list;
    }

    public IReadOnlyList<RouteDescriptor> Routes => _routes;

    public static RouteTable FromModules(IEnumerable<ElsieModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return new RouteTable(modules.SelectMany(m => m.Routes));
    }

    private static void EnsureNoConflicts(List<RouteDescriptor> routes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            var key = route.Method + " " + route.Template;
            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"Duplicate route registration: {key}");
            }
        }
    }
}
