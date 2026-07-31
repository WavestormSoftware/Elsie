# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Elsie is **unreleased** software; prereleases may include breaking API changes.

## [0.3.0-beta.2] — 2026-07-31

### Breaking
- Package **Elsie** is now the HTTP host (was a metapackage over `Elsie.Web`).
- Package **Elsie.Web** removed — use **Elsie** (same assembly `Elsie.Web.dll` / namespaces).
- Project paths: `src/Elsie.Core` (kernel), `src/Elsie` (host). `src/Elsie.Meta` deleted.
- Rate limit **default partition** no longer reads `X-Forwarded-For` (use `ForwardedPartitionKey`).
- Cookie auth `SameSite` is now `ElsieSameSite` (removed Auth-local `SameSiteMode`).

### Migration
- Apps: keep `PackageReference Include="Elsie"` (recommended).
- If you referenced **Elsie.Web** explicitly, switch to **Elsie**.
- Monorepo `ProjectReference` consumers: point host at `src/Elsie/Elsie.csproj`, core at `src/Elsie.Core/Elsie.Core.csproj`.
- Rate limit behind proxies: pass `partitionKey: ElsieRateLimit.ForwardedPartitionKey` when XFF is trusted.

### Added
- Connection caps, drain shutdown, header timeouts, listen backlog
- Request `TraceIdentifier` + `X-Request-Id`; `ActivitySource("Elsie")`; `Meter("Elsie")`
- Optional `ILoggerFactory` via `ElsieApp.Logging`
- Response compression (`.Compression()`)
- Static files: streaming, ETag, If-Modified-Since, Range
- Multipart file uploads (`ElsieFormFile`, `ReadFormAsync`)
- `IRateLimitStore`; `ElsieRateLimit.TokenBucket`; security headers helper
- Dependabot (NuGet + GitHub Actions)
- Antiforgery (header **or** form field) + minimal OIDC helpers (`Elsie.Auth`)
- Request body buffer shared across bind/antiforgery reads
- `Elsie.Validation` DataAnnotations package
- OpenAPI prebuilt document + embedded UI mode; `WriteToFileAsync`; `WithExample`
- Absolute `UrlFor(..., absolute: true)`; Problem `type` URI
- Samples: Dashboard CSRF/validation; Full/Api compression, headers, validation, logging
- Docs: architecture, lifecycle, production checklist, anti-patterns, minimal APIs migration
- CI: vulnerable package scan, coverage collect, Validation pack

### Non-breaking
- Namespaces `Elsie` / `Elsie.Web` unchanged.
- Assembly names `Elsie.dll` (core) and `Elsie.Web.dll` (host) unchanged.

## [0.3.0-beta.1] — 2026-07-31

### Added
- **`ElsieApp`** fluent host — TCP HTTP/1.1, TLS, opt-in HTTP/2, WebSockets, static files, OpenAPI
- `.Server(...)` limits + **`UseForwardedHeaders`** (`X-Forwarded-For` / `Proto` / `Host`)
- **413 Payload Too Large** when request body exceeds `MaxRequestBodyBytes`
- Multipart form field binding; native cookie AES-GCM tickets + JWT validation
- CORS preflight filter; loopback `ElsieTestHost`
- Expanded security suite (tickets, traversal, limits, forwarded headers, H1 parser)
- Sample **Dashboard** multi-page Fluid + cookie auth
- CI/publish: version from `Directory.Build.props`; metapackage dependency validation

### Changed
- **Elsie.Web** is self-contained (MS.DI only) — no ASP.NET shared framework
- Host entrypoint: `ElsieApp` / `ElsieWeb.Run`
- Cookie auth requires explicit `TicketKey` (or `AllowInsecureDevelopmentKey` for local only)
- Ticket secrets must be ≥ 16 characters when using `TicketKeyFromString`
- Package layout: HealthChecks + RateLimiting in **Elsie.Core**
- Default `ExceptionHandler` omits exception detail

### Security
- Constant-time compare for API-key / header gates
- Cookie tickets AES-GCM; reject missing production ticket keys
- Path-traversal checks on static files; body size caps (H1 + H2)
- Forwarded headers off by default (enable only behind trusted proxies)

### Removed
- ASP.NET host APIs (`WebApplication`, `MapElsie`, `HttpContext` escape hatch, TestServer)
- Historical package IDs: Elsie.AspNetCore, Elsie.HealthChecks, Elsie.RateLimiting, Elsie.FluentValidation

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
