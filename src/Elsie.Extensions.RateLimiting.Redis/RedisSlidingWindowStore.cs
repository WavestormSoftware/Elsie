using Elsie.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Sliding-window rate limit stored in Redis as a sorted set of per-request
/// timestamps. Mirrors the in-memory <c>SlidingWindowStore</c> trailing-window semantics.
/// </summary>
public sealed class RedisSlidingWindowStore : RedisRateLimitStore
{
    private readonly int _permitLimit;
    private readonly long _windowMilliseconds;
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates a store over an existing multiplexer. The caller owns the multiplexer.
    /// </summary>
    public RedisSlidingWindowStore(
        IConnectionMultiplexer connection,
        int permitLimit,
        TimeSpan window,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(connection, permitLimit, window, options, timeProvider, logger, ownsConnection: false)
    {
    }

    /// <summary>
    /// Creates a store from a Redis connection string. The returned store owns the
    /// connection and disposes it when the store is disposed.
    /// </summary>
    public static RedisSlidingWindowStore Create(
        string connectionString,
        int permitLimit,
        TimeSpan window,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        => new(Connect(connectionString), permitLimit, window, options, timeProvider, logger, ownsConnection: true);

    /// <summary>Test seam constructor (no live Redis required).</summary>
    internal RedisSlidingWindowStore(
        IRedisRateLimitExecutor executor,
        int permitLimit,
        TimeSpan window,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null)
        : base(executor, options, logger: null)
    {
        Validate(permitLimit, window);
        _permitLimit = permitLimit;
        _windowMilliseconds = Math.Max(1, (long)window.TotalMilliseconds);
        _time = timeProvider ?? TimeProvider.System;
    }

    private RedisSlidingWindowStore(
        IConnectionMultiplexer connection,
        int permitLimit,
        TimeSpan window,
        RedisRateLimitOptions? options,
        TimeProvider? timeProvider,
        ILogger? logger,
        bool ownsConnection)
        : base(connection, options, logger, ownsConnection)
    {
        Validate(permitLimit, window);
        _permitLimit = permitLimit;
        _windowMilliseconds = Math.Max(1, (long)window.TotalMilliseconds);
        _time = timeProvider ?? TimeProvider.System;
    }

    private static void Validate(int permitLimit, TimeSpan window)
    {
        if (permitLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(permitLimit), "Permit limit must be at least 1.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }
    }

    /// <inheritdoc />
    protected override string Script => RedisScripts.SlidingWindow;

    /// <inheritdoc />
    public override bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        try
        {
            // IRateLimitStore is synchronous; the single atomic Lua round-trip is awaited inline.
            var result = EvaluateScriptAsync(key, AcquireArgs()).GetAwaiter().GetResult();
            var limited = (long)result[0] == 1;
            var retryAfterMilliseconds = Math.Max(0, (long)result[1]);
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
            var count = (long)result[2];
            var retryAfterMilliseconds = Math.Max(0, (long)result[1]);
            var nowSeconds = _time.GetUtcNow().ToUnixTimeSeconds();
            var reset = retryAfterMilliseconds > 0
                ? nowSeconds + (long)Math.Ceiling(retryAfterMilliseconds / 1000d)
                : nowSeconds;
            counters = new RateLimitCounters(_permitLimit, Math.Max(0, _permitLimit - count), reset);
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

    private RedisValue[] Args(bool acquire) =>
    [
        _permitLimit,
        _time.GetUtcNow().ToUnixTimeMilliseconds(),
        _windowMilliseconds,
        Guid.NewGuid().ToString("N"),
        acquire ? "1" : "0"
    ];
}
