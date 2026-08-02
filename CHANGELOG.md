# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Elsie is **unreleased** software; prereleases may include breaking API changes.

## [Unreleased]

### Breaking
- Target framework is **net10.0 only** (net8.0 support dropped).
- **Middleware pipeline (Phase D, complete):** `IElsieMiddleware` / `ElsieMiddlewareDelegate` / `ElsieMiddlewarePipeline` in Core (`Elsie.Middleware`); `ElsieApp.Use(delegate)` / `Use<T>()` (DI, per-request scope); `ElsieModule.Use(...)` module-scoped middleware (module routes only); ordering FIFO pre / LIFO post; short-circuit via `ElsieContext.Result`. Legacy `Before` / `After` / `OnError` / `MapException` hooks, `IElsieRequestFilter`, and `ConfigureElsiePipelines` are **removed** — auth gates, rate limiting, CORS, security headers, antiforgery, health, static files, and the exception handler are first-class middleware. `AddElsieMiddleware(...)` composes the app pipeline; `ElsieDispatcher` runs the pipeline only (route lookup precedes the pipeline so middleware sees route values; 405 responses carry `Allow`).
- `IRateLimitStore` gained `TryPeek(string key, out RateLimitCounters counters)` (default impl throws `NotSupportedException`) — custom stores should implement it to opt into `X-RateLimit-*` headers.
- **Cookie hardening (breaking):** `ElsieCookieAuthOptions.Secure` now defaults to **true**; `MaxAge` defaults to 8 h (emitted in `Set-Cookie`); a `CookiePrefix` is enforced at startup (`__Host-` requires `Secure`, `Path=/`, no `Domain`, and the cookie name must start with the prefix).
- `IElsiePrincipalAttacher` is now async: `Task AttachAsync(ElsieRequest, CancellationToken)` (host-side principal restore now supports sessions/JWKS lookups).

### Changed
- **Middleware (Phase D):** dispatcher runs a middleware pipeline; gate/after factories plug in directly — `Use(ElsieAuth.RequireApiKey(...))`, `Use(ElsieRateLimit.FixedWindow(...))`, `Use(ElsieSecurityHeaders.DefaultAfter())`, `Use(ElsieRateLimitHeaders.Attach(store))`, `Use(ElsieAntiforgeryService.RequireAntiforgery())`. New `ElsieCorsMiddleware` in `Elsie.Cors` (preflight 204 + ACAO on actuals; `UseElsieCors()` pipeline extension). New `docs/middleware.md`.
- OIDC: `PrincipalFromIdToken` no longer accepts unvalidated id_tokens by default (`allowUnvalidated` opt-in); optional `expectedNonce` check; state/nonce are Base64Url.
- `ElsieMetrics` meter version string bumped to `0.4.0`.

