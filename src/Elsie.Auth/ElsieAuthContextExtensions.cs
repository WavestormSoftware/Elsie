using System.Security.Claims;

namespace Elsie.Auth;

/// <summary>Principal + sign-in helpers for Elsie-native auth.</summary>
public static class ElsieAuthContextExtensions
{
    public static ClaimsPrincipal GetUser(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ElsiePrincipal.GetUser(context);
    }

    public static Task SignInAsync(this ElsieContext context, ClaimsPrincipal principal, TimeSpan? expires = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);

        var options = context.GetRequiredService<ElsieAuthOptions>();
        var cookie = options.Cookie
            ?? throw new InvalidOperationException("Cookie authentication is not configured. Call AddElsieAuth with Cookie options.");

        if (cookie.TicketKey is null)
        {
            throw new InvalidOperationException("Cookie TicketKey is not configured.");
        }

        var lifetime = expires ?? cookie.ExpireTimeSpan;
        var exp = DateTimeOffset.UtcNow.Add(lifetime);
        var token = CookieTicketProtector.Protect(principal, exp, cookie.TicketKey);

        var cookieOptions = new ElsieCookieOptions
        {
            HttpOnly = cookie.HttpOnly,
            Secure = cookie.Secure,
            Path = cookie.CookiePath,
            Domain = cookie.CookieDomain,
            MaxAge = lifetime,
            SameSite = cookie.SameSite switch
            {
                SameSiteMode.None => ElsieSameSite.None,
                SameSiteMode.Strict => ElsieSameSite.Strict,
                _ => ElsieSameSite.Lax
            }
        };

        context.Response.SetCookie(cookie.CookieName, token, cookieOptions);
        ElsiePrincipal.SetUser(context.Request, principal);
        return Task.CompletedTask;
    }

    public static Task SignInCookieAsync(
        this ElsieContext context,
        string userName,
        IEnumerable<string>? roles = null,
        TimeSpan? expires = null)
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

        var identity = new ClaimsIdentity(claims, authenticationType: "Cookies");
        return context.SignInAsync(new ClaimsPrincipal(identity), expires);
    }

    public static Task SignOutAsync(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.GetService<ElsieAuthOptions>();
        var cookie = options?.Cookie;
        if (cookie is not null)
        {
            context.Response.SetCookie(cookie.CookieName, string.Empty, new ElsieCookieOptions
            {
                HttpOnly = cookie.HttpOnly,
                Secure = cookie.Secure,
                Path = cookie.CookiePath,
                Domain = cookie.CookieDomain,
                MaxAge = TimeSpan.Zero,
                SameSite = ElsieSameSite.Lax
            });
        }

        ElsiePrincipal.SetUser(context.Request, new ClaimsPrincipal(new ClaimsIdentity()));
        return Task.CompletedTask;
    }
}
