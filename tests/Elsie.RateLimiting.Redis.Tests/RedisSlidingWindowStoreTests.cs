using Elsie.Extensions.RateLimiting.Redis;
using StackExchange.Redis;
using Xunit;

namespace Elsie.RateLimiting.Redis.Tests;

public class RedisSlidingWindowStoreTests
{
    [Fact]
    public void Acquire_uses_unique_members_per_request()
    {
        var call = 0;
        var executor = new FakeRedisExecutor((_, _, _) =>
        {
            call++;
            return Task.FromResult(RedisStack.Array((RedisValue)0, 0, call));
        });

        using var store = new RedisSlidingWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60));

        Assert.True(store.TryAcquire("ip", out _));
        Assert.True(store.TryAcquire("ip", out _));

        Assert.Equal(2, executor.Calls.Count);
        Assert.NotEqual((string?)executor.Calls[0].Args[3], (string?)executor.Calls[1].Args[3]);
        Assert.All(executor.Calls, c => Assert.Equal("1", (string?)c.Args[4]));
        // Window is passed in millis
        Assert.Equal(60000, (long)executor.Calls[0].Args[2]);
    }

    [Fact]
    public void Acquire_limited_reports_retry_after_in_millis()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)1, 7000, 3);
        using var store = new RedisSlidingWindowStore(executor, permitLimit: 3, TimeSpan.FromSeconds(60));

        Assert.False(store.TryAcquire("ip", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(7), retryAfter);
    }

    [Fact]
    public void Peek_reports_remaining_and_reset_without_adding_members()
    {
        var time = new ManualTimeProvider(TestHelpers.FixedNow());
        var executor = FakeRedisExecutor.Returning((RedisValue)0, 2500, 4);
        using var store = new RedisSlidingWindowStore(executor, permitLimit: 10, TimeSpan.FromSeconds(60), timeProvider: time);

        Assert.True(store.TryPeek("ip", out var counters));
        Assert.Equal(10, counters.Limit);
        Assert.Equal(6, counters.Remaining);
        Assert.Equal(TestHelpers.NowUnixSeconds(time.GetUtcNow()) + 3, counters.ResetUnixSeconds);
        Assert.Equal("0", (string?)executor.Calls[0].Args[4]);
    }

    [Fact]
    public void Outage_fail_open_allows()
    {
        var executor = FakeRedisExecutor.ThrowingConnectionError();
        using var store = new RedisSlidingWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60));

        Assert.True(store.TryAcquire("ip", out _));
        Assert.False(store.TryPeek("ip", out _));
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)0, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisSlidingWindowStore(executor, 0, TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisSlidingWindowStore(executor, 1, TimeSpan.Zero));
    }
}
