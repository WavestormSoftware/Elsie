using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.AspNetCore;

public static class ElsieApplicationBuilderExtensions
{
    /// <summary>
    /// Adds Elsie as middleware. Unmatched requests continue down the pipeline unless
    /// <paramref name="terminal"/> is <c>true</c> (then 404 problem+json).
    /// </summary>
    public static IApplicationBuilder UseElsie(this IApplicationBuilder app, bool terminal = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ElsieMiddleware>(terminal);
    }

    /// <summary>
    /// Warms <see cref="Routing.RouteTable"/> then <see cref="UseElsie"/>.
    /// Default: unmatched requests fall through (OpenAPI, static files, host 404).
    /// <paramref name="terminal"/> <c>true</c> → Elsie answers unmatched with 404 problem+json.
    /// </summary>
    public static IApplicationBuilder MapElsie(this IApplicationBuilder app, bool terminal = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.ApplicationServices.GetRequiredService<Routing.RouteTable>();
        return app.UseElsie(terminal);
    }

    /// <summary>Convenience overload for <see cref="WebApplication"/>.</summary>
    public static WebApplication MapElsie(this WebApplication app, bool terminal = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        ((IApplicationBuilder)app).MapElsie(terminal);
        return app;
    }
}