### Added
- **Hot reload (IOptionsMonitor)**: `Elsie:Server` binds onto `ElsieServerOptions` and `Elsie:Cors` binds onto CORS policies; config reloads flow into the live instances so server limits/timeouts, compression, `LogRequests`, and CORS origins/methods/headers update without restart (safe-knob list documented in `docs/hosting-and-aot.md`; routing/modules/binding/listener settings stay restart-only)
- **OpenAPI 3.1** (`openapi: 3.1.0`, JSON Schema 2020-12): nullable properties and optional query parameters are emitted as type unions (`["string", "null"]`) instead of the removed `nullable` keyword
- **Offline Scalar UI**: bundled `@scalar/api-reference` standalone bundle (embedded resource, served at `{UiPath}/standalone.js`) when `UseScalarCdn = false`; update via `tools/UpdateScalarAssets.sh`
- **JWKS signing-key discovery** (`JwksResolver`): OIDC `/.well-known/openid-configuration` or explicit `JwksUrl` discovery via `ConfigurationManager`, refresh on `ElsieJwtBearerOptions.JwksRefreshInterval` (default 24 h), `kid` validation with rollover (previous keys kept), unreachable authority → clean 401 (never crashes); `AllowHttpMetadata` knob for dev/test
- **Server-side sessions (A2):** `IElsieSessionStore` + bounded `InMemoryElsieSessionStore` (~100k entries, sliding TTL, `TimeProvider`, LRU eviction); cookie v2 opaque ≥128-bit session ids; `SignInAsync` stores server-side when a store is configured; `SignOutAsync` removes the entry; principal restored per request with sliding renewal
- **Challenge/Forbid (A4):** `ElsieAuthResultExtensions.Challenge(ElsieContext)` (JWT → 401 + `WWW-Authenticate: Bearer`; cookie → 302 `ChallengeLoginPath`) and `Forbid(ElsieContext)` (302 `ForbidAccessDeniedPath` or 403); auth gates use them when configured
- **Named authorization policies (A5):** `ElsieAuthorizationPolicy` (requirement predicates + `RequireRole`/`RequireClaim` shortcuts), `AddElsiePolicy(...)` registry extension, `ElsieAuthGates.RequirePolicy(name)` with startup validation of unknown policy names
- **Redis distributed rate limiting** — new package `Elsie.Extensions.RateLimiting.Redis`: `RedisFixedWindowStore` / `RedisSlidingWindowStore` / `RedisTokenBucketStore` (atomic Lua scripts), `RedisRateLimit.FixedWindow/SlidingWindow/TokenBucket` factories (shared multiplexer or connection-string), `RedisRateLimitOptions` (key prefix `elsie:rl:`, ~100 ms op timeout, fail-open default / optional fail-closed outage policy)
- `RateLimitCounters` + `IRateLimitStore.TryPeek` and `ElsieRateLimitHeaders.Attach(store)` after-hook emitting `X-RateLimit-Limit` / `X-RateLimit-Remaining` / `X-RateLimit-Reset`
- `Microsoft.Extensions.Logging.Abstractions` / `DependencyInjection.Abstractions` / `DependencyInjection` pins bumped to 10.0.5 (StackExchange.Redis 3.1.0 requires Logging.Abstractions ≥ 10.0.5)
- Generic Host integration: `HostApplicationBuilder.UseElsie`, `AddElsieApp`, `ElsieApp.HostedService<T>`, config `Elsie:Urls` / `Elsie` section bind
- Structured request logging (`Elsie.Request`), W3C `traceparent` propagation, expanded `ElsieMetrics` (duration histogram, active requests, body sizes, websockets)
- `ElsieOptions.ShowExceptionDetails` (dev HTML exception page; auto-on in Generic Host Development)
- OIDC PKCE helpers (`CreateCodeVerifier` / `CreateCodeChallenge`) + `code_challenge` / `code_verifier` on authorize/token exchange.
- Streaming HTTP/1.1 request bodies (`Content-Length` + `Transfer-Encoding: chunked`) with keep-alive drain; unknown-length response `BodyWriter` uses chunked TE; static files stream with `Content-Length` (no double-buffer)
- `ElsieRequestException` (protocol 4xx mapped by dispatcher — body idle timeout → 408)
- Multipart file spill: `ElsieOptions.MultipartMemoryThresholdBytes` (default 1 MiB); large `ElsieFormFile` parts use temp files + `IAsyncDisposable`
- HTTP/2 protocol hardening (PING/SETTINGS/WINDOW_UPDATE/pseudo-headers/flow control) + Linux `h2spec` workflow (`tools/Elsie.H2SpecHost`)
- TCP keepalive on accept (`TcpKeepAlive` / `TcpKeepAliveTime` / `TcpKeepAliveInterval`) + `ConnectionIdleTimeout` for keep-alive gaps
- Unix domain sockets: `http+unix:///path`, `http://unix:/path`, `ElsieListenOptions.FromUnixSocketPath` (HTTP/1.1)
- **C4 quality gates**: `.editorconfig` + `dotnet format Elsie.sln --verify-no-changes` CI step; **nightly workflow** (`.github/workflows/nightly.yml`, cron + manual dispatch) with a 10-min black-box parser fuzz (`tests/Elsie.Fuzz`, standalone — raw-socket HTTP/1.1 + HTTP/2 frames/HPACK, no crashes/hangs/5xx-internal, bounded working set) and a 30-min soak/load smoke (route + dispatch + static streaming over h1/h2); native-AOT smoke tool `tools/Elsie.AotSmoke` (publish `PublishAot=true` + run trimmed binary) with `DynamicallyAccessedMembers` annotations in `ElsieServiceCollectionExtensions`

