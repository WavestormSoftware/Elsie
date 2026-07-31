using System.Collections.Concurrent;

namespace Elsie.RateLimiting;

internal sealed class TokenBucketStore : IRateLimitStore
{
    private readonly double _capacity;
    private readonly double _tokensPerSecond;
    private readonly TimeProvider _time;
    private readonly int _maxPartitions;
    private readonly ConcurrentDictionary<string, Bucket> _partitions =
        new(StringComparer.Ordinal);
    private int _ops;

    public TokenBucketStore(int capacity, double tokensPerSecond, TimeProvider time, int maxPartitions)
    {
        _capacity = capacity;
        _tokensPerSecond = tokensPerSecond;
        _time = time;
        _maxPartitions = maxPartitions;
    }

    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        MaybeCleanup();
        var now = _time.GetUtcNow();
        var bucket = _partitions.GetOrAdd(key, static (_, cap) => new Bucket(cap), _capacity);

        lock (bucket.Gate)
        {
            Refill(bucket, now);

            if (bucket.Tokens < 1d)
            {
                var deficit = 1d - bucket.Tokens;
                var seconds = deficit / _tokensPerSecond;
                retryAfter = TimeSpan.FromSeconds(Math.Max(0.001, seconds));
                return false;
            }

            bucket.Tokens -= 1d;
            bucket.LastAccess = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void Refill(Bucket bucket, DateTimeOffset now)
    {
        if (bucket.LastRefill == DateTimeOffset.MinValue)
        {
            bucket.LastRefill = now;
            bucket.LastAccess = now;
            return;
        }

        var elapsed = (now - bucket.LastRefill).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        bucket.Tokens = Math.Min(_capacity, bucket.Tokens + (elapsed * _tokensPerSecond));
        bucket.LastRefill = now;
    }

    private void MaybeCleanup()
    {
        if (!RateLimitPartitioning.ShouldCleanup(ref _ops, _partitions.Count, _maxPartitions))
        {
            return;
        }

        var cutoff = _time.GetUtcNow() - TimeSpan.FromMinutes(10);
        foreach (var (key, bucket) in _partitions)
        {
            lock (bucket.Gate)
            {
                if (bucket.LastAccess < cutoff)
                {
                    _partitions.TryRemove(key, out _);
                }
            }
        }

        RateLimitPartitioning.TrimToCap(_partitions, _maxPartitions, static b =>
        {
            lock (b.Gate)
            {
                return b.LastAccess.UtcTicks;
            }
        });
    }

    private sealed class Bucket
    {
        public Bucket(double capacity)
        {
            Tokens = capacity;
            LastRefill = DateTimeOffset.MinValue;
            LastAccess = DateTimeOffset.MinValue;
        }

        public object Gate { get; } = new();
        public double Tokens;
        public DateTimeOffset LastRefill;
        public DateTimeOffset LastAccess;
    }
}
