using Elsie.Pipelines;
using Elsie.Routing;

namespace Elsie;

/// <summary>
/// Base type for feature modules that register HTTP routes.
/// </summary>
public abstract class ElsieModule
{
    private readonly List<RouteDescriptor> _routes = [];

    /// <summary>Module-scoped before/after hooks.</summary>
    public ElsiePipelines Pipelines { get; } = new();

    public IReadOnlyList<RouteDescriptor> Routes => _routes;

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

    protected void Head(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("HEAD", template, handler);

    protected void Options(string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler) =>
        Add("OPTIONS", template, handler);

    private void Add(string method, string template, Func<ElsieContext, CancellationToken, Task<ElsieResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _routes.Add(new RouteDescriptor(method, template, (ctx, ct) => handler(ctx, ct), this));
    }
}
