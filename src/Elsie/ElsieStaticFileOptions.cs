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
}
