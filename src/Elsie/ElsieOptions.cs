using System.Reflection;
using System.Text.Json;

namespace Elsie;

/// <summary>
/// Handles uncaught exceptions from Elsie pipelines/handlers. Return a result to write; throw to rethrow.
/// </summary>
public delegate Task<ElsieResult> ElsieExceptionHandler(
    ElsieContext context,
    Exception exception,
    CancellationToken cancellationToken);

/// <summary>
/// Predicate for a custom route constraint. Receives the raw path segment value.
/// </summary>
public delegate bool ElsieRouteConstraint(string value);

/// <summary>
/// Configuration for Elsie module discovery and runtime behavior.
/// </summary>
public sealed class ElsieOptions
{
    private readonly List<ExceptionMapping> _exceptionMaps = [];

    /// <summary>
    /// Assemblies scanned for <see cref="ElsieModule"/> subclasses when AddElsie enables scanning.
    /// </summary>
    public IList<Assembly> AssembliesToScan { get; } = new List<Assembly>();

    /// <summary>
    /// When true (default), the entry assembly is included in module scanning.
    /// </summary>
    public bool ScanEntryAssembly { get; set; } = true;

    /// <summary>
    /// JSON serializer options used by <see cref="ElsieContext.BindJsonAsync{T}"/>,
    /// <see cref="ElsieContext.Json{T}"/>, and binding helpers when no explicit options are passed.
    /// Static <see cref="ElsieResult.Json{T}"/> uses <see cref="ElsieJson.DefaultOptions"/> (framework defaults).
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Handler for exceptions thrown by before hooks, handlers, or after hooks
    /// after typed <see cref="MapException{TException}(Func{ElsieContext, TException, ElsieResult})"/> maps and module <c>OnError</c>.
    /// Defaults to a safe 500 problem without exception detail. Set to <c>null</c> to rethrow to the host pipeline.
    /// </summary>
    public ElsieExceptionHandler? ExceptionHandler { get; set; } = DefaultExceptionHandler;

    private static Task<ElsieResult> DefaultExceptionHandler(ElsieContext ctx, Exception ex, CancellationToken ct)
        => Task.FromResult(ElsieResult.Problem(500, "Internal Server Error"));

    /// <summary>
    /// When true (default), a HEAD request that has no explicit HEAD route falls back to a matching GET handler.
    /// </summary>
    public bool ImplicitHead { get; set; } = true;

    /// <summary>
    /// Max request body size accepted by <see cref="ElsieContext.BindJsonAsync{T}"/> / form binding (default 4 MB).
    /// </summary>
    public long MaxBindBodySize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Custom route constraints keyed by name (case-insensitive). Built-ins cannot be overwritten.
    /// Example: <c>options.RouteConstraints["slug"] = v =&gt; v.All(char.IsLetterOrDigit);</c>
    /// </summary>
    public IDictionary<string, ElsieRouteConstraint> RouteConstraints { get; } =
        new Dictionary<string, ElsieRouteConstraint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map <typeparamref name="TException"/> (and assignable subclasses) to a result.
    /// First matching registration wins (registration order). Checked before module OnError and <see cref="ExceptionHandler"/>.
    /// </summary>
    public ElsieOptions MapException<TException>(Func<ElsieContext, TException, ElsieResult> handler)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(handler);
        _exceptionMaps.Add(new ExceptionMapping(
            typeof(TException),
            (ctx, ex, _) => Task.FromResult(handler(ctx, (TException)ex))));
        return this;
    }

    /// <summary>Async variant of <see cref="MapException{TException}(Func{ElsieContext, TException, ElsieResult})"/>.</summary>
    public ElsieOptions MapException<TException>(
        Func<ElsieContext, TException, CancellationToken, Task<ElsieResult>> handler)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(handler);
        _exceptionMaps.Add(new ExceptionMapping(
            typeof(TException),
            (ctx, ex, ct) => handler(ctx, (TException)ex, ct)));
        return this;
    }

    internal async Task<ElsieResult?> TryMapExceptionAsync(
        ElsieContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        foreach (var map in _exceptionMaps)
        {
            if (!map.ExceptionType.IsAssignableFrom(exception.GetType()))
            {
                continue;
            }

            return await map.Handler(context, exception, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private sealed class ExceptionMapping
    {
        public ExceptionMapping(
            Type exceptionType,
            Func<ElsieContext, Exception, CancellationToken, Task<ElsieResult>> handler)
        {
            ExceptionType = exceptionType;
            Handler = handler;
        }

        public Type ExceptionType { get; }
        public Func<ElsieContext, Exception, CancellationToken, Task<ElsieResult>> Handler { get; }
    }
}
