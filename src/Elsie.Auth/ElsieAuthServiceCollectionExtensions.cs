using System.Security.Claims;
using Elsie.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Elsie.Auth;

public static class ElsieAuthServiceCollectionExtensions
{
    internal const string DevelopmentTicketSecret = "elsie-dev-insecure-key-change-me";

    /// <summary>
    /// Registers Elsie cookie and/or JWT authentication.
    /// Cookie auth requires an explicit <see cref="ElsieCookieAuthOptions.TicketKey"/>
    /// unless <see cref="ElsieCookieAuthOptions.AllowInsecureDevelopmentKey"/> is true.
    /// </summary>
    public static IServiceCollection AddElsieAuth(
        this IServiceCollection services,
        Action<ElsieAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieAuthOptions();
        configure?.Invoke(options);

        if (options.Cookie is null && options.JwtBearer is null)
        {
            options.Cookie = new ElsieCookieAuthOptions { AllowInsecureDevelopmentKey = true };
        }

        if (options.Cookie is not null && options.Cookie.TicketKey is null)
        {
            if (options.Cookie.AllowInsecureDevelopmentKey)
            {
                options.Cookie.TicketKeyFromString(DevelopmentTicketSecret);
            }
            else
            {
                throw new InvalidOperationException(
                    "Elsie cookie auth requires TicketKey. Call Cookie.TicketKeyFromString(...) " +
                    "or set Cookie.AllowInsecureDevelopmentKey = true for local development only.");
            }
        }

        ValidateCookieOptions(options.Cookie);
        options.MarkConfigured();

        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IElsiePrincipalAttacher, ElsieAuthPrincipalAttacher>());
        return services;
    }

    /// <summary>
    /// Startup validation for cookie hardening (A3): a required <c>CookiePrefix</c> must match the
    /// cookie name, and <c>__Host-</c> additionally demands Secure, Path=/ and no Domain.
    /// </summary>
    private static void ValidateCookieOptions(ElsieCookieAuthOptions? cookie)
    {
        if (cookie is null || string.IsNullOrWhiteSpace(cookie.CookiePrefix))
        {
            return;
        }

        if (!cookie.CookieName.StartsWith(cookie.CookiePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CookieName '{cookie.CookieName}' must start with the required prefix '{cookie.CookiePrefix}'.");
        }

        if (cookie.CookiePrefix == "__Host-")
        {
            if (!cookie.Secure)
            {
                throw new InvalidOperationException("__Host- cookies require Secure = true.");
            }

            if (cookie.CookiePath is not null && cookie.CookiePath != "/")
            {
                throw new InvalidOperationException("__Host- cookies require Path = '/'.");
            }

            if (!string.IsNullOrEmpty(cookie.CookieDomain))
            {
                throw new InvalidOperationException("__Host- cookies cannot set a Domain.");
            }
        }
    }
}

public static class ElsieAuthAppExtensions
{
    public static ElsieApp Auth(this ElsieApp app, Action<ElsieAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services(s => s.AddElsieAuth(configure));
    }
}

internal sealed class ElsieAuthPrincipalAttacher : IElsiePrincipalAttacher
{
    private readonly ElsieAuthOptions _options;
    private readonly ILogger? _logger;
    private readonly object _jwksGate = new();
    private JwksResolver? _jwks;

    public ElsieAuthPrincipalAttacher(ElsieAuthOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _logger = loggerFactory?.CreateLogger("Elsie.Auth");
    }

    /// <inheritdoc />
    public async Task AttachAsync(ElsieRequest request, CancellationToken cancellationToken)
    {
        if (_options.JwtBearer is not null)
        {
            var auth = request.GetHeader("Authorization");
            if (!string.IsNullOrEmpty(auth) &&
                auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth["Bearer ".Length..].Trim();
                var jwtPrincipal = await JwtTokenValidator.TryValidateAsync(
                    token,
                    _options.JwtBearer,
                    GetJwksResolver(),
                    cancellationToken).ConfigureAwait(false);
                if (jwtPrincipal is not null)
                {
                    ElsiePrincipal.SetUser(request, jwtPrincipal);
                    return;
                }
            }
        }

        if (_options.Cookie is { TicketKey: { } key } cookie)
        {
            var raw = request.GetCookie(cookie.CookieName);
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            if (CookieTicketProtector.IsVersion2(raw))
            {
                await AttachFromSessionAsync(request, raw, cookie, cancellationToken).ConfigureAwait(false);
            }
            else if (CookieTicketProtector.TryUnprotect(raw, key, out var principal, out _) &&
                     principal is not null)
            {
                ElsiePrincipal.SetUser(request, principal);
            }
        }
    }

    private async Task AttachFromSessionAsync(
        ElsieRequest request,
        string raw,
        ElsieCookieAuthOptions cookie,
        CancellationToken cancellationToken)
    {
        var store = _options.SessionStore;
        if (store is null ||
            !CookieTicketProtector.TryGetSessionId(raw, out var sessionId))
        {
            return;
        }

        var id = CookieTicketProtector.ToSessionIdString(sessionId);
        var payload = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return;
        }

        var principal = CookieTicketProtector.TryDeserializePrincipal(payload);
        if (principal is null)
        {
            return;
        }

        // Sliding renewal: re-store with a fresh TTL so the session lives as long as it is used.
        await store.SetAsync(id, payload, cookie.ExpireTimeSpan, cancellationToken).ConfigureAwait(false);
        ElsiePrincipal.SetUser(request, principal);
    }

    private JwksResolver? GetJwksResolver()
    {
        var jwt = _options.JwtBearer;
        if (jwt is null || jwt.SigningKey is not null)
        {
            return null;
        }

        if (_jwks is not null)
        {
            return _jwks;
        }

        lock (_jwksGate)
        {
            _jwks ??= JwksResolver.TryCreate(jwt, logger: _logger);
            return _jwks;
        }
    }
}
