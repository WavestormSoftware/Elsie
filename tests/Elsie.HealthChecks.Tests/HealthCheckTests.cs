using System.Text.Json;
using Elsie.HealthChecks;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.HealthChecks.Tests;

public class HealthCheckTests
{
    [Fact]
    public async Task Healthz_with_no_checks_is_healthy()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks());

        var res = await host.GetAsync("/healthz");
        Assert.Equal(200, res.StatusCode);
        using var doc = JsonDocument.Parse(res.ReadAsString());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Healthz_aggregates_status_and_entries()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks(o =>
        {
            o.AddCheck("ok", () => ElsieHealthCheckResult.Healthy("fine"));
            o.AddCheck("slow", () => ElsieHealthCheckResult.Degraded("lag"));
        }));

        var res = await host.GetAsync("/healthz");
        Assert.Equal(200, res.StatusCode);
        using var doc = JsonDocument.Parse(res.ReadAsString());
        Assert.Equal("Degraded", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("entries").GetProperty("ok").GetProperty("status").GetString());
        Assert.Equal("Degraded", doc.RootElement.GetProperty("entries").GetProperty("slow").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Healthz_unhealthy_returns_503()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks(o =>
        {
            o.AddCheck("db", () => ElsieHealthCheckResult.Unhealthy("down"));
        }));

        var res = await host.GetAsync("/healthz");
        Assert.Equal(503, res.StatusCode);
        Assert.Contains("Unhealthy", res.ReadAsString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exception_in_check_is_unhealthy()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks(o =>
        {
            o.AddCheck("boom", () => throw new InvalidOperationException("explode"));
        }));

        var res = await host.GetAsync("/healthz");
        Assert.Equal(503, res.StatusCode);
        Assert.Contains("explode", res.ReadAsString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_and_ready_filter_by_tags()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks(o =>
        {
            o.AddCheck("proc", () => ElsieHealthCheckResult.Healthy(), ElsieHealthCheckTags.Live);
            o.AddCheck("db", () => ElsieHealthCheckResult.Unhealthy("nope"), ElsieHealthCheckTags.Ready);
        }));

        var live = await host.GetAsync("/healthz/live");
        Assert.Equal(200, live.StatusCode);
        Assert.Contains("\"status\":\"Healthy\"", live.ReadAsString(), StringComparison.Ordinal);

        var ready = await host.GetAsync("/healthz/ready");
        Assert.Equal(503, ready.StatusCode);
        Assert.Contains("Unhealthy", ready.ReadAsString(), StringComparison.Ordinal);

        // Aggregate still sees both.
        var all = await host.GetAsync("/healthz");
        Assert.Equal(503, all.StatusCode);
    }

    [Fact]
    public async Task Live_with_no_live_checks_is_healthy()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks(o =>
        {
            o.AddCheck("db", () => ElsieHealthCheckResult.Unhealthy("down"), ElsieHealthCheckTags.Ready);
        }));

        var live = await host.GetAsync("/healthz/live");
        Assert.Equal(200, live.StatusCode);
        using var doc = JsonDocument.Parse(live.ReadAsString());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
        Assert.Empty(doc.RootElement.GetProperty("entries").EnumerateObject());
    }

    [Fact]
    public async Task Check_can_resolve_request_services()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddSingleton(new Marker("yes"));
            s.AddElsieHealthChecks(o =>
            {
                o.AddCheck("svc", (sp, _) =>
                {
                    var m = sp.GetRequiredService<Marker>();
                    return Task.FromResult(ElsieHealthCheckResult.Healthy(m.Value));
                });
            });
        });

        var res = await host.GetAsync("/healthz");
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("yes", res.ReadAsString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_path_prefix()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieHealthChecks(o =>
        {
            o.PathPrefix = "/status";
            o.AddCheck("ok", () => ElsieHealthCheckResult.Healthy());
        }));

        Assert.Equal(200, (await host.GetAsync("/status")).StatusCode);
        Assert.Equal(200, (await host.GetAsync("/status/live")).StatusCode);
        Assert.Equal(404, (await host.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public void Duplicate_check_name_throws()
    {
        var o = new ElsieHealthCheckOptions();
        o.AddCheck("db", () => ElsieHealthCheckResult.Healthy());
        Assert.Throws<ArgumentException>(() => o.AddCheck("db", () => ElsieHealthCheckResult.Healthy()));
    }

    private sealed record Marker(string Value);
}
