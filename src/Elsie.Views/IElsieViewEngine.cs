namespace Elsie.Views;

/// <summary>Host-agnostic view rendering seam.</summary>
public interface IElsieViewEngine
{
    /// <summary>
    /// Render <paramref name="viewName"/> (path under the views root, extension optional)
    /// with <paramref name="model"/> and optional ambient request values.
    /// </summary>
    Task<string> RenderAsync(
        string viewName,
        object? model,
        ElsieViewAmbient? ambient = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Request ambient values exposed to templates as <c>Request</c>.</summary>
public sealed class ElsieViewAmbient
{
    public string Path { get; init; } = "/";
    public string QueryString { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public string? Scheme { get; init; }
    public string? Host { get; init; }
}
