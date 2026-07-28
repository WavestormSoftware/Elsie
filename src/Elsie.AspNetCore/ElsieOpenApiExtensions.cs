using Elsie.OpenApi;
using Elsie.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.AspNetCore;

/// <summary>Map OpenAPI JSON for Elsie routes.</summary>
public static class ElsieOpenApiExtensions
{
    /// <summary>
    /// Serves OpenAPI 3 JSON from the Elsie <see cref="RouteTable"/> at
    /// <see cref="ElsieOpenApiOptions.DocumentPath"/> (default <c>/openapi.json</c>).
    /// Wire Scalar/Swagger UI yourself against that path if wanted.
    /// </summary>
    public static WebApplication MapElsieOpenApi(
        this WebApplication app,
        Action<ElsieOpenApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new ElsieOpenApiOptions();
        configure?.Invoke(options);

        var documentPath = NormalizePath(options.DocumentPath);
        app.MapGet(documentPath, (HttpContext http) =>
        {
            var table = http.RequestServices.GetRequiredService<RouteTable>();
            var json = ElsieOpenApiDocument.ToUtf8Json(table, options.Info);
            return Results.Bytes(json, "application/json; charset=utf-8");
        }).WithDisplayName("Elsie OpenAPI");

        return app;
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
}
