namespace Elsie.HealthChecks;

/// <summary>Registers named health checks served by the health module.</summary>
public sealed class ElsieHealthCheckOptions
{
    private readonly List<ElsieHealthCheckRegistration> _checks = [];

    /// <summary>Path prefix for health endpoints. Default <c>/healthz</c>.</summary>
    public string PathPrefix { get; set; } = "/healthz";

    public IReadOnlyList<ElsieHealthCheckRegistration> Checks => _checks;

    public ElsieHealthCheckOptions AddCheck(
        string name,
        Func<ElsieHealthCheckResult> check,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(check);
        return AddCheck(name, (_, _) => Task.FromResult(check()), tags);
    }

    public ElsieHealthCheckOptions AddCheck(
        string name,
        Func<CancellationToken, Task<ElsieHealthCheckResult>> check,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(check);
        return AddCheck(name, (_, ct) => check(ct), tags);
    }

    public ElsieHealthCheckOptions AddCheck(
        string name,
        Func<IServiceProvider, CancellationToken, Task<ElsieHealthCheckResult>> check,
        params string[] tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);

        if (_checks.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A health check named '{name}' is already registered.", nameof(name));
        }

        var tagList = tags is { Length: > 0 }
            ? (IReadOnlyList<string>)tags.ToArray()
            : Array.Empty<string>();

        _checks.Add(new ElsieHealthCheckRegistration(name, check, tagList));
        return this;
    }
}
