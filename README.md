# Elsie

HTTP module framework for **.NET 8** and **.NET 10**. Define routes in small modules, return results, host on ASP.NET Core.

```bash
dotnet add package Elsie          # 0.2.0-alpha.2 — pulls Elsie.AspNetCore + Elsie.Core
```

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
dotnet run
# GET /           → Hello, Elsie!
# GET /hello/Ada  → Hello Ada!
```

Templates:

```bash
dotnet new install Elsie.Templates
dotnet new elsie -n HelloApp        # minimal app
dotnet new elsie-api -n TodosApi    # CRUD + API key + OpenAPI
```

Guides: [docs/](docs/) · Samples: [HelloWorld](samples/Elsie.Sample.HelloWorld) · [Hello](samples/Elsie.Sample.Hello) · [Api](samples/Elsie.Sample.Api) · [Views](samples/Elsie.Sample.Views) · [Full](samples/Elsie.Sample.Full) · NuGet: [Elsie](https://www.nuget.org/packages/Elsie)

---

## Quickstart (builder)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddElsie();                              // DI + quieter console logs
builder.Services.AddElsieModule<TodosModule>();  // or rely on entry-assembly scan

var app = builder.Build();
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "My API";
    o.UiPath = "/scalar";                        // optional Scalar CDN UI
});
app.MapElsie();
app.Run();
```

| API | Use |
|-----|-----|
| `ElsieWeb.Run<T>(args)` | Smallest host |
| `builder.AddElsie()` | Wire DI (`ScanEntryAssembly = true` by default) |
| `AddElsieModule<T>()` | Explicit module registration (prefer in tests) |
| `app.MapElsie()` | Map Elsie into the pipeline (non-terminal by default) |

Modules are **singletons**. Inject singleton-safe services in the ctor; resolve request-scoped services with `ctx.GetRequiredService<T>()` / `ctx.Services`.

JSON: `ElsieResult.Json(...)` uses framework defaults (`ElsieJson.DefaultOptions`); `ctx.Json(...)` uses app `ElsieOptions.JsonSerializerOptions`.

---

## Modules and routing

```csharp
public sealed class TodosModule : ElsieModule
{
    public TodosModule(ITodoStore store)
    {
        Path("/api");
        Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));

        Group("/todos", () =>
        {
            Get("/", ctx => ctx.Json(store.List(ctx.QueryOrDefault("q"))))
                .Named("listTodos")
                .WithTags("todos");

            Get("/{id:guid}", ctx =>
            {
                if (!ctx.RequireRoute("id", out Guid id, out var error))
                    return error!;
                return ctx.Json(store.Get(id));
            }).Named("getTodo").Produces<Todo>();

            Post("/", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
                if (!bind.IsSuccess) return bind.Error!;
                var created = store.Add(bind.Value!.Title);
                return ElsieResult.Created(ctx.UrlFor("getTodo", new { id = created.Id }), created);
            }).Accepts<CreateTodo>().Produces<Todo>(201);
        });
    }
}
```

**Route templates:** `{name}`, optional `{name?}`, default `{name=5}`, constraints `{id:int}`, catch-all `{*path}` (last segment only).

**Built-in constraints:** `int`, `long`, `guid`, `bool`, `alpha`, `datetime`, `decimal`, `double`, `minlength(n)`, `maxlength(n)`, `length(n|min,max)`, `min(n)`, `max(n)`, `range(a,b)`, `regex(...)`.

**Matching:** per segment, **static > constrained > param > catch-all**. Startup fails on unknown constraints, duplicate param names, bad catch-all placement, ambiguous routes, and duplicate route names. Wrong verb on a known path → **405** + `Allow` + problem+json. HEAD maps to GET when `ElsieOptions.ImplicitHead` is on (default).

---

## Results, binding, context

