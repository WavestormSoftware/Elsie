# Elsie

**A lightweight, Sinatra-style web framework for .NET 8 / .NET 10.**

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
# or:  dotnet new install ./artifacts/nuget/Elsie.Templates.*.nupkg
#      dotnet new elsie -n MyApp
```

| Sample | What it shows |
|--------|----------------|
| [`HelloWorld`](samples/Elsie.Sample.HelloWorld) | One-liner `ElsieWeb.Run` |
| [`Hello`](samples/Elsie.Sample.Hello) | DI, typed route/query, pipelines, link gen |
| [`Api`](samples/Elsie.Sample.Api) | CRUD, API-key gate, OpenAPI + Scalar |
| [`Views`](samples/Elsie.Sample.Views) | Fluid/Liquid templates + layouts |
| [`Full`](samples/Elsie.Sample.Full) | Auth + CORS + rate limit + health + static + views |

**Guides:** [docs/](docs/) — getting started, modules, routing, results, binding, pipelines, auth, CORS, rate limiting, health checks, OpenAPI, views, static files, testing, hosting/AOT.

---

## Why Elsie

| | |
|--|--|
| **Framework, not glue** | Router, dispatcher, results, pipelines, OpenAPI — first-class. ASP.NET Core is the default host, not the programming model. |
| **Module-shaped apps** | Feature modules with `Get` / `Post` / `Path` / `Group` / `Before` / `After`. Compose by registering modules. |
| **Host-agnostic core** | `Elsie` has no `HttpContext`. Same handlers run on ASP.NET Core or the in-memory test host. |
| **Honest surface** | Explicit routes, problem+json helpers, thin auth gates. Escape to ASP.NET when you need it. |

---

## Install

```bash
dotnet add package Elsie.AspNetCore          # apps (pulls core)
dotnet add package Elsie.Auth               # cookie/JWT gates
dotnet add package Elsie.Cors               # Elsie-native CORS
dotnet add package Elsie.HealthChecks       # /healthz live/ready
dotnet add package Elsie.RateLimiting       # before-hook rate limits
dotnet add package Elsie.Views              # Fluid/Liquid HTML
dotnet add package Elsie.FluentValidation   # BindAndValidateJsonAsync
dotnet add package Elsie.Testing            # in-memory + TestServer hosts
```

NuGet: [Elsie](https://www.nuget.org/packages/Elsie) · current `0.2.0-alpha.1`

Templates (after packing):

```bash
dotnet pack templates/Elsie.Templates.csproj -c Release -o artifacts/nuget
dotnet new install artifacts/nuget/Elsie.Templates.0.2.0-alpha.1.nupkg
dotnet new elsie -n HelloApp
dotnet new elsie-api -n TodosApi
```

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

var app = builder.Build();
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "My API";
    o.UiPath = "/scalar";                        // optional Scalar CDN page
});
app.MapElsie();
app.Run();
```

| Registration | When |
|--------------|------|
| `ElsieWeb.Run<T>()` | Smallest apps |
| `AddElsieModule<T>()` | Explicit, test-friendly |
| `AddElsie()` scan | `ScanEntryAssembly = true` (default) picks up concrete modules in the entry assembly |
| Tests | `ScanEntryAssembly = false` + explicit modules |

Modules are **singletons**. Ctor-inject singleton-safe services; resolve request-scoped services with `ctx.GetRequiredService<T>()` / `ctx.Services`.

**JSON rule:** static `ElsieResult.Json` uses framework defaults (`ElsieJson.DefaultOptions`); `ctx.Json` uses app `ElsieOptions.JsonSerializerOptions`.

---

## Routing

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

