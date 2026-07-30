namespace Elsie.HealthChecks;

/// <summary>Named health check with optional tags (e.g. <see cref="ElsieHealthCheckTags.Ready"/>).</summary>
public sealed class ElsieHealthCheckRegistration
{
    internal ElsieHealthCheckRegistration(
        string name,
        Func<IServiceProvider, CancellationToken, Task<ElsieHealthCheckResult>> check,
        IReadOnlyList<string> tags)
    {
        Name = name;
        Check = check;
        Tags = tags;
    }

    public string Name { get; }
    public Func<IServiceProvider, CancellationToken, Task<ElsieHealthCheckResult>> Check { get; }
    public IReadOnlyList<string> Tags { get; }

    public bool HasTag(string tag) =>
        Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
}
