# Modules

Routes live on **`ElsieModule`** subclasses (singletons).

## Registration

```csharp
// One module
ElsieApp.Run<App>(args);

// Explicit modules
ElsieApp.Create(args)
    .Configure(o => o.ScanEntryAssembly = false)
    .Module<HomeModule>()
    .Module<ApiModule>()
    .Run();

// Or DI
.Services(s => s.AddElsieModule<ApiModule>());
```

| API | Use |
|-----|-----|
| `ElsieApp.Run<TModule>(args)` | Build host, register `TModule`, run |
| `.Module<T>()` / `AddElsieModule<T>()` | Explicit registration (prefer in tests) |
| `AddElsie()` / `.Configure(...)` | Core services + `ElsieOptions` |
| `ScanEntryAssembly` | Default **true** for apps; tests set **false** |

## Defining routes

```csharp
public sealed class TodosModule : ElsieModule
{
    public TodosModule(ITodoStore store)
    {
        Path("/api");
        Use(ElsieAuth.RequireApiKey("secret", onlyMutatingMethods: true));

        Group("/todos", () =>
        {
            Get("/", ctx => ctx.Json(store.List()));
            Get("/{id:guid}", ctx => { /* … */ }).Named("getTodo");
            Post("/", async (ctx, ct) => { /* … */ });
        });
    }
}
```

- `Path` / `Group` compose prefixes  
- `Use(...)` adds module-scoped middleware (gates, transforms, full middleware)  
- Handlers: sync `Func<ElsieContext, ElsieResult>` or async with `CancellationToken`  
- Ctor DI for singletons; `ctx.GetRequiredService<T>()` for scoped  

## See also

- [routing.md](routing.md)
- [pipelines-and-errors.md](pipelines-and-errors.md)
- [getting-started.md](getting-started.md)
