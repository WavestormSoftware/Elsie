# Elsie

HTTP module framework for **.NET 10** (net10.0-only). Define routes in small modules, return results, run on Elsie's own lightweight host.

**Why Elsie?** Tiny Sinatra-style modules, one fluent host (`ElsieApp`), no ASP.NET tax. Hold the whole request path in your head — modules → middleware → results → server — without `WebApplication` ceremony or a shared-framework dependency.

> **No Kestrel / no ASP.NET — ever.** Elsie is a standalone HTTP framework with its own transport stack (`System.Net.Sockets`, `System.Net.Security`, `System.Net.Quic`). There is no ASP.NET adapter, and no `Microsoft.AspNetCore.*` package or type is used anywhere in Core or the host. Same constraint as [AGENTS.md](AGENTS.md).

```bash
dotnet add package Elsie          # 0.4.0-beta — host + Elsie.Core
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

Guides: [docs/](docs/) · [Samples](samples/README.md) · NuGet: [Elsie](https://www.nuget.org/packages/Elsie)

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
| `.Listen` / `.ContentRoot` / `.Server` | Bind, roots, limits |
| `.Logging` / `.Compression` | Observability + gzip/br |
| `.OpenApi(...)` / `.StaticFiles(...)` | Document UI + static files |

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
        Use(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));

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
var files = await ctx.ReadFormFilesAsync(ct);   // multipart ElsieFormFile
var query = ctx.BindQuery<SearchQuery>();
ctx.Route<int>("id");
ctx.Query<bool>("shout");
ctx.RequireRoute("id", out Guid id, out var error);

ctx.Request.Method / Path / Scheme / Host / PathBase / Protocol / RemoteIp
ctx.Request.GetHeader / GetQuery / GetCookie
ctx.UrlFor("getTodo", new { id })
ctx.UrlFor("getTodo", new { id }, absolute: true)
ctx.Response.Headers["X-App"] = "1";
ctx.Response.SetCookie("sid", value, new ElsieCookieOptions { HttpOnly = true, SameSite = ElsieSameSite.Lax });
ctx.GetRequiredService<IMyService>();
```

## Middleware

