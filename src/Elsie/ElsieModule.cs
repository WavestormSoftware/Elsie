using Elsie.Pipelines;
using Elsie.Routing;

namespace Elsie;

/// <summary>
/// Base type for feature modules that register HTTP routes.
/// </summary>
public abstract class ElsieModule
{
    private readonly List<RouteDescriptor> _routes = [];
    private string _pathPrefix = string.Empty;

    /// <summary>Module-scoped before/after hooks.</summary>
    public ElsiePipelines Pipelines { get; } = new();

    /// <summary>Current path prefix applied to newly registered routes.</summary>
    protected string PathPrefix => _pathPrefix;

    public IReadOnlyList<RouteDescriptor> Routes => _routes;

    /// <summary>
    /// Sets a base path prepended to every route registered after this call.
    /// Pass <c>"/"</c> or empty to clear. Prefer <see cref="Group"/> for nested scopes.
    /// </summary>
    protected void Path(string prefix) => _pathPrefix = NormalizePrefix(prefix);

    /// <summary>
    /// Temporarily extends the path prefix, runs <paramref name="configure"/>, then restores it.
    /// </summary>
    protected void Group(string prefix, Action configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var previous = _pathPrefix;
        _pathPrefix = CombinePaths(_pathPrefix, prefix);
        try
        {
            configure();
        }
        finally
        {
            _pathPrefix = previous;
        }
    }

    protected void Before(ElsieBeforeDelegate hook) => Pipelines.AddBefore(hook);

    protected void Before(Func<ElsieContext, ElsieResult?> hook) => Pipelines.AddBefore(hook);

    protected void After(ElsieAfterDelegate hook) => Pipelines.AddAfter(hook);

    protected void After(Action<ElsieContext, ElsieResult> hook) => Pipelines.AddAfter(hook);

    protected void Get(string template, Func<ElsieResult> handler) =>
        Add("GET", template, (_, _) => Task.FromResult(handler()));

    protected void Get(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("GET", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Get(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("GET", template, handler);

    protected void Post(string template, Func<ElsieResult> handler) =>
        Add("POST", template, (_, _) => Task.FromResult(handler()));

    protected void Post(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("POST", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Post(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("POST", template, handler);

    protected void Put(string template, Func<ElsieResult> handler) =>
        Add("PUT", template, (_, _) => Task.FromResult(handler()));

    protected void Put(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("PUT", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Put(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("PUT", template, handler);

    protected void Patch(string template, Func<ElsieResult> handler) =>
        Add("PATCH", template, (_, _) => Task.FromResult(handler()));

    protected void Patch(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("PATCH", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Patch(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("PATCH", template, handler);

    protected void Delete(string template, Func<ElsieResult> handler) =>
        Add("DELETE", template, (_, _) => Task.FromResult(handler()));

    protected void Delete(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("DELETE", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Delete(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("DELETE", template, handler);

    protected void Head(string template, Func<ElsieResult> handler) =>
        Add("HEAD", template, (_, _) => Task.FromResult(handler()));

    protected void Head(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("HEAD", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Head(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("HEAD", template, handler);

    protected void Options(string template, Func<ElsieResult> handler) =>
        Add("OPTIONS", template, (_, _) => Task.FromResult(handler()));

    protected void Options(string template, Func<ElsieContext, ElsieResult> handler) =>
        Add("OPTIONS", template, (ctx, _) => Task.FromResult(handler(ctx)));

    protected void Options(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("OPTIONS", template, handler);

    private void Add(string method, string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var fullTemplate = CombinePaths(_pathPrefix, template);
        _routes.Add(new RouteDescriptor(method, fullTemplate, (ctx, ct) => handler(ctx, ct), this));
    }

    internal static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix == "/")
        {
            return string.Empty;
        }

        var normalized = RouteDescriptor.NormalizeTemplate(prefix);
        return normalized == "/" ? string.Empty : normalized;
    }

    internal static string CombinePaths(string prefix, string template)
    {
        var normalizedTemplate = RouteDescriptor.NormalizeTemplate(template);
        if (string.IsNullOrEmpty(prefix))
        {
            return normalizedTemplate;
        }

        if (normalizedTemplate == "/")
        {
            return prefix;
        }

        return prefix.TrimEnd('/') + normalizedTemplate;
    }
}
