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
2. Static files (streamed; ETag / If-Modified-Since / Range)  
3. Principal attachers  
4. Request filters  
5. Dispatcher  

## 7. Dispatcher

Match → before hooks → handler → after hooks. Errors: `MapException` → module `OnError` → `ExceptionHandler`.

## 8. Write

`FromDispatch` merges response headers + cookies. Optional gzip/br compression. HTTP/1.1 keep-alive or chunked SSE. WebSocket upgrade ends the HTTP transaction.
