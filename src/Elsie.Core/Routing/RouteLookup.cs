namespace Elsie.Routing;

public enum RouteLookupStatus
{
    NotFound = 0,
    MethodNotAllowed = 1,
    Matched = 2
}

/// <summary>
/// Result of matching a method + path against the route table.
/// </summary>
public sealed class RouteLookup
{
    private static readonly IReadOnlyList<string> NoMethods = Array.Empty<string>();

    private RouteLookup(RouteLookupStatus status, RouteMatch? match, IReadOnlyList<string> allowedMethods)
    {
        Status = status;
        Match = match;
        AllowedMethods = allowedMethods;
    }

    public RouteLookupStatus Status { get; }
    public RouteMatch? Match { get; }
    public IReadOnlyList<string> AllowedMethods { get; }

    public static RouteLookup NotFound() => new(RouteLookupStatus.NotFound, match: null, NoMethods);

    public static RouteLookup MethodNotAllowed(IReadOnlyList<string> allowedMethods) =>
        new(RouteLookupStatus.MethodNotAllowed, match: null, allowedMethods);

    public static RouteLookup Matched(RouteMatch match) =>
        new(RouteLookupStatus.Matched, match, NoMethods);
}
