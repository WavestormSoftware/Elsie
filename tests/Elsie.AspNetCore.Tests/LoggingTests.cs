using System.Text;
using Elsie.AspNetCore.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elsie.AspNetCore.Tests;

public class LoggingTests
{
    [Fact]
    public void Console_logger_writes_information()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        using var provider = new ElsieConsoleLoggerProvider(writer, LogLevel.Information);
        var logger = provider.CreateLogger("Elsie.AspNetCore.ElsieMiddleware");

        logger.LogInformation("GET /hello → 200 1ms");

        var text = sb.ToString();
        Assert.Contains("elsie", text, StringComparison.Ordinal);
        Assert.Contains("GET /hello → 200 1ms", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Console_logger_skips_debug_when_min_information()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        using var provider = new ElsieConsoleLoggerProvider(writer, LogLevel.Information);
        var logger = provider.CreateLogger("Elsie.Test");

        logger.LogDebug("nope");
        Assert.Equal(string.Empty, sb.ToString());
    }
}
