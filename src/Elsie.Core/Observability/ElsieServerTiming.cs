using System.Diagnostics;
using System.Globalization;
using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Observability;

/// <summary>
/// Emits the <c>Server-Timing</c> response header (RFC 9110 §10.2.1 / W3C Server Timing) for the
/// whole request plus any metrics the app records with
/// <see cref="ElsieServerTiming.Add(ElsieContext, string, double, string?)"/>.
/// </summary>
public sealed class ElsieServerTimingMiddleware : IElsieMiddleware
{
    private readonly string _totalMetricName;

    public ElsieServerTimingMiddleware(string totalMetricName = "total")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(totalMetricName);
        _totalMetricName = totalMetricName;
    }

    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var start = Stopwatch.GetTimestamp();
        await next(context).ConfigureAwait(false);

        if (context.Result is not { } result)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var entries = new List<string>(2)
        {
            Format(_totalMetricName, elapsed, description: null)
        };

        if (context.Request.Items.TryGetValue(MetricsKey, out var metric) && metric is List<ElsieServerTimingMetric> metrics)
        {
            foreach (var item in metrics)
            {
                entries.Add(Format(item.Name, item.DurationMs, item.Description));
            }
        }

        context.Result = result.WithHeader("Server-Timing", string.Join(", ", entries));
    }

    internal static string Format(string name, double durationMs, string? description)
    {
        var value = $"{name};dur={durationMs.ToString("0.0#", CultureInfo.InvariantCulture)}";
        return description is null ? value : $"{value};desc=\"{description.Replace("\"", "'", StringComparison.Ordinal)}\"";
    }

    internal static readonly object MetricsKey = new();
}

/// <summary>A custom server-timing metric.</summary>
public readonly record struct ElsieServerTimingMetric(string Name, double DurationMs, string? Description);

/// <summary>Records custom <c>Server-Timing</c> metrics for the current request.</summary>
public static class ElsieServerTiming
{
    /// <summary>Add a metric (database time, cache time, …) to this request's Server-Timing header.</summary>
    public static void Add(ElsieContext context, string name, double durationMs, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!context.Request.Items.TryGetValue(ElsieServerTimingMiddleware.MetricsKey, out var existing)
            || existing is not List<ElsieServerTimingMetric> list)
        {
            list = [];
            context.Request.Items[ElsieServerTimingMiddleware.MetricsKey] = list;
        }

        list.Add(new ElsieServerTimingMetric(name, durationMs, description));
    }

    /// <summary>Measure <paramref name="work"/> and record it as a Server-Timing metric.</summary>
    public static async Task<T> MeasureAsync<T>(
        ElsieContext context,
        string name,
        Func<Task<T>> work,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

        var start = Stopwatch.GetTimestamp();
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Add(context, name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, description);
        }
    }
}

/// <summary>Registration for <see cref="ElsieServerTimingMiddleware"/>.</summary>
public static class ElsieServerTimingExtensions
{
    public static IServiceCollection AddElsieServerTiming(
        this IServiceCollection services,
        Action<ElsieServerTimingMiddlewareOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieServerTimingMiddlewareOptions();
        configure?.Invoke(options);
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use(new ElsieServerTimingMiddleware(options.TotalMetricName))));
        return services;
    }
}

/// <summary>Options for the server-timing middleware.</summary>
public sealed class ElsieServerTimingMiddlewareOptions
{
    /// <summary>Name used for the total request duration. Default <c>total</c>.</summary>
    public string TotalMetricName { get; set; } = "total";
}
