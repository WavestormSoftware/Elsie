using Elsie.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Cors;

public static class ElsieCorsServiceCollectionExtensions
{
    /// <summary>
    /// Registers CORS options and an Elsie after-hook that adds ACAO headers on matched responses.
    /// Pair with <see cref="UseElsieCors"/> for OPTIONS preflight.
    /// </summary>
    public static IServiceCollection AddElsieCors(
        this IServiceCollection services,
        Action<ElsieCorsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElsie();
        var options = new ElsieCorsOptions();
        configure?.Invoke(options);
        if (!options.TryGetPolicy(options.DefaultPolicy, out _))
        {
            options.AddDefaultPolicy(_ => { });
        }

        services.AddSingleton(options);
        services.TryAddSingleton<ElsieCorsApplier>();
        services.ConfigureElsiePipelines(p =>
        {
            p.AddAfter((ctx, result, _) =>
            {
                var applier = ctx.GetService<ElsieCorsApplier>();
                return Task.FromResult(applier is null ? result : applier.Apply(ctx, result));
            });
        });

        return services;
    }

    /// <summary>
    /// Inserts Elsie CORS preflight middleware. Call before <c>MapElsie</c>.
    /// </summary>
    public static IApplicationBuilder UseElsieCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ElsieCorsMiddleware>();
    }
}

/// <summary>Applies CORS headers to Elsie results after the handler runs.</summary>
internal sealed class ElsieCorsApplier
{
    private readonly ElsieCorsOptions _options;
    private readonly RouteTable _routes;

    public ElsieCorsApplier(ElsieCorsOptions options, RouteTable routes)
    {
        _options = options;
        _routes = routes;
    }

    public ElsieResult Apply(ElsieContext ctx, ElsieResult result)
    {
        var origin = ctx.Request.GetHeader("Origin");
        if (string.IsNullOrEmpty(origin))
        {
            return result;
        }

        var lookup = _routes.Lookup(ctx.Request.Method, ctx.Request.Path);
        var policyName = lookup.Status == RouteLookupStatus.Matched &&
                         lookup.Match!.Route.TryGetCorsPolicyName(out var named) &&
                         named is not null
            ? named
            : _options.DefaultPolicy;

        if (!_options.TryGetPolicy(policyName, out var policy))
        {
            return result;
        }

        if (!ElsieCorsEvaluator.TryBuildActualHeaders(policy, origin, out var headers))
        {
            return result;
        }

        return result.WithHeaders(headers);
    }
}
