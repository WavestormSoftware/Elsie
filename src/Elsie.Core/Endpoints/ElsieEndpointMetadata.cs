namespace Elsie.Endpoints;

/// <summary>
/// Authorization requirement attached to an endpoint (or route group) by
/// <c>RequireAuthorization(...)</c>. Enforced by the endpoint authorization middleware in
/// <c>Elsie.Auth</c>; metadata-only so Core stays free of principal plumbing.
/// </summary>
public sealed class ElsieAuthorizationRequirements
{
    private readonly List<string> _policies = [];

    /// <summary>True once <c>RequireAuthorization</c> has been called.</summary>
    public bool Required { get; internal set; }

    /// <summary>Named policies that must all pass (in addition to an authenticated principal).</summary>
    public IReadOnlyList<string> Policies => _policies;

    internal void Add(IEnumerable<string> policies)
    {
        Required = true;
        foreach (var policy in policies)
        {
            if (!string.IsNullOrWhiteSpace(policy) && !_policies.Contains(policy, StringComparer.Ordinal))
            {
                _policies.Add(policy);
            }
        }
    }
}

/// <summary>Marks an endpoint as callable without authentication, overriding a group requirement.</summary>
public sealed class ElsieAllowAnonymousMetadata
{
    private ElsieAllowAnonymousMetadata()
    {
    }

    /// <summary>Shared marker instance.</summary>
    public static ElsieAllowAnonymousMetadata Instance { get; } = new();
}

/// <summary>CORS policy requirement for an endpoint (<c>RequireCors</c>).</summary>
public sealed class ElsieCorsMetadata
{
    public ElsieCorsMetadata(string? policyName)
    {
        PolicyName = policyName;
    }

    /// <summary>Named CORS policy; null means the application default policy.</summary>
    public string? PolicyName { get; }
}

/// <summary>Rate-limiting policy requirement for an endpoint (<c>RequireRateLimiting</c>).</summary>
public sealed class ElsieRateLimitingMetadata
{
    public ElsieRateLimitingMetadata(string? policyName)
    {
        PolicyName = policyName;
    }

    /// <summary>Named rate-limit policy; null means the application default policy.</summary>
    public string? PolicyName { get; }
}

/// <summary>
/// Display metadata for an endpoint. <see cref="Routing.RouteDescriptor.Name"/> remains the link
/// generation / operationId key; this carries a human-facing label.
/// </summary>
public sealed class ElsieEndpointDisplayNameMetadata
{
    public ElsieEndpointDisplayNameMetadata(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName;
    }

    public string DisplayName { get; }
}
