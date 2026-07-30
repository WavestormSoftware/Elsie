# Modules

An **`ElsieModule`** is a class that registers routes and optional pipeline hooks in its constructor.

## Shape

```csharp
public sealed class TodosModule : ElsieModule
{
    public TodosModule(ITodoStore store) // ctor DI — module is a singleton
    {
        Path("/api");
        Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));

        Group("/todos", () =>
        {
            Get("/", ctx => ctx.Json(store.List()));
            Post("/", async (ctx, ct) => { /* ... */ });
        });
    }
}
```

## Registration

| API | Behavior |
|-----|----------|
| `ElsieWeb.Run<TModule>(args)` | Build host, register `TModule`, `MapElsie`, run |
| `builder.AddElsie()` | Core services + optional `configure` on `ElsieOptions` |
| `services.AddElsieModule<T>()` | Explicit module (preferred in tests) |
| `ScanEntryAssembly = true` | Default: discover concrete `ElsieModule` types in the entry assembly |

```csharp
builder.AddElsie(o => o.ScanEntryAssembly = false);
builder.Services.AddElsieModule<HomeModule>();
builder.Services.AddElsieModule<TodosModule>();
```

## Lifetimes

- **Modules are singletons.** Register once; share across requests.
- Ctor-inject **singleton-safe** services only.
- Request-scoped services: `ctx.GetRequiredService<T>()` or `ctx.Services`.
- Test hosts enable `ValidateScopes` and create a scope per request.

## Composition helpers

| Helper | Purpose |
|--------|---------|
| `Path("/api")` | Prefix for subsequent routes in this module |
| `Group("/todos", () => { ... })` | Nested prefix block |
| `Before(...)` / `After(...)` | Module pipeline hooks |
| `OnError(...)` | Module-level exception mapper |
| `Get` / `Post` / `Put` / `Patch` / `Delete` / `Head` / `Options` | Verb helpers |
| `Map(method, template, handler)` | Arbitrary method |
| `MapMethods(methods, template, handler)` | Multi-method registration |

Verb helpers return a **`RouteBuilder`** for metadata (`.Named`, `.WithTags`, `.Accepts`, `.Produces`, …).

## See also

- [routing.md](routing.md)
- [pipelines-and-errors.md](pipelines-and-errors.md)
- [testing.md](testing.md)
