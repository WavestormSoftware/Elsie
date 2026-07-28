namespace Elsie.Routing;

/// <summary>
/// Immutable route collection + matcher built at startup.
/// </summary>
public sealed class RouteTable
{
    private readonly RouteMatcher _matcher;

    public RouteTable(IEnumerable<RouteDescriptor> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var list = routes.ToList();
        EnsureNoConflicts(list);
        Routes = list;
        _matcher = new RouteMatcher(list);
    }

    public IReadOnlyList<RouteDescriptor> Routes { get; }

    public static RouteTable FromModules(IEnumerable<ElsieModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return new RouteTable(modules.SelectMany(m => m.Routes));
    }

    /// <summary>Full lookup: matched, method-not-allowed, or not found.</summary>
    public RouteLookup Lookup(string method, string path) => _matcher.Lookup(method, path);

    /// <summary>True only when method+path match a handler.</summary>
    public bool TryMatch(string method, string path, out RouteMatch? match) =>
        _matcher.TryMatch(method, path, out match);

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
