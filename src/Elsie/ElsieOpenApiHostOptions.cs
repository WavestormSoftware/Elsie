using Elsie.OpenApi;

namespace Elsie.Web;

/// <summary>Host options for serving OpenAPI JSON and optional Scalar UI.</summary>
public sealed class ElsieOpenApiHostOptions
{
    public ElsieOpenApiInfo Info { get; set; } = new();
    public string DocumentPath { get; set; } = "/openapi.json";

    /// <summary>When set (e.g. <c>/scalar</c>), serves an API reference HTML page.</summary>
    public string? UiPath { get; set; }

    /// <summary>
    /// When true (default), the UI page loads the Scalar standalone bundle from CDN.
    /// When false, the bundled offline Scalar UI is served from embedded resources
    /// (no external network at runtime).
    /// </summary>
    public bool UseScalarCdn { get; set; } = true;

    /// <summary>Optional prebuilt OpenAPI JSON bytes (skips reflection document build).</summary>
    public byte[]? PrebuiltDocumentUtf8 { get; set; }

    /// <summary>Optional path to a prebuilt openapi.json file.</summary>
    public string? PrebuiltDocumentPath { get; set; }
}
