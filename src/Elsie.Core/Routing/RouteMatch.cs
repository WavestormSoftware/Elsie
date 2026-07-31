namespace Elsie.Routing;

public sealed class RouteMatch
{
    public RouteMatch(RouteDescriptor route, IReadOnlyDictionary<string, string> routeValues)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        RouteValues = routeValues ?? throw new ArgumentNullException(nameof(routeValues));
    }

    public RouteDescriptor Route { get; }
    public IReadOnlyDictionary<string, string> RouteValues { get; }
}
