# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Elsie is **unreleased** software; alphas may include breaking API changes.

## [0.3.0-alpha.1] — unreleased

### Added
- **`ElsieApp`** fluent host — TCP HTTP/1.1 server, listen URLs, OpenAPI routes, static files
- HTTPS listen options (PEM/PFX via `SslStream`); opt-in HTTP/2 over ALPN (HEADERS/CONTINUATION/DATA, concurrent streams, HPACK)
- `.Server(...)` limits: max body/headers/frame size, concurrent H2 streams
- WebSocket upgrade via `ElsieResult.WebSocket(...)` (RFC 6455 framing, text/binary/ping/close)
- TLS + HTTP/2 integration tests with self-signed certificates
- Multipart `multipart/form-data` field binding in core (`BindFormAsync`)
- `ElsiePrincipal` + native cookie tickets (AES-GCM) and JWT validation (`System.IdentityModel.Tokens.Jwt`)
- CORS preflight as `IElsieRequestFilter` (no middleware pipeline)
- Loopback `ElsieTestHost` over the real host
- Sample **`Elsie.Sample.Dashboard`** — multi-page Fluid views with cookie auth + form posts

### Changed
- **Elsie.Web** is a self-contained host (MS.DI only); no shared-framework host dependency
- Host entrypoint: prefer `ElsieApp` / `ElsieWeb.Run` (thin wrapper)
- Auth/CORS packages wire through DI + host filters/principal attachers
- Package layout: HealthChecks + RateLimiting in **Elsie.Core**; meta **Elsie** → **Elsie.Web** → **Elsie.Core**
- Default `ExceptionHandler` returns 500 problem+json without exception detail (set `null` to rethrow)
- Cookie defaults: `HttpOnly = true`, `SameSite = Lax` (`Secure` still false for local HTTP)
- Health checks hide exception details by default; optional `DefaultTimeout`
- Routing: precompiled constraint predicates + first-segment candidate index
- OpenAPI JSON baked when the host starts
- `BindJsonAsync` returns **415** for non-JSON Content-Type (empty type still accepted)
- Query/form binding supports repeated keys → `string[]` / `List<T>`
- `ctx.Problem(...)` adds `instance` + optional `traceId`

### Security
- Constant-time compare for `ElsieAuth.RequireHeader` / `RequireApiKey`
- Cookie session tickets encrypted with AES-GCM

### Removed
- `WebApplication` / `MapElsie` / `UseElsie` / `MapElsieOpenApi` / `TryGetHttpContext` host APIs
- ASP.NET Authentication / TestServer dependencies from Auth, Cors, Testing
- Package IDs: **Elsie.AspNetCore** (historical), **Elsie.HealthChecks**, **Elsie.RateLimiting**, **Elsie.FluentValidation**
- `ctx.Negotiate`, legacy typed route/query helpers, `ReadJsonAsync`
- `ElsieResult.NotAcceptable` (was only used by Negotiate)
- `RouteTable.TryMatch` (use `Lookup`)
- Dead `ElsieOptionsSetup` registration

### Fixed
- `ElsieResult.Problem` is the single problem+json builder (`instance` / `traceId` optional); `ctx.Problem` delegates to it

## [0.2.0-alpha.2] — 2026-07-30

### Changed

- **`Elsie`** is now an app-facing **meta-package** (`dotnet add package Elsie` → `Elsie.Web` → `Elsie.Core`)
- Host-agnostic assemblies publish as **`Elsie.Core`** (was package id `Elsie` in `0.2.0-alpha.1`)
- Templates reference package `Elsie` instead of `Elsie.Web`
- README rewritten for a shorter quickstart and clearer package layout

## [0.2.0-alpha.1] — 2026-07-30

### Added

- Multi-targeting **`net8.0;net10.0`** with Central Package Management
- Routing precedence (static > constrained > param > catch-all), optional params/defaults, richer constraints, ambiguity/name validation, `RouteBuilder` metadata, `ctx.UrlFor`
- Request model: Scheme/Host/PathBase/Protocol/RemoteIp, multi-value headers, cookies
- Results: Html, File, Created, Accepted, 307/308, NotModified, SSE, header/cookie fluent helpers
- Binding: typed `Route`/`Query`/`Require*`, `BindQuery`/`BindRoute`/`BindFormAsync`, JSON body size guard
- Pipelines: transformable after-hooks; `MapException` + module `OnError` chain
- Host: `ElsieWeb.RunAsync` / non-generic run, terminal `MapElsie`, static files, OpenAPI + optional Scalar UI page
- **`Elsie.Views`** rebuilt on Fluid (Liquid)
- **`Elsie.Auth`** — cookie/JWT, `RequireAuthenticated` / Role / Claim / Policy, sign-in helpers
- **`Elsie.Cors`** — Elsie-native preflight + ACAO after-hook
- **`Elsie.HealthChecks`** — `/healthz`, live, ready
- **`Elsie.RateLimiting`** — fixed/sliding window before-hooks
- **`Elsie.Templates`** — `dotnet new elsie` / `elsie-api`
- Sample **`Elsie.Sample.Full`** kitchen sink
- Committed guides under `docs/`

### Changed

- `ElsieOptions` registration composed safely; removed detached options bug
- `ElsieJson.DefaultOptions` is an immutable fallback (no static `Configure` mutation)
- Modules remain singletons; test hosts scope per request with `ValidateScopes`
- `ElsieAuth.RequireApiKey` defaults to **all methods** (`onlyMutatingMethods: false`)
- Single response bake path: `ElsieHttpResponse.FromDispatch`

### Fixed

- OpenAPI `Produces<object>()` free-form schema lookup
- Dispatcher cancellation-token linking
- Scoped DI capture in in-memory / test hosts

## [0.1.0-alpha.1] — prior

Initial alpha surface (modules, routing, results, ASP.NET host, testing, FluentValidation, early views/OpenAPI). Superseded by 0.2.0-alpha.1.
