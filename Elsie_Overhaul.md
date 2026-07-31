# Elsie package & project overhaul

**Status:** Implemented (0.3.0-beta.2, branch refactor/package-layout)  
**Goal:** Professional NuGet layout, one clear install, honest package metadata — without changing the product architecture (host-agnostic core + custom host).  
**Consumer promise:** `dotnet add package Elsie` and you can run an app.

---

## 1. Executive summary

### Decision

| Today | Tomorrow |
|-------|----------|
| Metapackage `Elsie` (`src/Elsie.Meta`) → `Elsie.Web` → `Elsie.Core` | **Host package id `Elsie`** → `Elsie.Core` |
| Folder `src/Elsie` = package **Elsie.Core** (name mismatch) | Folder **`src/Elsie.Core`** = package **Elsie.Core** |
| Folder `src/Elsie.Web` = package **Elsie.Web** | Folder **`src/Elsie`** = package **`Elsie`** (the host) |
| Folder `src/Elsie.Meta` exists | **Deleted** |

### Dependency graph (target)

```text
                    ┌─────────────────────────────────────┐
  apps install →    │  Elsie          (host + DX)         │
                    │  PackageId: Elsie                     │
                    │  Project:   src/Elsie                 │
                    │  Assembly:  Elsie.Web (keep for now)  │
                    │  Namespace: Elsie.Web                 │
                    └─────────────────┬───────────────────┘
                                      │ ProjectReference / PackageReference
                    ┌─────────────────▼───────────────────┐
                    │  Elsie.Core     (kernel)              │
                    │  PackageId: Elsie.Core                │
                    │  Project:   src/Elsie.Core            │
                    │  Assembly:  Elsie                     │
                    │  Namespace: Elsie                     │
                    └─────────────────────────────────────┘

  Optional (unchanged roles):
    Elsie.Auth, Elsie.Cors, Elsie.Views  → depend on Elsie and/or Elsie.Core as appropriate
    Elsie.Testing                        → app authors’ test hosts (not repo tests/)
    Elsie.Templates                      → dotnet new; PackageReference Elsie
```

### Why this is the professional choice

1. **Product = runnable stack.** Sinatra-style frameworks sell “define routes and run.” That is **host + modules**, not a hollow meta package.
2. **No fake package.** Metapackages with no real surface confuse NuGet.org (frameworks/deps display) and maintainers (“what is Elsie.Meta?”).
3. **No dependency cycle.** Core never depends on host; host depends on core.
4. **Matches .NET norms.** Install the thing that runs (e.g. Hangfire.AspNetCore → Hangfire.Core); optional satellites stay separate.
5. **Folder = package id.** Open the solution and see `Elsie` / `Elsie.Core` matching nuget.org.

---

## 2. Current state (baseline before overhaul)

### Packages (0.3.0-beta.1 era)

| Project path | PackageId | Assembly | Role |
|--------------|-----------|----------|------|
| `src/Elsie` | **Elsie.Core** | Elsie | Kernel |
| `src/Elsie.Web` | **Elsie.Web** | Elsie.Web | Custom HTTP host |
| `src/Elsie.Meta` | **Elsie** | Elsie.Meta | Metapackage → Web → Core |
| `src/Elsie.Auth` | Elsie.Auth | Elsie.Auth | Cookie/JWT |
| `src/Elsie.Cors` | Elsie.Cors | Elsie.Cors | CORS |
| `src/Elsie.Views` | Elsie.Views | Elsie.Views | Fluid views |
| `src/Elsie.Testing` | Elsie.Testing | Elsie.Testing | In-memory + loopback test hosts |
| `templates/Elsie.Templates` | Elsie.Templates | — | `dotnet new` |

### Current install graph

```text
Elsie (Meta) → Elsie.Web → Elsie.Core
```

### Pain points

| Issue | Detail |
|-------|--------|
| Name mismatch | Folder `src/Elsie` is **not** package `Elsie` |
| Extra project | Meta exists only for branding PackageId |
| NuGet gallery | Empty/meta packages historically hid TFMs; marker DLL was a workaround |
| Cognitive load | Three packages for two layers (core + host) |
| Docs/CI | Must special-case Meta in pack lists and validation |

### What stays (product architecture)

- Custom TCP host (no ASP.NET)
- `ElsieApp` / `ElsieWeb.Run` DX
- Host-agnostic dispatcher, modules, results
- Optional Auth / Cors / Views / Testing
- MS.DI only in core
- Multi-TFM `net8.0;net10.0`
- Trusted Publishing via GitHub Actions

