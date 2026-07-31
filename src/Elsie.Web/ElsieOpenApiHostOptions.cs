using Elsie.OpenApi;

namespace Elsie.Web;

/// <summary>Host options for serving OpenAPI JSON and optional Scalar UI.</summary>
public sealed class ElsieOpenApiHostOptions
{
    public ElsieOpenApiInfo Info { get; set; } = new();
    public string DocumentPath { get; set; } = "/openapi.json";

    /// <summary>When set (e.g. <c>/scalar</c>), serves a Scalar CDN HTML page.</summary>
    public string? UiPath { get; set; }
}
