namespace Elsie.Views;

/// <summary>File-based Fluid (Liquid) view engine options.</summary>
public sealed class ElsieViewOptions
{
    /// <summary>Directory containing view files, relative to <see cref="ContentRoot"/>.</summary>
    public string RootPath { get; set; } = "Views";

    /// <summary>Absolute content root used to resolve <see cref="RootPath"/>.</summary>
    public string ContentRoot { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>Default template extension when <c>viewName</c> has none (default <c>.liquid</c>).</summary>
    public string Extension { get; set; } = ".liquid";

    /// <summary>
    /// When true (default), re-parse templates when the file mtime changes (dev reload).
    /// Set false in production for a pure path-keyed cache.
    /// </summary>
    public bool ReloadOnChange { get; set; } = true;
}
