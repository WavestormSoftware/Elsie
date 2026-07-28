# Elsie

Lightweight, low-ceremony HTTP modules for **ASP.NET Core** (.NET 8 / .NET 9).

Elsie is inspired by the developer experience of Sinatra-style frameworks (small modules, explicit routes, minimal wiring). It is an **original** WavestormSoftware project under the MIT license — not a fork of any other framework.

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddElsie();
builder.Services.AddElsieModule<HelloModule>();

var app = builder.Build();
app.MapElsie();
app.Run();

public sealed class HelloModule : ElsieModule
{
    public HelloModule()
    {
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteValues["name"]}"));

        Get("/items/{id:int}", ctx =>
            ElsieResult.Json(new { id = ctx.RouteValues["id"] }));

        // Module pipeline: return a result to short-circuit the handler
        Before(ctx => ctx.QueryOrDefault("key") is null
            ? ElsieResult.Status(401)
            : null);
    }
}
```

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Modules, routing, pipelines, context, results |
| `Elsie.AspNetCore` | DI + `MapElsie` / middleware |
| `Elsie.Testing` | In-process test host |

## Features (0.1.x)

- Sinatra-style `ElsieModule` route DSL (`Get`/`Post`/`Put`/`Patch`/`Delete`/…)
- Route parameters and constraints: `{name}`, `{id:int}`, `{id:long}`, `{id:guid}`, `{flag:bool}`
- Catch-all segments: `/files/{*path}` (concrete routes win over catch-alls)
- `405 Method Not Allowed` + `Allow` when the path matches another verb
- `ElsieResult` helpers: text, JSON, bytes, status, redirect, custom headers
- `ElsieOptions.JsonSerializerOptions` (+ `ctx.Json(value)` / `ReadJsonAsync`)
- Module + application before/after pipelines (`ConfigureElsiePipelines` composes)
- ASP.NET Core host via `AddElsie` / `MapElsie`
- `ElsieTestHost` for in-process tests

## Build

```bash
dotnet restore Elsie.sln
dotnet build Elsie.sln -c Release
dotnet test Elsie.sln -c Release
dotnet run --project samples/Elsie.Sample.Hello
```

## Docs

- [`AGENTS.md`](AGENTS.md) — contributor / coding-agent notes

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
