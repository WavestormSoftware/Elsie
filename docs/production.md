# Production hardening

This guide covers the request-lifecycle and connection-level knobs introduced in 0.4.1-beta.1:
per-request deadlines, connection governance, output caching, compression, and decompression.
It pairs with [production-checklist.md](production-checklist.md) and [middleware.md](middleware.md).

## Request deadline

`ElsieApp.UseRequestDeadline(TimeSpan)` (or `Services(s => s.AddRequestDeadline(...))`) aborts a
handler that exceeds the span with **408 Request Timeout** when the response has not been started.
The deadline is linked into the handler's dispatch cancellation token, so the handler observes
`RequestAborted` cancellation. WebSocket upgrades and streaming (`BodyWriter` / SSE) responses are
exempt because their handler returns a terminal result immediately.

```csharp
.UseRequestDeadline(TimeSpan.FromSeconds(10))
```

| Concern | Behavior |
|---------|----------|
| Slow handler | Cancelled; 408 when the response hasn't started |
| Fast handler | Unaffected |
| WebSocket / SSE | Exempt — the deadline does not cancel the streaming phase |
| Zero / negative deadline | Disables enforcement (pass-through) |

## Connection governance

`ElsieServerOptions` (via `.Server(o => ...)`) controls connection acceptance and keep-alive:

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxConcurrentConnections` | 10 000 | Total concurrent accepted connections (TCP + HTTP/3). Over the limit, new TCP connections get a graceful **503**; HTTP/3 connections are **refused** (no response possible on QUIC pre-handshake). |
| `MaxConnectionsPerIp` | 0 (off) | Per-source-IP concurrent-connection cap. Opt-in; NATs / shared egress IPs can cause false positives, so set conservatively. Over the cap → 503 (TCP) / refused (h3). |
| `KeepAliveMaxRequests` | 1000 | Max requests per HTTP/1.1 keep-alive connection before the server sends `Connection: close` and closes it. `0` disables the cap. |

```csharp
.Server(o =>
{
    o.MaxConcurrentConnections = 5000;
    o.MaxConnectionsPerIp = 50;      // opt-in
    o.KeepAliveMaxRequests = 2000;
})
```

## Output caching

`ElsieApp.UseOutputCaching()` (or `Services(s => s.AddOutputCaching(...))`) caches successful
GET/HEAD responses in an in-memory LRU (default **1024 entries / 64 MiB**) keyed by
method + route + query + `Accept-Encoding` (pre-compressed variants are memoized independently).
It honors `Cache-Control: no-store` / `no-cache` on the request and response, and composes with
`WithETag` so a cached response is served as **304** when `If-None-Match` matches the stored ETag.

```csharp
.UseOutputCaching()
// or
.Services(s => s.AddOutputCaching(o => { o.MaxEntries = 1024; o.MaxCacheBytes = 64L * 1024 * 1024; }))
```

Only buffered (non-streaming) 200 responses are cached. `Cache-Control: no-store`/`no-cache` on the
request or response opts out, and responses that carry `Set-Cookie` are never cached (a shared cache
would replay one client's cookie to another). Because the cache is in-memory and per-process, it is not shared across
instances — use it for single-node or sticky sessions, and pair with a shared cache (e.g. Redis) for
horizontal scaling.

## Response compression

`ElsieApp.Compression()` enables gzip/brotli response compression (`.Server(o => o.EnableResponseCompression = true)`).
Compression is applied by the host after the Core pipeline, so it composes with output caching —
each `Accept-Encoding` variant is cached as a separate entry. SSE is left uncompressed to preserve
per-event delivery semantics.

## Request decompression

`ElsieApp.UseRequestDecompression()` decodes `gzip`/`deflate`/`br` request bodies (stacked codings
decoded in reverse application order). Unsupported codings → 415; a decoded body above
`ElsieRequestDecompressionOptions.MaxDecompressedBodySize` (default 10 MiB) → 413 mid-stream.

**`ContentLength` after decompression:** decoding is streaming (never buffered), so
`ElsieRequest.ContentLength` keeps reporting the **wire (compressed) size** the client sent. It is
**not** rewritten to the decoded length. Read the decoded stream rather than trusting `ContentLength`
for the decoded byte count.

## See also

- [middleware.md](middleware.md)
- [production-checklist.md](production-checklist.md)
- [http3.md](http3.md)