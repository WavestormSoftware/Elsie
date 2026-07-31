# Rate limiting

Built into **Elsie.Core** (`Elsie.RateLimiting`) as **before-hooks**.

```csharp
Before(ElsieRateLimit.FixedWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.SlidingWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.TokenBucket(capacity: 20, tokensPerSecond: 5));
```

Partitioning defaults to remote IP when available (`ElsieRequest.RemoteIp`).

Returns **429** problem+json when exceeded.

## Partition keys

- **Default:** `RemoteIp` only (no `X-Forwarded-For`).
- **Proxy:** `ElsieRateLimit.ForwardedPartitionKey` only when forwarded headers are trusted.

## Custom store

Implement `IRateLimitStore` and pass `store:` into `FixedWindow` / `SlidingWindow` / `TokenBucket`.

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
