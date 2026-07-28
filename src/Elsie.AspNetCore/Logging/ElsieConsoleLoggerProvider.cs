using Microsoft.Extensions.Logging;

namespace Elsie.AspNetCore.Logging;

/// <summary>Simple stdout logger used by Elsie app hosts.</summary>
public sealed class ElsieConsoleLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _output;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();

    public ElsieConsoleLoggerProvider(TextWriter? output = null, LogLevel minLevel = LogLevel.Information)
    {
        _output = output ?? Console.Out;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) =>
        new ElsieConsoleLogger(categoryName, _output, _minLevel, _gate);

    public void Dispose()
    {
    }
}

internal sealed class ElsieConsoleLogger : ILogger
{
    private readonly string _label;
    private readonly TextWriter _output;
    private readonly LogLevel _minLevel;
    private readonly object _gate;

    public ElsieConsoleLogger(string categoryName, TextWriter output, LogLevel minLevel, object gate)
    {
        _label = ShortLabel(categoryName);
        _output = output;
        _minLevel = minLevel;
        _gate = gate;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var line = exception is null
            ? $"{_label}  {message}"
            : $"{_label}  {message}{Environment.NewLine}{exception}";

        lock (_gate)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }

    private static string ShortLabel(string categoryName)
    {
        if (categoryName.StartsWith("Elsie", StringComparison.Ordinal))
        {
            return "elsie";
        }

        var lastDot = categoryName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < categoryName.Length - 1
            ? categoryName[(lastDot + 1)..]
            : categoryName;
    }
}
