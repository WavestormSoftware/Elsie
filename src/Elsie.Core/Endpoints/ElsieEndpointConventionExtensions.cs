using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Endpoints;

/// <summary>
/// Fluent conventions for endpoints and route groups: metadata, filters, authorization and
/// policy requirements. Metadata is read back by middleware and by the OpenAPI builder.
/// </summary>
public static class ElsieEndpointConventionExtensions
{
    // ---- Single endpoint (RouteBuilder) -------------------------------------------------

    /// <summary>Add arbitrary metadata to the endpoint.</summary>
    public static RouteBuilder WithMetadata(this RouteBuilder builder, object metadata)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddMetadata(metadata);
        return builder;
    }

    /// <summary>Set a human-facing display name (does not affect URL generation).</summary>
    public static RouteBuilder WithDisplayName(this RouteBuilder builder, string displayName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddMetadata(new ElsieEndpointDisplayNameMetadata(displayName));
        return builder;
    }

    /// <summary>Add a filter that runs around this endpoint's handler.</summary>
    public static RouteBuilder AddEndpointFilter(this RouteBuilder builder, IElsieEndpointFilter filter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddFilter(filter);
        return builder;
    }

    /// <summary>Add an inline filter that runs around this endpoint's handler.</summary>
    public static RouteBuilder WithFilter(
        this RouteBuilder builder,
        Func<ElsieEndpointFilterContext, ElsieEndpointFilterDelegate, ValueTask<object?>> filter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(filter);
        builder.Descriptor.AddFilter(new InlineEndpointFilter(filter));
        return builder;
    }

    /// <summary>
    /// Require an authenticated principal, plus (optionally) named authorization policies.
    /// Enforced by <c>AddElsieEndpointAuthorization()</c> from <c>Elsie.Auth</c>.
    /// </summary>
    public static RouteBuilder RequireAuthorization(this RouteBuilder builder, params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.GetOrAddMetadata(static () => new ElsieAuthorizationRequirements()).Add(policies ?? []);
        return builder;
    }

    /// <summary>Allow anonymous access even when the enclosing group requires authorization.</summary>
    public static RouteBuilder AllowAnonymous(this RouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddMetadata(ElsieAllowAnonymousMetadata.Instance);
        return builder;
    }

    /// <summary>Require a CORS policy (enforced by <c>Elsie.Cors</c>).</summary>
    public static RouteBuilder RequireCors(this RouteBuilder builder, string? policy = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddMetadata(new ElsieCorsMetadata(policy));
        return builder;
    }

    /// <summary>Require a named rate-limiting policy (enforced by <c>ElsieRateLimiting</c>).</summary>
    public static RouteBuilder RequireRateLimiting(this RouteBuilder builder, string? policy = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Descriptor.AddMetadata(new ElsieRateLimitingMetadata(policy));
        return builder;
    }

    // ---- Route group / app -------------------------------------------------------------

    /// <summary>Add metadata inherited by every endpoint mapped on this builder.</summary>
    public static TBuilder WithMetadata<TBuilder>(this TBuilder builder, object metadata)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Conventions.AddMetadata(metadata);
        return builder;
    }

    /// <summary>Add a filter inherited by every endpoint mapped on this builder.</summary>
    public static TBuilder AddEndpointFilter<TBuilder>(this TBuilder builder, IElsieEndpointFilter filter)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Conventions.AddFilter(filter);
        return builder;
    }

    /// <summary>Add an inline filter inherited by every endpoint mapped on this builder.</summary>
    public static TBuilder WithFilter<TBuilder>(
        this TBuilder builder,
        Func<ElsieEndpointFilterContext, ElsieEndpointFilterDelegate, ValueTask<object?>> filter)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(filter);
        builder.Conventions.AddFilter(new InlineEndpointFilter(filter));
        return builder;
    }

    /// <summary>Require authorization for every endpoint mapped on this builder.</summary>
    public static TBuilder RequireAuthorization<TBuilder>(this TBuilder builder, params string[] policies)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var requirements = new ElsieAuthorizationRequirements();
        requirements.Add(policies ?? []);
        builder.Conventions.AddMetadata(requirements);
        return builder;
    }

    /// <summary>Require a CORS policy for every endpoint mapped on this builder.</summary>
    public static TBuilder RequireCors<TBuilder>(this TBuilder builder, string? policy = null)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Conventions.AddMetadata(new ElsieCorsMetadata(policy));
        return builder;
    }

    /// <summary>Require a rate-limiting policy for every endpoint mapped on this builder.</summary>
    public static TBuilder RequireRateLimiting<TBuilder>(this TBuilder builder, string? policy = null)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Conventions.AddMetadata(new ElsieRateLimitingMetadata(policy));
        return builder;
    }

    /// <summary>Tag every endpoint mapped on this builder (OpenAPI grouping).</summary>
    public static TBuilder WithTags<TBuilder>(this TBuilder builder, params string[] tags)
        where TBuilder : IElsieEndpointRouteBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var tag in tags ?? [])
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                builder.Conventions.AddMetadata(new ElsieTagMetadata(tag));
            }
        }

        return builder;
    }

    private sealed class InlineEndpointFilter(
        Func<ElsieEndpointFilterContext, ElsieEndpointFilterDelegate, ValueTask<object?>> filter)
        : IElsieEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(ElsieEndpointFilterContext context, ElsieEndpointFilterDelegate next) =>
            filter(context, next);
    }
}

/// <summary>OpenAPI tag attached by <c>WithTags</c> on a route group.</summary>
public sealed class ElsieTagMetadata
{
    public ElsieTagMetadata(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        Tag = tag;
    }

    public string Tag { get; }
}

/// <summary>DI entry points for endpoint mapping outside the Elsie host.</summary>
public static class ElsieEndpointServiceCollectionExtensions
{
    /// <summary>
    /// Register a fresh <see cref="ElsieEndpointRegistry"/> and map endpoints on it. Useful for
    /// in-memory test hosts and custom transports that build their own container.
    /// </summary>
    public static IServiceCollection AddElsieEndpoints(
        this IServiceCollection services,
        Action<IElsieEndpointRouteBuilder> configure,
        Action<ElsieOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddElsie(configureOptions);
        var registry = new ElsieEndpointRegistry();
        configure(registry);
        services.AddSingleton(registry);
        return services;
    }
}
