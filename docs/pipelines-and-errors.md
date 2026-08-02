# Pipelines and errors

Elsie routes requests through a single **middleware pipeline** (`Elsie.Middleware`).
The legacy `Before` / `After` / `OnError` / `MapException` hooks are removed.

## Order

```text
app middleware pre  → module middleware pre → handler → module middleware post → app middleware post
```

Components run in registration order; pre-logic is FIFO, post-logic (after `await next`) is LIFO.
Short-circuit by setting `ElsieContext.Result` and returning without calling `next` — outer
middleware still runs its post-logic.

Application-wide:

```csharp
.Services(s => s.AddElsieMiddleware(p =>
{
    p.Use(ctx =>
    {
        ctx.Response.Headers["X-Request-Id"] = Guid.NewGuid().ToString("n");
        return null; // continue
    });
    p.Use((ctx, result) =>
    {
        ctx.Response.Headers["X-App"] = "1";
        return result;
    });
}))
```

Or directly on the app:

```csharp
var app = ElsieApp.Create()
    .Use(async (ctx, next) => { ctx.Response.Headers["X-App"] = "1"; await next(ctx); })
    .Module<MyModule>();
```

Module-level: `Use(...)` in the module ctor (runs for that module's routes only).

## Auth gates (before-style middleware)

Core header gates:

```csharp
Use(ElsieAuth.RequireApiKey("secret"));
```

Package gates (`Elsie.Auth`):

```csharp
Use(ElsieAuthGates.RequireAuthenticated());
```

## Exceptions

The terminal `ElsieExceptionHandlerMiddleware` (registered automatically as the outermost app
middleware) maps exceptions. `ElsieRequestException` becomes a problem result; anything else goes
to `ElsieOptions.ExceptionHandler` (default: safe 500 problem **without** exception detail;
`ShowExceptionDetails` opts into the HTML page). Set `ExceptionHandler = null` to rethrow to the
host pipeline.

Typed mapping is expressed as middleware:

```csharp
.Services(s => s.AddElsieMiddleware(p =>
{
    p.Use(async (ctx, next) =>
    {
        try
        {
            await next(ctx);
        }
        catch (KeyNotFoundException ex)
        {
            ctx.Result = ElsieResult.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            ctx.Result = ElsieResult.BadRequest(ex.Message);
        }
    });
}))
```

Module-scoped mapping uses the same pattern inside the module ctor.

## See also

- [middleware.md](middleware.md)
- [auth.md](auth.md)
- [results.md](results.md)
