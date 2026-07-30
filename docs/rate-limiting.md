# Rate limiting

Ships in **`Elsie.Core`** (via meta package `Elsie`) — before-hook factories with in-memory stores.

## Fixed / sliding window

```csharp
using Elsie.RateLimiting;

// Module-wide
Before(ElsieRateLimit.FixedWindow(permitLimit: 100, window: TimeSpan.FromMinutes(1)));

Before(ElsieRateLimit.SlidingWindow(
    permitLimit: 30,
    window: TimeSpan.FromSeconds(10),
    partitionKey: ctx => ctx.Request.GetHeader("X-Api-Key") ?? "anon",
    timeProvider: TimeProvider.System));
```

Each factory call creates a **private store** shared by that returned hook instance.

## Partition key

Default: `Request.RemoteIp`, else first `X-Forwarded-For` hop, else `"unknown"`.

```csharp
ElsieRateLimit.DefaultPartitionKey(ctx);
```

## Response

When exceeded:

- **429** `application/problem+json`
- **`Retry-After`** header (seconds)

## Testing

Pass a fake **`TimeProvider`** for deterministic windows. Bounded partition count (`maxPartitions`, default 10_000) with cleanup.

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
- [auth.md](auth.md)

## Security notes

1. Default partition uses `RemoteIp`, else first `X-Forwarded-For` hop — **XFF is spoofable** unless a trusted proxy overwrites it. Prefer an explicit `partitionKey` (API key, user id).
2. In-memory stores **evict** partitions under `maxPartitions` (default 10_000) — eviction is **fail-open** for that key (fresh budget).