```csharp
ElsieResult.Text("ok")
ElsieResult.Html("<p>hi</p>")
ElsieResult.Json(payload, statusCode: 201)   // framework JSON defaults
ctx.Json(payload)                            // app JsonSerializerOptions
ElsieResult.Created("/items/1", body)
ElsieResult.Accepted()
ElsieResult.File(bytes, "application/pdf", downloadName: "a.pdf")
ElsieResult.Redirect("/elsewhere")           // 302; 307/308 helpers also
ElsieResult.NoContent()
ElsieResult.NotModified()
ElsieResult.Problem(400, "Bad Request", detail)
ElsieResult.ValidationProblem(errors)
ElsieResult.ServerSentEvents(async (w, ct) => await w.WriteEventAsync("tick", "1", ct))

var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
var form = await ctx.BindFormAsync<LoginForm>(ct);
var query = ctx.BindQuery<SearchQuery>();
ctx.Route<int>("id");
ctx.Query<bool>("shout");
ctx.RequireRoute("id", out Guid id, out var error);

ctx.Request.Method / Path / Scheme / Host / PathBase / Protocol / RemoteIp
ctx.Request.GetHeader / GetQuery / GetCookie
ctx.UrlFor("getTodo", new { id })
ctx.Response.Headers["X-App"] = "1";
ctx.Response.SetCookie("sid", value, new ElsieCookieOptions { HttpOnly = true });
ctx.GetRequiredService<IMyService>();
```

Exception handling (first match wins):

```csharp
builder.AddElsie(o =>
{
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error", ex.Message));
});
// MapException → module OnError → ExceptionHandler → rethrow
// After-hooks still run when the exception is mapped to a result
```

Need `HttpContext`? Host escape hatch (core stays free of it):

```csharp
Get("/trace", ctx =>
    ctx.TryGetHttpContext(out var http)
        ? ElsieResult.Text(http!.TraceIdentifier)
        : ElsieResult.Text("no-host"));
```

---

## Pipelines

App-wide or per-module **before** / **after** hooks. After-hooks may transform the result.

```csharp
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddBefore((ctx, _) =>
    {
        ctx.Response.Headers["X-Request-Id"] = Guid.NewGuid().ToString("n");
        return Task.FromResult<ElsieResult?>(null); // null = continue
    });
    p.AddAfter((ctx, result) =>
    {
        ctx.Response.Headers["X-Status"] = result.StatusCode.ToString();
        return result;
    });
});
// Order: app.Before → module.Before → handler → module.After → app.After
```

Core gates (no ASP.NET auth stack):

```csharp
Before(ElsieAuth.RequireApiKey("dev-secret"));                    // all methods (default)
Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("session"));
```

Cookie/JWT policies → **`Elsie.Auth`**.

---

## Optional packages

### Auth — `Elsie.Auth`

```csharp
builder.Services.AddElsieAuth(o => o.Cookie = c => c.Cookie.Name = "elsie-auth");
app.UseElsieAuth(); // before MapElsie

Before(ElsieAuthGates.RequireAuthenticated());
Before(ElsieAuthGates.RequireRole("admin"));
await ctx.SignInCookieAsync("ada", roles: ["user"]);
var user = ctx.GetUser();
```

### CORS — `Elsie.Cors`

Elsie handles **OPTIONS preflight** itself (ASP.NET `UseCors` does not see those requests).

```csharp
builder.Services.AddElsieCors(o => o.AddDefaultPolicy(p => p
    .AllowOrigins("http://localhost:5173")
    .AllowMethods("GET", "POST", "OPTIONS")
    .AllowHeaders("Content-Type")
    .AllowCredentials()));
app.UseElsieCors(); // before MapElsie
// optional: Get(...).WithCors("policy-name")
```

### Health — `Elsie.HealthChecks`

```csharp
builder.Services.AddElsieHealthChecks(o =>
{
    o.AddCheck("self", () => ElsieHealthCheckResult.Healthy(), ElsieHealthCheckTags.Live);
    o.AddCheck("db", ct => CheckDbAsync(ct), ElsieHealthCheckTags.Ready);
});
// GET /healthz | /healthz/live | /healthz/ready  (unhealthy → 503)
```

### Rate limiting — `Elsie.RateLimiting`

```csharp
Before(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.SlidingWindow(30, TimeSpan.FromSeconds(10),
    partitionKey: ctx => ctx.Request.GetHeader("X-Api-Key") ?? "anon"));
// 429 problem+json + Retry-After; uses TimeProvider
```

### OpenAPI (in host)

Route metadata (`.Named` / `.Accepts` / `.Produces` / `.WithSecurity` / …) builds the document.

```csharp
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "My API";
    o.Info.SecuritySchemes["ApiKey"] = ElsieOpenApiSecurityScheme.ApiKeyHeader();
    o.UiPath = "/scalar";
});
```

### Views — `Elsie.Views` (Fluid / Liquid)

```csharp
builder.Services.AddElsieViews(o => o.ContentRoot = builder.Environment.ContentRootPath);
return await ctx.ViewAsync("home", new { Title = "Hi", Name = "Ada" }, cancellationToken: ct);
```

