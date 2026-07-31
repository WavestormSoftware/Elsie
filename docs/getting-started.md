# Getting started

Elsie is a Sinatra-style HTTP module framework for **.NET 8** and **.NET 10**. Define routes in modules, return results, run on Elsie’s own lightweight host.

## Install

```bash
dotnet add package Elsie          # metapackage → Elsie.Web → Elsie.Core (app host)
# optional:
dotnet add package Elsie.Auth
dotnet add package Elsie.Cors
dotnet add package Elsie.Views
dotnet add package Elsie.Testing  # for *your* app's unit tests (hosts + asserts)
```

`Elsie` is a **metapackage** (`src/Elsie.Meta`): it has no app code of its own; NuGet depends on `Elsie.Web` for net8/net10 so one install is enough.

Templates:

```bash
dotnet new install Elsie.Templates
dotnet new elsie -n HelloApp
dotnet new elsie-api -n TodosApi
```

## Minimal app

```csharp
using Elsie;
using Elsie.Web;

ElsieApp.Run<App>(args);

public sealed class App : ElsieModule
{
    public App()
    {
        Get("/", () => ElsieResult.Text("Hello, Elsie!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}
```

```bash
dotnet run
# GET /           → Hello, Elsie!
# GET /hello/Ada  → Hello Ada!
```

`ElsieWeb.Run<T>(args)` is an equivalent one-liner wrapper.

## Fluent host

```csharp
ElsieApp.Create(args)
    .Module<TodosModule>()
    .Services(s => s.AddSingleton<ITodoStore, TodoStore>())
    .Configure(o =>
    {
        o.ScanEntryAssembly = false;
        o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    })
    .Listen("http://127.0.0.1:5000")
    .OpenApi(o =>
    {
        o.Info.Title = "My API";
        o.UiPath = "/scalar";
    })
    .StaticFiles(s =>
    {
        s.Root = "wwwroot";
        s.RequestPath = "/assets";
    })
    .Run();
```

| API | Use |
|-----|-----|
| `ElsieApp.Run<T>(args)` | Smallest host |
| `ElsieApp.Create(args)` | Fluent builder |
| `.Module<T>()` | Register a module |
| `.Services(...)` | MS.DI registrations |
| `.Configure(...)` | `ElsieOptions` |
| `.Listen(...)` | Bind URLs (default `http://127.0.0.1:5000`) |
| `.OpenApi` / `.StaticFiles` | Host features |

Modules are **singletons**. Inject singleton-safe services in the ctor; resolve request-scoped services with `ctx.GetRequiredService<T>()` / `ctx.Services`.

## Next

- [modules.md](modules.md) — routing in modules  
- [hosting-and-aot.md](hosting-and-aot.md) — TLS, HTTP/2, WebSockets  
- [testing.md](testing.md) — in-memory and loopback hosts  
- [auth.md](auth.md) — cookies and JWT  
