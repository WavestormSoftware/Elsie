using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.AspNetCore;

/// <summary>One-liner host helpers for Elsie on ASP.NET Core.</summary>
public static class ElsieWeb
{
    /// <summary>Build, map, and run an Elsie app with a single explicit module.</summary>
    public static void Run<TModule>(string[]? args = null, Action<ElsieOptions>? configure = null)
        where TModule : ElsieModule
    {
        var app = CreateApp<TModule>(args, configure);
        app.Run();
    }

    /// <summary>Build, map, and run; modules come from entry-assembly scan (default).</summary>
    public static void Run(string[]? args = null, Action<ElsieOptions>? configure = null)
    {
        var app = CreateApp(args, configure);
        app.Run();
    }

    /// <summary>Build and map Elsie without blocking on <c>Run</c> (tests / custom hosts).</summary>
    public static WebApplication CreateApp<TModule>(string[]? args = null, Action<ElsieOptions>? configure = null)
        where TModule : ElsieModule
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.AddElsie(configure);
        builder.Services.AddElsieModule<TModule>();
        var app = builder.Build();
        app.MapElsie();
        return app;
    }

    /// <summary>Build and map using entry-assembly module scan.</summary>
    public static WebApplication CreateApp(string[]? args = null, Action<ElsieOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.AddElsie(configure);
        var app = builder.Build();
        app.MapElsie();
        return app;
    }
}