```liquid
{% layout '_Layout.liquid' %}
<h1>Hello {{ Name }}!</h1>
```

### Static files (host)

```csharp
app.MapElsieStaticFiles("/assets", Path.Combine(contentRoot, "wwwroot"));
// ETag / Last-Modified + 304; no range requests
```

### Validation — `Elsie.FluentValidation`

```csharp
var bind = await ctx.BindAndValidateJsonAsync<CreateTodo>(ct);
```

### Testing — `Elsie.Testing`

```csharp
await using var mem = ElsieInMemoryHost.Create(s => s.AddElsieModule<HelloModule>());
var r = await mem.GetAsync("/hello/Ada");
Assert.Equal(200, r.StatusCode);

await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
```

In tests, set `ScanEntryAssembly = false` and register modules explicitly.

---

## Package layout

| Package | Contents |
|---------|----------|
| **[Elsie](https://www.nuget.org/packages/Elsie)** | Meta-package for apps → `Elsie.AspNetCore` → `Elsie.Core` |
| [Elsie.AspNetCore](https://www.nuget.org/packages/Elsie.AspNetCore) | `ElsieWeb`, `AddElsie`, `MapElsie`, OpenAPI, static files |
| [Elsie.Core](https://www.nuget.org/packages/Elsie.Core) | Host-agnostic modules, routing, dispatcher, results, pipelines |
| [Elsie.Auth](https://www.nuget.org/packages/Elsie.Auth) | Cookie/JWT + auth gates |
| [Elsie.Cors](https://www.nuget.org/packages/Elsie.Cors) | Elsie-native CORS |
| [Elsie.HealthChecks](https://www.nuget.org/packages/Elsie.HealthChecks) | `/healthz` live/ready |
| [Elsie.RateLimiting](https://www.nuget.org/packages/Elsie.RateLimiting) | Before-hook rate limits |
| [Elsie.Views](https://www.nuget.org/packages/Elsie.Views) | Fluid/Liquid views |
| [Elsie.FluentValidation](https://www.nuget.org/packages/Elsie.FluentValidation) | `BindAndValidateJsonAsync` |
| [Elsie.Testing](https://www.nuget.org/packages/Elsie.Testing) | In-memory + TestServer hosts |
| [Elsie.Templates](https://www.nuget.org/packages/Elsie.Templates) | `dotnet new elsie` / `elsie-api` |

Current version: **`0.2.0-alpha.2`** (prerelease; APIs may still change).

Namespaces stay `Elsie` / `Elsie.AspNetCore` regardless of package id. Library authors who want the host-agnostic surface only should reference **`Elsie.Core`**.

---

## Request flow

```
HTTP request
  → host (ASP.NET Core or in-memory test host)
  → ElsieRequest
  → RouteTable.Lookup → before hooks → handler → after hooks → ElsieResult
  → ElsieHttpResponse.FromDispatch
  → host writes status / headers / body
```

`MapElsie` is non-terminal by default so OpenAPI, static files, and other endpoints can coexist. `MapElsie(terminal: true)` returns 404 problem+json for unmatched routes.

---

## Docs

| Topic | |
|-------|--|
| [Getting started](docs/getting-started.md) | Install, smallest app, samples |
| [Modules](docs/modules.md) | Registration, DI lifetimes |
| [Routing](docs/routing.md) | Templates, constraints, precedence |
| [Results](docs/results.md) | Response factories |
| [Binding](docs/binding.md) | Route/query/JSON/form |
| [Pipelines & errors](docs/pipelines-and-errors.md) | Before/after, exception maps |
| [Auth](docs/auth.md) | Core gates + `Elsie.Auth` |
| [CORS](docs/cors.md) | `Elsie.Cors` |
| [Rate limiting](docs/rate-limiting.md) | Fixed/sliding windows |
| [Health checks](docs/health-checks.md) | Live/ready |
| [OpenAPI](docs/openapi.md) | Document + Scalar |
| [Views](docs/views.md) | Fluid/Liquid |
| [Static files](docs/static-files.md) | `MapElsieStaticFiles` |
| [Testing](docs/testing.md) | In-memory + TestServer |
| [Hosting & AOT](docs/hosting-and-aot.md) | Host notes |

Changelog: [CHANGELOG.md](CHANGELOG.md)

---

## License

MIT © [WavestormSoftware](https://github.com/WavestormSoftware) — [LICENSE](LICENSE)
