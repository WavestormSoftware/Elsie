using System.Collections.Concurrent;

namespace Elsie.RateLimiting;

internal sealed class SlidingWindowStore : IRateLimitStore
{
    private readonly int _permitLimit;
    private readonly long _windowTicks;
    private readonly TimeProvider _time;
    private readonly int _maxPartitions;
    private readonly ConcurrentDictionary<string, Partition> _partitions =
        new(StringComparer.Ordinal);
    private int _ops;

    public SlidingWindowStore(int permitLimit, TimeSpan window, TimeProvider time, int maxPartitions)
    {
        _permitLimit = permitLimit;
        _windowTicks = window.Ticks;
        _time = time;
        _maxPartitions = maxPartitions;
    }

    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        MaybeCleanup();
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var cutoff = nowTicks - _windowTicks;
        var partition = _partitions.GetOrAdd(key, static _ => new Partition());

        lock (partition.Gate)
        {
            while (partition.Timestamps.Count > 0 && partition.Timestamps.Peek() <= cutoff)
            {
                partition.Timestamps.Dequeue();
            }

            if (partition.Timestamps.Count >= _permitLimit)
            {
                var oldest = partition.Timestamps.Peek();
                retryAfter = TimeSpan.FromTicks(Math.Max(0, oldest + _windowTicks - nowTicks));
                partition.LastAccessTicks = nowTicks;
                return false;
            }

            partition.Timestamps.Enqueue(nowTicks);
            partition.LastAccessTicks = nowTicks;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public bool TryPeek(string key, out RateLimitCounters counters)
    {
        MaybeCleanup();
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var cutoff = nowTicks - _windowTicks;
        var reset = new DateTimeOffset(nowTicks, TimeSpan.Zero).ToUnixTimeSeconds();

        if (!_partitions.TryGetValue(key, out var partition))
        {
            counters = new RateLimitCounters(_permitLimit, _permitLimit, reset);
            return true;
        }

        lock (partition.Gate)
        {
            while (partition.Timestamps.Count > 0 && partition.Timestamps.Peek() <= cutoff)
            {
                partition.Timestamps.Dequeue();
            }

            if (partition.Timestamps.Count == 0)
            {
                counters = new RateLimitCounters(_permitLimit, _permitLimit, reset);
                return true;
            }

            var oldest = partition.Timestamps.Peek();
            var remaining = Math.Max(0, _permitLimit - partition.Timestamps.Count);
            var resetTicks = oldest + _windowTicks;
            counters = new RateLimitCounters(
                _permitLimit,
                remaining,
                new DateTimeOffset(resetTicks, TimeSpan.Zero).ToUnixTimeSeconds());
            return true;
        }
    }

    private void MaybeCleanup()
    {
        if (!RateLimitPartitioning.ShouldCleanup(ref _ops, _partitions.Count, _maxPartitions))
        {
            return;
        }

        var nowTicks = _time.GetUtcNow().UtcTicks;
        var cutoff = nowTicks - _windowTicks;
        foreach (var (key, partition) in _partitions)
        {
            lock (partition.Gate)
            {
                while (partition.Timestamps.Count > 0 && partition.Timestamps.Peek() <= cutoff)
                {
                    partition.Timestamps.Dequeue();
                }

                if (partition.Timestamps.Count == 0)
                {
                    _partitions.TryRemove(key, out _);
                }
            }
        }

        RateLimitPartitioning.TrimToCap(_partitions, _maxPartitions, static p =>
        {
            lock (p.Gate)
            {
                return p.LastAccessTicks;
            }
        });
    }

    private sealed class Partition
    {
        public object Gate { get; } = new();
        public Queue<long> Timestamps { get; } = new();
        public long LastAccessTicks;
    }
}
