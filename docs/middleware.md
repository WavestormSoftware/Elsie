# Middleware

Elsie's request pipeline is a single ordered middleware chain (replacing the legacy
`Before` / `After` / `OnError` / `MapException` hooks — hooks are deprecated and removed).

## The model

```csharp
public interface IElsieMiddleware
{
    Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next);
}
```

- `next` continues the pipeline (its delegate type is `Func<ElsieContext, Task>`).
- Code before `await next(context)` runs on the way **in** — FIFO in registration order.
- Code after `await next(context)` runs on the way **back out** — LIFO.
- Short-circuit by setting `context.Result` and returning without calling `next`.

`ElsieContext.Result` is the pipeline outcome (`ElsieResult`). When the pipeline completes
without a result the request is not handled (404).

## Registration

Application-wide (host):

```csharp
var app = ElsieApp.Create()
    .Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-App"] = "1";      // pre (FIFO)
        await next(ctx);
        ctx.Response.Headers["X-App-Post"] = "1"; // post (LIFO)
    })
    .Use<MyMiddleware>()                          // DI-resolved per request scope
    .Module<MyModule>();
```

Module-scoped (runs only for that module's routes, between the app pipeline and the handler):

```csharp
public sealed class MyModule : ElsieModule
{
    public MyModule()
    {
        Use(async (ctx, next) => { ctx.Response.Headers["X-Mod"] = "1"; await next(ctx); });
        Get("/x", () => ElsieResult.Text("ok"));
    }
}
```

## Built-ins as middleware

The existing gate/after **factories plug straight into the pipeline**:

```csharp
.Use(ElsieAuth.RequireApiKey("secret"))                       // gate → 401 when missing
.Use(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1))) // gate → 429 when limited
.Use(ElsieSecurityHeaders.DefaultAfter())                       // transform on the way out
.Use(ElsieRateLimitHeaders.Attach(store))                       // X-RateLimit-* on the way out
.Use(ElsieAntiforgeryService.RequireAntiforgery())              // async gate → 403 when invalid
```

CORS ships a dedicated middleware (`Elsie.Cors`); `AddElsieCors` registers it into the app pipeline automatically:

```csharp
.Services(s => s.AddElsieCors(o => o.AddDefaultPolicy(p => p.AllowOrigin("https://app.example"))))
// preflight 204 + ACAO on actuals — no extra Use call needed
```

Inbound request decompression (`Elsie.RequestDecompression`) is Core-level, so it covers every
protocol: `AddRequestDecompression` / `ElsieApp.UseRequestDecompression()` decode `gzip`/`deflate`/`br`
bodies (stacked codings are decoded in reverse application order), reject unsupported codings with
415, and fail over-limit decoded bodies with 413 mid-stream (default cap 10 MiB via
`ElsieRequestDecompressionOptions.MaxDecompressedBodySize`). Requests without `Content-Encoding`
pass through untouched. Note: decoding is streaming, so `ElsieRequest.ContentLength` keeps reporting
the wire (compressed) size — it is **not** rewritten to the decoded length.

## Per-request deadline

`ElsieApp.UseRequestDeadline(TimeSpan)` (Core `AddRequestDeadline`) aborts a handler that exceeds the
span with `408 Request Timeout` when the response has not been started. The deadline is linked into
the handler's dispatch cancellation token, so the handler observes `RequestAborted` cancellation.
WebSocket upgrades and streaming (`BodyWriter`/SSE) responses are exempt — their handler returns a
terminal result immediately.

```csharp
.UseRequestDeadline(TimeSpan.FromSeconds(10))
// or
.Services(s => s.AddRequestDeadline(o => o.Deadline = TimeSpan.FromSeconds(10)))
```

## Output caching

`ElsieApp.UseOutputCaching()` (Core `AddOutputCaching`) caches successful GET/HEAD responses in an
in-memory LRU (default 1024 entries / 64 MiB) keyed by method + route + query + `Accept-Encoding`
(pre-compressed variants memoized independently). It honors `Cache-Control: no-store`/`no-cache` on
the request and response, and composes with `WithETag` so a cached response is served as `304` when
`If-None-Match` matches the stored ETag.

```csharp
.UseOutputCaching()
// or
.Services(s => s.AddOutputCaching(o => { o.MaxEntries = 1024; o.MaxCacheBytes = 64L * 1024 * 1024; }))
```

## 103 Early Hints

`ctx.SendEarlyHints(params string[] links)` (RFC 9118) emits `103 Early Hints` with `Link` headers
before the final response on HTTP/1.1, HTTP/2, and HTTP/3. Repeatable; a no-op once the response has
started or for a WebSocket upgrade.

```csharp
Get("/blog", ctx =>
{
    ctx.SendEarlyHints("</app.css>; rel=preload; as=style", "</app.js>; rel=preload; as=script");
    return ElsieResult.Text("...");
});
```

## Ordering example

```csharp
.Use(A)   // A-pre → B-pre → handler → B-post → A-post
.Use(B)
```

## Exceptions

The terminal `ElsieExceptionHandlerMiddleware` (registered automatically as the outermost app
middleware) maps exceptions: `ElsieRequestException` → problem result; everything else →
`ElsieOptions.ExceptionHandler` (default: safe 500 problem without exception detail,
`ShowExceptionDetails` opts into the HTML page) or rethrow when the handler is `null`.
Typed mapping is plain middleware (`try` / `catch` around `await next`).

## See also

- [lifecycle.md](lifecycle.md)
- [pipelines-and-errors.md](pipelines-and-errors.md)
- [auth.md](auth.md)
- [rate-limiting.md](rate-limiting.md)
