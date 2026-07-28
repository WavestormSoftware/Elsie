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

`ElsieWeb.Run` / `builder.AddElsie()` quiet the console by default (no Microsoft.Hosting spam). Opt out: `builder.AddElsie(quietConsole: false)`.

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
ElsieResult.Text / Json / NoContent / Redirect / Problem / ValidationProblem / ...
ctx.BindJsonAsync<T>()          // 400 problem+json on bad body
ctx.ViewAsync("home", model)    // Elsie.Views — HTML + layouts
ctx.Request.Method / Path / GetHeader / GetQuery / GetQueryValues / GetCookie
ctx.Response.Headers["X-App"] = "1"   // before/after hooks
ctx.RequestServices / GetRequiredService<T>()
```

`Query` / `Headers` stay first-value maps; use `GetQueryValues` / `GetHeaderValues` for multi-value keys.

Optional: `ElsieOptions.ExceptionHandler` maps handler exceptions → results.

### Auth hooks

```csharp
Before(ElsieAuth.RequireApiKey("dev-secret"));     // mutating verbs by default
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("session"));
```

JWT/cookie **auth middleware** stays with ASP.NET — these are thin before-hook gates.

### FluentValidation

```csharp
builder.Services.AddElsieFluentValidation(typeof(CreateTodoValidator).Assembly);
// handler:
var bind = await ctx.BindAndValidateJsonAsync<CreateTodo>(ct);
if (!bind.IsSuccess) return bind.Error!;
```

### OpenAPI + Scalar

```csharp
app.MapElsieOpenApi(o => o.Title = "My API"); // /openapi.json + /scalar
app.MapElsie();
```

### ASP.NET escape hatch

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

Get("/", async (ctx, ct) =>
    await ctx.ViewAsync("home", new { Title = "Hi", Name = "Ada" }, cancellationToken: ct));
```

```html
@layout _Layout
<h1>Hello {{Name}}</h1>
```

`{{x}}` HTML-encodes; `{{{x}}}` raw; layout uses `{{body}}`.

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Modules, router, dispatcher, results, `ElsieAuth`, OpenAPI builder |
| `Elsie.AspNetCore` | `ElsieWeb` / `MapElsie` / `MapElsieOpenApi` / Scalar |
| `Elsie.FluentValidation` | `BindAndValidateJsonAsync` + validator DI |
| `Elsie.Views` | File templates + layouts |
| `Elsie.Testing` | In-memory + TestServer hosts + asserts |

```bash
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

**Publish (nuget.org):** GitHub Actions [`publish-nuget.yml`](.github/workflows/publish-nuget.yml) uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC) — no long-lived API key.

1. nuget.org → Trusted Publishing policy: repo `WavestormSoftware/Elsie`, workflow `publish-nuget.yml`, environment `nuget`, package owner `WavestormSoftware`.
2. GitHub repo variable **`NUGET_USER`** = nuget.org username of the **person who created the policy** (Account settings → Username) — **not** the org `WavestormSoftware`.
3. Run **Actions → publish-nuget** or publish a GitHub Release.

## Testing

```csharp
await using var mem = ElsieInMemoryHost.Create(s => s.AddElsieModule<HelloModule>());
var r = await mem.GetAsync("/hello/Ada");
Assert.Equal(200, r.StatusCode);

await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
```

## Samples

| Sample | Level |
|--------|-------|
| `samples/Elsie.Sample.HelloWorld` | `ElsieWeb.Run` quickstart |
| `samples/Elsie.Sample.Hello` | DI, query, constraints, pipelines |
| `samples/Elsie.Sample.Api` | CRUD + API key + OpenAPI/Scalar |
| `samples/Elsie.Sample.Views` | HTML templates + layout |

## Build

```bash
dotnet test Elsie.sln -c Release
```

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
