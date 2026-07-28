# AGENTS.md — Elsie

Instructions for coding agents working on this repository.

## What this is

**Elsie** is a greenfield, MIT-licensed, lightweight HTTP module framework for .NET (net8/net9). Core is **host-agnostic**; `Elsie.AspNetCore` adapts ASP.NET Core. Inspired by Sinatra-style DX — **not** a NancyFx fork.

## Clean-room (mandatory)

- Do **not** copy third-party framework source into this repo.
- Do **not** add foreign product namespaces or package IDs.

## Repo map

| Path | Role |
|------|------|
| `src/Elsie` | Host-agnostic core: modules, routing, dispatcher, context, results, pipelines, `AddElsie` |
| `src/Elsie.AspNetCore` | `MapElsie` / `UseElsie`, `HttpContext` → `ElsieRequest` adapter |
| `src/Elsie.Testing` | `ElsieInMemoryHost` + ASP.NET `ElsieTestHost` + asserts |
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
dotnet run --project samples/Elsie.Sample.HelloWorld
dotnet run --project samples/Elsie.Sample.Hello
dotnet run --project samples/Elsie.Sample.Api
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## Architecture rules

- **No `HttpContext` in core.** Use `ElsieRequest` / `ElsieResponse` / `ElsieDispatcher`.
- Core package refs: MS.DI only (no `Microsoft.AspNetCore.App`).
- ASP.NET types stay in `Elsie.AspNetCore` (and Testing’s TestServer host).
- Do not reintroduce FrameworkReference on `Elsie` without an explicit product decision.

## Module registration

- Prefer **explicit** `AddElsieModule<T>()` in apps and tests.
- `AddElsie()` defaults `ScanEntryAssembly = true`.
- Test hosts set `ScanEntryAssembly = false`.
- Modules are **singletons**. Ctor-inject singleton-safe services; `ctx.GetRequiredService<T>()` for request scope.
- `Path` / `Group`, `BindJsonAsync`, problem results, optional `ExceptionHandler`.

## Samples

- HelloWorld (simplest): `samples/Elsie.Sample.HelloWorld`
- Easy: `samples/Elsie.Sample.Hello`
- Advanced API: `samples/Elsie.Sample.Api`
- All samples use ASP.NET Core (`MapElsie`)

## Engineering rules

1. YAGNI → reuse Elsie types → BCL → MS.Ext → ASP.NET adapter → smallest diff.
2. `nullable enable`, latest C#, async all the way — no `.Result` / `.Wait()`.
3. MS.DI only (`IServiceCollection` / `IServiceProvider`).
4. Prefer `System.Text.Json`.
5. Keep `dotnet test` green.
6. Conventional commits: `feat:`, `fix:`, `test:`, `docs:`, `chore:`, `ci:`.

## Product owner

WavestormSoftware — https://github.com/WavestormSoftware/Elsie
