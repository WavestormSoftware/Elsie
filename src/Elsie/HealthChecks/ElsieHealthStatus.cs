namespace Elsie.HealthChecks;

/// <summary>Aggregate or per-check health status.</summary>
public enum ElsieHealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2
}
