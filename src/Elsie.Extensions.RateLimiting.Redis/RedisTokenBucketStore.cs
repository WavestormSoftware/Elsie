using Elsie.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Token-bucket rate limit stored in Redis as a hash with lazy refill.
/// Mirrors the in-memory <c>TokenBucketStore</c> refill math.
/// </summary>
public sealed class RedisTokenBucketStore : RedisRateLimitStore
{
    private readonly long _capacity;
    private readonly double _tokensPerSecond;
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates a store over an existing multiplexer. The caller owns the multiplexer.
    /// </summary>
    public RedisTokenBucketStore(
        IConnectionMultiplexer connection,
        int capacity,
        double tokensPerSecond,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(connection, capacity, tokensPerSecond, options, timeProvider, logger, ownsConnection: false)
    {
    }

    /// <summary>
    /// Creates a store from a Redis connection string. The returned store owns the
    /// connection and disposes it when the store is disposed.
    /// </summary>
    public static RedisTokenBucketStore Create(
        string connectionString,
        int capacity,
        double tokensPerSecond,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        => new(Connect(connectionString), capacity, tokensPerSecond, options, timeProvider, logger, ownsConnection: true);

    /// <summary>Test seam constructor (no live Redis required).</summary>
    internal RedisTokenBucketStore(
        IRedisRateLimitExecutor executor,
        int capacity,
        double tokensPerSecond,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null)
        : base(executor, options, logger: null)
    {
        Validate(capacity, tokensPerSecond);
        _capacity = capacity;
        _tokensPerSecond = tokensPerSecond;
        _time = timeProvider ?? TimeProvider.System;
    }

    private RedisTokenBucketStore(
        IConnectionMultiplexer connection,
        int capacity,
        double tokensPerSecond,
        RedisRateLimitOptions? options,
        TimeProvider? timeProvider,
        ILogger? logger,
        bool ownsConnection)
        : base(connection, options, logger, ownsConnection)
    {
        Validate(capacity, tokensPerSecond);
        _capacity = capacity;
        _tokensPerSecond = tokensPerSecond;
        _time = timeProvider ?? TimeProvider.System;
    }

    private static void Validate(int capacity, double tokensPerSecond)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");
        }

        if (tokensPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokensPerSecond), "Tokens per second must be positive.");
        }
    }

    /// <inheritdoc />
    protected override string Script => RedisScripts.TokenBucket;

    /// <inheritdoc />
    public override bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        try
        {
            // IRateLimitStore is synchronous; the single atomic Lua round-trip is awaited inline.
            var result = EvaluateScriptAsync(key, AcquireArgs()).GetAwaiter().GetResult();
            var limited = (long)result[0] == 1;
            var retryAfterMilliseconds = Math.Max(1, (long)result[2]);
            retryAfter = limited ? TimeSpan.FromMilliseconds(retryAfterMilliseconds) : TimeSpan.Zero;
            return !limited;
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            return OnOutage(ex, out retryAfter);
        }
    }

    /// <inheritdoc />
    public override bool TryPeek(string key, out RateLimitCounters counters)
    {
        try
        {
            var result = EvaluateScriptAsync(key, PeekArgs()).GetAwaiter().GetResult();
            var remaining = Math.Max(0, (long)result[1]);
            var retryAfterMilliseconds = Math.Max(0, (long)result[2]);
            var nowSeconds = _time.GetUtcNow().ToUnixTimeSeconds();
            var reset = retryAfterMilliseconds > 0
                ? nowSeconds + (long)Math.Ceiling(retryAfterMilliseconds / 1000d)
                : nowSeconds;
            counters = new RateLimitCounters(_capacity, remaining, reset);
            return true;
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            counters = default;
            return false;
        }
    }

    private RedisValue[] AcquireArgs() => Args(acquire: true);

    private RedisValue[] PeekArgs() => Args(acquire: false);

    private RedisValue[] Args(bool acquire)
    {
        var now = _time.GetUtcNow();
        var nowSeconds = now.ToUnixTimeSeconds() + (now.Millisecond / 1000d);
        return
        [
            _capacity,
            _tokensPerSecond,
            nowSeconds,
            RedisValue.EmptyString,
            acquire ? "1" : "0"
        ];
    }
}
