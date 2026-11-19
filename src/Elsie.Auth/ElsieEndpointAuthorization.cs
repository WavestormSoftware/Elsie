using System.Collections.Concurrent;
using Elsie.Endpoints;
using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Auth;

/// <summary>
/// Enforces endpoint authorization metadata produced by <c>RequireAuthorization(...)</c> /
/// <c>AllowAnonymous()</c> on minimal-API endpoints and route groups.
/// </summary>
/// <remarks>
/// Runs as application middleware, so it applies to every mapped endpoint regardless of how the
/// endpoint was registered. Endpoints without authorization metadata are unaffected; module routes
/// keep using the explicit gates (<c>ElsieAuthGates.RequireRole(...)</c> etc.).
/// </remarks>
public sealed class ElsieEndpointAuthorizationMiddleware : IElsieMiddleware
{
    private readonly ElsieAuthOptions _options;
    private readonly ConcurrentDictionary<string, Func<ElsieContext, ElsieResult?>> _policyGates = new(StringComparer.Ordinal);

    public ElsieEndpointAuthorizationMiddleware(ElsieAuthOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var endpoint = context.Endpoint;
        if (endpoint is null || endpoint.GetMetadata<ElsieAllowAnonymousMetadata>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        ElsieAuthorizationRequirements? requirements = null;
        foreach (var candidate in endpoint.GetMetadataOfType<ElsieAuthorizationRequirements>())
        {
            if (candidate.Required)
            {
                requirements = candidate;
                break;
            }
        }

        if (requirements is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var user = context.GetUser();
        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = context.Challenge();
            return;
        }

        foreach (var policy in CollectPolicies(endpoint))
        {
            var gate = _policyGates.GetOrAdd(policy, name => ElsieAuthGates.RequirePolicy(_options, name));
            if (gate(context) is { } failure)
            {
                context.Result = failure;
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static IEnumerable<string> CollectPolicies(Routing.RouteDescriptor endpoint)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirements in endpoint.GetMetadataOfType<ElsieAuthorizationRequirements>())
        {
            foreach (var policy in requirements.Policies)
            {
                if (seen.Add(policy))
                {
                    yield return policy;
                }
            }
        }
    }
}

/// <summary>Registration for <see cref="ElsieEndpointAuthorizationMiddleware"/>.</summary>
public static class ElsieEndpointAuthorizationExtensions
{
    /// <summary>
    /// Add endpoint-metadata authorization to the application pipeline. Call after
    /// <c>AddElsieAuth(...)</c>; policies named by <c>RequireAuthorization("policy")</c> must be
    /// registered with <c>AddElsiePolicy(...)</c>.
    /// </summary>
    public static IServiceCollection AddElsieEndpointAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElsie();
        services.TryAddSingleton<ElsieEndpointAuthorizationMiddleware>();
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use<ElsieEndpointAuthorizationMiddleware>()));
        return services;
    }
}
