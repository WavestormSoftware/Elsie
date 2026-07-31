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
}
