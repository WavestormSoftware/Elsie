# Rate limiting

Built into **Elsie.Core** (`Elsie.RateLimiting`) as **before-hooks**.

```csharp
Before(ElsieRateLimit.FixedWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.SlidingWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.TokenBucket(capacity: 20, tokensPerSecond: 5));

// Custom partition (e.g. API key):
Before(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1),
    partitionKey: ctx => ctx.Request.GetHeader("X-Api-Key") ?? "anon"));
```

| Algorithm | Behavior |
|-----------|----------|
| **FixedWindow** | Cap requests per wall-clock window |
| **SlidingWindow** | Smoother window across recent history |
| **TokenBucket** | Burst up to `capacity`, refill at `tokensPerSecond` |

Returns **429** problem+json + **`Retry-After`** when exceeded. Uses `TimeProvider` (injectable for tests).

## Partition keys

- **Default:** `RemoteIp` only (no `X-Forwarded-For`).
- **Proxy:** `ElsieRateLimit.ForwardedPartitionKey` only when forwarded headers are trusted (`UseForwardedHeaders`).

## Custom store

Implement `IRateLimitStore` and pass `store:` into `FixedWindow` / `SlidingWindow` / `TokenBucket` (shared or distributed counters).

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
- [security.md](security.md)
