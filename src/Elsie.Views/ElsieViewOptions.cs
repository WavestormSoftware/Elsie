namespace Elsie.Views;

/// <summary>File-based view engine options.</summary>
public sealed class ElsieViewOptions
{
    /// <summary>Directory containing view files, relative to <see cref="ContentRoot"/>.</summary>
    public string RootPath { get; set; } = "Views";

    /// <summary>View file extension, including the dot.</summary>
    public string Extension { get; set; } = ".html";

    /// <summary>Absolute content root used to resolve <see cref="RootPath"/>.</summary>
    public string ContentRoot { get; set; } = Directory.GetCurrentDirectory();
}
