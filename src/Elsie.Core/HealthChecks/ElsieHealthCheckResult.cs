namespace Elsie.HealthChecks;

/// <summary>Outcome of a single health check invocation.</summary>
public sealed class ElsieHealthCheckResult
{
    private ElsieHealthCheckResult(ElsieHealthStatus status, string? description, IReadOnlyDictionary<string, object?>? data)
    {
        Status = status;
        Description = description;
        Data = data;
    }

    public ElsieHealthStatus Status { get; }
    public string? Description { get; }
    public IReadOnlyDictionary<string, object?>? Data { get; }

    public static ElsieHealthCheckResult Healthy(string? description = null, IReadOnlyDictionary<string, object?>? data = null) =>
        new(ElsieHealthStatus.Healthy, description, data);

    public static ElsieHealthCheckResult Degraded(string? description = null, IReadOnlyDictionary<string, object?>? data = null) =>
        new(ElsieHealthStatus.Degraded, description, data);

    public static ElsieHealthCheckResult Unhealthy(string? description = null, IReadOnlyDictionary<string, object?>? data = null) =>
        new(ElsieHealthStatus.Unhealthy, description, data);
}
