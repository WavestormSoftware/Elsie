using Microsoft.AspNetCore.Http;

namespace Elsie.Routing;

public interface IRouteMatcher
{
    /// <summary>
    /// Full lookup: matched handler, method-not-allowed (path hits other methods), or not found.
    /// </summary>
    RouteLookup Lookup(string method, PathString path);

    /// <summary>
    /// Convenience wrapper over <see cref="Lookup"/> that only returns method+path matches.
    /// </summary>
    bool TryMatch(string method, PathString path, out RouteMatch? match);
}
