using Elsie.Routing;

namespace Elsie.Cors;

public static class ElsieCorsRouteExtensions
{
    public const string PolicyItemKey = "Elsie.Cors.Policy";

    /// <summary>Attach a named CORS policy to this route (used for preflight + response headers).</summary>
    public static RouteBuilder WithCors(this RouteBuilder route, string policyName)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        route.Descriptor.Items[PolicyItemKey] = policyName;
        return route;
    }

    public static bool TryGetCorsPolicyName(this RouteDescriptor route, out string? policyName)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Items.TryGetValue(PolicyItemKey, out var value) && value is string name && name.Length > 0)
        {
            policyName = name;
            return true;
        }

        policyName = null;
        return false;
    }
}
