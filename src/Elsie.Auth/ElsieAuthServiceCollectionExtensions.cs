using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Auth;

public static class ElsieAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers ASP.NET authentication/authorization for Elsie hosts.
    /// Configure cookie and/or JWT via <paramref name="configure"/>; call <see cref="UseElsieAuth"/> in the pipeline.
    /// </summary>
    public static IServiceCollection AddElsieAuth(
        this IServiceCollection services,
        Action<ElsieAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Authorization policy cache resolves EndpointDataSource (ValidateOnBuild hosts).
        services.AddRouting();

        var options = new ElsieAuthOptions();
        configure?.Invoke(options);

        if (options.Cookie is null && options.JwtBearer is null)
        {
            // Sensible cookie default so SignInAsync works out of the box.
            options.Cookie = _ => { };
        }

        var defaultScheme = options.DefaultScheme
            ?? (options.Cookie is not null && options.JwtBearer is null
                ? options.CookieScheme
                : options.JwtBearer is not null && options.Cookie is null
                    ? options.JwtBearerScheme
                    : options.CookieScheme);

        var authBuilder = services.AddAuthentication(o =>
        {
            o.DefaultScheme = defaultScheme;
            o.DefaultAuthenticateScheme = defaultScheme;
            o.DefaultChallengeScheme = defaultScheme;
        });

        if (options.Cookie is not null)
        {
            authBuilder.AddCookie(options.CookieScheme, options.Cookie);
        }

        if (options.JwtBearer is not null)
        {
            authBuilder.AddJwtBearer(options.JwtBearerScheme, options.JwtBearer);
        }

        if (options.Authorization is not null)
        {
            services.AddAuthorization(options.Authorization);
        }
        else
        {
            services.AddAuthorization();
        }

        return services;
    }

    /// <summary>
    /// Advanced escape hatch — configure <see cref="AuthenticationBuilder"/> directly after defaults.
    /// </summary>
    public static AuthenticationBuilder AddElsieAuth(
        this IServiceCollection services,
        Action<AuthenticationBuilder> configureAuth,
        Action<Microsoft.AspNetCore.Authorization.AuthorizationOptions>? configureAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureAuth);

        services.AddRouting();
        var builder = services.AddAuthentication();
        configureAuth(builder);
        if (configureAuthorization is not null)
        {
            services.AddAuthorization(configureAuthorization);
        }
        else
        {
            services.AddAuthorization();
        }

        return builder;
    }

    /// <summary>Adds <c>UseAuthentication</c> + <c>UseAuthorization</c> (call before <c>MapElsie</c>).</summary>
    public static IApplicationBuilder UseElsieAuth(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
