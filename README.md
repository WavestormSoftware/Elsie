# Elsie

Lightweight, low-ceremony HTTP modules for .NET 8 / .NET 9.

Sinatra-style modules, explicit routes, minimal wiring. **Original** WavestormSoftware MIT project — not a fork of any other framework.

Apps use ASP.NET Core via `Elsie.AspNetCore`. Core stays host-agnostic (no `HttpContext`).

## Quick start

```csharp
using Elsie;
using Elsie.AspNetCore;

ElsieWeb.Run<HelloModule>(args);

public sealed class HelloModule : ElsieModule
{
    public HelloModule()
    {
        Get("/", () => ElsieResult.Text("Hello, world!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}
```

```bash
dotnet run --project samples/Elsie.Sample.HelloWorld  # simplest
dotnet run --project samples/Elsie.Sample.Hello       # easy features
dotnet run --project samples/Elsie.Sample.Api         # advanced API
dotnet run --project samples/Elsie.Sample.Views       # HTML templates
```

`ElsieWeb.Run` / `builder.AddElsie()` install quiet **Elsie console logging** by default (no Microsoft.Hosting spam). Opt out: `o.UseElsieConsoleLogging = false`.

## Module registration

| Approach | How | Notes |
|----------|-----|--------|
| **`ElsieWeb.Run<T>()`** | one-liner host | Apps / demos |
| **Explicit** | `services.AddElsieModule<TModule>()` | Clear, test-friendly |
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
ctx.ViewAsync("home", model)    // Elsie.Views — HTML + layouts
ctx.Request.Method / Path / GetHeader / GetQuery
ctx.Response.Headers["X-App"] = "1"   // before/after hooks
ctx.RequestServices / GetRequiredService<T>()
```

Optional: `ElsieOptions.ExceptionHandler` maps handler exceptions → results.

ASP.NET escape hatch (core stays free of `HttpContext`):

```csharp
using Elsie.AspNetCore;

Get("/trace", ctx =>
    ctx.TryGetHttpContext(out var http)
        ? ElsieResult.Text(http.TraceIdentifier)
        : ElsieResult.Text("core-only"));
```

## Views

```csharp
builder.AddElsie();
builder.Services.AddElsieViews(o => o.ContentRoot = builder.Environment.ContentRootPath);

Get("/", async (ctx, ct) => await ctx.ViewAsync("home", new { Title = "Hi", Name = "Ada" }, cancellationToken: ct));
```

```html
@layout _Layout
<h1>Hello {{Name}}</h1>
```

`{{x}}` HTML-encodes; `{{{x}}}` raw; layout uses `{{body}}`.

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Host-agnostic modules, router, dispatcher, results (MS.DI only) |
| `Elsie.AspNetCore` | `ElsieWeb` / `MapElsie` / `UseElsie` + console logging |
| `Elsie.Views` | Minimal file templates + layouts |
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
|--------|-------|
| `samples/Elsie.Sample.HelloWorld` | `ElsieWeb.Run` quickstart |
| `samples/Elsie.Sample.Hello` | DI, query, constraints, pipelines |
| `samples/Elsie.Sample.Api` | Path/Group CRUD, bind, API key, ExceptionHandler |
| `samples/Elsie.Sample.Views` | HTML templates + layout |

## Build

```bash
dotnet test Elsie.sln -c Release
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
