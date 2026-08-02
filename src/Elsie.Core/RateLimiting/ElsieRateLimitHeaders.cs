using System.Globalization;

namespace Elsie.RateLimiting;

/// <summary>
/// Emits <c>X-RateLimit-Limit</c>, <c>X-RateLimit-Remaining</c> and <c>X-RateLimit-Reset</c>
/// response headers on every response for the request's partition.
/// </summary>
public static class ElsieRateLimitHeaders
{
    /// <summary>
    /// Builds an after-hook that attaches rate-limit counters to the response when the
    /// <paramref name="store"/> supports <see cref="IRateLimitStore.TryPeek"/>.
    /// Stores without peek support (or an outage) leave the response unchanged.
    /// </summary>
    /// <param name="store">The store backing the limit. Must be the same instance passed to the rate-limit gate.</param>
    /// <param name="partitionKey">Partition selector; defaults to <see cref="ElsieRateLimit.DefaultPartitionKey"/>.</param>
    public static Func<ElsieContext, ElsieResult, ElsieResult> Attach(
        IRateLimitStore store,
        Func<ElsieContext, string>? partitionKey = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var keySelector = partitionKey ?? ElsieRateLimit.DefaultPartitionKey;

        return (ctx, result) =>
        {
            var key = keySelector(ctx) ?? "unknown";
            RateLimitCounters counters;
            try
            {
                if (!store.TryPeek(key, out counters))
                {
                    return result;
                }
            }
            catch (NotSupportedException)
            {
                return result;
            }

            return result
                .WithHeader("X-RateLimit-Limit", counters.Limit.ToString(CultureInfo.InvariantCulture))
                .WithHeader("X-RateLimit-Remaining", counters.Remaining.ToString(CultureInfo.InvariantCulture))
                .WithHeader("X-RateLimit-Reset", counters.ResetUnixSeconds.ToString(CultureInfo.InvariantCulture));
        };
    }
}
