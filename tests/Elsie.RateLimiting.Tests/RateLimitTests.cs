using System.Globalization;
using Elsie.RateLimiting;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.RateLimiting.Tests;

public class RateLimitTests
{
    private sealed class GatedModule : ElsieModule
    {
        public GatedModule(Func<ElsieContext, ElsieResult?> gate)
        {
            Before(gate);
            Get("/ping", () => ElsieResult.Text("pong"));
        }
    }

    private static ElsieInMemoryHost CreateHost(Func<ElsieContext, ElsieResult?> gate) =>
        ElsieInMemoryHost.Create(s =>
        {
            s.AddSingleton(gate);
            s.AddElsieModule<GatedModule>();
        });

    [Fact]
    public async Task Fixed_window_blocks_after_limit_with_retry_after()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var gate = ElsieRateLimit.FixedWindow(2, TimeSpan.FromSeconds(10), _ => "ip", time);
        await using var host = CreateHost(gate);

        Assert.Equal(200, (await host.GetAsync("/ping")).StatusCode);
        Assert.Equal(200, (await host.GetAsync("/ping")).StatusCode);

        var blocked = await host.GetAsync("/ping");
        Assert.Equal(429, blocked.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", blocked.ContentType);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var values));
        Assert.True(int.Parse(values![0], CultureInfo.InvariantCulture) >= 1);
        Assert.Contains("Too Many Requests", blocked.ReadAsString(), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(200, (await host.GetAsync("/ping")).StatusCode);
    }

    [Fact]
    public async Task Sliding_window_uses_trailing_window()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var gate = ElsieRateLimit.SlidingWindow(2, TimeSpan.FromSeconds(10), _ => "ip", time);
        await using var host = CreateHost(gate);

        Assert.Equal(200, (await host.GetAsync("/ping")).StatusCode);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(200, (await host.GetAsync("/ping")).StatusCode);

        // Still within 10s of first request → blocked
        Assert.Equal(429, (await host.GetAsync("/ping")).StatusCode);

        // First request ages out → one slot frees
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(200, (await host.GetAsync("/ping")).StatusCode);
    }

    [Fact]
    public async Task Partitions_are_independent()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gate = ElsieRateLimit.FixedWindow(
            1,
            TimeSpan.FromMinutes(1),
            ctx => ctx.Request.GetHeader("X-Client") ?? "none",
            time);

        await using var host = CreateHost(gate);

        var a = await host.SendAsync(
            "GET",
            "/ping",
            headers: new Dictionary<string, string> { ["X-Client"] = "a" });
        var b = await host.SendAsync(
            "GET",
            "/ping",
            headers: new Dictionary<string, string> { ["X-Client"] = "b" });
        var a2 = await host.SendAsync(
            "GET",
            "/ping",
            headers: new Dictionary<string, string> { ["X-Client"] = "a" });

        Assert.Equal(200, a.StatusCode);
        Assert.Equal(200, b.StatusCode);
        Assert.Equal(429, a2.StatusCode);
    }

    [Fact]
    public void Default_partition_prefers_remote_ip()
    {
        var req = new ElsieRequest("GET", "/", remoteIp: "1.2.3.4");
        var ctx = new ElsieContext(req, new ElsieResponse(), new Dictionary<string, string>());
        Assert.Equal("1.2.3.4", ElsieRateLimit.DefaultPartitionKey(ctx));
    }

    [Fact]
    public void Default_partition_uses_forwarded_for()
    {
        var req = new ElsieRequest(
            "GET",
            "/",
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "9.9.9.9, 8.8.8.8" });
        var ctx = new ElsieContext(req, new ElsieResponse(), new Dictionary<string, string>());
        Assert.Equal("9.9.9.9", ElsieRateLimit.DefaultPartitionKey(ctx));
    }

    [Fact]
    public void Invalid_args_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ElsieRateLimit.FixedWindow(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElsieRateLimit.SlidingWindow(1, TimeSpan.Zero));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc;

        public ManualTimeProvider(DateTimeOffset utc) => _utc = utc.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utc;

        public void Advance(TimeSpan by) => _utc += by;
    }
}
