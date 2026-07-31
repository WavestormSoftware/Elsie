namespace Elsie.Auth;

/// <summary>Before-hook gates over <see cref="ElsiePrincipal"/>.</summary>
public static class ElsieAuthGates
{
    /// <summary>401 when the principal is missing or unauthenticated.</summary>
    public static Func<ElsieContext, ElsieResult?> RequireAuthenticated() =>
        ctx => ctx.GetUser().Identity?.IsAuthenticated == true
            ? null
            : ElsieResult.Unauthorized("Authentication required.");

    /// <summary>401 if anonymous; 403 if authenticated but missing all listed roles.</summary>
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
                return ElsieResult.Unauthorized("Authentication required.");
            }

            return roles.Any(user.IsInRole)
                ? null
                : ElsieResult.Forbidden("Missing required role.");
        };
    }

    /// <summary>401 if anonymous; 403 if claim type (and optional value) is missing.</summary>
    public static Func<ElsieContext, ElsieResult?> RequireClaim(string type, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return ctx =>
        {
            var user = ctx.GetUser();
            if (user.Identity?.IsAuthenticated != true)
            {
                return ElsieResult.Unauthorized("Authentication required.");
            }

            var ok = value is null
                ? user.HasClaim(c => c.Type == type)
                : user.HasClaim(type, value);

            return ok ? null : ElsieResult.Forbidden($"Missing required claim '{type}'.");
        };
    }
}
