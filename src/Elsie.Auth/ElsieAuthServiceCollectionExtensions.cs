using Elsie.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IElsiePrincipalAttacher, ElsieAuthPrincipalAttacher>());
        return services;
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

    public ElsieAuthPrincipalAttacher(ElsieAuthOptions options)
    {
        _options = options;
    }

    public void Attach(ElsieRequest request)
    {
        if (_options.JwtBearer is not null)
        {
            var auth = request.GetHeader("Authorization");
            if (!string.IsNullOrEmpty(auth) &&
                auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth["Bearer ".Length..].Trim();
                if (JwtTokenValidator.TryValidate(token, _options.JwtBearer, out var jwtPrincipal) &&
                    jwtPrincipal is not null)
                {
                    ElsiePrincipal.SetUser(request, jwtPrincipal);
                    return;
                }
            }
        }

        if (_options.Cookie is { TicketKey: { } key } cookie)
        {
            var raw = request.GetCookie(cookie.CookieName);
            if (!string.IsNullOrEmpty(raw) &&
                CookieTicketProtector.TryUnprotect(raw, key, out var principal, out _) &&
                principal is not null)
            {
                ElsiePrincipal.SetUser(request, principal);
            }
        }
    }
}
