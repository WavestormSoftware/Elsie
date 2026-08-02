# Rate limiting

Built into **Elsie.Core** (`Elsie.RateLimiting`). Rate-limit gates are middleware factories.

```csharp
Use(ElsieRateLimit.FixedWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
Use(ElsieRateLimit.SlidingWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
Use(ElsieRateLimit.TokenBucket(capacity: 20, tokensPerSecond: 5));

// Custom partition (e.g. API key):
Use(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1),
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

## Rate-limit response headers

`ElsieRateLimitHeaders.Attach(store)` is an after-style transform that adds `X-RateLimit-Limit`,
`X-RateLimit-Remaining` and `X-RateLimit-Reset` (unix seconds) to every response. It requires
`IRateLimitStore.TryPeek` (implemented by all built-in stores); unsupported stores simply
skip the headers.

```csharp
var store = new FixedWindowStore(permitLimit: 30, window: TimeSpan.FromMinutes(1));
Use(ctx => store.TryAcquire(ctx.Request.RemoteIp ?? "unknown", out var retryAfter)
    ? null : ElsieResult.Problem(429, "Too Many Requests", "limited"));
Use(ElsieRateLimitHeaders.Attach(store));
```

## Custom store

Implement `IRateLimitStore` (and `TryPeek` for headers) and pass `store:` into `FixedWindow` /
`SlidingWindow` / `TokenBucket` (shared or distributed counters).

## Redis (distributed)

Package **`Elsie.Extensions.RateLimiting.Redis`** ships Redis-backed stores that mirror the
in-memory algorithms, implemented as **atomic Lua scripts** (one round-trip per request).

```csharp
using StackExchange.Redis;

var redis = ConnectionMultiplexer.Connect("localhost:6379");

// Shared multiplexer (recommended — reuse one mux per app)
Use(RedisRateLimit.FixedWindow(redis, permitLimit: 1000, window: TimeSpan.FromMinutes(1)));
Use(ElsieRateLimitHeaders.Attach(new RedisFixedWindowStore(redis, 1000, TimeSpan.FromMinutes(1))));

// Or a dedicated connection string (the store owns the connection):
Use(RedisRateLimit.SlidingWindow("localhost:6379", 1000, TimeSpan.FromMinutes(1)));
```

### Keys

All keys are prefixed with `elsie:rl:` by default (`RedisRateLimitOptions.KeyPrefix`), followed
by the partition key — e.g. `elsie:rl:203.0.113.7`.

- **FixedWindow:** `INCR` + `EXPIRE` on a single key (window starts at the first request).
- **SlidingWindow:** a sorted set of per-request timestamps (`ZADD`/`ZREMRANGEBYSCORE`).
- **TokenBucket:** a hash with `tokens` / `ts` fields and lazy refill math.

### Outage policy

Redis operations are capped at a **~100 ms timeout** (`OperationTimeoutMilliseconds`).
When Redis is unreachable the default behavior is **fail-open** (allow the request, log a
warning). Set `RedisOutageMode.FailClosed` to reject with 429 until Redis recovers:

```csharp
var options = new RedisRateLimitOptions { OutageMode = RedisOutageMode.FailClosed };
Use(RedisRateLimit.FixedWindow(redis, 1000, TimeSpan.FromMinutes(1), options: options));
```

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
- [security.md](security.md)
