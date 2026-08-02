using Elsie.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Fixed-window rate limit stored in Redis (INCR + EXPIRE, window starts at the
/// first request for a partition). Mirrors the in-memory <c>FixedWindowStore</c> semantics.
/// </summary>
public sealed class RedisFixedWindowStore : RedisRateLimitStore
{
    private readonly int _permitLimit;
    private readonly long _windowSeconds;
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates a store over an existing multiplexer. The caller owns the multiplexer.
    /// </summary>
    public RedisFixedWindowStore(
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
    public static RedisFixedWindowStore Create(
        string connectionString,
        int permitLimit,
        TimeSpan window,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        => new(Connect(connectionString), permitLimit, window, options, timeProvider, logger, ownsConnection: true);

    /// <summary>Test seam constructor (no live Redis required).</summary>
    internal RedisFixedWindowStore(
        IRedisRateLimitExecutor executor,
        int permitLimit,
        TimeSpan window,
        RedisRateLimitOptions? options = null,
        TimeProvider? timeProvider = null)
        : base(executor, options, logger: null)
    {
        Validate(permitLimit, window);
        _permitLimit = permitLimit;
        _windowSeconds = Math.Max(1, (long)window.TotalSeconds);
        _time = timeProvider ?? TimeProvider.System;
    }

    private RedisFixedWindowStore(
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
        _windowSeconds = Math.Max(1, (long)window.TotalSeconds);
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
    protected override string Script => RedisScripts.FixedWindow;

    /// <inheritdoc />
    public override bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        try
        {
            // IRateLimitStore is synchronous; the single atomic Lua round-trip is awaited inline.
            var result = EvaluateScriptAsync(key, AcquireArgs()).GetAwaiter().GetResult();
            var limited = (long)result[2] == 1;
            retryAfter = limited ? TimeSpan.FromSeconds(Math.Max(0, (long)result[1])) : TimeSpan.Zero;
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
            var count = (long)result[0];
            var ttlSeconds = (long)result[1];
            var nowSeconds = _time.GetUtcNow().ToUnixTimeSeconds();
            var reset = ttlSeconds > 0 ? nowSeconds + ttlSeconds : nowSeconds + _windowSeconds;
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
        _windowSeconds,
        _time.GetUtcNow().ToUnixTimeSeconds(),
        acquire ? "1" : "0"
    ];
}
