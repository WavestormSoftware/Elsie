namespace Elsie.Web;

/// <summary>One-liner host helpers — thin wrappers over <see cref="ElsieApp"/>.</summary>
public static class ElsieWeb
{
    /// <summary>Build, map, and run an Elsie app with a single explicit module.</summary>
    public static void Run<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
        where TModule : ElsieModule =>
        ElsieApp.Run<TModule>(args, configure, quietConsole);

    /// <summary>
    /// Build, map, and run using modules discovered via <see cref="ElsieOptions"/> scan
    /// (entry assembly by default).
    /// </summary>
    public static void Run(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true) =>
        ElsieApp.Run(args, configure, quietConsole);

    /// <summary>Async variant of <see cref="Run{TModule}"/>.</summary>
    public static Task RunAsync<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true,
        CancellationToken cancellationToken = default)
        where TModule : ElsieModule =>
        ElsieApp.RunAsync<TModule>(args, configure, quietConsole, cancellationToken);

    /// <summary>Async variant of scan-based <see cref="Run"/>.</summary>
    public static Task RunAsync(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true,
        CancellationToken cancellationToken = default) =>
        ElsieApp.RunAsync(args, configure, quietConsole, cancellationToken);
}
