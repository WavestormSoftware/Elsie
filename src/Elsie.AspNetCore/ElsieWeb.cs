using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.AspNetCore;

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
}
