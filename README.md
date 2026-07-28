# Elsie

Lightweight, low-ceremony HTTP modules for .NET 8 / .NET 9.

Elsie is inspired by Sinatra-style frameworks (small modules, explicit routes, minimal wiring). It is an **original** WavestormSoftware project under the MIT license — not a fork of any other framework.

**Core is host-agnostic.** Routing, modules, pipelines, and results live in `Elsie` with no ASP.NET Core dependency. `Elsie.AspNetCore` is the first-party host adapter (`MapElsie` / `UseElsie`).

## Quick start (ASP.NET Core)

```csharp
using Elsie;
using Elsie.AspNetCore;

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
        Get("/", () => ElsieResult.Text("hi"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}
```

```bash
dotnet run --project samples/Elsie.Sample.Hello
dotnet run --project samples/Elsie.Sample.Api   # advanced
```

## Host-agnostic core

```csharp
var services = new ServiceCollection();
services.AddElsie(o => o.ScanEntryAssembly = false);
services.AddElsieModule<HelloModule>();
await using var sp = services.BuildServiceProvider();

var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/hello/Ada"));
// outcome.Status / outcome.Result / outcome.Response.Headers
```

In tests without ASP.NET: `ElsieInMemoryHost` in `Elsie.Testing`.

## Module registration

| Approach | How | Notes |
|----------|-----|--------|
| **Explicit (recommended)** | `services.AddElsieModule<TModule>()` | Clear, test-friendly |
| Entry-assembly scan | `AddElsie()` default `ScanEntryAssembly = true` | Concrete modules in entry assembly |
| Disable scan | `AddElsie(o => o.ScanEntryAssembly = false)` | Default in test hosts |

Modules are **singletons**. Ctor-inject singleton-safe services; use `ctx.GetRequiredService<T>()` for request scope.

## Routing

- Params: `{name}`, constraints `{id:int|long|guid|bool}`, catch-all `{*path}` (final)
- `Path("/api")` + `Group("/todos", () => { ... })` prefixes
- Duplicate method+template → startup throw; wrong verb → `405` + `Allow`

## Results & context

```csharp
ElsieResult.Text / Json / NoContent / Redirect / Problem / BadRequest / NotFound / ...
ctx.BindJsonAsync<T>()          // 400 problem+json on bad body
ctx.Request.Method / Path / GetHeader / GetQuery
ctx.Response.Headers["X-App"] = "1"   // before/after hooks
ctx.RequestServices / GetRequiredService<T>()
```

Optional: `ElsieOptions.ExceptionHandler` maps handler exceptions → results.

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Host-agnostic modules, router, dispatcher, results (MS.DI only) |
| `Elsie.AspNetCore` | `MapElsie` / `UseElsie` adapter over `HttpContext` |
| `Elsie.Testing` | `ElsieInMemoryHost` + ASP.NET `ElsieTestHost` + asserts |

## Testing

```csharp
// Pure core (no ASP.NET)
await using var mem = ElsieInMemoryHost.Create(s => s.AddElsieModule<HelloModule>());
var r = await mem.GetAsync("/hello/Ada");
Assert.Equal(200, r.StatusCode);

// Full ASP.NET TestServer
await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
```

## Samples

| Sample | Level |
|--------|--------|
| `samples/Elsie.Sample.Hello` | Easy — minimal module |
| `samples/Elsie.Sample.Api` | Advanced — Path/Group, DI store, bind, API key, exception handler |

## Build

```bash
dotnet test Elsie.sln -c Release
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
