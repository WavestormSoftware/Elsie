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
    /// <see cref="ElsieContext.Json{T}"/>, and <see cref="ElsieResult.Json{T}"/> defaults
    /// when no explicit options are passed.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Optional handler for exceptions thrown by before hooks or route handlers.
    /// When null, exceptions propagate to the ASP.NET Core pipeline.
    /// </summary>
    public ElsieExceptionHandler? ExceptionHandler { get; set; }

    /// <summary>
    /// When true (default), <c>WebApplicationBuilder.AddElsie</c> / <c>ElsieWeb.Run</c>
    /// replace default ASP.NET console logging with Elsie console logging.
    /// Ignored by <see cref="ElsieServiceCollectionExtensions.AddElsie"/> (IServiceCollection-only).
    /// </summary>
    public bool UseElsieConsoleLogging { get; set; } = true;
}