---

## 3. Target state (detailed)

### 3.1 Package catalog

| PackageId | Install when | Contents |
|-----------|--------------|----------|
| **Elsie** | Almost always (apps) | Host: `ElsieApp`, server, static files, OpenAPI routes, WS/H2 surface in `Elsie.Web` namespaces; **depends on Elsie.Core** |
| **Elsie.Core** | Library authors, advanced, or “dispatch only” | Modules, routing, pipelines, results, health, rate limit, principal, multipart bind |
| **Elsie.Auth** | Cookie/JWT apps | Tickets, JWT validation, gates |
| **Elsie.Cors** | Browser APIs | Preflight filter + after-hook |
| **Elsie.Views** | HTML / Liquid | Fluid engine |
| **Elsie.Testing** | App unit/integration tests | `ElsieInMemoryHost`, `ElsieTestHost`, asserts |
| **Elsie.Templates** | Scaffolding | `elsie`, `elsie-api` templates |

### 3.2 Project tree (target)

```text
src/
  Elsie.Core/                 # was src/Elsie
    Elsie.Core.csproj         # PackageId Elsie.Core, AssemblyName Elsie
    ... (existing kernel sources)

  Elsie/                      # was src/Elsie.Web
    Elsie.csproj              # PackageId Elsie, AssemblyName Elsie.Web (see §3.3)
    ... (host sources: ElsieApp, Hosting/, Http/, Http2/, …)

  Elsie.Auth/
  Elsie.Cors/
  Elsie.Views/
  Elsie.Testing/

  # DELETED
  # Elsie.Meta/
```

### 3.3 Assembly & namespace policy (non-breaking for apps)

| Layer | AssemblyName | RootNamespace / public namespaces | Breaking? |
|-------|--------------|-----------------------------------|-----------|
| Core | **`Elsie`** (keep) | **`Elsie`**, `Elsie.Routing`, … | No — same as today |
| Host | **`Elsie.Web`** (keep for this overhaul) | **`Elsie.Web`**, `Elsie.Web.Hosting` | No — same as today |
| Host PackageId | **`Elsie`** | — | **Yes for package id** — consumers of **Elsie.Web** package must migrate |

**Rationale:** Decouple **NuGet package identity** from **assembly file name**. Package `Elsie` may contain `Elsie.Web.dll`. That is normal and avoids renaming every `using Elsie.Web` and InternalsVisibleTo in one PR.

**Future (optional, separate breaking change):** rename assembly `Elsie.Web` → `Elsie.Hosting` or `Elsie` (if Core assembly becomes `Elsie.Core`). **Out of scope for this overhaul** unless explicitly pulled in.

### 3.4 ProjectReference matrix (target)

| From | To |
|------|-----|
| `src/Elsie` (host) | `src/Elsie.Core` |
| `src/Elsie.Auth` | `Elsie.Core` + host project as today (Web/Elsie) |
| `src/Elsie.Cors` | `Elsie.Core` + host |
| `src/Elsie.Views` | `Elsie.Core` |
| `src/Elsie.Testing` | `Elsie.Core` + host |
| Samples | Prefer host project (`src/Elsie`) |
| `tests/Elsie.Tests` | Core (+ Testing as needed) |
| `tests/Elsie.Web.Tests` | Host (+ Testing, Auth, …) |
| Templates | PackageReference **`Elsie`** (version from Directory.Build.props) |

### 3.5 csproj property sketch

**`src/Elsie.Core/Elsie.Core.csproj`** (was `src/Elsie/Elsie.csproj`):

```xml
<PackageId>Elsie.Core</PackageId>
<AssemblyName>Elsie</AssemblyName>
<RootNamespace>Elsie</RootNamespace>
<!-- Description: host-agnostic modules, routing, … -->
```

**`src/Elsie/Elsie.csproj`** (was `src/Elsie.Web/Elsie.Web.csproj`):

```xml
<PackageId>Elsie</PackageId>
<AssemblyName>Elsie.Web</AssemblyName>
<RootNamespace>Elsie.Web</RootNamespace>
<!-- Description: Elsie HTTP host — ElsieApp, HTTP/1.1, TLS, HTTP/2, … -->
<!-- ProjectReference → ..\Elsie.Core\Elsie.Core.csproj -->
```

**Delete** `src/Elsie.Meta/**`.

---

## 4. Breaking changes (explicit)

### 4.1 Package consumers

