# Request lifecycle

Detailed path from socket to bytes.

## 1. Accept

`ElsieServer` accepts TCP connections under `MaxConcurrentConnections`. Excess connections are closed immediately (`elsie.connections_rejected` metric).

## 2. TLS (optional)

`SslStream` with TLS 1.2/1.3. ALPN selects HTTP/1.1 or HTTP/2.

## 3. Parse

- **HTTP/1.1:** `Http1RequestReader` (header timeout = `RequestHeadersTimeout`).
- **HTTP/2:** `Http2Connection` frame loop (experimental).

Bodies are size-capped by `MaxRequestBodyBytes` → **413**.

## 4. Scope

Per request: `IServiceScope` + `ElsieRequest` (scheme/host/path/remote IP, optional forwarded headers).

## 5. Trace id

If missing, host sets `TraceIdentifier` from `X-Request-Id` / `X-Correlation-Id` or a new GUID. Echoed as `X-Request-Id` on the response. `ActivitySource("Elsie")` wraps dispatch.

## 6. HostDispatch order

1. OpenAPI document / UI
2. Principal attachers
3. Dispatcher (middleware pipeline — see below)

Static files are **middleware** now (`StaticFileMiddleware`, registered by `.StaticFiles(...)`)
and short-circuit inside the pipeline before routes. CORS is middleware (`ElsieCorsMiddleware`,
registered by `AddElsieCors`).

## 7. Dispatcher

`ElsieDispatcher`: route lookup → middleware pipeline → handler.

- Route values are populated right after lookup, **before** the pipeline runs, so middleware can
  bind `{tenant}`-style parameters.
- The pipeline is a single ordered chain: app middleware (FIFO pre / LIFO post) → module
  middleware → handler.
- The terminal `ElsieExceptionHandlerMiddleware` (outermost, registered automatically) maps
  `ElsieRequestException` → problem, then `ElsieOptions.ExceptionHandler` (default: safe 500),
  or rethrows when the handler is `null`.
- Unmatched route → 404 (`NotFound`); matched-path wrong-method → 405 with `Allow`.

## 8. Write

`FromDispatch` merges response headers + cookies. Optional gzip/br compression. HTTP/1.1 keep-alive or chunked SSE. WebSocket upgrade ends the HTTP transaction.

## See also

- [middleware.md](middleware.md)
- [pipelines-and-errors.md](pipelines-and-errors.md)
