# Architecture

Elsie is a **host-agnostic core** plus a **custom HTTP host** (no ASP.NET).

## Packages

| Package | Role |
|---------|------|
| **Elsie** | HTTP host (`ElsieApp`, assembly `Elsie.Web.dll`) |
| **Elsie.Core** | Modules, routing, dispatcher, results, health, rate limit |
| **Elsie.Auth** | Cookie/JWT, antiforgery, OIDC helpers |
| **Elsie.Cors** | CORS preflight + ACAO |
| **Elsie.Views** | Fluid/Liquid |
| **Elsie.Validation** | DataAnnotations → `IElsieValidator` |
| **Elsie.Testing** | In-memory + loopback hosts for app tests |

## Request lifecycle

```text
TCP accept (+ optional TLS/ALPN)
  → HTTP/1.1 parse  or  HTTP/2 streams (experimental subset)
  → HostDispatch
       OpenAPI / static files (short-circuit)
       Principal attachers (Auth)
       IElsieRequestFilter (CORS preflight, …)
       ElsieDispatcher
         RouteTable.Lookup
         app Before → module Before → handler
         module After → app After
         exception maps / OnError / ExceptionHandler
  → ElsieHttpResponse.FromDispatch
  → optional compression, X-Request-Id
  → write status/headers/body (or WebSocket upgrade)
```

## Design rules

- Core never references the host.
- Modules are **singletons** — resolve scoped services from `ctx.Services`.
- Single materialize path: `ElsieHttpResponse.FromDispatch`.
- MS.DI only; `ValidateScopes` on the host.
- Security defaults: no XFF unless opted in; cookie tickets require a key; rate limit does not trust XFF by default.
- Cookie/antiforgery `SameSite` uses core `ElsieSameSite` (not an Auth-local enum).
- Package id **`Elsie`** is the host; assembly remains `Elsie.Web.dll`. Do not publish package id `Elsie.Web`.

## Observability

- `ActivitySource("Elsie")` around dispatch
- `Meter("Elsie")` — connections, rejections, request totals
- Optional `ILoggerFactory` via `.Logging(...)`
- Request `TraceIdentifier` echoed as `X-Request-Id`

## HTTP/2

HTTP/2 support is a **subset** (settings, headers, data, ping, window update, rst, goaway). Not h2spec-complete. Prefer HTTP/1.1 behind a reverse proxy for production unless you validate your traffic profile.
