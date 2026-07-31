using System.Security.Claims;

namespace Elsie;

/// <summary>
/// Per-request principal storage on <see cref="ElsieRequest.Items"/> (host/auth packages).
/// </summary>
public static class ElsiePrincipal
{
    private static readonly object UserKey = new();

    public static void SetUser(ElsieRequest request, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        request.Items[UserKey] = principal;
    }

    public static ClaimsPrincipal GetUser(ElsieRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.TryGetValue(UserKey, out var value) && value is ClaimsPrincipal principal)
        {
            return principal;
        }

        return new ClaimsPrincipal(new ClaimsIdentity());
    }

    public static ClaimsPrincipal GetUser(ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return GetUser(context.Request);
    }
}
