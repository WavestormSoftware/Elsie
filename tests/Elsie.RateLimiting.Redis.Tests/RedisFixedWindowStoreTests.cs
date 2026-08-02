using Elsie.Extensions.RateLimiting.Redis;
using Elsie.RateLimiting;
using StackExchange.Redis;
using Xunit;

namespace Elsie.RateLimiting.Redis.Tests;

public class RedisFixedWindowStoreTests
{
    private static ManualTimeProvider Now() => new(TestHelpers.FixedNow());

    [Fact]
    public void Acquire_increments_until_limit_then_reports_retry_after()
    {
        var time = Now();
        var call = 0;
        var executor = new FakeRedisExecutor((_, _, _) =>
        {
            call++;
            return Task.FromResult(call <= 2
                ? RedisStack.Array((RedisValue)call, 60, 0)
                : RedisStack.Array((RedisValue)3, 59, 1));
        });

        using var store = new RedisFixedWindowStore(executor, permitLimit: 2, TimeSpan.FromSeconds(60), timeProvider: time);

        Assert.True(store.TryAcquire("ip", out _));
        Assert.True(store.TryAcquire("ip", out _));
        Assert.False(store.TryAcquire("ip", out var retryAfter));
        Assert.InRange(retryAfter.TotalSeconds, 59, 60);

        // Acquire path uses INCR: last arg is "1"
        Assert.All(executor.Calls, c => Assert.Equal("1", (string?)c.Args[3]));
    }

    [Fact]
    public void Peek_reports_limit_remaining_and_reset()
    {
        var time = Now();
        var executor = FakeRedisExecutor.Returning((RedisValue)4, 30, 0);

        using var store = new RedisFixedWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60), timeProvider: time);

        Assert.True(store.TryPeek("ip", out var counters));
        Assert.Equal(5, counters.Limit);
        Assert.Equal(1, counters.Remaining);
        Assert.Equal(TestHelpers.NowUnixSeconds(time.GetUtcNow()) + 30, counters.ResetUnixSeconds);

        // Peek path must not increment: last arg is "0"
        Assert.Equal("0", (string?)executor.Calls[0].Args[3]);
    }

    [Fact]
    public void Peek_uses_prefixed_key_and_custom_prefix()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)0, -2, 0);
        var options = new RedisRateLimitOptions { KeyPrefix = "tenant:rl:" };
        using var store = new RedisFixedWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60), options, Now());

        store.TryPeek("api-key", out _);

        Assert.Equal("tenant:rl:api-key", (string?)executor.Calls[0].Key);
    }

    [Fact]
    public void Outage_fail_open_allows_when_redis_throws()
    {
        var executor = FakeRedisExecutor.ThrowingConnectionError();
        using var store = new RedisFixedWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60));

        Assert.True(store.TryAcquire("ip", out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void Outage_fail_closed_denies_with_retry_after()
    {
        var executor = FakeRedisExecutor.ThrowingConnectionError();
        var options = new RedisRateLimitOptions { OutageMode = RedisOutageMode.FailClosed };
        using var store = new RedisFixedWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60), options);

        Assert.False(store.TryAcquire("ip", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(60), retryAfter);
    }

    [Fact]
    public void Operation_timeout_applies_outage_policy()
    {
        var executor = FakeRedisExecutor.Hanging();
        var options = new RedisRateLimitOptions { OperationTimeoutMilliseconds = 50, OutageMode = RedisOutageMode.FailOpen };
        using var store = new RedisFixedWindowStore(executor, permitLimit: 5, TimeSpan.FromSeconds(60), options);

        Assert.True(store.TryAcquire("ip", out _));
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        var executor = FakeRedisExecutor.Returning((RedisValue)0, 60, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisFixedWindowStore(executor, 0, TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisFixedWindowStore(executor, 1, TimeSpan.Zero));
    }
}

/// <summary>Helper for building multi-bulk Redis results from Lua scripts.</summary>
internal static class RedisStack
{
    public static RedisResult Array(RedisValue a, RedisValue b, RedisValue c) =>
        RedisResult.Create(new[] { a, b, c });
}
