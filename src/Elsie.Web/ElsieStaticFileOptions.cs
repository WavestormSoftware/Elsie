namespace Elsie.Web;

/// <summary>Options for <c>MapElsieStaticFiles</c>.</summary>
public sealed class ElsieStaticFileOptions
{
    /// <summary>Default document name when the request path maps to a directory (default <c>index.html</c>).</summary>
    public string DefaultFileName { get; set; } = "index.html";

    /// <summary>When true (default), serve <see cref="DefaultFileName"/> for directory requests.</summary>
    public bool ServeDefaultFile { get; set; } = true;

    /// <summary>
    /// Optional content-type overrides by extension (including leading dot, e.g. <c>.json</c>).
    /// Merged over the built-in map.
    /// </summary>
    public Dictionary<string, string> ContentTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
}
