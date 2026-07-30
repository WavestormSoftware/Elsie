using System.Globalization;

namespace Elsie.RateLimiting;

/// <summary>
/// Before-hook factories for fixed/sliding window rate limits.
/// Each call creates a private in-memory store shared by the returned hook.
/// </summary>
public static class ElsieRateLimit
{
    /// <summary>
    /// Fixed window: at most <paramref name="permitLimit"/> requests per <paramref name="window"/> per partition.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> FixedWindow(
        int permitLimit,
        TimeSpan window,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        int maxPartitions = 10_000)
    {
        Validate(permitLimit, window, maxPartitions);
        var store = new FixedWindowStore(permitLimit, window, timeProvider ?? TimeProvider.System, maxPartitions);
        var keySelector = partitionKey ?? DefaultPartitionKey;
        return ctx =>
        {
            var key = keySelector(ctx) ?? "unknown";
            return store.TryAcquire(key, out var retryAfter)
                ? null
                : TooManyRequests(retryAfter);
        };
    }

    /// <summary>
    /// Sliding window: at most <paramref name="permitLimit"/> requests in any trailing <paramref name="window"/>.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> SlidingWindow(
        int permitLimit,
        TimeSpan window,
        Func<ElsieContext, string>? partitionKey = null,
        TimeProvider? timeProvider = null,
        int maxPartitions = 10_000)
    {
        Validate(permitLimit, window, maxPartitions);
        var store = new SlidingWindowStore(permitLimit, window, timeProvider ?? TimeProvider.System, maxPartitions);
        var keySelector = partitionKey ?? DefaultPartitionKey;
        return ctx =>
        {
            var key = keySelector(ctx) ?? "unknown";
            return store.TryAcquire(key, out var retryAfter)
                ? null
                : TooManyRequests(retryAfter);
        };
    }

    /// <summary>Default partition: remote IP, else first X-Forwarded-For hop, else <c>unknown</c>.</summary>
    public static string DefaultPartitionKey(ElsieContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!string.IsNullOrWhiteSpace(ctx.Request.RemoteIp))
        {
            return ctx.Request.RemoteIp;
        }

        var forwarded = ctx.Request.GetHeader("X-Forwarded-For");
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var comma = forwarded.IndexOf(',');
            var hop = comma < 0 ? forwarded : forwarded[..comma];
            hop = hop.Trim();
            if (hop.Length > 0)
            {
                return hop;
            }
        }

        return "unknown";
    }

    private static void Validate(int permitLimit, TimeSpan window, int maxPartitions)
    {
        if (permitLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(permitLimit), "Permit limit must be at least 1.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }

        if (maxPartitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPartitions), "Max partitions must be at least 1.");
        }
    }

    private static ElsieResult TooManyRequests(TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        return ElsieResult.Problem(429, "Too Many Requests", "Rate limit exceeded.")
            .WithHeader("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
    }
}
