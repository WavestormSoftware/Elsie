# Elsie

**A lightweight, Sinatra-style web framework for .NET 8 / .NET 9.**

Define routes in small modules. Ship JSON APIs, HTML pages, or both. Low ceremony, explicit control, no controller hierarchy required.

MIT © [WavestormSoftware](https://github.com/WavestormSoftware) — original greenfield project, **not** a fork of any other framework.

```csharp
using Elsie;
using Elsie.AspNetCore;

ElsieWeb.Run<App>(args);

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
dotnet add package Elsie.AspNetCore
dotnet run --project samples/Elsie.Sample.HelloWorld
```

| Sample | What it shows |
|--------|----------------|
| [`HelloWorld`](samples/Elsie.Sample.HelloWorld) | One-liner app |
| [`Hello`](samples/Elsie.Sample.Hello) | DI, query, constraints, pipelines |
| [`Api`](samples/Elsie.Sample.Api) | CRUD API, auth hooks, OpenAPI |
| [`Views`](samples/Elsie.Sample.Views) | HTML templates + layouts |

---

## Why Elsie

| | |
|--|--|
| **Framework, not glue** | Router, dispatcher, results, pipelines, auth hooks, OpenAPI — first-class. ASP.NET Core is the default host, not the programming model. |
| **Module-shaped apps** | Feature modules with `Get` / `Post` / `Path` / `Group` / `Before` / `After`. Compose by registering modules. |
| **Host-agnostic core** | `Elsie` has no `HttpContext`. Same handlers run on ASP.NET Core or the in-memory test host. |
| **Honest surface** | Explicit routes, problem+json helpers, thin auth gates. Escape to ASP.NET when you need it. |

---

## Install

```bash
dotnet add package Elsie.AspNetCore          # apps (pulls core)
dotnet add package Elsie.Views              # optional HTML
dotnet add package Elsie.FluentValidation   # optional validation
dotnet add package Elsie.Testing            # tests
```

NuGet: [Elsie](https://www.nuget.org/packages/Elsie) · current `0.1.0-alpha.1`

---

## Application model

### One-liner

```csharp
ElsieWeb.Run<HelloModule>(args);
```

Quiets noisy framework console logs by default. Opt out: `ElsieWeb.Run<HelloModule>(args, quietConsole: false)`.

### Builder (full control)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddElsie();                              // DI + quiet console
builder.Services.AddElsieModule<TodosModule>();
builder.Services.AddElsieModule<HealthModule>();

var app = builder.Build();
app.MapElsieOpenApi(o => o.Info.Title = "My API"); // optional /openapi.json
app.MapElsie();
app.Run();
```

| Registration | When |
|--------------|------|
| `ElsieWeb.Run<T>()` | Smallest apps |
| `AddElsieModule<T>()` | Explicit, test-friendly |
| `AddElsie()` scan | `ScanEntryAssembly = true` (default) picks up concrete modules in the entry assembly |
| Tests | `ScanEntryAssembly = false` + explicit modules |

Modules are **singletons**. Ctor-inject singleton-safe services; resolve request-scoped services with `ctx.GetRequiredService<T>()`.

---

## Routing

```csharp
public sealed class TodosModule : ElsieModule
{
    public TodosModule(ITodoStore store)
    {
        Path("/api");
        Before(ElsieAuth.RequireApiKey("dev-secret")); // mutating verbs by default

        Group("/todos", () =>
        {
            Get("/", ctx => ctx.Json(store.List(ctx.QueryOrDefault("q"))));
            Get("/{id:guid}", ctx => /* ... */);
            Post("/", async (ctx, ct) => /* BindJsonAsync ... */);
            Delete("/{id:guid}", ctx => /* ... */);
        });
    }
}
```

- Parameters: `{name}`, constraints `{id:int|long|guid|bool}`, catch-all `{*path}` (final segment only)
- `Path` + `Group` nest prefixes
- Duplicate method+template → **startup throw**
- Wrong verb on a known path → **405** + `Allow`

---

## Handlers, results, context

```csharp
// Results
ElsieResult.Text("ok")
ElsieResult.Json(payload, statusCode: 201)
ElsieResult.NoContent()
ElsieResult.Redirect("/elsewhere")
ElsieResult.Problem(400, "Bad Request", detail)
ElsieResult.ValidationProblem(errors)

// Body
var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
if (!bind.IsSuccess) return bind.Error!;

// Request
ctx.Request.Method / Path
ctx.Request.GetHeader / GetQuery / GetQueryValues / GetCookie / GetHeaderValues
ctx.RouteOrDefault("id") / TryGetRouteGuid / RequireRouteInt / ...

// Response hooks
ctx.Response.Headers["X-App"] = "1";

// DI
ctx.GetRequiredService<IMyService>()
```

`Query` / `Headers` are first-value maps; use `GetQueryValues` / `GetHeaderValues` when keys repeat.

Optional process-wide exception mapping:

```csharp
builder.AddElsie(o =>
{
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error", ex.Message));
});
```

---

## Pipelines & auth

Application-wide or per-module **before** / **after** hooks:

```csharp
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddBefore((ctx, _) => { /* correlation id */ return Task.FromResult<ElsieResult?>(null); });
    p.AddAfter((ctx, result) => ctx.Response.Headers["X-Status"] = result.StatusCode.ToString());
});

