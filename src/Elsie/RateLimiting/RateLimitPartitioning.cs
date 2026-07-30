using System.Collections.Concurrent;

namespace Elsie.RateLimiting;

internal static class RateLimitPartitioning
{
    public static bool ShouldCleanup(ref int ops, int partitionCount, int maxPartitions) =>
        Interlocked.Increment(ref ops) % 64 == 0 || partitionCount > maxPartitions;

    public static void TrimToCap<T>(
        ConcurrentDictionary<string, T> partitions,
        int maxPartitions,
        Func<T, long> lastAccessTicks)
    {
        if (partitions.Count <= maxPartitions)
        {
            return;
        }

        foreach (var key in partitions
                     .OrderBy(kv => lastAccessTicks(kv.Value))
                     .Select(kv => kv.Key)
                     .Take(partitions.Count - maxPartitions)
                     .ToArray())
        {
            partitions.TryRemove(key, out _);
        }
    }
}
