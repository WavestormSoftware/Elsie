using System.Collections.Concurrent;

namespace Elsie.RateLimiting;

internal sealed class FixedWindowStore : IRateLimitStore
{
    private readonly int _permitLimit;
    private readonly long _windowTicks;
    private readonly TimeProvider _time;
    private readonly int _maxPartitions;
    private readonly ConcurrentDictionary<string, WindowCounter> _partitions =
        new(StringComparer.Ordinal);
    private int _ops;

    public FixedWindowStore(int permitLimit, TimeSpan window, TimeProvider time, int maxPartitions)
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
        var windowId = nowTicks / _windowTicks;
        var counter = _partitions.GetOrAdd(key, static _ => new WindowCounter());

        lock (counter.Gate)
        {
            if (counter.WindowId != windowId)
            {
                counter.WindowId = windowId;
                counter.Count = 0;
            }

            if (counter.Count >= _permitLimit)
            {
                var windowEnd = (windowId + 1) * _windowTicks;
                retryAfter = TimeSpan.FromTicks(Math.Max(0, windowEnd - nowTicks));
                return false;
            }

            counter.Count++;
            counter.LastAccessTicks = nowTicks;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public bool TryPeek(string key, out RateLimitCounters counters)
    {
        MaybeCleanup();
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var windowId = nowTicks / _windowTicks;
        var windowEndTicks = (windowId + 1) * _windowTicks;
        var reset = new DateTimeOffset(windowEndTicks, TimeSpan.Zero).ToUnixTimeSeconds();

        if (!_partitions.TryGetValue(key, out var counter))
        {
            counters = new RateLimitCounters(_permitLimit, _permitLimit, reset);
            return true;
        }

        lock (counter.Gate)
        {
            if (counter.WindowId != windowId)
            {
                counters = new RateLimitCounters(_permitLimit, _permitLimit, reset);
                return true;
            }

            var remaining = Math.Max(0, _permitLimit - counter.Count);
            counters = new RateLimitCounters(_permitLimit, remaining, reset);
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
        var currentWindow = nowTicks / _windowTicks;
        foreach (var (key, counter) in _partitions)
        {
            lock (counter.Gate)
            {
                if (counter.WindowId < currentWindow - 1)
                {
                    _partitions.TryRemove(key, out _);
                }
            }
        }

        RateLimitPartitioning.TrimToCap(_partitions, _maxPartitions, static c =>
        {
            lock (c.Gate)
            {
                return c.LastAccessTicks;
            }
        });
    }

    private sealed class WindowCounter
    {
        public object Gate { get; } = new();
        public long WindowId;
        public int Count;
        public long LastAccessTicks;
    }
}
