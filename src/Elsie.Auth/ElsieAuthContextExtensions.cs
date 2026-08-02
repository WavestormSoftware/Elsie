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

    /// <summary>
    /// Signs the principal in. With an <see cref="ElsieAuthOptions.SessionStore"/> configured the
    /// cookie carries an opaque v2 session id (≥128-bit) and the principal lives server-side;
    /// otherwise the default client-side encrypted v1 ticket is used.
    /// </summary>
    public static async Task SignInAsync(this ElsieContext context, ClaimsPrincipal principal, TimeSpan? expires = null)
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
        string token;
        if (options.SessionStore is { } store)
        {
            var sessionId = CookieTicketProtector.NewSessionId();
            await store.SetAsync(
                CookieTicketProtector.ToSessionIdString(sessionId),
                CookieTicketProtector.SerializePrincipal(principal),
                lifetime,
                context.Request.RequestAborted).ConfigureAwait(false);
            token = CookieTicketProtector.ProtectServerSideSession(sessionId);
        }
        else
        {
            var exp = DateTimeOffset.UtcNow.Add(lifetime);
            token = CookieTicketProtector.Protect(principal, exp, cookie.TicketKey);
        }

        var cookieOptions = new ElsieCookieOptions
        {
            HttpOnly = cookie.HttpOnly,
            Secure = cookie.Secure,
            Path = cookie.CookiePath,
            Domain = cookie.CookieDomain,
            MaxAge = lifetime,
            SameSite = cookie.SameSite
        };

        context.Response.SetCookie(cookie.CookieName, token, cookieOptions);
        ElsiePrincipal.SetUser(context.Request, principal);
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

    /// <summary>
    /// Signs out: removes the server-side session (when a v2 cookie and a session store are
    /// configured) and clears the cookie.
    /// </summary>
    public static async Task SignOutAsync(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.GetService<ElsieAuthOptions>();
        var cookie = options?.Cookie;
        if (cookie is not null)
        {
            var raw = context.Request.GetCookie(cookie.CookieName);
            if (options!.SessionStore is { } store &&
                !string.IsNullOrEmpty(raw) &&
                CookieTicketProtector.TryGetSessionId(raw, out var sessionId))
            {
                await store.RemoveAsync(
                    CookieTicketProtector.ToSessionIdString(sessionId),
                    context.Request.RequestAborted).ConfigureAwait(false);
            }

            context.Response.SetCookie(cookie.CookieName, string.Empty, new ElsieCookieOptions
            {
                HttpOnly = cookie.HttpOnly,
                Secure = cookie.Secure,
                Path = cookie.CookiePath,
                Domain = cookie.CookieDomain,
                MaxAge = TimeSpan.Zero,
                SameSite = cookie.SameSite
            });
        }

        ElsiePrincipal.SetUser(context.Request, new ClaimsPrincipal(new ClaimsIdentity()));
    }
}
