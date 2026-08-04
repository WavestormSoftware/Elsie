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
pass through untouched.

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
