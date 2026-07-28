using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.AspNetCore;

public static class ElsieApplicationBuilderExtensions
{
    /// <summary>
    /// Adds Elsie as middleware. Unmatched requests continue down the pipeline.
    /// </summary>
    public static IApplicationBuilder UseElsie(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ElsieMiddleware>(false);
    }

    /// <summary>
    /// Warms <see cref="Routing.RouteTable"/> then <see cref="UseElsie"/>.
    /// Unmatched requests fall through (OpenAPI, static files, host 404).
    /// </summary>
    public static IApplicationBuilder MapElsie(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.ApplicationServices.GetRequiredService<Routing.RouteTable>();
        return app.UseElsie();
    }

    /// <summary>Convenience overload for <see cref="WebApplication"/>.</summary>
    public static WebApplication MapElsie(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ((IApplicationBuilder)app).MapElsie();
        return app;
    }
}
