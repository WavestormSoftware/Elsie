using System.Globalization;
using Elsie.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Redis-backed before-hook factories mirroring <see cref="ElsieRateLimit"/>.
/// Each call creates a Redis store (shared multiplexer or dedicated connection)
/// used by the returned hook. See <c>docs/rate-limiting.md</c>.
/// </summary>
public static class RedisRateLimit
{
    /// <summary>
    /// Fixed window over an existing multiplexer: at most <paramref name="permitLimit"/>
    /// requests per <paramref name="window"/> per partition.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> FixedWindow(
        IConnectionMultiplexer connection,
        int permitLimit,
        TimeSpan window,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
    {
        var store = new RedisFixedWindowStore(connection, permitLimit, window, options, timeProvider, logger);
        return Gate(store, partitionKey);
    }

    /// <summary>
    /// Fixed window from a Redis connection string: at most <paramref name="permitLimit"/>
    /// requests per <paramref name="window"/> per partition. The store owns the connection.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> FixedWindow(
        string connectionString,
        int permitLimit,
        TimeSpan window,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
    {
        var store = RedisFixedWindowStore.Create(connectionString, permitLimit, window, options, timeProvider, logger);
        return Gate(store, partitionKey);
    }

    /// <summary>
    /// Sliding window over an existing multiplexer: at most <paramref name="permitLimit"/>
    /// requests in any trailing <paramref name="window"/> per partition.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> SlidingWindow(
        IConnectionMultiplexer connection,
        int permitLimit,
        TimeSpan window,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
    {
        var store = new RedisSlidingWindowStore(connection, permitLimit, window, options, timeProvider, logger);
        return Gate(store, partitionKey);
    }

    /// <summary>
    /// Sliding window from a Redis connection string: at most <paramref name="permitLimit"/>
    /// requests in any trailing <paramref name="window"/> per partition.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> SlidingWindow(
        string connectionString,
        int permitLimit,
        TimeSpan window,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
    {
        var store = RedisSlidingWindowStore.Create(connectionString, permitLimit, window, options, timeProvider, logger);
        return Gate(store, partitionKey);
    }

    /// <summary>
    /// Token bucket over an existing multiplexer: burst up to <paramref name="capacity"/>,
    /// refill at <paramref name="tokensPerSecond"/> per partition.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> TokenBucket(
        IConnectionMultiplexer connection,
        int capacity,
        double tokensPerSecond,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
    {
        var store = new RedisTokenBucketStore(connection, capacity, tokensPerSecond, options, timeProvider, logger);
        return Gate(store, partitionKey);
    }

    /// <summary>
    /// Token bucket from a Redis connection string: burst up to <paramref name="capacity"/>,
    /// refill at <paramref name="tokensPerSecond"/> per partition.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> TokenBucket(
        string connectionString,
        int capacity,
        double tokensPerSecond,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
    {
        var store = RedisTokenBucketStore.Create(connectionString, capacity, tokensPerSecond, options, timeProvider, logger);
        return Gate(store, partitionKey);
    }

    private static Func<ElsieContext, ElsieResult?> Gate(
        IRateLimitStore store,
        Func<ElsieContext, string>? partitionKey)
    {
        var keySelector = partitionKey ?? ElsieRateLimit.DefaultPartitionKey;
        return ctx =>
        {
            var key = keySelector(ctx) ?? "unknown";
            return store.TryAcquire(key, out var retryAfter)
                ? null
                : TooManyRequests(retryAfter);
        };
    }

    private static ElsieResult TooManyRequests(TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        return ElsieResult.Problem(429, "Too Many Requests", "Rate limit exceeded.")
            .WithHeader("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
    }
}
