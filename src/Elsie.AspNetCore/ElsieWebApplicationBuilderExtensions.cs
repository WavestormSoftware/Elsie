using Elsie.AspNetCore.Logging;
using Microsoft.AspNetCore.Builder;

namespace Elsie.AspNetCore;

public static class ElsieWebApplicationBuilderExtensions
{
    /// <summary>
    /// Registers Elsie on a <see cref="WebApplicationBuilder"/> and, by default,
    /// replaces noisy ASP.NET console logging with Elsie console logging.
    /// </summary>
    public static WebApplicationBuilder AddElsie(
        this WebApplicationBuilder builder,
        Action<ElsieOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ElsieOptions();
        configure?.Invoke(options);

        if (options.UseElsieConsoleLogging)
        {
            builder.Logging.UseElsieConsoleLogging();
        }

        builder.Services.AddElsie(o => ApplyOptions(options, o));
        return builder;
    }

    internal static void ApplyOptions(ElsieOptions source, ElsieOptions target)
    {
        target.ScanEntryAssembly = source.ScanEntryAssembly;
        target.UseElsieConsoleLogging = source.UseElsieConsoleLogging;
        target.ExceptionHandler = source.ExceptionHandler;
        target.JsonSerializerOptions = source.JsonSerializerOptions;
        target.AssembliesToScan.Clear();
        foreach (var assembly in source.AssembliesToScan)
        {
            target.AssembliesToScan.Add(assembly);
        }
    }
}