| Before | After | Action |
|--------|-------|--------|
| `PackageReference Include="Elsie"` | Same | Still correct; content is now **host**, not meta (still pulls Core transitively) |
| `PackageReference Include="Elsie.Web"` | Prefer **`Elsie`** | Add `Elsie` or keep Web id **if** we dual-publish (see §7) |
| `PackageReference Include="Elsie.Core"` | Same | Unchanged role |
| Templates / samples using project refs to Web | Point at `src/Elsie` | Path update |

### 4.2 Dual-publish option (recommended for one release)

To avoid stranding anyone who already took `Elsie.Web`:

| Release | Behavior |
|---------|----------|
| **0.3.0-beta.2 / 0.4.0** | PackageId **`Elsie`** = host. Also pack a **deprecated** `Elsie.Web` that **only** depends on `Elsie` (or is a type-forward meta) with clear description: “Use package Elsie.” |
| Later | Stop shipping `Elsie.Web` package id |

**Minimum viable:** only rename PackageId Web → Elsie; document `Elsie.Web` as retired. Dual-publish is nicer for professionalism.

### 4.3 Source / API

| Surface | Break? |
|---------|--------|
| `namespace Elsie` types | **No** |
| `namespace Elsie.Web` types | **No** (assembly name unchanged) |
| `ElsieApp`, `ElsieWeb.Run` | **No** |
| Project path `src/Elsie.Web` | **Yes** for contributors (path → `src/Elsie`) |

---

## 5. Implementation plan (ordered, detailed)

Work in a dedicated branch, e.g. `refactor/package-layout`. Prefer **one PR** if automation is solid; otherwise two:

1. **PR A — Rename paths + PackageIds (no dual-publish yet)**  
2. **PR B — Docs, templates, CI, dual-publish shim (optional)**

### Phase 0 — Preconditions

- [ ] `dotnet test Elsie.sln -c Release` green on main  
- [ ] Note current version in `src/Directory.Build.props`  
- [ ] Snapshot `artifacts/nuget` nuspecs if needed for comparison  
- [ ] Branch from main  

### Phase 1 — Rename Core project

1. `git mv src/Elsie src/Elsie.Core`  
2. `git mv src/Elsie.Core/Elsie.csproj src/Elsie.Core/Elsie.Core.csproj`  
3. Confirm PackageId remains **`Elsie.Core`**, AssemblyName **`Elsie`**  
4. Update every ProjectReference:
   - `..\Elsie\Elsie.csproj` → `..\Elsie.Core\Elsie.Core.csproj`  
5. Update `Elsie.sln` project path and display name  
6. Update InternalsVisibleTo if any referenced old path only (usually assembly name, not path)  
7. Build + test  

### Phase 2 — Promote Web to package Elsie

1. `git mv src/Elsie.Web src/Elsie`  
   - **Conflict note:** after Phase 1, `src/Elsie` is free (Core moved).  
2. `git mv src/Elsie/Elsie.Web.csproj src/Elsie/Elsie.csproj`  
3. In `Elsie.csproj`:
   - `PackageId` → **`Elsie`**  
   - Keep `AssemblyName` = `Elsie.Web`  
   - Keep `RootNamespace` = `Elsie.Web`  
   - ProjectReference → `..\Elsie.Core\Elsie.Core.csproj`  
4. Description: “Elsie HTTP host and app package — depends on Elsie.Core.”  
5. Update solution, all ProjectReferences to Web → `src/Elsie/Elsie.csproj`  
6. InternalsVisibleTo: still `Elsie.Web` assembly / `Elsie.Web.Tests`  
7. Build + test  

### Phase 3 — Remove Meta

1. Remove `src/Elsie.Meta` from solution  
2. `git rm -r src/Elsie.Meta`  
3. Remove from CI pack lists and publish workflow  
4. Remove meta-specific nuspec validation (or replace with: package **Elsie** must depend on **Elsie.Core** and contain host assembly)  
5. Build + test + pack  

### Phase 4 — Samples, templates, benchmarks

| Area | Change |
|------|--------|
| Samples `*.csproj` | ProjectReference → `..\..\src\Elsie\Elsie.csproj` |
| Templates `PackageReference` | `Elsie` version aligned with Directory.Build.props |
| Benchmarks | Core and/or host refs as needed |
| `using Elsie.Web` | Unchanged |

### Phase 5 — Tests

| Project | Refs |
|---------|------|
| `Elsie.Tests` | Elsie.Core (+ Testing) |
| `Elsie.Web.Tests` | Host `src/Elsie` (maybe rename test project later to `Elsie.Host.Tests` — **optional**, not required) |
| Auth/Cors/Views/Testing tests | Update paths only |

