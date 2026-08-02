using Elsie.Middleware;

namespace Elsie.Web.Hosting;

/// <summary>
/// Terminal static-file middleware: when a GET/HEAD request maps to a file under the static
/// root it short-circuits with the file result (ETag/304/Range/streaming), before routes run.
/// This is the middleware replacement for the legacy pre-dispatch static serving in
/// <c>HostDispatch</c>. Registered automatically by <see cref="ElsieApp.StaticFiles"/>.
/// </summary>
public sealed class StaticFileMiddleware : IElsieMiddleware
{
    private readonly ElsieStaticFileOptions _options;
    private readonly string _contentRoot;

    /// <summary>Create the middleware over static-file options and the app content root.</summary>
    public StaticFileMiddleware(ElsieStaticFileOptions options, string contentRoot)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contentRoot = contentRoot ?? throw new ArgumentNullException(nameof(contentRoot));
    }

    /// <inheritdoc />
    public Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in context.Request.Headers)
        {
            headerMap[k] = v;
        }

        var file = StaticFileHandler.TryServe(
            context.Request.Method,
            context.Request.Path,
            headerMap,
            _options,
            _contentRoot);
        if (file is not null)
        {
            context.Result = file;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
