namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Behavior when Redis is unreachable or an operation times out.
/// </summary>
public enum RedisOutageMode
{
    /// <summary>Allow the request (no limit enforced) and log a warning. Default.</summary>
    FailOpen = 0,

    /// <summary>Reject the request with 429 until Redis recovers.</summary>
    FailClosed = 1,
}

/// <summary>
/// Options for <see cref="RedisRateLimitStore"/> implementations.
/// </summary>
public sealed class RedisRateLimitOptions
{
    /// <summary>
    /// Key prefix applied to every rate-limit key. Defaults to <c>elsie:rl:</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "elsie:rl:";

    /// <summary>
    /// Maximum wall time for a single Redis operation before it counts as an outage. Default 100 ms.
    /// </summary>
    public int OperationTimeoutMilliseconds { get; set; } = 100;

    /// <summary>
    /// Outage policy. Defaults to <see cref="RedisOutageMode.FailOpen"/>.
    /// </summary>
    public RedisOutageMode OutageMode { get; set; } = RedisOutageMode.FailOpen;

    /// <summary>
    /// Retry-After suggested to clients when <see cref="OutageMode"/> is
    /// <see cref="RedisOutageMode.FailClosed"/> and Redis is unreachable. Default 60 s.
    /// </summary>
    public TimeSpan FailClosedRetryAfter { get; set; } = TimeSpan.FromSeconds(60);
}
