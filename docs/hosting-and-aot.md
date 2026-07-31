# Hosting and AOT

## Default host

**Package `Elsie`** (assembly/namespaces `Elsie.Web`) ships a custom TCP server (`ElsieApp`):

```csharp
ElsieApp.Run<App>(args);
// or
ElsieApp.Create(args)
    .Module<App>()
    .Listen("http://127.0.0.1:5000")
    .Run();
```

| API | Notes |
|-----|--------|
| `ElsieApp.Run` / `RunAsync` | Generic module or scan |
| `ElsieWeb.Run` | Thin wrapper over `ElsieApp` |
| `ElsieApp.Create(args)` | Fluent host builder |
| `.Listen(url)` / `.Listen(url, o => …)` | Bind endpoints; HTTPS needs a certificate |
| `.OpenApi(...)` | OpenAPI JSON (+ optional Scalar UI) |
| `.StaticFiles(...)` | Built-in static file serving |
| `.Services(...)` / `.Module<T>()` | MS.DI + modules |

Default listen (when none specified): `http://127.0.0.1:5000`.  
Override with `.Listen(...)` or `--urls http://…`.

## TLS and protocols

```csharp
.Listen("https://0.0.0.0:5001", https => https
    .CertificateFromPem("cert.pem", "key.pem")
    .WithProtocols(ElsieHttpProtocols.Http1AndHttp2))
```

- Default protocol: **HTTP/1.1**
- **HTTP/2**: opt-in via `ElsieHttpProtocols.Http2` / `Http1AndHttp2` (TLS + ALPN)
- HTTP/2 supports SETTINGS/HEADERS/CONTINUATION/DATA/PING/WINDOW_UPDATE/RST/GOAWAY, concurrent streams, HPACK static + literal (+ Huffman decode)
- Putting TLS termination on a reverse proxy and serving cleartext HTTP/1.1 is fully supported

## Server limits

```csharp
.Server(o =>
{
    o.MaxRequestBodyBytes = 5 * 1024 * 1024;
    o.MaxHeaderBytes = 32 * 1024;
    o.MaxConcurrentStreams = 100;
    o.MaxFrameSize = 16384;
    o.MaxConcurrentConnections = 10_000;
    o.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
})
.Compression()
.Logging(loggerFactory)
```

Bodies over the limit return **413** problem+json and close the connection.

## Reverse proxy / forwarded headers

Deploy TLS at nginx/Caddy and speak cleartext HTTP/1.1 to Elsie, **or** terminate TLS on Elsie.

When behind a trusted proxy, enable:

```csharp
.Server(o => o.UseForwardedHeaders = true)
```

This honors `X-Forwarded-For` (client IP), `X-Forwarded-Proto`, and `X-Forwarded-Host`.  
**Do not enable** on the public internet without a trusted proxy stripping client-supplied forwarded headers.

### Production profile (recommended)

```text
Client → TLS (proxy) → http://127.0.0.1:5000 (Elsie, UseForwardedHeaders)
```

## WebSockets

```csharp
Get("/ws", () => ElsieResult.WebSocket(async (ws, ct) =>
{
    var msg = await ws.ReceiveAsync(ct);
    if (msg?.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
        await ws.SendTextAsync("echo:" + msg.GetText(), ct);
}));
```

HTTP/1.1 upgrade only (H2 extended CONNECT later).

## Pipeline features (no middleware order)

Features register on the host / DI — not as `UseX` ordering:

- CORS: `AddElsieCors` → preflight filter + after-hook ACAO  
- Auth: `AddElsieAuth` → principal attacher + cookie/JWT  
- Static / OpenAPI: `.StaticFiles` / `.OpenApi` on `ElsieApp`

## JSON source generation

Elsie uses **`System.Text.Json`**. For trimmed / AOT-friendly serialization:

```csharp
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(CreateTodo))]
internal partial class AppJsonContext : JsonSerializerContext;

ElsieApp.Create(args)
    .Configure(o =>
    {
        o.JsonSerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = AppJsonContext.Default
        };
    })
    // ...
    .Run();
```

Static `ElsieResult.Json` still uses **`ElsieJson.DefaultOptions`** unless you pass `options:` explicitly.

## Trimming / AOT guidance

| Area | Guidance |
|------|----------|
| Core routing | Expression trees / reflection at **startup** for route table; request path is match + dispatch |
| OpenAPI schemas | Reflection over DTO shapes at **document build** — keep document generation out of native AOT critical path or pregenerate |
| Views (Fluid) | Template parse/runtime — treat as non-AOT-first unless you validate your Fluid version's trim surface |
| BindQuery / BindRoute / BindJson | Reflection binders — prefer explicit accessors or source-gen DTOs for strict AOT |
| Modules | Concrete types registered in DI — avoid relying on entry-assembly scan under trim; use **`AddElsieModule<T>()`** |

Elsie does **not** currently ship a full native-AOT guarantee. Prefer:

1. Explicit module registration  
2. `ctx.Json` + `JsonSerializerContext`  
3. Avoid OpenAPI reflection in the trimmed published app if you hit linker warnings (serve a prebuilt document instead)

## Multi-TFM

Libraries target **`net8.0;net10.0`**. Samples commonly use `net8.0` for simplicity.

## See also

- [getting-started.md](getting-started.md)
- [openapi.md](openapi.md)
- [testing.md](testing.md)
