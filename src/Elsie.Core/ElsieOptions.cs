using System.Net;
using System.Reflection;
using System.Text;
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
    /// JSON serializer options used by <see cref="ElsieContext.BindJsonAsync{T}"/>,
    /// <see cref="ElsieContext.Json{T}"/>, and binding helpers when no explicit options are passed.
    /// Static <see cref="ElsieResult.Json{T}"/> uses <see cref="ElsieJson.DefaultOptions"/> (framework defaults).
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// When true, the default exception handler returns an HTML page with exception type/message/stack
    /// (escaped). Keep false in production. Host Generic-Host integration enables this in Development.
    /// </summary>
    public bool ShowExceptionDetails { get; set; }

    /// <summary>
    /// Handler for uncaught exceptions thrown by middleware or route handlers.
    /// Applied by the terminal <see cref="Middleware.ElsieExceptionHandlerMiddleware"/> after
    /// <see cref="ElsieRequestException"/> mapping. Defaults to a safe 500 problem without
    /// exception detail. Set to <c>null</c> to rethrow to the host pipeline.
    /// </summary>
    public ElsieExceptionHandler? ExceptionHandler { get; set; }

    /// <summary>Creates options with the built-in default exception handler.</summary>
    public ElsieOptions()
    {
        ExceptionHandler = DefaultExceptionHandler;
    }

    private Task<ElsieResult> DefaultExceptionHandler(ElsieContext ctx, Exception ex, CancellationToken ct)
    {
        if (!ShowExceptionDetails)
        {
            return Task.FromResult(ctx.Problem(500, "Internal Server Error"));
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><title>Server Error</title>");
        sb.Append("<style>body{font-family:ui-monospace,monospace;margin:2rem;background:#1e1e1e;color:#f3f3f3}");
        sb.Append("h1{color:#f14c4c}pre{white-space:pre-wrap;background:#111;padding:1rem;border-radius:6px}</style></head><body>");
        sb.Append("<h1>Unhandled exception</h1>");
        sb.Append("<p><strong>").Append(WebUtility.HtmlEncode(ex.GetType().FullName)).Append("</strong></p>");
        sb.Append("<p>").Append(WebUtility.HtmlEncode(ex.Message)).Append("</p>");
        if (!string.IsNullOrEmpty(ctx.Request.TraceIdentifier))
        {
            sb.Append("<p>traceId: ").Append(WebUtility.HtmlEncode(ctx.Request.TraceIdentifier)).Append("</p>");
        }

        sb.Append("<pre>").Append(WebUtility.HtmlEncode(ex.ToString())).Append("</pre>");
        sb.Append("</body></html>");
        return Task.FromResult(ElsieResult.Html(sb.ToString(), statusCode: 500));
    }

    /// <summary>
    /// When true (default), a HEAD request that has no explicit HEAD route falls back to a matching GET handler.
    /// </summary>
    public bool ImplicitHead { get; set; } = true;

    /// <summary>
    /// Max request body size accepted by <see cref="ElsieContext.BindJsonAsync{T}"/> / form binding (default 4 MB).
    /// </summary>
    public long MaxBindBodySize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Max size of a single multipart file part (default 20 MB).</summary>
    public long MaxFormFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Max number of multipart file parts accepted (default 20).</summary>
    public int MaxFormFiles { get; set; } = 20;

    /// <summary>
    /// Multipart file parts at or below this size stay in memory; larger parts spill to a temp file
    /// (deleted when the <see cref="ElsieFormFile"/> / form collection is disposed). Default 1 MiB.
    /// </summary>
    public long MultipartMemoryThresholdBytes { get; set; } = 1L * 1024 * 1024;

    /// <summary>
    /// Custom route constraints keyed by name (case-insensitive). Built-ins cannot be overwritten.
    /// Example: <c>options.RouteConstraints["slug"] = v =&gt; v.All(char.IsLetterOrDigit);</c>
    /// </summary>
    public IDictionary<string, ElsieRouteConstraint> RouteConstraints { get; } =
        new Dictionary<string, ElsieRouteConstraint>(StringComparer.OrdinalIgnoreCase);

}
