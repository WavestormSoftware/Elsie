namespace Elsie.HealthChecks;

/// <summary>Aggregate report for a health endpoint invocation.</summary>
public sealed class ElsieHealthReport
{
    public ElsieHealthReport(
        ElsieHealthStatus status,
        TimeSpan totalDuration,
        IReadOnlyDictionary<string, ElsieHealthReportEntry> entries)
    {
        Status = status;
        TotalDuration = totalDuration;
        Entries = entries;
    }

    public ElsieHealthStatus Status { get; }
    public TimeSpan TotalDuration { get; }
    public IReadOnlyDictionary<string, ElsieHealthReportEntry> Entries { get; }
}

/// <summary>Per-check entry in an <see cref="ElsieHealthReport"/>.</summary>
public sealed class ElsieHealthReportEntry
{
    public ElsieHealthReportEntry(
        ElsieHealthStatus status,
        string? description,
        TimeSpan duration,
        IReadOnlyDictionary<string, object?>? data)
    {
        Status = status;
        Description = description;
        Duration = duration;
        Data = data;
    }

    public ElsieHealthStatus Status { get; }
    public string? Description { get; }
    public TimeSpan Duration { get; }
    public IReadOnlyDictionary<string, object?>? Data { get; }
}
