# AGENTS.md — Elsie

Instructions for coding agents working on this repository.

## What this is

**Elsie** is a greenfield, MIT-licensed, lightweight HTTP module framework for .NET (net8/net10). Core is **host-agnostic**; `Elsie.Web` is the custom HTTP host (`ElsieApp`). Inspired by Sinatra-style DX — **not** a NancyFx fork.

## Clean-room (mandatory)

- Do **not** copy third-party framework source into this repo.
- Do **not** add foreign product namespaces or package IDs.

## Repo map

| Path | Role |
|------|------|
| `src/Elsie` | Host-agnostic core (package **`Elsie.Core`**): modules, routing, dispatcher, context, results, pipelines, OpenAPI builder, `ElsieAuth`, HealthChecks, RateLimiting |
| `src/Elsie.Web` | Host package: `ElsieApp` / `ElsieWeb.Run`, HTTP/1.1 server, static files, OpenAPI routes |
| `src/Elsie.Meta` | **Keep.** NuGet package id **`Elsie`** (metapackage) → depends on `Elsie.Web` → `Elsie.Core`. Enables `dotnet add package Elsie`. |
| `src/Elsie.Views` | Fluid (Liquid) views + layouts/partials (`ViewAsync`, `IElsieViewEngine`) |
| `src/Elsie.Auth` | Cookie/JWT wiring + RequireAuthenticated/Role/Claim/Policy gates |
| `src/Elsie.Cors` | Elsie-native CORS (preflight middleware + after-hook ACAO) |
| `src/Elsie.Testing` | **Keep.** Consumer test helpers (`ElsieInMemoryHost`, loopback `ElsieTestHost`, asserts). Distinct from `tests/` (our own unit tests of the framework). |
| `templates/` | `dotnet new` templates (`elsie`, `elsie-api`) → `Elsie.Templates` |
| `benchmarks/Elsie.Benchmarks` | BenchmarkDotNet (route/dispatch/views); not CI-gated |
| `tests/*` | Unit / integration tests |
| `samples/*` | Runnable samples |
| `docs/*.md` | Committed guides (not PLAN/ARCHITECTURE) |
| `README.md` | Public product docs |
| `CHANGELOG.md` | Release notes |
| `AGENTS.md` | This file |

## Do not commit

- `docs/PLAN.md`, `docs/ARCHITECTURE.md` (local agent planning only; gitignored)
- `.local/`, `.pi-subagents/`, `.pi/`

## Commands

```bash
cd /home/damian/Documents/Projects/Elsie
dotnet restore Elsie.sln
dotnet build Elsie.sln -c Release
dotnet test Elsie.sln -c Release
dotnet run --project samples/Elsie.Sample.HelloWorld
dotnet run --project samples/Elsie.Sample.Hello
dotnet run --project samples/Elsie.Sample.Api
dotnet run --project samples/Elsie.Sample.Views
dotnet run --project samples/Elsie.Sample.Dashboard
dotnet run --project samples/Elsie.Sample.Full
dotnet pack Elsie.sln -c Release -o artifacts/nuget
dotnet pack templates/Elsie.Templates.csproj -c Release -o artifacts/nuget
# packages land in artifacts/nuget/ (gitignored)
```

Guides (committed): `docs/*.md` — not `docs/PLAN.md` / `docs/ARCHITECTURE.md` (gitignored).
Changelog: `CHANGELOG.md`.

## Architecture rules

- **No ASP.NET types in the repo.** Use `ElsieRequest` / `ElsieResponse` / `ElsieDispatcher`.
- Core package refs: MS.DI only.
- App DX: prefer `ElsieApp.Run` / `ElsieApp.Create` / `ElsieWeb.Run` (`quietConsole: true` default).
- Tests: `ElsieInMemoryHost` or `ElsieTestHost` (loopback); `IServiceCollection.AddElsie`.
- Auth: `ElsieAuth.*` header gates + `Elsie.Auth` cookie/JWT principal.
- OpenAPI: core `ElsieOpenApiDocument`; host `.OpenApi(...)`.
- Dispatch bake: `ElsieHttpResponse.FromDispatch` — single materialize path (host + in-memory).
- Routing: `RouteTable.Lookup` owns matcher (`RouteMatcher` internal).

## Module registration

- Apps: `ElsieApp.Run<T>()` or `ElsieApp.Create().Module<T>().Run()`.
- Prefer **explicit** `.Module<T>()` / `AddElsieModule<T>()` in tests.
- `AddElsie()` defaults `ScanEntryAssembly = true`.
- Test hosts set `ScanEntryAssembly = false`.
- Modules are **singletons**. Ctor-inject singleton-safe services; `ctx.GetRequiredService<T>()` / `ctx.Services` for request scope (test hosts ValidateScopes + per-request scope).
- JSON: static `ElsieResult.Json` → framework defaults (`ElsieJson.DefaultOptions`); `ctx.Json` → app `ElsieOptions.JsonSerializerOptions`.
- Routing: precedence static > constrained > param > catch-all; startup validates constraints/ambiguity/dup names; `RouteBuilder` metadata + `ctx.UrlFor`.
- `Path` / `Group`, `BindJsonAsync`, problem results, optional `ExceptionHandler`.
- Views: `AddElsieViews` + `ctx.ViewAsync` (`Elsie.Views`, Fluid/Liquid `.liquid`; `IElsieViewEngine` seam).
- OpenAPI: route metadata (`.Named`/`.Accepts`/`.Produces`/`.WithSecurity`/`.AcceptsQuery`) → `ElsieOpenApiDocument`; host `.OpenApi(...)` (+ optional `UiPath` Scalar CDN page).

## Samples

- HelloWorld: `ElsieWeb.Run` / `ElsieApp.Run` — `samples/Elsie.Sample.HelloWorld`
- Easy: `samples/Elsie.Sample.Hello`
- Advanced API: `samples/Elsie.Sample.Api`
- Views: `samples/Elsie.Sample.Views`
- Dashboard (multi-page views + cookie auth): `samples/Elsie.Sample.Dashboard`
- Full kitchen sink: `samples/Elsie.Sample.Full` (auth, CORS, rate limit, health, static, views)
- All samples use the Elsie host

## Engineering rules

1. YAGNI → reuse Elsie types → BCL → MS.Ext → ASP.NET adapter → smallest diff.
2. `nullable enable`, latest C#, async all the way — no `.Result` / `.Wait()`.
3. MS.DI only (`IServiceCollection` / `IServiceProvider`).
4. Prefer `System.Text.Json`.
5. Keep `dotnet test` green.
6. Conventional commits: `feat:`, `fix:`, `test:`, `docs:`, `chore:`, `ci:`.

## Product owner

WavestormSoftware — https://github.com/WavestormSoftware/Elsie
