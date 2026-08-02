using System.Security.Claims;

namespace Elsie.Auth;

/// <summary>
/// A named authorization policy: an ordered list of requirements, each a predicate over the
/// request principal. All requirements must pass. Registered on
/// <see cref="ElsieAuthOptions.Policies"/> via <see cref="ElsieAuthorizationPolicyExtensions.AddElsiePolicy"/>
/// and enforced with <see cref="ElsieAuthGates.RequirePolicy(string)"/>.
/// </summary>
public sealed class ElsieAuthorizationPolicy
{
    private readonly List<Func<ClaimsPrincipal, bool>> _requirements = [];

    internal ElsieAuthorizationPolicy(string name)
    {
        Name = name;
    }

    /// <summary>Policy name used by <see cref="ElsieAuthGates.RequirePolicy(string)"/>.</summary>
    public string Name { get; }

    /// <summary>Requirements, evaluated in order; all must return true for the policy to pass.</summary>
    public IReadOnlyList<Func<ClaimsPrincipal, bool>> Requirements => _requirements;

    /// <summary>Appends a custom requirement predicate.</summary>
    public ElsieAuthorizationPolicy AddRequirement(Func<ClaimsPrincipal, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        _requirements.Add(requirement);
        return this;
    }

    /// <summary>Requires the principal to hold at least one of the listed roles.</summary>
    public ElsieAuthorizationPolicy RequireRole(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Length == 0)
        {
            throw new ArgumentException("At least one role is required.", nameof(roles));
        }

        return AddRequirement(principal => roles.Any(principal.IsInRole));
    }

    /// <summary>Requires a claim of the given type; when <paramref name="value"/> is set the claim value must match.</summary>
    public ElsieAuthorizationPolicy RequireClaim(string type, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return AddRequirement(principal =>
            value is null
                ? principal.HasClaim(c => c.Type == type)
                : principal.HasClaim(type, value));
    }
}

/// <summary>Registers named authorization policies on <see cref="ElsieAuthOptions.Policies"/>.</summary>
public static class ElsieAuthorizationPolicyExtensions
{
    /// <summary>
    /// Registers a named policy. Duplicate names throw immediately (configuration-time failure).
    /// </summary>
    public static ElsieAuthOptions AddElsiePolicy(
        this ElsieAuthOptions options,
        string name,
        Action<ElsieAuthorizationPolicy>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (options.Policies.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Authorization policy '{name}' is already registered. Policy names must be unique.");
        }

        var policy = new ElsieAuthorizationPolicy(name);
        configure?.Invoke(policy);
        options.Policies.Add(name, policy);
        return options;
    }
}
