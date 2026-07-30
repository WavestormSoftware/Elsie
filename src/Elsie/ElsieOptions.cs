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
    /// <summary>
    /// Assemblies scanned for <see cref="ElsieModule"/> subclasses when AddElsie enables scanning.
    /// </summary>
    public IList<Assembly> AssembliesToScan { get; } = new List<Assembly>();

    /// <summary>
    /// When true (default), the entry assembly is included in module scanning.
    /// </summary>
    public bool ScanEntryAssembly { get; set; } = true;

    /// <summary>
    /// JSON serializer options used by <see cref="ElsieContext.ReadJsonAsync{T}"/>,
    /// <see cref="ElsieContext.Json{T}"/>, and binding helpers when no explicit options are passed.
    /// Static <see cref="ElsieResult.Json{T}"/> uses <see cref="ElsieJson.DefaultOptions"/> (framework defaults).
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Optional handler for exceptions thrown by before hooks or route handlers.
    /// When null, exceptions propagate to the host pipeline.
    /// </summary>
    public ElsieExceptionHandler? ExceptionHandler { get; set; }

    /// <summary>
    /// When true (default), a HEAD request that has no explicit HEAD route falls back to a matching GET handler.
    /// </summary>
    public bool ImplicitHead { get; set; } = true;

    /// <summary>
    /// Custom route constraints keyed by name (case-insensitive). Built-ins cannot be overwritten.
    /// Example: <c>options.RouteConstraints["slug"] = v =&gt; v.All(char.IsLetterOrDigit);</c>
    /// </summary>
    public IDictionary<string, ElsieRouteConstraint> RouteConstraints { get; } =
        new Dictionary<string, ElsieRouteConstraint>(StringComparer.OrdinalIgnoreCase);
}
