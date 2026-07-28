using Microsoft.Extensions.Logging;

namespace Elsie.AspNetCore.Logging;

public static class ElsieLoggingBuilderExtensions
{
    /// <summary>Adds the Elsie console logger provider.</summary>
    public static ILoggingBuilder AddElsieConsole(this ILoggingBuilder builder, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddProvider(new ElsieConsoleLoggerProvider(output));
        return builder;
    }

    /// <summary>
    /// Clears default providers and installs quiet Elsie-oriented filters + console logger.
    /// </summary>
    public static ILoggingBuilder UseElsieConsoleLogging(this ILoggingBuilder builder, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ClearProviders();
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddFilter("Microsoft", LogLevel.None);
        builder.AddFilter("System", LogLevel.None);
        builder.AddFilter("Elsie", LogLevel.Information);
        builder.AddElsieConsole(output);
        return builder;
    }
}
