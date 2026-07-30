using System.Security.Claims;
using Elsie.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Elsie.Auth;

/// <summary>Principal + sign-in helpers over the stashed ASP.NET <see cref="HttpContext"/>.</summary>
public static class ElsieAuthContextExtensions
{
    /// <summary>
    /// Returns <see cref="HttpContext.User"/> when hosted on ASP.NET Core; otherwise an empty principal.
    /// </summary>
    public static ClaimsPrincipal GetUser(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetHttpContext(out var http)
            ? http.User
            : new ClaimsPrincipal(new ClaimsIdentity());
    }

    /// <summary>Sign in with the default scheme (cookie when configured via <c>AddElsieAuth</c>).</summary>
    public static Task SignInAsync(
        this ElsieContext context,
        ClaimsPrincipal principal,
        AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);
        return context.GetHttpContext().SignInAsync(principal, properties);
    }

    /// <summary>Sign in with an explicit scheme.</summary>
    public static Task SignInAsync(
        this ElsieContext context,
        string scheme,
        ClaimsPrincipal principal,
        AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(principal);
        return context.GetHttpContext().SignInAsync(scheme, principal, properties);
    }

    /// <summary>Convenience cookie sign-in from name/role claims.</summary>
    public static Task SignInCookieAsync(
        this ElsieContext context,
        string userName,
        IEnumerable<string>? roles = null,
        AuthenticationProperties? properties = null,
        string scheme = CookieAuthenticationDefaults.AuthenticationScheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var claims = new List<Claim> { new(ClaimTypes.Name, userName) };
        if (roles is not null)
        {
            foreach (var role in roles)
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, scheme);
        return context.SignInAsync(scheme, new ClaimsPrincipal(identity), properties);
    }

    /// <summary>Sign out of the default scheme.</summary>
    public static Task SignOutAsync(this ElsieContext context, AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetHttpContext().SignOutAsync(properties);
    }

    /// <summary>Sign out of an explicit scheme.</summary>
    public static Task SignOutAsync(
        this ElsieContext context,
        string scheme,
        AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        return context.GetHttpContext().SignOutAsync(scheme, properties);
    }
}