**Do not rename `Elsie.Web.Tests` in the same PR** unless low cost — reduces noise.

### Phase 6 — CI / publish

**`ci.yml` / `publish-nuget.yml` pack list (target):**

```text
src/Elsie.Core/Elsie.Core.csproj
src/Elsie/Elsie.csproj                 # PackageId Elsie
src/Elsie.Auth/Elsie.Auth.csproj
src/Elsie.Cors/Elsie.Cors.csproj
src/Elsie.Views/Elsie.Views.csproj
src/Elsie.Testing/Elsie.Testing.csproj
templates/Elsie.Templates.csproj
# optional dual-publish shim project if used
```

**Validate packs:**

- Required nupkgs: `Elsie.{version}.nupkg`, `Elsie.Core.{version}.nupkg`, …  
- **Not** required: `Elsie.Web.*` unless dual-publish  
- Unzip `Elsie.*.nupkg`:
  - nuspec has dependency `Elsie.Core`
  - `lib/net8.0/` and `lib/net10.0/` contain host assembly (`Elsie.Web.dll`)
  - **No** need for Meta marker assembly  

Trusted Publishing workflow file name stays `publish-nuget.yml` (nuget.org policy must match).

### Phase 7 — Documentation

Update comprehensively:

| Doc | Content |
|-----|---------|
| `README.md` | Package table: Elsie = host; Elsie.Core = kernel; no Meta/Web package |
| `AGENTS.md` | Repo map paths |
| `docs/getting-started.md` | `dotnet add package Elsie` |
| `docs/hosting-and-aot.md` | Host package name |
| `docs/testing.md` | Testing vs tests/ (already); package refs |
| `docs/security.md` | Unchanged product advice |
| `CHANGELOG.md` | Breaking: Elsie.Web package id → Elsie; Meta removed |
| This file | Mark Status: **Implemented** when done |

### Phase 8 — Versioning & release notes

Recommended version bump when this lands:

- **`0.4.0-alpha.1`** or **`0.3.0-beta.2`** if still pre-1.0  

Changelog section must state:

```markdown
### Breaking
- Package **Elsie** is now the HTTP host (was a metapackage).
- Package **Elsie.Web** removed (or deprecated shim → Elsie).
- Project paths: `src/Elsie.Core`, `src/Elsie` (host).

### Migration
- Apps: keep `PackageReference Include="Elsie"` (recommended).
- If you referenced **Elsie.Web** explicitly, switch to **Elsie**.
- ProjectReference monorepo consumers: update paths (see Elsie_Overhaul.md).
```

### Phase 9 — Verification checklist

- [ ] `dotnet restore Elsie.sln`  
- [ ] `dotnet build Elsie.sln -c Release`  
- [ ] `dotnet test Elsie.sln -c Release`  
- [ ] `dotnet pack` all packages; inspect `Elsie` nuspec  
- [ ] Local: `dotnet new` template pack install + create app (optional)  
- [ ] Smoke: HelloWorld sample run  
- [ ] `rg Elsie.Meta|Elsie.Web.csproj|src/Elsie.Web` → zero stale refs  
- [ ] `rg PackageId.*Elsie.Web` → only shim if any  
- [ ] CI green  
- [ ] Release + Trusted Publishing push  

---

## 6. Dual-publish shim (optional but professional)

If `Elsie.Web` was already published to nuget.org:

**Option A — Dependency redirect package**

```text
src/Elsie.Web.Shim/   (or pack from a tiny csproj)
  PackageId: Elsie.Web
  Description: DEPRECATED. Use package Elsie instead. This package only depends on Elsie.
  Dependencies: Elsie (= same version)
  IncludeBuildOutput: false + lib placeholders OR empty with dependency groups
```

**Option B — Hard break**

- Stop packing `Elsie.Web`  
- Document only  

**Recommendation:** Option A for at least one minor line, then remove.

---

## 7. What about Elsie.Testing?

| Question | Answer |
|----------|--------|
| Remove because we have `tests/`? | **No.** |
| `tests/` | Our CI tests **of** Elsie |
| `Elsie.Testing` | NuGet helpers **for app authors** (`ElsieInMemoryHost`, loopback host, asserts) |

**Out of scope to remove.** Only update project paths to Core + host.

---

## 8. Rejected alternatives (and why)