- Parameters: `{name}`, optional `{name?}`, defaults `{name=5}`, constraints `{id:int}`, catch-all `{*path}` (final only)
- Built-in constraints: `int`, `long`, `guid`, `bool`, `alpha`, `datetime`, `decimal`, `double`, `minlength(n)`, `maxlength(n)`, `length(n|min,max)`, `min(n)`, `max(n)`, `range(a,b)`, `regex(...)`
- Precedence per segment: **static > constrained > param > catch-all** (deterministic; registration order does not decide ties)
- Startup validates unknown constraints, duplicate param names, catch-all position, **ambiguity**, and duplicate route names
- Wrong verb on a known path → **405** + `Allow` + problem+json
- HEAD falls back to GET by default (`ElsieOptions.ImplicitHead`)

---

## Handlers, results, context

```csharp
// Results
ElsieResult.Text("ok")
ElsieResult.Html("<p>hi</p>")
ElsieResult.Json(payload, statusCode: 201)   // framework JSON defaults
ctx.Json(payload)                            // app JsonSerializerOptions
ElsieResult.Created("/items/1", body)
ElsieResult.Accepted()
ElsieResult.File(bytes, "application/pdf", downloadName: "a.pdf")
ElsieResult.Redirect("/elsewhere")           // 302; also 307/308 helpers
ElsieResult.NoContent()
ElsieResult.NotModified()
ElsieResult.Problem(400, "Bad Request", detail)
ElsieResult.ValidationProblem(errors)
ElsieResult.ServerSentEvents(async (w, ct) => await w.WriteEventAsync("tick", "1", ct))

// Binding
var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
var form = await ctx.BindFormAsync<LoginForm>(ct);
var query = ctx.BindQuery<SearchQuery>();
ctx.Route<int>("id");  ctx.Query<bool>("shout");
ctx.RequireRoute("id", out Guid id, out var error);

// Request
ctx.Request.Method / Path / Scheme / Host / PathBase / Protocol / RemoteIp
ctx.Request.GetHeader / GetQuery / GetCookie
ctx.UrlFor("getTodo", new { id })

// Response hooks
ctx.Response.Headers["X-App"] = "1";
ctx.Response.SetCookie("sid", value, new ElsieCookieOptions { HttpOnly = true });

// DI
ctx.GetRequiredService<IMyService>()
ctx.Services   // request scope
```

Typed exception maps and optional process-wide handler:

```csharp
builder.AddElsie(o =>
{
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error", ex.Message));
});
```

Order: `MapException` → module `OnError` → `ExceptionHandler` → rethrow. After-hooks still run for mapped results.

---

## Pipelines

Application-wide or per-module **before** / **after** hooks. After-hooks may **transform** the result.

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
```

Order: app.Before → module.Before → handler → module.After → app.After.

Thin core gates (no ASP.NET auth middleware):

```csharp
Before(ElsieAuth.RequireApiKey("dev-secret"));          // all methods by default
Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("session"));
```

Full cookie/JWT → package **`Elsie.Auth`** (`AddElsieAuth` / `UseElsieAuth` + `ElsieAuthGates`).

---

## Feature packages (tour)

### Auth (`Elsie.Auth`)

```csharp
builder.Services.AddElsieAuth(o => o.Cookie = c => c.Cookie.Name = "elsie-auth");
// ...
app.UseElsieAuth(); // before MapElsie

Before(ElsieAuthGates.RequireAuthenticated());
Before(ElsieAuthGates.RequireRole("admin"));
await ctx.SignInCookieAsync("ada", roles: ["user"]);
var user = ctx.GetUser();
```

### CORS (`Elsie.Cors`)

Elsie answers **OPTIONS preflight** itself (ASP.NET `UseCors` never sees Elsie-handled OPTIONS).

```csharp
builder.Services.AddElsieCors(o => o.AddDefaultPolicy(p => p
    .AllowOrigins("http://localhost:5173")
    .AllowMethods("GET", "POST", "OPTIONS")
    .AllowHeaders("Content-Type")
    .AllowCredentials()));