Requests flow through a **single middleware pipeline** (Core `Elsie.Middleware`). App-wide middleware is added with `app.Use(...)` / `Use<T>()` (DI); module-scoped middleware with `Module.Use(...)` (runs only for the module's routes). Ordering is FIFO pre / LIFO post; a step short-circuits by setting `ctx.Result` (or by not calling `next`).

```csharp
// app-wide: runs for every request
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Request-Id"] = Guid.NewGuid().ToString("n");
    await next(ctx);                 // post-processing runs after next() returns
    ctx.Response.Headers["X-Status"] = ctx.Result?.StatusCode.ToString();
});

// or as a class (resolved from DI, per-request scope)
app.Use<RequestLoggingMiddleware>();

// module-scoped: only for routes in this module
public sealed class AdminModule : ElsieModule
{
    public AdminModule()
    {
        Path("/admin");
        Use(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));
    }
}
```

**Exception handling** — `ElsieOptions.ExceptionHandler` is the terminal middleware: when an unhandled exception bubbles past every middleware, it produces the response.

```csharp
.Configure(o =>
{
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error"));
})
```

Built-ins are first-class middleware: auth gates (`ElsieAuth.*`), rate limiting (`ElsieRateLimit.*`), CORS, security headers, antiforgery, health checks, and static files. Legacy `Before` / `After` / `OnError` / `MapException` hooks are removed.

---

## Optional packages

### Auth — `Elsie.Auth`

```csharp
.Services(s =>
{
    s.AddElsieAuth(o =>
    {
        o.Cookie = new ElsieCookieAuthOptions
        {
            CookieName = "elsie-auth",
            HttpOnly = true,
            SameSite = ElsieSameSite.Lax   // core enum
        };
        o.Cookie.TicketKeyFromString("change-me-at-least-16");
    });
    s.AddElsieAntiforgery(); // double-submit cookie
})

Use(ElsieAuthGates.RequireAuthenticated());
Use(ElsieAuthGates.RequireRole("admin"));
Use(ElsieAntiforgeryService.RequireAntiforgery()); // header X-CSRF-TOKEN or form field
await ctx.SignInCookieAsync("ada", roles: ["user"]);
var user = ctx.GetUser();
var csrf = ctx.GetAntiforgeryToken(); // Base64Url
```

### CORS — `Elsie.Cors`

Preflight and the ACAO response header are handled by middleware.

```csharp
.Services(s => s.AddElsieCors(o => o.AddDefaultPolicy(p => p
    .AllowOrigins("http://localhost:5173")
    .AllowMethods("GET", "POST", "OPTIONS")
    .AllowHeaders("Content-Type", "X-CSRF-TOKEN")
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
Use(ElsieRateLimit.FixedWindow(100, TimeSpan.FromMinutes(1)));
Use(ElsieRateLimit.SlidingWindow(30, TimeSpan.FromSeconds(10),
    partitionKey: ctx => ctx.Request.GetHeader("X-Api-Key") ?? "anon"));
Use(ElsieRateLimit.TokenBucket(capacity: 20, tokensPerSecond: 5));
// 429 problem+json + Retry-After; default partition = RemoteIp only (not XFF)
// Behind a trusted proxy: partitionKey: ElsieRateLimit.ForwardedPartitionKey
```

### Validation — `Elsie.Validation`

```csharp
.Services(s => s.AddElsieDataAnnotationsValidation())
// after bind:
if (ctx.ValidateWithDataAnnotations(body) is { } invalid) return invalid;
```

### OpenAPI (host)

Route metadata (`.Named` / `.Accepts` / `.Produces` / `.WithSecurity` / `.WithExample` / …) builds the document.

```csharp
.OpenApi(o =>
{
    o.Info.Title = "My API";
    o.UiPath = "/scalar";              // Scalar CDN by default
    // o.UseScalarCdn = false;         // minimal embedded UI
    // o.PrebuiltDocumentPath = "…";  // skip reflection at runtime
})
```

### Views — `Elsie.Views` (Fluid / Liquid)

```csharp
.Services(s => s.AddElsieViews(o => o.ContentRoot = contentRoot))
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
// streams; ETag / If-Modified-Since / Range
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
| [Elsie.Extensions.RateLimiting.Redis](https://www.nuget.org/packages/Elsie.Extensions.RateLimiting.Redis) | Distributed rate limiting over Redis (Lua, fail-open) |
| [Elsie.Extensions.Auth.Redis](https://www.nuget.org/packages/Elsie.Extensions.Auth.Redis) | Server-side cookie sessions over Redis (`RedisSessionStore`) |
| [Elsie.Grpc](https://www.nuget.org/packages/Elsie.Grpc) | Native gRPC server over Elsie's HTTP/2 + HTTP/3 (ServiceBinderBase, reflection-lite) |

Current version: **`0.4.0-beta`** (prerelease; APIs may still change).

Namespaces stay `Elsie` / `Elsie.Web` (host assembly is still `Elsie.Web.dll`). Library authors who want the host-agnostic surface only should reference **`Elsie.Core`**. Former package id **`Elsie.Web`** is retired — use **`Elsie`**.

---

## Request flow

```
TCP / UDP(+TLS, ALPN h1/h2/h3)
  → HTTP/1.1, HTTP/2, or HTTP/3 (QUIC) parse
  → ElsieRequest
  → middleware pipeline (auth gates, rate limiting, CORS, security headers, …)
  → RouteTable.Lookup → handler → ElsieResult
  → ElsieHttpResponse.FromDispatch
  → host writes status / headers / body (or WebSocket / gRPC upgrade)
```

Unmatched routes return 404 problem+json from the host. HTTP/3 runs on its own QUIC listener (`ElsieListenOptions.EnableHttp3`) with full QPACK; WebSocket works over HTTP/1.1 (Upgrade) and HTTP/3 (RFC 9220 extended CONNECT); gRPC (package `Elsie.Grpc`) runs over HTTP/2 and HTTP/3.

---

## Docs

| Topic | |
|-------|--|
| [Getting started](docs/getting-started.md) | Install, smallest app, samples |
| [Modules](docs/modules.md) | Registration, DI lifetimes |
| [Routing](docs/routing.md) | Templates, constraints, `UrlFor` |
| [Results](docs/results.md) | Response factories |
| [Binding](docs/binding.md) | Route/query/JSON/form/files + validation |
| [Pipelines & errors](docs/pipelines-and-errors.md) | Middleware model, exception handler |
| [Auth](docs/auth.md) | Gates, cookies, JWT, CSRF, OIDC helpers |
| [CORS](docs/cors.md) | `Elsie.Cors` |
| [Rate limiting](docs/rate-limiting.md) | Fixed / sliding / token bucket |
| [Health checks](docs/health-checks.md) | Live/ready |
| [OpenAPI](docs/openapi.md) | Document, Scalar, prebuilt JSON |
| [Views](docs/views.md) | Fluid/Liquid |
| [Static files](docs/static-files.md) | Stream, ETag, Range |
| [Testing](docs/testing.md) | In-memory + loopback |
| [Hosting & AOT](docs/hosting-and-aot.md) | TLS, HTTP/2, HTTP/3, WebSockets, limits, reverse proxy |
| [HTTP/3](docs/http3.md) | QUIC + QPACK, dynamic tables, WebSocket over h3 |
| [gRPC](docs/grpc.md) | Native gRPC over h2/h3, reflection |
| [Security](docs/security.md) | Tickets, CSRF, XFF, CI scan |
| [Production checklist](docs/production-checklist.md) | Deploy gates |
| [Lifecycle](docs/lifecycle.md) | Socket → response path |
| [Architecture](docs/architecture.md) | Package/host layout |
| [Anti-patterns](docs/anti-patterns.md) | Common pitfalls |
| [Minimal APIs migration](docs/minimal-apis-migration.md) | Cheat sheet |

### Production sketch

```csharp
ElsieApp.Create(args)
    .Logging(loggerFactory)
    .Compression()
    .Server(o =>
    {
        o.MaxRequestBodyBytes = 1_000_000;
        o.MaxConcurrentConnections = 10_000;
    })
    .Services(s =>
    {
        s.AddElsieAuth(/* TicketKey from secret store */);
        s.AddElsieAntiforgery();
        s.AddElsieDataAnnotationsValidation();
        s.AddElsieMiddleware(p => p.Use(ElsieSecurityHeaders.DefaultAfter()));
    })
    .Module<AppModule>()
    .Run();
```

Full list: [production-checklist.md](docs/production-checklist.md).

Changelog: [CHANGELOG.md](CHANGELOG.md)

---

## License

MIT © [WavestormSoftware](https://github.com/WavestormSoftware) — [LICENSE](LICENSE)