// In a module:
Before(ElsieAuth.RequireApiKey("dev-secret"));
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("session"));
```

These are thin gates. Full JWT / cookie authentication middleware stays with ASP.NET Core when you need it.

---

## OpenAPI

```csharp
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "My API";
    o.Info.Version = "v1";
});
// Serves /openapi.json from the Elsie route table.
// Wire Scalar, Swagger UI, or any client against that URL.
```

---

## Views (HTML)

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

`{{x}}` HTML-encodes · `{{{x}}}` raw · layout slot `{{body}}`

---

## FluentValidation

```csharp
services.AddSingleton<IValidator<CreateTodo>, CreateTodoValidator>();
// or FluentValidation's assembly registration

var bind = await ctx.BindAndValidateJsonAsync<CreateTodo>(ct);
if (!bind.IsSuccess) return bind.Error!; // 400 validation problem+json
```

---

## ASP.NET Core when you need it

Elsie runs on ASP.NET Core via `Elsie.AspNetCore`. Drop to the host when useful:

```csharp
using Elsie.AspNetCore;

Get("/trace", ctx =>
    ctx.TryGetHttpContext(out var http)
        ? ElsieResult.Text(http.TraceIdentifier)
        : ElsieResult.Text("no-host"));
```

Core types (`ElsieRequest`, `ElsieResponse`, `ElsieDispatcher`, `ElsieHttpResponse`) stay free of `HttpContext`.

---

## Testing

```csharp
// Pure Elsie — no web server
await using var mem = ElsieInMemoryHost.Create(s => s.AddElsieModule<HelloModule>());
var r = await mem.GetAsync("/hello/Ada");
Assert.Equal(200, r.StatusCode);

// Full ASP.NET TestServer
await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
```

---

## Packages

| Package | Role |
|---------|------|
| **`Elsie`** | Web framework core — modules, routing, dispatcher, results, pipelines, auth hooks, OpenAPI document builder |
| **`Elsie.AspNetCore`** | Default host — `ElsieWeb`, `AddElsie`, `MapElsie`, `MapElsieOpenApi` |
| **`Elsie.Views`** | File templates + layouts |
| **`Elsie.FluentValidation`** | `BindAndValidateJsonAsync` |
| **`Elsie.Testing`** | In-memory host, TestServer host, HTTP asserts |

```bash
dotnet test Elsie.sln -c Release
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

### Publishing packages

GitHub Actions [`publish-nuget.yml`](.github/workflows/publish-nuget.yml) pushes to nuget.org with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC).

1. nuget.org policy: repo `WavestormSoftware/Elsie`, workflow `publish-nuget.yml`, environment `nuget`, owner `WavestormSoftware`
2. Repo variable **`NUGET_USER`** = nuget.org username of the **policy creator** (not the org name)
3. Run **Actions → publish-nuget** or publish a GitHub Release

---

## Architecture (short)

```
HTTP request
  → host adapter (ASP.NET / in-memory)
  → ElsieRequest
  → RouteTable.Lookup → pipelines → handler → ElsieResult
  → ElsieHttpResponse.FromDispatch   // single bake path
  → host writes status / headers / body
```

- Unmatched routes fall through (`MapElsie` is non-terminal) so OpenAPI and other endpoints coexist
- Multi-value query/headers are first-class; first-wins maps are views over them

---

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