app.UseElsieCors(); // before MapElsie
// optional per-route: Get(...).WithCors("name")
```

### Health checks (`Elsie.HealthChecks`)

```csharp
builder.Services.AddElsieHealthChecks(o =>
{
    o.AddCheck("self", () => ElsieHealthCheckResult.Healthy(), ElsieHealthCheckTags.Live);
    o.AddCheck("db", ct => CheckDbAsync(ct), ElsieHealthCheckTags.Ready);
});
// GET /healthz  |  /healthz/live  |  /healthz/ready   (unhealthy → 503)
```

### Rate limiting (`Elsie.RateLimiting`)

```csharp
Before(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.SlidingWindow(30, TimeSpan.FromSeconds(10),
    partitionKey: ctx => ctx.Request.GetHeader("X-Api-Key") ?? "anon"));
// 429 problem+json + Retry-After; TimeProvider-friendly
```

### OpenAPI

Route metadata → document. Optional Scalar CDN page via `UiPath`.

```csharp
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "My API";
    o.Info.SecuritySchemes["ApiKey"] = ElsieOpenApiSecurityScheme.ApiKeyHeader();
    o.UiPath = "/scalar";
});
```

### Views (`Elsie.Views` — Fluid / Liquid)

```csharp
builder.Services.AddElsieViews(o => o.ContentRoot = builder.Environment.ContentRootPath);
// handler:
return await ctx.ViewAsync("home", new { Title = "Hi", Name = "Ada" }, cancellationToken: ct);
```

```liquid
{% layout '_Layout.liquid' %}
<h1>Hello {{ Name }}!</h1>
```

### Static files

```csharp
app.MapElsieStaticFiles("/assets", Path.Combine(contentRoot, "wwwroot"));
// ETag / Last-Modified + 304; no range requests
```

---

## ASP.NET Core when you need it

```csharp
using Elsie.AspNetCore;

Get("/trace", ctx =>
    ctx.TryGetHttpContext(out var http)
        ? ElsieResult.Text(http.TraceIdentifier)
        : ElsieResult.Text("no-host"));
```

Core types stay free of `HttpContext`. Multipart: use the host escape hatch + `Elsie.Testing` multipart builder.

---

## Testing

```csharp
await using var mem = ElsieInMemoryHost.Create(s => s.AddElsieModule<HelloModule>());
var r = await mem.GetAsync("/hello/Ada");
Assert.Equal(200, r.StatusCode);

await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
```

---

## Packages

| Package | Role |
|---------|------|
| **`Elsie`** | Core — modules, routing, dispatcher, results, pipelines, auth hooks, OpenAPI builder |
| **`Elsie.AspNetCore`** | Host — `ElsieWeb`, `AddElsie`, `MapElsie`, `MapElsieOpenApi`, static files |
| **`Elsie.Auth`** | Cookie/JWT + `RequireAuthenticated` / Role / Claim / Policy |
| **`Elsie.Cors`** | Elsie-native CORS (preflight + ACAO) |
| **`Elsie.HealthChecks`** | `/healthz`, live, ready |
| **`Elsie.RateLimiting`** | Fixed/sliding window before-hooks |
| **`Elsie.Views`** | Fluid/Liquid views |
| **`Elsie.FluentValidation`** | `BindAndValidateJsonAsync` |
| **`Elsie.Testing`** | In-memory host, TestServer host, asserts |
| **`Elsie.Templates`** | `dotnet new elsie` / `elsie-api` |

```bash
dotnet test Elsie.sln -c Release
dotnet pack Elsie.sln -c Release -o artifacts/nuget
dotnet pack templates/Elsie.Templates.csproj -c Release -o artifacts/nuget
```

### Publishing packages

GitHub Actions [`publish-nuget.yml`](.github/workflows/publish-nuget.yml) pushes to nuget.org with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC).

1. nuget.org policy: repo `WavestormSoftware/Elsie`, workflow `publish-nuget.yml`, environment `nuget`, owner `WavestormSoftware`
2. Repo variable **`NUGET_USER`** = nuget.org username of the **policy creator**
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

- Unmatched routes fall through (`MapElsie` is non-terminal by default) so OpenAPI and other endpoints coexist
- `MapElsie(terminal: true)` answers unmatched with 404 problem+json
- Multi-value query/headers are first-class

---

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
