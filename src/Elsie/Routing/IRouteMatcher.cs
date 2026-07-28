using Microsoft.AspNetCore.Http;

namespace Elsie.Routing;

public interface IRouteMatcher
{
    bool TryMatch(string method, PathString path, out RouteMatch? match);
}