| Alternative | Why rejected |
|-------------|--------------|
| Core PackageId = `Elsie`, host = `Elsie.Hosting` | `dotnet add package Elsie` does not run a server; bad DX |
| Core depends on host | Package cycle |
| Keep Meta forever | Extra empty layer; gallery/UX tax |
| Single mega-package (Auth+Views+Cors inside Elsie) | Fat default install; couples optional deps; harder versioning |
| Rename all namespaces to `Elsie.Core.*` | Huge break for no user benefit |
| Rename host assembly to `Elsie` while Core assembly is `Elsie` | DLL name collision |

---

## 9. Risk register

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Missed ProjectReference path | Medium | `rg` + full solution build |
| CI still packs Meta / old version strings | Medium | Dynamic version from Directory.Build.props; pack list review |
| Consumers on Elsie.Web stranded | Medium | Dual-publish shim + changelog |
| Confusion “package Elsie has Elsie.Web.dll” | Low | README package table; description text |
| git mv history lost | Low | Use `git mv` only |

---

## 10. Contributor / monorepo cheat sheet (after)

```bash
# App sample
ProjectReference → src/Elsie/Elsie.csproj          # package Elsie (host)
# transitive core

# Kernel-only experiment
ProjectReference → src/Elsie.Core/Elsie.Core.csproj

# NuGet app
dotnet add package Elsie
```

```csharp
using Elsie;       // modules, results
using Elsie.Web;   // ElsieApp — assembly still Elsie.Web.dll

ElsieApp.Run<App>(args);
```

---

## 11. Mapping table (search/replace aid)

| Old | New |
|-----|-----|
| `src/Elsie/Elsie.csproj` | `src/Elsie.Core/Elsie.Core.csproj` |
| `src/Elsie.Web/Elsie.Web.csproj` | `src/Elsie/Elsie.csproj` |
| `src/Elsie.Meta/` | *(deleted)* |
| PackageId `Elsie.Web` | PackageId `Elsie` |
| PackageId `Elsie` (meta) | PackageId `Elsie` (host — same id, new meaning) |
| Pack list entry Meta | Remove |
| Pack list entry Web | `src/Elsie/Elsie.csproj` |
| NuGet validate “depends on Elsie.Web” | “depends on Elsie.Core”; host dll present |
| Docs “Elsie.Web package” | “Elsie package (host)” |
| Docs “metapackage” | Remove |

---

## 12. Suggested PR description (copy-paste)

```markdown
## Summary
Restructure NuGet layout: package **Elsie** is the HTTP host; **Elsie.Core** is the kernel.
Remove the Elsie.Meta metapackage. Align project folders with package IDs.

## Breaking
- Package Elsie is no longer a thin meta; it contains the host (was Elsie.Web).
- Package Elsie.Web retired / shimmed — use Elsie.
- Repo paths: src/Elsie.Core, src/Elsie (host).

## Non-breaking
- Namespaces Elsie / Elsie.Web unchanged.
- Assembly Elsie.dll (core) and Elsie.Web.dll (host) names unchanged this PR.

## Test plan
- [ ] dotnet test Elsie.sln -c Release
- [ ] pack + nuspec inspect for Elsie → Elsie.Core
- [ ] sample HelloWorld
```

---

## 13. Success criteria

1. **`dotnet add package Elsie`** restores host + Core; app compiles with `ElsieApp` / modules.  
2. **nuget.org** (after publish) shows **net8.0 / net10.0** and dependency **Elsie.Core** for package Elsie.  
3. **No** `Elsie.Meta` project or pack step.  
4. **Folders** `src/Elsie` and `src/Elsie.Core` match package ids.  
5. **All tests green**; CI pack validation updated.  
6. **Docs/README/AGENTS** describe the new layout only.  
7. This document marked **Implemented** with date and PR link.

---

## 14. Out of scope (follow-ups)

- HTTP/2 / security feature work (already on main)  
- Renaming `namespace Elsie.Web` → `Elsie`  
- Renaming test project `Elsie.Web.Tests` → `Elsie.Tests.Host`  
- Merging Auth/Views into default install  
- Single-TFM or netstandard  
- Changing Trusted Publishing policy name (keep `publish-nuget.yml` unless nuget.org policy updated)

---

## 15. Implementation status

| Item | Status |
|------|--------|
| Design locked | **Yes** (this document) |
| Code rename | **Done** |
| Dual-publish shim | Skipped (hard break + changelog) |
| Published to nuget.org | After implementation + release |

When implementing, work top-to-bottom through **§5**, then tick **§9** and **§13**.

---

## 16. One-line summary

> **Elsie is the host. Elsie.Core is the kernel. Meta is gone. Folders match packages. `dotnet add package Elsie` is enough.**
