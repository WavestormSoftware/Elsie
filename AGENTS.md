# AGENTS.md — Elsie

Instructions for coding agents working on this repository.

## What this is

**Elsie** is a greenfield, MIT-licensed, lightweight HTTP module framework for ASP.NET Core (net8/net9). It is *inspired by* the developer experience of Sinatra-style frameworks (small modules, explicit routes, minimal wiring). It is **not** a fork of NancyFx or any other framework.

## Clean-room (mandatory)

- Do **not** copy third-party framework source into this repo.
- Do **not** add foreign product namespaces or package IDs.

## Repo map

| Path | Role |
|------|------|
| `src/Elsie` | Core: modules, routing, context, results, pipelines |
| `src/Elsie.AspNetCore` | `AddElsie`, `MapElsie` / middleware |
| `src/Elsie.Testing` | In-process test host helpers |
| `tests/*` | Unit / integration tests |
| `samples/*` | Runnable samples |
| `README.md` | Public product docs |
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
dotnet run --project samples/Elsie.Sample.Hello
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## Module registration

- Prefer **explicit** `AddElsieModule<T>()` in apps and tests.
- `AddElsie()` defaults `ScanEntryAssembly = true` (entry assembly concrete modules).
- `ElsieTestHost` sets `ScanEntryAssembly = false` — always register modules in the configure callback.
- Modules are **singletons**. Ctor-inject singleton-safe services; use `ctx.GetRequiredService<T>()` / `ctx.RequestServices` for request scope.
- Use `Path("/api")` + `Group("/x", () => { ... })` for prefixes; `BindJsonAsync` / problem results for input errors; optional `ElsieOptions.ExceptionHandler`.

## Samples

- Easy: `samples/Elsie.Sample.Hello`
- Advanced: `samples/Elsie.Sample.Api`

## Engineering rules

1. YAGNI → reuse Elsie types → BCL / ASP.NET Core → smallest diff.
2. `nullable enable`, latest C#, async all the way — no `.Result` / `.Wait()`.
3. MS.DI only (`IServiceCollection` / `IServiceProvider`).
4. Prefer `System.Text.Json`.
5. Keep `dotnet test` green.
6. Conventional commits: `feat:`, `fix:`, `test:`, `docs:`, `chore:`, `ci:`.

## Product owner

WavestormSoftware — https://github.com/WavestormSoftware/Elsie
