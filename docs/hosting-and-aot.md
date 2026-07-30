# Hosting and AOT

## Default host

**`Elsie.AspNetCore`** on Kestrel / ASP.NET Core:

```csharp
ElsieWeb.Run<App>(args);
// or
ElsieWeb.Run(args); // scan-based
// or builder.AddElsie() + MapElsie()
```

| API | Notes |
|-----|--------|
| `ElsieWeb.Run` / `RunAsync` | Generic module or scan |
| `ElsieWeb.CreateApp` | Build `WebApplication` without running |
| `builder.AddElsie(configure, quietConsole: true)` | DI + log filter |
| `app.MapElsie(terminal: false)` | Non-terminal default — unmatched falls through |
| `app.MapElsie(terminal: true)` | Unmatched → 404 problem+json |
| `app.UseElsie(terminal)` | Middleware form |
| `app.MapElsieOpenApi` | OpenAPI JSON (+ optional UI) |
| `app.MapElsieStaticFiles` | Static files |

## Pipeline order (typical full app)

```csharp
app.UseElsieCors();
app.UseElsieAuth();
app.MapElsieStaticFiles("/assets", wwwroot);
app.MapElsieOpenApi(o => o.UiPath = "/scalar");
app.MapElsie();
```

## Escape hatch

```csharp
using Elsie.AspNetCore;

if (ctx.TryGetHttpContext(out var http))
{
    // full ASP.NET surface
}
```

## JSON source generation

Elsie uses **`System.Text.Json`**. For trimmed / AOT-friendly serialization:

```csharp
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(CreateTodo))]
internal partial class AppJsonContext : JsonSerializerContext;

builder.AddElsie(o =>
{
    o.JsonSerializerOptions = new JsonSerializerOptions
    {
        TypeInfoResolver = AppJsonContext.Default
    };
});

// Handlers should prefer ctx.Json (app options) over ElsieResult.Json
// when relying on source-gen resolvers.
return ctx.Json(todo);
```

Static `ElsieResult.Json` still uses **`ElsieJson.DefaultOptions`** unless you pass `options:` explicitly.

## Trimming / AOT guidance

| Area | Guidance |
|------|----------|
| Core routing | Expression trees / reflection at **startup** for route table; request path is match + dispatch |
| OpenAPI schemas | Reflection over DTO shapes at **document build** — keep document generation out of native AOT critical path or pregenerate |
| Views (Fluid) | Template parse/runtime — treat as non-AOT-first unless you validate your Fluid version's trim surface |
| BindQuery / BindRoute / BindJson | Reflection binders — prefer explicit accessors or source-gen DTOs for strict AOT |
| Modules | Concrete types registered in DI — avoid relying on entry-assembly scan under trim; use **`AddElsieModule<T>()`** |

Elsie does **not** currently ship a full native-AOT guarantee. Prefer:

1. Explicit module registration  
2. `ctx.Json` + `JsonSerializerContext`  
3. Avoid OpenAPI reflection in the trimmed published app if you hit linker warnings (serve a prebuilt document instead)

## Multi-TFM

Libraries target **`net8.0;net10.0`**. Samples commonly use `net8.0` for simplicity.

## See also

- [getting-started.md](getting-started.md)
- [openapi.md](openapi.md)
- [testing.md](testing.md)
