namespace Elsie.Web;

/// <summary>Serve files from a root directory under an optional request path prefix.</summary>
public sealed class ElsieStaticFileOptions
{
    /// <summary>Physical directory (default <c>wwwroot</c> under content root).</summary>
    public string Root { get; set; } = "wwwroot";

    /// <summary>URL prefix (e.g. <c>/assets</c>). Empty or <c>/</c> serves at site root.</summary>
    public string RequestPath { get; set; } = "";

    /// <summary>Default cache max-age; null omits Cache-Control.</summary>
    public TimeSpan? MaxAge { get; set; }

    public string? ContentRoot { get; set; }

    /// <summary>
    /// Serve sibling <c>.br</c> / <c>.gz</c> files when the client accepts the encoding and the
    /// variant is not older than the original. Default true. Range requests always get the
    /// original bytes.
    /// </summary>
    public bool EnablePrecompressedFiles { get; set; } = true;

    /// <summary>
    /// Send <c>Cache-Control: public, max-age=…, immutable</c> for content-addressed file names
    /// (e.g. <c>app.4f3a9c1d.js</c>). Default true.
    /// </summary>
    public bool ImmutableFingerprintedAssets { get; set; } = true;

    /// <summary>Max-age used for fingerprinted assets. Default one year.</summary>
    public TimeSpan ImmutableMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// SPA fallback document relative to <see cref="Root"/> (e.g. <c>index.html</c>). Served with
    /// <c>no-cache</c> for extension-less GET/HEAD requests that do not match a file.
    /// </summary>
    public string? SpaFallback { get; set; }
}
