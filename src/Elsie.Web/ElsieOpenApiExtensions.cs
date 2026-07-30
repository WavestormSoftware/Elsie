using System.Text;
using Elsie.OpenApi;
using Elsie.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web;

/// <summary>Map OpenAPI JSON (and optional Scalar CDN page) for Elsie routes.</summary>
public static class ElsieOpenApiExtensions
{
    /// <summary>
    /// Serves OpenAPI 3 JSON from the Elsie <see cref="RouteTable"/> at
    /// <see cref="ElsieOpenApiOptions.DocumentPath"/> (default <c>/openapi.json</c>).
    /// Optionally maps a Scalar CDN HTML page at <see cref="ElsieOpenApiOptions.UiPath"/>.
    /// </summary>
    public static WebApplication MapElsieOpenApi(
        this WebApplication app,
        Action<ElsieOpenApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new ElsieOpenApiOptions();
        configure?.Invoke(options);

        var documentPath = NormalizePath(options.DocumentPath);
        // RouteTable is singleton; bake once at map time (MapElsie warms the table).
        var table = app.Services.GetRequiredService<RouteTable>();
        var json = ElsieOpenApiDocument.ToUtf8Json(table, options.Info);
        app.MapGet(documentPath, () => Results.Bytes(json, "application/json; charset=utf-8"))
            .WithDisplayName("Elsie OpenAPI");

        if (!string.IsNullOrWhiteSpace(options.UiPath))
        {
            var uiPath = NormalizePath(options.UiPath);
            var html = BuildScalarHtml(documentPath, options.Info.Title);
            var bytes = Encoding.UTF8.GetBytes(html);
            app.MapGet(uiPath, () => Results.Bytes(bytes, "text/html; charset=utf-8"))
                .WithDisplayName("Elsie OpenAPI UI");
        }

        return app;
    }

    private static string BuildScalarHtml(string documentPath, string title)
    {
        // Copy-paste-friendly Scalar CDN page (no NuGet UI dependency).
        return
            $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
              <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@scalar/api-reference" />
            </head>
            <body>
              <script id="api-reference" data-url="{{documentPath}}"></script>
              <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
            </body>
            </html>
            """;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.StartsWith('/') ? path : "/" + path;
    }
}

/// <summary>Options for <see cref="ElsieOpenApiExtensions.MapElsieOpenApi"/>.</summary>
public sealed class ElsieOpenApiOptions
{
    public ElsieOpenApiInfo Info { get; set; } = new();
    public string DocumentPath { get; set; } = "/openapi.json";

    /// <summary>
    /// When set (e.g. <c>/scalar</c>), maps a Scalar CDN HTML page pointing at <see cref="DocumentPath"/>.
    /// Null/empty = JSON only (default).
    /// </summary>
    public string? UiPath { get; set; }
}
