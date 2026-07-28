using Elsie.OpenApi;
using Elsie.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;

namespace Elsie.AspNetCore;

/// <summary>Map OpenAPI JSON (and optional Scalar UI) for Elsie routes.</summary>
public static class ElsieOpenApiExtensions
{
    /// <summary>
    /// Serves OpenAPI 3 JSON from the Elsie <see cref="RouteTable"/>.
    /// When <see cref="ElsieOpenApiOptions.EnableScalar"/> is true (default), also maps Scalar UI.
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
            var info = new ElsieOpenApiInfo
            {
                Title = options.Title,
                Version = options.Version,
                Description = options.Description
            };
            var json = ElsieOpenApiDocument.ToUtf8Json(table, info);
            return Results.Bytes(json, "application/json; charset=utf-8");
        }).WithDisplayName("Elsie OpenAPI");

        if (options.EnableScalar)
        {
            var scalarPath = NormalizePath(options.ScalarPath).TrimStart('/');
            app.MapScalarApiReference(scalarPath, scalar =>
            {
                scalar.WithOpenApiRoutePattern(documentPath);
                scalar.WithTitle(options.Title);
            });
        }

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
    public string Title { get; set; } = "Elsie API";
    public string Version { get; set; } = "v1";
    public string? Description { get; set; }
    public string DocumentPath { get; set; } = "/openapi.json";
    public bool EnableScalar { get; set; } = true;
    public string ScalarPath { get; set; } = "/scalar";
}
