# Elsie samples

Standalone demo apps. Each folder is **self-contained**: NuGet package references only (no `src/` project links). Copy a folder anywhere and run.

```bash
# requires .NET 8 SDK + nuget.org
cd Elsie.Sample.HelloWorld
dotnet run
```

| Sample | Packages | Try |
|--------|----------|-----|
| [Elsie.Sample.HelloWorld](Elsie.Sample.HelloWorld) | `Elsie` | `GET /` · `/hello/Ada` |
| [Elsie.Sample.Hello](Elsie.Sample.Hello) | `Elsie` | DI, constraints, pipelines |
| [Elsie.Sample.Api](Elsie.Sample.Api) | `Elsie`, `Elsie.Validation` | CRUD + API key + OpenAPI `/scalar` |
| [Elsie.Sample.Views](Elsie.Sample.Views) | `Elsie`, `Elsie.Views` | Fluid/Liquid home page |
| [Elsie.Sample.Dashboard](Elsie.Sample.Dashboard) | + Auth, Validation, Views | Cookie login, form CSRF (`ada@elsie.dev` / `pass`) |
| [Elsie.Sample.Full](Elsie.Sample.Full) | + Auth, Cors, Validation, Views | Kitchen sink (`ada` / `pass`, `GET /csrf`) |

Package version is pinned in each `.csproj` (currently **0.3.0-beta.2**). Bump the `PackageReference` versions when you want a newer Elsie release.

Copy **the whole sample directory** (including `Views/` / `wwwroot/` when present).
