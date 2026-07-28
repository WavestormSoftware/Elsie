# Elsie

Lightweight, low-ceremony HTTP modules for **ASP.NET Core** (.NET 8 / .NET 9).

Elsie is inspired by the developer experience of Sinatra-style frameworks (small modules, explicit routes, minimal wiring). It is an **original** WavestormSoftware project under the MIT license — not a fork of any other framework.

## Quick start

```csharp
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
# Advanced multi-module API sample:
dotnet run --project samples/Elsie.Sample.Api
```

## Module registration

| Approach | How | Notes |
|----------|-----|--------|
| **Explicit (recommended)** | `services.AddElsieModule<TModule>()` | Clear, test-friendly |
| Entry-assembly scan | `AddElsie()` default `ScanEntryAssembly = true` | Concrete `ElsieModule` types in entry assembly |
| Extra assemblies | `AddElsie(o => o.AssembliesToScan.Add(asm))` | Combined with optional entry scan |
| Disable scan | `AddElsie(o => o.ScanEntryAssembly = false)` | **Default in `ElsieTestHost`** |

Modules are **singletons**. Ctor-inject singleton-safe services; use `ctx.GetRequiredService<T>()` for request scope.

## Routing

```csharp
public sealed class TodosModule : ElsieModule
{
    public TodosModule(ITodoStore store)
    {
        Path("/api");                     // module base path
        Group("/todos", () =>
        {
            Get("/", ctx => ctx.Json(store.List()));
            Get("/{id:guid}", ctx =>
            {
                if (!ctx.RequireRouteGuid("id", out var id, out var err))
                    return err!;
                return ctx.Json(store.Get(id));
            });
            Post("/", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
                if (!bind.IsSuccess) return bind.Error!;
                return ctx.Json(store.Add(bind.Value!), 201);
            });
        });
    }
}
```

- Params: `{name}`, constraints `{id:int|long|guid|bool}`, catch-all `{*path}` (final segment)
- Duplicate method+template → startup throw
- Wrong verb on known path → `405` + `Allow`
- Concrete routes win over catch-alls

## Results & errors

```csharp
ElsieResult.Text("ok");
ElsieResult.Json(dto);
ElsieResult.NoContent();
ElsieResult.Redirect("/elsewhere");
ElsieResult.BadRequest("Title required");   // problem+json
ElsieResult.NotFound();
ElsieResult.Unauthorized("missing key");

// Optional app-level exception mapping
builder.Services.AddElsie(o =>
{
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ex is KeyNotFoundException
            ? ElsieResult.NotFound(ex.Message)
            : ElsieResult.Problem(500, "Server Error"));
});
```

## Pipelines

```csharp
// Module
Before(ctx => ctx.Request.Headers.ContainsKey("X-Api-Key") ? null : ElsieResult.Unauthorized());
After((ctx, result) => ctx.Response.Headers["X-Module"] = "1");

// Application (composes)
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddBefore(...);
    p.AddAfter(...);
});
```

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Modules, routing, pipelines, context, results |
| `Elsie.AspNetCore` | DI + `MapElsie` (terminal 404) / `UseElsie` (pass-through) |
| `Elsie.Testing` | `ElsieTestHost` + response assert helpers |

## Testing

```csharp
await using var host = ElsieTestHost.Create(s =>
{
    s.AddSingleton<ITodoStore, InMemoryTodoStore>();
    s.AddElsieModule<TodosModule>();
});

var response = await host.GetAsync("/api/todos");
response.AssertStatus(200);
var items = await response.AssertJsonAsync<Todo[]>();
```

## Samples

| Sample | Level | What it shows |
|--------|-------|----------------|
| `samples/Elsie.Sample.Hello` | Easy | Minimal module + `MapElsie` |
| `samples/Elsie.Sample.Api` | Advanced | Path/Group, DI store, JSON bind, API key `Before`, exception handler, catch-all |

API sample write routes need header `X-Api-Key: dev-secret`.

## Build

```bash
dotnet restore Elsie.sln
dotnet build Elsie.sln -c Release
dotnet test Elsie.sln -c Release
dotnet pack Elsie.sln -c Release -o artifacts/nuget
```

## Docs

- [`AGENTS.md`](AGENTS.md) — contributor / coding-agent notes

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).
