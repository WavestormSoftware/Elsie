using System.Collections.Concurrent;

namespace Elsie.Auth;

/// <summary>
/// In-process session store: bounded (default ~100k entries), sliding TTL,
/// injectable <see cref="TimeProvider"/>. Single-node only — use the Redis package
/// (<c>Elsie.Extensions.Auth.Redis</c>) for multi-instance deployments.
/// </summary>
public sealed class InMemoryElsieSessionStore : IElsieSessionStore
{
    private readonly TimeSpan _defaultTtl;
    private readonly TimeProvider _time;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _ops;

    /// <summary>Creates a store with a default sliding TTL and entry cap.</summary>
    public InMemoryElsieSessionStore(
        TimeSpan? defaultTtl = null,
        int maxEntries = 100_000,
        TimeProvider? timeProvider = null)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(8);
        _time = timeProvider ?? TimeProvider.System;
        _maxEntries = maxEntries < 1 ? throw new ArgumentOutOfRangeException(nameof(maxEntries)) : maxEntries;
    }

    /// <inheritdoc />
    public Task SetAsync(string sessionId, byte[] payload, TimeSpan slidingTtl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(payload);
        MaybeCleanup();

        var nowTicks = _time.GetUtcNow().UtcTicks;
        var entry = _entries.GetOrAdd(sessionId, static _ => new Entry());
        lock (entry.Gate)
        {
            entry.Payload = payload;
            entry.ExpiresTicks = nowTicks + slidingTtl.Ticks;
            entry.LastAccessTicks = nowTicks;
            entry.TtlTicks = slidingTtl.Ticks;
        }

        TrimToCap();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<byte[]?>(null);
        }

        MaybeCleanup();
        var nowTicks = _time.GetUtcNow().UtcTicks;
        if (!_entries.TryGetValue(sessionId, out var entry))
        {
            return Task.FromResult<byte[]?>(null);
        }

        lock (entry.Gate)
        {
            if (entry.ExpiresTicks <= nowTicks)
            {
                _entries.TryRemove(sessionId, out _);
                return Task.FromResult<byte[]?>(null);
            }

            // Sliding renewal on every read: extend by the session's own TTL.
            entry.ExpiresTicks = nowTicks + (entry.TtlTicks > 0 ? entry.TtlTicks : _defaultTtl.Ticks);
            entry.LastAccessTicks = nowTicks;
            return Task.FromResult<byte[]?>(entry.Payload);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _entries.TryRemove(sessionId, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>Current number of live sessions (test/diagnostics).</summary>
    public int Count => _entries.Count;

    private void MaybeCleanup()
    {
        var count = _entries.Count;
        if (count < _maxEntries && Interlocked.Increment(ref _ops) % 256 != 0)
        {
            return;
        }

        var nowTicks = _time.GetUtcNow().UtcTicks;
        foreach (var (key, entry) in _entries)
        {
            lock (entry.Gate)
            {
                if (entry.ExpiresTicks <= nowTicks)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    private void TrimToCap()
    {
        if (_entries.Count <= _maxEntries)
        {
            return;
        }

        // Drop expired first, then evict least-recently-accessed entries.
        MaybeCleanup();
        var overflow = _entries.Count - _maxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var (key, entry) in _entries
                     .OrderBy(static kv => Volatile.Read(ref kv.Value.LastAccessTicks))
                     .Take(overflow + 16))
        {
            _entries.TryRemove(key, out _);
        }
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();
        public byte[] Payload = [];
        public long ExpiresTicks;
        public long LastAccessTicks;
        public long TtlTicks;
    }
}
