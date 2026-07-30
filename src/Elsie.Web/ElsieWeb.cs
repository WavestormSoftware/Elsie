using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elsie.Web;

/// <summary>One-liner host helpers for Elsie on ASP.NET Core.</summary>
public static class ElsieWeb
{
    /// <summary>Build, map, and run an Elsie app with a single explicit module.</summary>
    public static void Run<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
        where TModule : ElsieModule
    {
        CreateApp<TModule>(args, configure, quietConsole).Run();
    }

    /// <summary>
    /// Build, map, and run using modules discovered via <see cref="ElsieOptions"/> scan
    /// (entry assembly by default).
    /// </summary>
    public static void Run(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
    {
        CreateApp(args, configure, quietConsole).Run();
    }

    /// <summary>Async variant of <see cref="Run{TModule}"/>.</summary>
    public static Task RunAsync<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true,
        CancellationToken cancellationToken = default)
        where TModule : ElsieModule
    {
        return RunHostAsync(CreateApp<TModule>(args, configure, quietConsole), cancellationToken);
    }

    /// <summary>Async variant of scan-based <see cref="Run"/>.</summary>
    public static Task RunAsync(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true,
        CancellationToken cancellationToken = default)
    {
        return RunHostAsync(CreateApp(args, configure, quietConsole), cancellationToken);
    }

    private static Task RunHostAsync(WebApplication app, CancellationToken cancellationToken) =>
        HostingAbstractionsHostExtensions.RunAsync(app, cancellationToken);

    /// <summary>Build and map Elsie without blocking on <c>Run</c> (tests / custom hosts).</summary>
    public static WebApplication CreateApp<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
        where TModule : ElsieModule
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.AddElsie(configure, quietConsole);
        builder.Services.AddElsieModule<TModule>();
        var app = builder.Build();
        app.MapElsie();
        return app;
    }

    /// <summary>
    /// Build and map Elsie without an explicit module type — relies on assembly scan
    /// (<see cref="ElsieOptions.ScanEntryAssembly"/> / <see cref="ElsieOptions.AssembliesToScan"/>).
    /// </summary>
    public static WebApplication CreateApp(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.AddElsie(configure, quietConsole);
        var app = builder.Build();
        app.MapElsie();
        return app;
    }
}
