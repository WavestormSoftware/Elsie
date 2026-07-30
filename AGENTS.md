# AGENTS.md — Elsie

Instructions for coding agents working on this repository.

## What this is

**Elsie** is a greenfield, MIT-licensed, lightweight HTTP module framework for .NET (net8/net10). Core is **host-agnostic**; `Elsie.AspNetCore` adapts ASP.NET Core. Inspired by Sinatra-style DX — **not** a NancyFx fork.

## Clean-room (mandatory)

- Do **not** copy third-party framework source into this repo.
- Do **not** add foreign product namespaces or package IDs.

## Repo map

| Path | Role |
|------|------|
| `src/Elsie` | Host-agnostic core (package **`Elsie.Core`**): modules, routing, dispatcher, context, results, pipelines, OpenAPI builder, `ElsieAuth` |
| `src/Elsie.AspNetCore` | Host package: `ElsieWeb` / `MapElsie` / `UseElsie` / `MapElsieOpenApi`, logging, `HttpContext` adapter |
| `src/Elsie.Meta` | App meta-package **`Elsie`** → pulls `Elsie.AspNetCore` (+ transitive `Elsie.Core`) |
| `src/Elsie.FluentValidation` | `BindAndValidateJsonAsync` |
| `src/Elsie.Views` | Fluid (Liquid) views + layouts/partials (`ViewAsync`, `IElsieViewEngine`) |
| `src/Elsie.HealthChecks` | Host-agnostic `/healthz` + live/ready module |
| `src/Elsie.RateLimiting` | Fixed/sliding window before-hook gates (429 + Retry-After) |
| `src/Elsie.Auth` | Cookie/JWT wiring + RequireAuthenticated/Role/Claim/Policy gates |
| `src/Elsie.Cors` | Elsie-native CORS (preflight middleware + after-hook ACAO) |
| `src/Elsie.Testing` | `ElsieInMemoryHost` + ASP.NET `ElsieTestHost` + asserts |
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
dotnet run --project samples/Elsie.Sample.Full
dotnet pack Elsie.sln -c Release -o artifacts/nuget
dotnet pack templates/Elsie.Templates.csproj -c Release -o artifacts/nuget
```

Guides (committed): `docs/*.md` — not `docs/PLAN.md` / `docs/ARCHITECTURE.md` (gitignored).
Changelog: `CHANGELOG.md`.

## Architecture rules

- **No `HttpContext` in core or Views.** Use `ElsieRequest` / `ElsieResponse` / `ElsieDispatcher`.
- Core package refs: MS.DI only (no `Microsoft.AspNetCore.App`).
- ASP.NET types stay in `Elsie.AspNetCore` (and Testing’s TestServer host).
- App DX: prefer `ElsieWeb.Run` / `builder.AddElsie()` (`quietConsole: true` default).
- Tests: `IServiceCollection.AddElsie` (no log rewiring).
- Auth: `ElsieAuth.RequireApiKey` / `RequireHeader` / `RequireBearer` / `RequireCookie` before-hooks.
- OpenAPI: core `ElsieOpenApiDocument`; host `MapElsieOpenApi` (JSON only; UI optional).
- Dispatch bake: `ElsieHttpResponse.FromDispatch` — single materialize path (ASP.NET + in-memory).
- Routing: `RouteTable.Lookup` owns matcher (`RouteMatcher` internal).
- Do not reintroduce FrameworkReference on `Elsie` without an explicit product decision.

## Module registration

- Apps: `ElsieWeb.Run<T>()` or `builder.AddElsie()` + `AddElsieModule<T>()`.
- Prefer **explicit** `AddElsieModule<T>()` in tests.
- `AddElsie()` defaults `ScanEntryAssembly = true`.
- Test hosts set `ScanEntryAssembly = false`.
- Modules are **singletons**. Ctor-inject singleton-safe services; `ctx.GetRequiredService<T>()` / `ctx.Services` for request scope (test hosts ValidateScopes + per-request scope).
- JSON: static `ElsieResult.Json` → framework defaults (`ElsieJson.DefaultOptions`); `ctx.Json` → app `ElsieOptions.JsonSerializerOptions`.
- Routing: precedence static > constrained > param > catch-all; startup validates constraints/ambiguity/dup names; `RouteBuilder` metadata + `ctx.UrlFor`.
- `Path` / `Group`, `BindJsonAsync`, problem results, optional `ExceptionHandler`.
- Views: `AddElsieViews` + `ctx.ViewAsync` (`Elsie.Views`, Fluid/Liquid `.liquid`; `IElsieViewEngine` seam).
- OpenAPI: route metadata (`.Named`/`.Accepts`/`.Produces`/`.WithSecurity`/`.AcceptsQuery`) → `ElsieOpenApiDocument`; host `MapElsieOpenApi` (+ optional `UiPath` Scalar CDN page).

## Samples

- HelloWorld: `ElsieWeb.Run` — `samples/Elsie.Sample.HelloWorld`
- Easy: `samples/Elsie.Sample.Hello`
- Advanced API: `samples/Elsie.Sample.Api`
- Views: `samples/Elsie.Sample.Views`
- Full kitchen sink: `samples/Elsie.Sample.Full` (auth, CORS, rate limit, health, static, views)
- All samples use ASP.NET Core

## Engineering rules

1. YAGNI → reuse Elsie types → BCL → MS.Ext → ASP.NET adapter → smallest diff.
2. `nullable enable`, latest C#, async all the way — no `.Result` / `.Wait()`.
3. MS.DI only (`IServiceCollection` / `IServiceProvider`).
4. Prefer `System.Text.Json`.
5. Keep `dotnet test` green.
6. Conventional commits: `feat:`, `fix:`, `test:`, `docs:`, `chore:`, `ci:`.

## Product owner

WavestormSoftware — https://github.com/WavestormSoftware/Elsie
