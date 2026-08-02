# Elsie samples

Standalone demo apps. During development each folder builds against the repo `src/` projects (project references) so samples always exercise the current API. When releasing, swap the `ProjectReference` entries back to `PackageReference` pins to publish self-contained copies.

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

For a published, self-contained copy, replace the `ProjectReference` entries with `PackageReference Include="Elsie" Version="0.4.0-beta" />` (and per-sample packages) — see the `Elsie.Templates` output for a reference shape.

Copy **the whole sample directory** (including `Views/` / `wwwroot/` when present).
