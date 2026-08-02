using Elsie.Extensions.RateLimiting.Redis;
using Elsie.RateLimiting;
using StackExchange.Redis;
using Xunit;

namespace Elsie.RateLimiting.Redis.Tests;

/// <summary>
/// Exercises the public <see cref="RedisRateLimit"/> factory surface against an
/// unreachable Redis endpoint. With the default fail-open outage policy the gates
/// must allow requests; with FailClosed they must return 429.
/// </summary>
public class RedisRateLimitFactoryTests
{
    private const string Unreachable = "localhost:1,abortConnect=false,connectTimeout=100,connectRetry=0,asyncTimeout=100,syncTimeout=100";

    [Fact]
    public void Fixed_window_factory_allows_when_redis_unreachable()
    {
        using var mux = ConnectionMultiplexer.Connect(Unreachable);
        var gate = RedisRateLimit.FixedWindow(mux, 2, TimeSpan.FromMinutes(1));

        var ctx = new ElsieContext(new ElsieRequest("GET", "/"), new ElsieResponse(), new Dictionary<string, string>());
        Assert.Null(gate(ctx)); // fail-open: no 429
    }

    [Fact]
    public void Fixed_window_factory_denies_when_fail_closed()
    {
        using var mux = ConnectionMultiplexer.Connect(Unreachable);
        var options = new RedisRateLimitOptions
        {
            OutageMode = RedisOutageMode.FailClosed,
            OperationTimeoutMilliseconds = 100,
        };
        var gate = RedisRateLimit.FixedWindow(mux, 2, TimeSpan.FromMinutes(1), options: options);

        var ctx = new ElsieContext(new ElsieRequest("GET", "/"), new ElsieResponse(), new Dictionary<string, string>());
        var result = gate(ctx);
        Assert.NotNull(result);
        Assert.Equal(429, result!.StatusCode);
        Assert.True(result.Headers.TryGetValues("Retry-After", out var values));
        Assert.True(int.Parse(values![0], System.Globalization.CultureInfo.InvariantCulture) >= 1);
    }

    [Fact]
    public void Connection_string_factory_allows_when_redis_unreachable()
    {
        var gate = RedisRateLimit.SlidingWindow(Unreachable, 2, TimeSpan.FromMinutes(1));

        var ctx = new ElsieContext(new ElsieRequest("GET", "/"), new ElsieResponse(), new Dictionary<string, string>());
        Assert.Null(gate(ctx));
    }

    [Fact]
    public void Factories_validate_arguments()
    {
        using var mux = ConnectionMultiplexer.Connect(Unreachable);
        Assert.Throws<ArgumentOutOfRangeException>(() => RedisRateLimit.FixedWindow(mux, 0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedisRateLimit.TokenBucket(mux, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedisRateLimit.TokenBucket(mux, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedisRateLimit.SlidingWindow(mux, 1, TimeSpan.Zero));
    }

    [Fact]
    public void Token_bucket_factory_reuses_default_partition_key()
    {
        using var mux = ConnectionMultiplexer.Connect(Unreachable);
        var gate = RedisRateLimit.TokenBucket(mux, 5, 1);

        var req = new ElsieRequest("GET", "/", remoteIp: "10.0.0.7");
        var ctx = new ElsieContext(req, new ElsieResponse(), new Dictionary<string, string>());
        Assert.Null(gate(ctx)); // fail-open, but proves partition key path runs
    }
}
