using Elsie.Endpoints;
using Elsie.Middleware;
using Elsie.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class HostingPolishTests
{
    private static ServiceProvider Build(
        Action<IElsieEndpointRouteBuilder> map,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddElsieEndpoints(map, o => o.ScanEntryAssembly = false);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task<ElsieDispatchResult> DispatchAsync(ServiceProvider provider, ElsieRequest request) =>
        await provider.GetRequiredService<ElsieDispatcher>().DispatchAsync(request);

    private static ElsieRequest Request(
        string path,
        string? scheme = "http",
        string? host = "example.com",
        IReadOnlyDictionary<string, string>? headers = null) =>
        new("GET", path, scheme: scheme, host: host, headers: headers ?? new Dictionary<string, string>());

    // ---- HTTPS redirection ---------------------------------------------------------

    [Fact]
    public async Task Http_requests_are_redirected_to_https()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieHttpsRedirection());

        var outcome = await DispatchAsync(sp, Request("/x?q=1"));

        Assert.Equal(307, outcome.Result!.StatusCode);
        Assert.Equal("https://example.com/x?q=1", outcome.Result.Headers["Location"]);
    }

    [Fact]
    public async Task Https_and_forwarded_requests_pass_through()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieHttpsRedirection());

        var secure = await DispatchAsync(sp, Request("/x", scheme: "https"));
        var forwarded = await DispatchAsync(sp, Request(
            "/x",
            headers: new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" }));

        Assert.Equal(200, secure.Result!.StatusCode);
        Assert.Equal(200, forwarded.Result!.StatusCode);
    }

    [Fact]
    public async Task Redirect_options_respect_port_and_permanence_and_exclusions()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieHttpsRedirection(o =>
            {
                o.HttpsPort = 8443;
                o.Permanent = true;
                o.ExcludedPaths.Add("/health");
            }));

        var redirect = await DispatchAsync(sp, Request("/x"));
        var excluded = await DispatchAsync(sp, new ElsieRequest("GET", "/health", scheme: "http", host: "example.com"));

        Assert.Equal(308, redirect.Result!.StatusCode);
        Assert.Equal("https://example.com:8443/x", redirect.Result.Headers["Location"]);
        Assert.Null(excluded.Result); // passthrough: no route matched
    }

    // ---- Host routing --------------------------------------------------------------

    [Fact]
    public async Task Host_allow_list_rejects_other_hosts()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieHostRouting(o => o.AllowedHosts.Add("api.example.com")));

        var allowed = await DispatchAsync(sp, Request("/x", host: "api.example.com"));
        var denied = await DispatchAsync(sp, Request("/x", host: "evil.example.com"));

        Assert.Equal(200, allowed.Result!.StatusCode);
        Assert.Equal(421, denied.Result!.StatusCode);
    }

    [Fact]
    public async Task Wildcard_host_matches_subdomains_but_not_apex()
    {
        Assert.True(HostMatcher.Matches("*.example.com", "api.example.com"));
        Assert.True(HostMatcher.Matches("*.example.com", "a.b.example.com"));
        Assert.False(HostMatcher.Matches("*.example.com", "example.com"));
        Assert.False(HostMatcher.Matches("*.example.com", "notexample.com"));
        Assert.True(HostMatcher.Matches("example.com:8080", "example.com:8080"));
        Assert.False(HostMatcher.Matches("example.com:8080", "example.com:9090"));
    }

    [Fact]
    public async Task Endpoint_host_metadata_is_enforced()
    {
        await using var sp = Build(
            app =>
            {
                app.MapGet("/api", () => "api").RequireHost("api.example.com");
                app.MapGet("/public", () => "public");
            },
            services => services.AddElsieHostRouting());

        var match = await DispatchAsync(sp, Request("/api", host: "api.example.com"));
        var mismatch = await DispatchAsync(sp, Request("/api", host: "www.example.com"));
        var unrelated = await DispatchAsync(sp, Request("/public", host: "www.example.com"));

        Assert.Equal(200, match.Result!.StatusCode);
        Assert.Equal(421, mismatch.Result!.StatusCode);
        Assert.Equal(200, unrelated.Result!.StatusCode);
    }

    [Fact]
    public async Task Missing_host_is_rejected_when_required()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieHostRouting(o => o.AllowedHosts.Add("example.com")));

        var outcome = await DispatchAsync(sp, new ElsieRequest("GET", "/x"));

        Assert.Equal(400, outcome.Result!.StatusCode);
    }

    // ---- Server timing -------------------------------------------------------------

    [Fact]
    public async Task Server_timing_reports_total_duration()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieServerTiming());

        var outcome = await DispatchAsync(sp, Request("/x", scheme: "https"));

        var header = outcome.Result!.Headers["Server-Timing"];
        Assert.StartsWith("total;dur=", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_metrics_appear_in_server_timing()
    {
        await using var sp = Build(
            app => app.MapGet("/x", async (ElsieContext ctx) =>
            {
                ElsieServerTiming.Add(ctx, "db", 12.5, "select users");
                var value = await ElsieServerTiming.MeasureAsync(ctx, "cache", () => Task.FromResult(1));
                return ElsieResults.Ok(value);
            }),
            services => services.AddElsieServerTiming());

        var outcome = await DispatchAsync(sp, Request("/x", scheme: "https"));

        var header = outcome.Result!.Headers["Server-Timing"];
        Assert.Contains("db;dur=12.5;desc=\"select users\"", header, StringComparison.Ordinal);
        Assert.Contains("cache;dur=", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_timing_name_is_configurable()
    {
        await using var sp = Build(
            app => app.MapGet("/x", () => "ok"),
            services => services.AddElsieServerTiming(o => o.TotalMetricName = "app"));

        var outcome = await DispatchAsync(sp, Request("/x", scheme: "https"));

        Assert.StartsWith("app;dur=", outcome.Result!.Headers["Server-Timing"], StringComparison.Ordinal);
    }
}
