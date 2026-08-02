using Elsie.Extensions.RateLimiting.Redis;
using StackExchange.Redis;
using Xunit;

namespace Elsie.RateLimiting.Redis.Tests;

public class RedisTokenBucketStoreTests
{
    [Fact]
    public void Acquire_allows_while_tokens_remain()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)0, 4, 0);
        using var store = new RedisTokenBucketStore(executor, capacity: 5, tokensPerSecond: 1);

        Assert.True(store.TryAcquire("ip", out _));

        var args = executor.Calls[0].Args;
        Assert.Equal(5, (long)args[0]);
        Assert.Equal("1", (string?)args[1]);
        Assert.Equal("1", (string?)args[4]);
    }

    [Fact]
    public void Acquire_limited_reports_retry_after()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)1, 0, 500);
        using var store = new RedisTokenBucketStore(executor, capacity: 1, tokensPerSecond: 1);

        Assert.False(store.TryAcquire("ip", out var retryAfter));
        Assert.InRange(retryAfter.TotalMilliseconds, 500, 600);
    }

    [Fact]
    public void Peek_reports_remaining_and_does_not_consume()
    {
        var time = new ManualTimeProvider(TestHelpers.FixedNow());
        var executor = FakeRedisExecutor.Returning((RedisValue)0, 2, 0);
        using var store = new RedisTokenBucketStore(executor, capacity: 5, tokensPerSecond: 1, timeProvider: time);

        Assert.True(store.TryPeek("ip", out var counters));
        Assert.Equal(5, counters.Limit);
        Assert.Equal(2, counters.Remaining);
        Assert.Equal(TestHelpers.NowUnixSeconds(time.GetUtcNow()), counters.ResetUnixSeconds);
        Assert.Equal("0", (string?)executor.Calls[0].Args[4]);
    }

    [Fact]
    public void Outage_fail_open_allows()
    {
        var executor = FakeRedisExecutor.ThrowingConnectionError();
        using var store = new RedisTokenBucketStore(executor, capacity: 5, tokensPerSecond: 1);

        Assert.True(store.TryAcquire("ip", out _));
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)0, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisTokenBucketStore(executor, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisTokenBucketStore(executor, 1, 0));
    }
}
