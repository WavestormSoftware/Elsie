namespace Elsie.Auth;

/// <summary>Before-hook gates over <see cref="ElsiePrincipal"/>.</summary>
public static class ElsieAuthGates
{
    /// <summary>
    /// 401 when the principal is missing or unauthenticated. When JWT or a cookie challenge
    /// login path is configured, <see cref="ElsieAuthResultExtensions.Challenge(ElsieContext)"/>
    /// shapes the response (401 + WWW-Authenticate, or a 302 login redirect).
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireAuthenticated() =>
        ctx => ctx.GetUser().Identity?.IsAuthenticated == true
            ? null
            : ctx.Challenge();

    /// <summary>
    /// 401 if anonymous (or challenge/redirect per configuration); 403 if authenticated but
    /// missing all listed roles.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireRole(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Length == 0)
        {
            throw new ArgumentException("At least one role is required.", nameof(roles));
        }

        return ctx =>
        {
            var user = ctx.GetUser();
            if (user.Identity?.IsAuthenticated != true)
            {
                return ctx.Challenge();
            }

            return roles.Any(user.IsInRole)
                ? null
                : ctx.Forbid();
        };
    }

    /// <summary>
    /// 401 if anonymous (or challenge/redirect per configuration); 403 if claim type (and
    /// optional value) is missing.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireClaim(string type, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return ctx =>
        {
            var user = ctx.GetUser();
            if (user.Identity?.IsAuthenticated != true)
            {
                return ctx.Challenge();
            }

            var ok = value is null
                ? user.HasClaim(c => c.Type == type)
                : user.HasClaim(type, value);

            return ok ? null : ctx.Forbid();
        };
    }

    /// <summary>
    /// Requires that the named policy registered via
    /// <see cref="ElsieAuthorizationPolicyExtensions.AddElsiePolicy"/> passes for the request
    /// principal. The policy name is validated eagerly against the most recently configured
    /// <see cref="ElsieAuthOptions"/> (module constructors run during app build, so an unknown
    /// name surfaces as a startup exception) and re-resolved per request from the current
    /// options. When multiple apps configure auth in one process, prefer
    /// <see cref="RequirePolicy(ElsieAuthOptions, string)"/>.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequirePolicy(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var ambient = ElsieAuthOptions.LastConfigured
            ?? throw new InvalidOperationException(
                "Elsie authentication is not configured. Call AddElsieAuth(...) before using RequirePolicy.");
        if (!ambient.Policies.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Authorization policy '{name}' is not registered. " +
                $"Register it with AddElsiePolicy(\"{name}\", ...) on the ElsieAuthOptions before building the app.");
        }

        return ctx => EvaluatePolicy(ctx, name);
    }

    /// <summary>
    /// Requires a policy resolved eagerly from an explicit options instance. Unknown policy
    /// names throw immediately (startup exception when used in a module constructor).
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequirePolicy(ElsieAuthOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!options.Policies.TryGetValue(name, out _))
        {
            throw new InvalidOperationException(
                $"Authorization policy '{name}' is not registered. " +
                $"Register it with AddElsiePolicy(\"{name}\", ...) on the ElsieAuthOptions before building the app.");
        }

        return ctx => EvaluatePolicy(ctx, name);
    }

    private static ElsieResult? EvaluatePolicy(ElsieContext ctx, string name)
    {
        var options = ctx.GetRequiredService<ElsieAuthOptions>();
        if (!options.Policies.TryGetValue(name, out var policy))
        {
            throw new InvalidOperationException(
                $"Authorization policy '{name}' is not registered. " +
                "Register it with AddElsiePolicy(...) on the ElsieAuthOptions before building the app.");
        }

        var user = ctx.GetUser();
        if (user.Identity?.IsAuthenticated != true)
        {
            return ctx.Challenge();
        }

        foreach (var requirement in policy.Requirements)
        {
            if (!requirement(user))
            {
                return ctx.Forbid();
            }
        }

        return null;
    }
}
