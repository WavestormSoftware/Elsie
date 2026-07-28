# Elsie

Lightweight, low-ceremony HTTP modules for **ASP.NET Core** (.NET 8 / .NET 9).

Elsie is inspired by the developer experience of Sinatra-style frameworks (small modules, explicit routes, minimal wiring). It is an **original** WavestormSoftware project under the MIT license — not a fork of any other framework.

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddElsie();
builder.Services.AddSingleton<IGreeter, Greeter>();
builder.Services.AddElsieModule<HelloModule>(); // preferred: explicit registration

var app = builder.Build();
app.MapElsie();
app.Run();

public sealed class HelloModule : ElsieModule
{
    // Modules are singletons — ctor-inject singleton-safe services
    public HelloModule(IGreeter greeter)
    {
        Get("/hello/{name}", ctx =>
            ElsieResult.Text(greeter.Greet(ctx.RouteValues["name"])));

        // Request-scoped services: resolve per request
        Get("/items/{id:int}", ctx =>
        {
            var svc = ctx.GetRequiredService<IItemService>();
            return ctx.Json(svc.Get(ctx.RouteValues["id"]));
        });

        // Module pipeline: return a result to short-circuit the handler
        Before(ctx => ctx.QueryOrDefault("key") is null
            ? ElsieResult.Status(401)
            : null);
    }
}
```

## Module registration

| Approach | How | Notes |
|----------|-----|--------|
| **Explicit (recommended)** | `services.AddElsieModule<TModule>()` | Clear, test-friendly, no surprises |
| Entry-assembly scan | `AddElsie()` default `ScanEntryAssembly = true` | Picks up concrete `ElsieModule` types in the entry assembly |
| Extra assemblies | `AddElsie(o => o.AssembliesToScan.Add(asm))` | Combined with optional entry scan |
| Disable scan | `AddElsie(o => o.ScanEntryAssembly = false)` | **Default in `ElsieTestHost`** |

Duplicate method+template across modules throws at startup when the route table is built.

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Modules, routing, pipelines, context, results |
| `Elsie.AspNetCore` | DI + `MapElsie` / `UseElsie` middleware |
| `Elsie.Testing` | `ElsieTestHost` + response assert helpers |

## Features (0.1.x)

- Sinatra-style `ElsieModule` route DSL (`Get`/`Post`/`Put`/`Patch`/`Delete`/…)
- Route parameters and constraints: `{name}`, `{id:int}`, `{id:long}`, `{id:guid}`, `{flag:bool}`
- Catch-all segments: `/files/{*path}` (concrete routes win over catch-alls)
- `405 Method Not Allowed` + `Allow` when the path matches another verb
- `ElsieResult` helpers: text, JSON, bytes, status, redirect, stream, custom headers
- `ElsieOptions.JsonSerializerOptions` (+ `ctx.Json(value)` / `ReadJsonAsync`)
- DI: module ctor injection + `ctx.GetRequiredService<T>()` / `ctx.RequestServices`
- Module + application before/after pipelines (`ConfigureElsiePipelines` composes)
- ASP.NET Core host via `AddElsie` / `MapElsie` (terminal) or `UseElsie` (pass-through)
- `ElsieTestHost` + `AssertStatus` / `AssertTextAsync` / `AssertHeader` / `AssertJsonAsync`

## Testing

```csharp
await using var host = ElsieTestHost.Create(s =>
{
    s.AddSingleton<IGreeter, Greeter>();
    s.AddElsieModule<HelloModule>();
});

var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
await response.AssertTextAsync("Hello Ada!");
```

## Build

```bash
dotnet restore Elsie.sln
dotnet build Elsie.sln -c Release
dotnet test Elsie.sln -c Release
dotnet run --project samples/Elsie.Sample.Hello
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## Docs

- [`AGENTS.md`](AGENTS.md) — contributor / coding-agent notes

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
