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

    /// <summary>Optional module-level exception mapper (after options.MapException, before global ExceptionHandler).</summary>
    public ElsieExceptionHandler? OnErrorHandler { get; private set; }

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

    protected void After(Func<ElsieContext, ElsieResult, ElsieResult> hook) => Pipelines.AddAfter(hook);

    /// <summary>Module exception handler. Return a result to handle; throw to continue the error chain.</summary>
    protected void OnError(ElsieExceptionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        OnErrorHandler = handler;
    }

    protected void OnError(Func<ElsieContext, Exception, ElsieResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        OnErrorHandler = (ctx, ex, _) => Task.FromResult(handler(ctx, ex));
    }

    protected RouteBuilder Get(string template, Func<ElsieResult> handler) =>
        Map("GET", template, handler);

    protected RouteBuilder Get(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("GET", template, handler);

    protected RouteBuilder Get(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("GET", template, handler);

    protected RouteBuilder Post(string template, Func<ElsieResult> handler) =>
        Map("POST", template, handler);

    protected RouteBuilder Post(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("POST", template, handler);

    protected RouteBuilder Post(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("POST", template, handler);

    protected RouteBuilder Put(string template, Func<ElsieResult> handler) =>
        Map("PUT", template, handler);

    protected RouteBuilder Put(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("PUT", template, handler);

    protected RouteBuilder Put(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("PUT", template, handler);

    protected RouteBuilder Patch(string template, Func<ElsieResult> handler) =>
        Map("PATCH", template, handler);

    protected RouteBuilder Patch(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("PATCH", template, handler);

    protected RouteBuilder Patch(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("PATCH", template, handler);

    protected RouteBuilder Delete(string template, Func<ElsieResult> handler) =>
        Map("DELETE", template, handler);

    protected RouteBuilder Delete(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("DELETE", template, handler);

    protected RouteBuilder Delete(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("DELETE", template, handler);

    protected RouteBuilder Head(string template, Func<ElsieResult> handler) =>
        Map("HEAD", template, handler);

    protected RouteBuilder Head(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("HEAD", template, handler);

    protected RouteBuilder Head(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("HEAD", template, handler);

    protected RouteBuilder Options(string template, Func<ElsieResult> handler) =>
        Map("OPTIONS", template, handler);

    protected RouteBuilder Options(string template, Func<ElsieContext, ElsieResult> handler) =>
        Map("OPTIONS", template, handler);

    protected RouteBuilder Options(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Map("OPTIONS", template, handler);

    /// <summary>Register a handler for an arbitrary HTTP method.</summary>
    protected RouteBuilder Map(string method, string template, Func<ElsieResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Map(method, template, (_, _) => Task.FromResult(handler()));
    }

    /// <summary>Register a handler for an arbitrary HTTP method.</summary>
    protected RouteBuilder Map(string method, string template, Func<ElsieContext, ElsieResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Map(method, template, (ctx, _) => Task.FromResult(handler(ctx)));
    }

    /// <summary>Register a handler for an arbitrary HTTP method.</summary>
    protected RouteBuilder Map(string method, string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        var fullTemplate = CombinePaths(_pathPrefix, template);
        var descriptor = new RouteDescriptor(method, fullTemplate, (ctx, ct) => handler(ctx, ct), this);
        _routes.Add(descriptor);
        return new RouteBuilder(descriptor);
    }

    /// <summary>Register the same handler for multiple HTTP methods.</summary>
    protected RouteBuilder[] MapMethods(IEnumerable<string> methods, string template, Func<ElsieResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return MapMethods(methods, template, (_, _) => Task.FromResult(handler()));
    }

    /// <summary>Register the same handler for multiple HTTP methods.</summary>
    protected RouteBuilder[] MapMethods(IEnumerable<string> methods, string template, Func<ElsieContext, ElsieResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return MapMethods(methods, template, (ctx, _) => Task.FromResult(handler(ctx)));
    }

    /// <summary>Register the same handler for multiple HTTP methods.</summary>
    protected RouteBuilder[] MapMethods(
        IEnumerable<string> methods,
        string template,
        Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(handler);
        return methods.Select(m => Map(m, template, handler)).ToArray();
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
