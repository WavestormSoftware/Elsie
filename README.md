# Elsie

HTTP module framework for **.NET 8** and **.NET 10**. Define routes in small modules, return results, run on Elsie’s own lightweight host.

**Why Elsie?** Tiny Sinatra-style modules, one fluent host (`ElsieApp`), no ASP.NET tax. Hold the whole request path in your head — modules → results → server — without `WebApplication` ceremony or a shared-framework dependency.

```bash
dotnet add package Elsie          # 0.3.0-beta.2 — host + Elsie.Core
```

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

Templates:

```bash
dotnet new install Elsie.Templates
dotnet new elsie -n HelloApp        # minimal app
dotnet new elsie-api -n TodosApi    # CRUD + cookie auth + OpenAPI
```

Guides: [docs/](docs/) · Samples: [HelloWorld](samples/Elsie.Sample.HelloWorld) · [Hello](samples/Elsie.Sample.Hello) · [Api](samples/Elsie.Sample.Api) · [Views](samples/Elsie.Sample.Views) · [Dashboard](samples/Elsie.Sample.Dashboard) · [Full](samples/Elsie.Sample.Full) · NuGet: [Elsie](https://www.nuget.org/packages/Elsie)

---

## Quickstart (fluent host)

```csharp
ElsieApp.Create(args)
    .Module<TodosModule>()
    .Services(s => s.AddSingleton<ITodoStore, TodoStore>())
    .Configure(o => o.ScanEntryAssembly = false)
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
| `ElsieApp.Run<T>(args)` / `ElsieWeb.Run<T>` | Smallest host |
| `ElsieApp.Create(args)` | Fluent host builder |
| `.Module<T>()` / `.Services(...)` | Modules + MS.DI |
| `.Listen(...)` / `.OpenApi(...)` / `.StaticFiles(...)` | Endpoints and host features |

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
ElsieResult.WebSocket(async (ws, ct) => { /* … */ })

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
.Configure(o =>
{
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error"));
})
// MapException → module OnError → ExceptionHandler → rethrow
// After-hooks still run when the exception is mapped to a result
```

---

## Pipelines

App-wide or per-module **before** / **after** hooks. After-hooks may transform the result.

```csharp
.Services(s => s.ConfigureElsiePipelines(p =>
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
}))
// Order: app.Before → module.Before → handler → module.After → app.After
```

Core gates:

```csharp
Before(ElsieAuth.RequireApiKey("dev-secret"));                    // all methods (default)
Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("session"));
```

Cookie sessions + JWT → **`Elsie.Auth`**.

---

## Optional packages

### Auth — `Elsie.Auth`

```csharp
.Services(s => s.AddElsieAuth(o =>
{
    o.Cookie = new ElsieCookieAuthOptions { CookieName = "elsie-auth" };
    o.Cookie.TicketKeyFromString("change-me");
}))

Before(ElsieAuthGates.RequireAuthenticated());
Before(ElsieAuthGates.RequireRole("admin"));
await ctx.SignInCookieAsync("ada", roles: ["user"]);
var user = ctx.GetUser();
```

### CORS — `Elsie.Cors`

Preflight is handled by a host request filter; ACAO is applied on actual responses via an after-hook.

```csharp
.Services(s => s.AddElsieCors(o => o.AddDefaultPolicy(p => p
    .AllowOrigins("http://localhost:5173")
    .AllowMethods("GET", "POST", "OPTIONS")
    .AllowHeaders("Content-Type")
    .AllowCredentials())))
// optional: Get(...).WithCors("policy-name")
```

### Health checks (in `Elsie.Core`)

```csharp
.Services(s => s.AddElsieHealthChecks(o =>
{
    o.AddCheck("self", () => ElsieHealthCheckResult.Healthy(), ElsieHealthCheckTags.Live);
    o.AddCheck("db", ct => CheckDbAsync(ct), ElsieHealthCheckTags.Ready);
}))
// GET /healthz | /healthz/live | /healthz/ready  (unhealthy → 503)
```

### Rate limiting (in `Elsie.Core`)

```csharp
Before(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1)));
Before(ElsieRateLimit.SlidingWindow(30, TimeSpan.FromSeconds(10),
    partitionKey: ctx => ctx.Request.GetHeader("X-Api-Key") ?? "anon"));
// 429 problem+json + Retry-After; uses TimeProvider
```

### OpenAPI (host)

Route metadata (`.Named` / `.Accepts` / `.Produces` / `.WithSecurity` / …) builds the document.

```csharp
.OpenApi(o =>
{
    o.Info.Title = "My API";
    o.UiPath = "/scalar";
})
```

### Views — `Elsie.Views` (Fluid / Liquid)

```csharp
.Services(s => s.AddElsieViews(o => o.ContentRoot = Directory.GetCurrentDirectory()))
return await ctx.ViewAsync("home", new { Title = "Hi", Name = "Ada" }, cancellationToken: ct);
```

```liquid
{% layout '_Layout.liquid' %}
<h1>Hello {{ Name }}!</h1>
```

### Static files (host)

```csharp
.StaticFiles(s =>
{
    s.Root = "wwwroot";
    s.RequestPath = "/assets";
})
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
| **[Elsie](https://www.nuget.org/packages/Elsie)** | **HTTP host** (`ElsieApp`), server, static files, OpenAPI — depends on `Elsie.Core`. Use this: `dotnet add package Elsie` |
| [Elsie.Core](https://www.nuget.org/packages/Elsie.Core) | Modules, routing, dispatcher, results, pipelines, health, rate limit |
| [Elsie.Auth](https://www.nuget.org/packages/Elsie.Auth) | Cookie tickets + JWT + auth gates |
| [Elsie.Cors](https://www.nuget.org/packages/Elsie.Cors) | Elsie-native CORS |
| [Elsie.Views](https://www.nuget.org/packages/Elsie.Views) | Fluid/Liquid views |
| [Elsie.Validation](https://www.nuget.org/packages/Elsie.Validation) | DataAnnotations validation adapter |
| [Elsie.Testing](https://www.nuget.org/packages/Elsie.Testing) | Helpers for **your** tests (not the same as repo `tests/`) |
| [Elsie.Templates](https://www.nuget.org/packages/Elsie.Templates) | `dotnet new elsie` / `elsie-api` |

Current version: **`0.3.0-beta.2`** (prerelease; APIs may still change).

Namespaces stay `Elsie` / `Elsie.Web` (host assembly is still `Elsie.Web.dll`). Library authors who want the host-agnostic surface only should reference **`Elsie.Core`**. Former package id **`Elsie.Web`** is retired — use **`Elsie`**.

---

## Request flow

```
TCP (+ optional TLS/ALPN)
  → HTTP/1.1 or HTTP/2 parse
  → ElsieRequest
  → principal / filters (CORS preflight, …)
  → RouteTable.Lookup → before hooks → handler → after hooks → ElsieResult
  → ElsieHttpResponse.FromDispatch
  → host writes status / headers / body (or WebSocket upgrade)
```

Unmatched routes return 404 problem+json from the host.

---

## Docs

| Topic | |
|-------|--|
| [Getting started](docs/getting-started.md) | Install, smallest app, samples |
| [Modules](docs/modules.md) | Registration, DI lifetimes |
| [Routing](docs/routing.md) | Templates, constraints, precedence |
| [Results](docs/results.md) | Response factories |
| [Binding](docs/binding.md) | Route/query/JSON/form/multipart |
| [Pipelines & errors](docs/pipelines-and-errors.md) | Before/after, exception maps |
| [Auth](docs/auth.md) | Core gates + `Elsie.Auth` |
| [CORS](docs/cors.md) | `Elsie.Cors` |
| [Rate limiting](docs/rate-limiting.md) | Fixed/sliding windows |
| [Health checks](docs/health-checks.md) | Live/ready |
| [OpenAPI](docs/openapi.md) | Document + Scalar |
| [Views](docs/views.md) | Fluid/Liquid |
| [Static files](docs/static-files.md) | Built-in host static files |
| [Testing](docs/testing.md) | In-memory + loopback |
| [Hosting & AOT](docs/hosting-and-aot.md) | TLS, HTTP/2, WebSockets, limits, reverse proxy |
| [Security](docs/security.md) | Tickets, limits, forwarded headers |

Changelog: [CHANGELOG.md](CHANGELOG.md)

---

## License

MIT © [WavestormSoftware](https://github.com/WavestormSoftware) — [LICENSE](LICENSE)
