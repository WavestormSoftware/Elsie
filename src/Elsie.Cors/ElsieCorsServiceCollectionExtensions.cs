using Elsie.Routing;
using Elsie.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elsie.Cors;

public static class ElsieCorsServiceCollectionExtensions
{
    /// <summary>
    /// Registers CORS options, after-hook ACAO headers, and preflight <see cref="IElsieRequestFilter"/>.
    /// The <c>Elsie:Cors</c> config section binds via <see cref="IOptionsMonitor{T}"/> when present
    /// and reloads are applied to the live options (hot reload of origins/methods/headers).
    /// </summary>
    public static IServiceCollection AddElsieCors(
        this IServiceCollection services,
        Action<ElsieCorsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddElsieCors(configuration: null, configure);
    }

    /// <inheritdoc cref="AddElsieCors(IServiceCollection, Action{ElsieCorsOptions}?)"/>
    public static IServiceCollection AddElsieCors(
        this IServiceCollection services,
        IConfiguration? configuration,
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
        services.AddOptions<ElsieCorsOptions>().Configure(o => o.CopyFrom(options));
        services.AddOptions<ElsieCorsConfigurationOptions>();
        if (configuration is not null)
        {
            services.AddOptions<ElsieCorsConfigurationOptions>().BindConfiguration("Elsie:Cors");
        }

        services.TryAddSingleton<ElsieCorsConfigRelay>();
        services.TryAddSingleton<ElsieCorsApplier>();
        services.TryAddSingleton<ElsieCorsMiddleware>(sp => new ElsieCorsMiddleware(
            sp.GetRequiredService<ElsieCorsOptions>(),
            sp.GetRequiredService<ElsieCorsConfigRelay>(),
            sp.GetRequiredService<RouteTable>(),
            sp.GetRequiredService<ElsieCorsApplier>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IElsieRequestFilter, ElsieCorsPreflightFilter>());
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
}

/// <summary>Applies CORS headers to Elsie results after the handler runs.</summary>
internal sealed class ElsieCorsApplier
{
    private readonly ElsieCorsOptions _options;
    private readonly RouteTable _routes;

    public ElsieCorsApplier(ElsieCorsOptions options, ElsieCorsConfigRelay relay, RouteTable routes)
    {
        _options = options;
        _routes = routes;
        _ = relay; // constructed so config reload forwarding is live
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

internal sealed class ElsieCorsPreflightFilter : IElsieRequestFilter
{
    private readonly ElsieCorsOptions _options;
    private readonly RouteTable _routes;

    public ElsieCorsPreflightFilter(ElsieCorsOptions options, ElsieCorsConfigRelay relay, RouteTable routes)
    {
        _options = options;
        _routes = routes;
        _ = relay; // constructed so config reload forwarding is live
    }

    public Task<ElsieHttpResponse?> TryHandleAsync(ElsieRequest request, CancellationToken cancellationToken)
    {
        var origin = request.GetHeader("Origin");
        if (string.IsNullOrEmpty(origin) ||
            !request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(request.GetHeader("Access-Control-Request-Method")))
        {
            return Task.FromResult<ElsieHttpResponse?>(null);
        }

        var policy = ResolvePreflightPolicy(request);
        var requestMethod = request.GetHeader("Access-Control-Request-Method");
        var requestHeaders = request.GetHeader("Access-Control-Request-Headers");

        ElsieResult result;
        if (ElsieCorsEvaluator.TryBuildPreflightHeaders(
                policy,
                origin!,
                requestMethod,
                string.IsNullOrEmpty(requestHeaders) ? null : requestHeaders,
                out var headers))
        {
            result = ElsieResult.NoContent().WithHeaders(headers);
        }
        else
        {
            // Origin/method not allowed — empty 204 (browser enforces missing ACAO).
            result = ElsieResult.NoContent();
        }

        var response = ElsieHttpResponse.FromDispatch(
            ElsieDispatchResult.Handled(result, new ElsieResponse()));
        return Task.FromResult(response);
    }

    private ElsieCorsPolicy ResolvePreflightPolicy(ElsieRequest request)
    {
        var requestMethod = request.GetHeader("Access-Control-Request-Method");
        if (!string.IsNullOrEmpty(requestMethod))
        {
            var lookup = _routes.Lookup(requestMethod!, request.Path);
            if (lookup.Status == RouteLookupStatus.Matched &&
                lookup.Match!.Route.TryGetCorsPolicyName(out var named) &&
                named is not null &&
                _options.TryGetPolicy(named, out var policy))
            {
                return policy;
            }
        }

        return _options.GetRequiredPolicy(_options.DefaultPolicy);
    }
}

public static class ElsieCorsAppExtensions
{
    /// <summary>Register CORS on an <see cref="ElsieApp"/>.</summary>
    public static ElsieApp Cors(this ElsieApp app, Action<ElsieCorsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services(s => s.AddElsieCors(configure));
    }
}
