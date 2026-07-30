using System.Diagnostics;

namespace Elsie.HealthChecks;

/// <summary>Executes registered health checks and builds aggregate reports.</summary>
public sealed class ElsieHealthCheckRunner
{
    private readonly ElsieHealthCheckOptions _options;

    public ElsieHealthCheckRunner(ElsieHealthCheckOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ElsieHealthCheckOptions Options => _options;

    public async Task<ElsieHealthReport> RunAsync(
        IServiceProvider services,
        Func<ElsieHealthCheckRegistration, bool>? predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var selected = predicate is null
            ? _options.Checks
            : _options.Checks.Where(predicate).ToArray();

        var total = Stopwatch.StartNew();
        var entries = new Dictionary<string, ElsieHealthReportEntry>(StringComparer.OrdinalIgnoreCase);
        var aggregate = ElsieHealthStatus.Healthy;

        foreach (var registration in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            ElsieHealthCheckResult result;
            try
            {
                result = await registration.Check(services, cancellationToken).ConfigureAwait(false)
                    ?? ElsieHealthCheckResult.Unhealthy("Check returned null.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = ElsieHealthCheckResult.Unhealthy(ex.Message);
            }

            sw.Stop();
            entries[registration.Name] = new ElsieHealthReportEntry(
                result.Status,
                result.Description,
                sw.Elapsed,
                result.Data);

            if (result.Status > aggregate)
            {
                aggregate = result.Status;
            }
        }

        total.Stop();
        return new ElsieHealthReport(aggregate, total.Elapsed, entries);
    }

    /// <summary>
    /// Maps a report to JSON. Healthy/Degraded → 200; Unhealthy → 503.
    /// </summary>
    public static ElsieResult ToResult(ElsieHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var statusCode = report.Status == ElsieHealthStatus.Unhealthy ? 503 : 200;
        var payload = new HealthPayload(
            Status: report.Status.ToString(),
            TotalDuration: report.TotalDuration,
            Entries: report.Entries.ToDictionary(
                static kv => kv.Key,
                static kv => new EntryPayload(
                    Status: kv.Value.Status.ToString(),
                    Description: kv.Value.Description,
                    Duration: kv.Value.Duration,
                    Data: kv.Value.Data),
                StringComparer.OrdinalIgnoreCase));

        return ElsieResult.Json(payload, statusCode);
    }

    private sealed record HealthPayload(
        string Status,
        TimeSpan TotalDuration,
        IReadOnlyDictionary<string, EntryPayload> Entries);

    private sealed record EntryPayload(
        string Status,
        string? Description,
        TimeSpan Duration,
        IReadOnlyDictionary<string, object?>? Data);
}