### Added
- **HTTP/3 (Phase E):** `ElsieListenOptions.EnableHttp3` starts a UDP `QuicListener` (TLS + ALPN `h3`, guarded by `QuicListener.IsSupported`); `Http3Connection` serves requests through the same pipeline as HTTP/1.1/2; clean-room **QPACK** codec (RFC 9204: static table, literals, Huffman) + HTTP/3 framing (RFC 9114) + control streams (SETTINGS/GOAWAY/MAX_PUSH_ID); lazy DATA-frame request bodies (bounded by `MaxRequestBodyBytes`); response trailers on h3. Verified by unit tests + `http3.yml` interop (`curl --http3` + aioquic). WebSocket-over-h3 (RFC 9220) pending — WebTransport explicitly out of scope.
- **HTTP/2 response trailers (E1):** `ElsieResponse.AddTrailer(name, value)` → trailing HEADERS frame (h2 + h3) — the gRPC status channel.
- **`Elsie.Extensions.Auth.Redis` (B2):** `RedisSessionStore : IElsieSessionStore` (StackExchange.Redis, `elsie:session:` prefix, sliding TTL, per-op timeout) + Testcontainers integration tests.
- **Fuzz throughput:** `tests/Elsie.Fuzz` batches inputs per keep-alive/TLS connection with tight read budgets and runs 8 concurrent workers (~30 inputs/s locally).
- **Full QPACK (RFC 9204, E2):** HTTP/3 now uses real dynamic tables — decoder with encoder-instruction parsing (Insert With/Without Name Reference, Duplicate, Set Dynamic Table Capacity), static+dynamic references incl. post-base indexing, Required Insert Count reconstruction, and blocked-stream delivery; encoder inserts repeated response fields up to the peer's advertised capacity and emits encoder-stream instructions plus the required decoder instructions (Section Acknowledgment / Insert Count Increment / Stream Cancellation). `ElsieServerOptions.QpackMaxTableCapacity` (default 4096) and `QpackBlockedStreams` (default 100) configure the advertised SETTINGS; capacity-0 interop remains. RFC 9204 Appendix B test vectors.
- **WebSocket over HTTP/3 (RFC 9220, E3):** extended CONNECT (`:protocol: websocket`) upgrades the request stream into a WebSocket tunnel (`SETTINGS_ENABLE_CONNECT_PROTOCOL=1`); unknown `:protocol` → 501. Sample `Elsie.Sample.Http3` gained a `CONNECT /ws` echo route.
- **gRPC (Phase F):** new package **`Elsie.Grpc`** — `ElsieServiceBinder` (`Grpc.Core.ServiceBinderBase`, all 4 `AddMethod` families), `ElsieServerCallContext` (deadline → `RequestAborted`, metadata, cancellation, status), 5-byte framing, `grpc-status`/`grpc-message` trailers over h2 + h3, `application/grpc` gate (415), gRPC↔HTTP status mapping, deadline → `DEADLINE_EXCEEDED`, reflection-lite (`grpc.reflection.v1alpha`), `ElsieApp.MapGrpcService<T>()`. HTTP/2 first + HTTP/3 when the h3 listener is active. Sample `Elsie.Sample.Grpc` (Greeter, unary + streaming), `docs/grpc.md`, CI `grpc.yml` (grpcurl list/call/stream).
- **h2/h3 response streaming:** `BodyWriter` responses (SSE, static files, gRPC) stream as DATA frames instead of being buffered; trailers are re-read after the body writer completes so `grpc-status` is emitted correctly.

### Fixed
- HTTP/3 QPACK conformance: literal-name Huffman flag at bit 4 (not bit 7), post-base `0001`/`0000` vs `001` prefixes, correct QPACK static table (RFC 9204 Appendix A, 99 entries).
- HTTP/3 per-stream body state (removed the shared `BodyStream` 413 race) and `QuicVarInt.Read` returns a protocol-error sentinel (no `OverflowException`) for >2 GiB frame lengths; frame reader caps payload sizes.
- README documents the middleware model and the **NO-KESTREL** constraint; stale `MapException` / `ConfigureElsiePipelines` / `Before(...)` examples removed.
- Response writer no longer buffers `BodyWriter` when `Content-Length` is already set (static files / known-length streams)
- HTTP/1.1 smuggling defenses: reject CL+TE, differing duplicate Content-Length, non-`chunked` Transfer-Encoding; cap chunk-size/line length
- `Expect: 100-continue` interim response (disable via `ElsieServerOptions.DisableContinue`)
- Canonicalize request paths at host boundary (`//`, `.`/`..`; reject root escape / `\\` / NUL)
- Client disconnect cancels `RequestAborted` (toggle `AbortRequestsOnClientDisconnect`)
- Emit RFC 7231 `Date` response header; empty body for 204/304; compression respects `q=` and always sets `Vary: Accept-Encoding`
- Enforce `RequestBodyIdleTimeout` on body reads (408)
- Shutdown force-closes drained connections (`ShutdownAbortConnections`)
- Static files: path containment uses directory boundary (blocks sibling root-prefix escapes)
- Reject CR/LF/NUL in response header names/values, cookie Path/Domain, and file download names
- Ignore unsafe client `X-Request-Id` / `X-Correlation-Id` when echoing `X-Request-Id`
- Route matching: invalid percent-encoding no longer throws

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
- Docs/README/templates aligned with host package id **Elsie**, CSRF, token bucket, validation, production samples.
- Samples use NuGet `PackageReference` (pinned `0.3.0-beta.2`) — no `src/` project refs; copy a sample folder and `dotnet run`.

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
