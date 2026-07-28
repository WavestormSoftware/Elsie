using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Elsie.AspNetCore;

public static class ElsieWebApplicationBuilderExtensions
{
    /// <summary>
    /// Registers Elsie on a <see cref="WebApplicationBuilder"/>.
    /// When <paramref name="quietConsole"/> is true (default), clears noisy framework
    /// console logs and keeps a single-line console logger.
    /// </summary>
    public static WebApplicationBuilder AddElsie(
        this WebApplicationBuilder builder,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (quietConsole)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddFilter("Microsoft", LogLevel.None);
            builder.Logging.AddFilter("System", LogLevel.None);
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }

        builder.Services.AddElsie(configure);
        return builder;
    }
}
