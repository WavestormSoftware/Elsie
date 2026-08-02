namespace Elsie.RateLimiting;

/// <summary>
/// Snapshot of a rate-limit counter for a partition, used to emit
/// <c>X-RateLimit-Limit</c> / <c>X-RateLimit-Remaining</c> / <c>X-RateLimit-Reset</c>
/// response headers without consuming a permit.
/// </summary>
public readonly record struct RateLimitCounters(long Limit, long Remaining, long ResetUnixSeconds);
