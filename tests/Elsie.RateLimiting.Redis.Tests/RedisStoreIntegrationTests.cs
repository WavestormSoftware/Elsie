using DotNet.Testcontainers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Elsie.Extensions.RateLimiting.Redis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Elsie.RateLimiting.Redis.Tests;

/// <summary>
/// Live-Redis integration tests (Testcontainers). Skipped when Docker is unavailable.
/// </summary>
[Trait("Category", "RedisIntegration")]
public class RedisStoreIntegrationTests : IAsyncLifetime
{
    private IContainer? _container;
    private string? _connectionString;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("redis:7-alpine")
                .WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
                .Build();
            await _container.StartAsync();
            _connectionString = $"localhost:{_container.GetMappedPublicPort(6379)}";
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Fixed_window_enforces_limit_and_ttl()
    {
        if (!_available)
        {
            return; // no-op without Docker; these run for real in CI (ubuntu runner)
        }

        using var mux = await ConnectionMultiplexer.ConnectAsync(_connectionString + ",allowAdmin=true");
        var store = new RedisFixedWindowStore(mux, permitLimit: 2, TimeSpan.FromSeconds(60));

        Assert.True(store.TryAcquire("ip-a", out _));
        Assert.True(store.TryAcquire("ip-a", out _));
        Assert.False(store.TryAcquire("ip-a", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero, "Retry-After should be positive when limited");

        Assert.True(store.TryPeek("ip-a", out var counters));
        Assert.Equal(0, counters.Remaining);

        // A different partition key is unaffected.
        Assert.True(store.TryAcquire("ip-b", out _));

        // Keys live under the configured prefix.
        var db = mux.GetDatabase();
        var keys = mux.GetServer(mux.GetEndPoints().First()).Keys(pattern: "elsie:rl:*").ToArray();
        Assert.Contains(keys, k => k.ToString().EndsWith("ip-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sliding_window_tracks_trailing_window()
    {
        if (!_available)
        {
            return; // no-op without Docker; these run for real in CI (ubuntu runner)
        }

        using var mux = await ConnectionMultiplexer.ConnectAsync(_connectionString!);
        var store = new RedisSlidingWindowStore(mux, permitLimit: 2, TimeSpan.FromSeconds(3));

        Assert.True(store.TryAcquire("ip", out _));
        Assert.True(store.TryAcquire("ip", out _));
        Assert.False(store.TryAcquire("ip", out _));

        // After the window elapses, permits free up again.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(store.TryAcquire("ip", out _));
    }

    [Fact]
    public async Task Token_bucket_refills_over_time()
    {
        if (!_available)
        {
            return; // no-op without Docker; these run for real in CI (ubuntu runner)
        }

        using var mux = await ConnectionMultiplexer.ConnectAsync(_connectionString!);
        var store = new RedisTokenBucketStore(mux, capacity: 2, tokensPerSecond: 1);

        Assert.True(store.TryAcquire("ip", out _));
        Assert.True(store.TryAcquire("ip", out _));
        Assert.False(store.TryAcquire("ip", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.True(store.TryAcquire("ip", out _));
    }

    [Fact]
    public async Task Headers_hook_emits_x_rate_limit_headers()
    {
        if (!_available)
        {
            return; // no-op without Docker; these run for real in CI (ubuntu runner)
        }

        using var mux = await ConnectionMultiplexer.ConnectAsync(_connectionString!);
        var store = new RedisFixedWindowStore(mux, permitLimit: 5, TimeSpan.FromMinutes(1));
        await using var host = Elsie.Testing.ElsieInMemoryHost.Create(s =>
        {
            s.AddSingleton<IRateLimitStore>(store);
            s.AddElsieModule<HeaderModule>();
        });

        var response = await host.GetAsync("/ping");
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-RateLimit-Limit", out var limit));
        Assert.Equal("5", limit!.Single());
        Assert.True(response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining));
        Assert.Equal("4", remaining!.Single());
        Assert.True(response.Headers.TryGetValues("X-RateLimit-Reset", out var reset));
        Assert.True(long.TryParse(reset!.Single(), out var resetUnix));
        Assert.True(resetUnix > 0);
    }

    private sealed class HeaderModule : Elsie.ElsieModule
    {
        public HeaderModule(Elsie.RateLimiting.IRateLimitStore store)
        {
            Use(ctx =>
            {
                var key = ctx.Request.RemoteIp ?? "test";
                return store.TryAcquire(key, out var retryAfter)
                    ? null
                    : Elsie.ElsieResult.Problem(429, "Too Many Requests", "limited");
            });
            Use(Elsie.RateLimiting.ElsieRateLimitHeaders.Attach(store, _ => "test"));
            Get("/ping", () => Elsie.ElsieResult.Text("pong"));
        }
    }
}
