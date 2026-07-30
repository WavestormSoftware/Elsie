namespace Elsie.HealthChecks;

/// <summary>Serves <c>/healthz</c>, <c>/healthz/live</c>, and <c>/healthz/ready</c>.</summary>
public sealed class ElsieHealthChecksModule : ElsieModule
{
    public ElsieHealthChecksModule(ElsieHealthCheckRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var prefix = string.IsNullOrWhiteSpace(runner.Options.PathPrefix)
            ? "/healthz"
            : runner.Options.PathPrefix.TrimEnd('/');

        Get(prefix, async (ctx, ct) =>
        {
            var report = await runner.RunAsync(ctx.Services, predicate: null, ct).ConfigureAwait(false);
            return ElsieHealthCheckRunner.ToResult(report);
        });

        Get(prefix + "/live", async (ctx, ct) =>
        {
            var report = await runner
                .RunAsync(ctx.Services, r => r.HasTag(ElsieHealthCheckTags.Live), ct)
                .ConfigureAwait(false);
            return ElsieHealthCheckRunner.ToResult(report);
        });

        Get(prefix + "/ready", async (ctx, ct) =>
        {
            var report = await runner
                .RunAsync(ctx.Services, r => r.HasTag(ElsieHealthCheckTags.Ready), ct)
                .ConfigureAwait(false);
            return ElsieHealthCheckRunner.ToResult(report);
        });
    }
}
