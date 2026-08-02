namespace Elsie.RateLimiting;

/// <summary>
/// Pluggable rate-limit backend. Default implementations are in-process memory stores.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Try to consume one permit for <paramref name="key"/>.
    /// Returns false when limited; <paramref name="retryAfter"/> suggests wait time.
    /// </summary>
    bool TryAcquire(string key, out TimeSpan retryAfter);

    /// <summary>
    /// Reads the current counters for <paramref name="key"/> without consuming a permit.
    /// Returns false when the store cannot report counters
    /// (for example an outage, or a backend that does not support peeking).
    /// </summary>
    bool TryPeek(string key, out RateLimitCounters counters)
    {
        throw new NotSupportedException(
            $"'{GetType().Name}' does not support peeking rate-limit counters. " +
            "Implement IRateLimitStore.TryPeek to emit X-RateLimit-* headers.");
    }
}
