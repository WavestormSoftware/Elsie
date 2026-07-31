# Pipelines and errors

## Order

```text
app Before → module Before → handler → module After → app After
```

Short-circuit before still runs afters. After hooks may replace the result.

```csharp
.Services(s => s.ConfigureElsiePipelines(p =>
{
    p.AddBefore((ctx, ct) =>
    {
        ctx.Response.Headers["X-Request-Id"] = Guid.NewGuid().ToString("n");
        return Task.FromResult<ElsieResult?>(null);
    });
    p.AddAfter((ctx, result) =>
    {
        ctx.Response.Headers["X-App"] = "1";
        return result;
    });
}))
```

Module-level: `Before(...)` / `After(...)` in the module ctor.

## Auth gates (before-hooks)

Core header gates:

```csharp
Before(ElsieAuth.RequireApiKey("secret"));
```

Package gates (`Elsie.Auth`):

```csharp
Before(ElsieAuthGates.RequireAuthenticated());
```

## Exceptions

```csharp
.Configure(o =>
{
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.MapException<ArgumentException>((_, ex) => ElsieResult.BadRequest(ex.Message));
    o.ExceptionHandler = (ctx, ex, _) =>
        Task.FromResult(ElsieResult.Problem(500, "Internal Server Error"));
});
```

First match: `MapException` → module `OnError` → `ExceptionHandler` → rethrow.  
Default handler returns 500 problem+json **without** exception detail.

## See also

- [auth.md](auth.md)
- [results.md](results.md)
