# AGENTS.md — Elsie

Instructions for coding agents working on this repository.

## What this is

**Elsie** is a greenfield, MIT-licensed, lightweight HTTP module framework for ASP.NET Core (net8/net9). It is *inspired by* the developer experience of Nancy (Sinatra-style modules, low ceremony). It is **not** a fork, port, or rename of NancyFx.

## Clean-room (mandatory)

- Do **not** copy Nancy source, tests, comments, or project structure into this repo.
- Do **not** add `Nancy*` namespaces or package IDs.
- Mentions of Nancy in docs are historical inspiration only.

## Repo map

| Path | Role |
|------|------|
| `src/Elsie` | Core: modules, routing, context, results |
| `src/Elsie.AspNetCore` | `AddElsie`, `MapElsie` / middleware |
| `src/Elsie.Testing` | In-process test host helpers |
| `tests/*` | Unit / integration tests |
| `samples/Elsie.Sample.Hello` | Minimal runnable sample |
| `docs/PLAN.md` | Executable implementation plan |
| `docs/ARCHITECTURE.md` | System design snapshot |
| `AGENTS.md` | This file |

## Commands

```bash
cd /home/damian/Documents/Projects/Elsie
dotnet restore Elsie.sln
dotnet build Elsie.sln -c Release
dotnet test Elsie.sln -c Release
dotnet run --project samples/Elsie.Sample.Hello
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## v0.1 scope

**In:** modules + route DSL + matcher + ASP.NET Core host + testing helpers + sample + CI.

**Out:** views, auth packages, content negotiation framework, custom IoC, OWIN/System.Web, Nancy API compatibility.

Follow `docs/PLAN.md`. Do not expand scope without the user.

## Engineering rules

1. YAGNI → reuse Elsie types → BCL / ASP.NET Core → smallest diff.
2. `nullable enable`, latest C#, async all the way — no `.Result` / `.Wait()`.
3. Prefer MS.DI only (`IServiceCollection` / `IServiceProvider`).
4. Prefer `System.Text.Json`.
5. One plan task per change-set when possible; keep `dotnet test` green.
6. Commit prefix: `phaseX.Y: …` matching `docs/PLAN.md`.
7. Ignore agent junk: `.pi-subagents/`, `.pi/` (listed in `.gitignore`).

## Where to start

1. Read `docs/PLAN.md` locked decisions + current phase tasks.  
2. Read `docs/ARCHITECTURE.md`.  
3. Implement the next unchecked task; verify with commands above.

## Product owner

WavestormSoftware — local path `/home/damian/Documents/Projects/Elsie`. Recreate GitHub `WavestormSoftware/Elsie` when pushing (see plan Task 4.3).
