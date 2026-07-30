# Pipelines and errors

## Order

```
app.Before → module.Before → handler → module.After → app.After
```

- Before-hooks may **short-circuit** by returning an `ElsieResult` (non-null).
- After-hooks still run after short-circuits and after error-mapped results.
- After-hooks may **transform** the result (`Task<ElsieResult>` / `Func<…, ElsieResult>`).

## Registration

**Module:**

```csharp
Before(ElsieAuth.RequireApiKey("secret"));
After((ctx, result) =>
{
    ctx.Response.Headers["X-Status"] = result.StatusCode.ToString();
    return result;
});
```

**Application:**

```csharp
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddBefore((ctx, ct) => Task.FromResult<ElsieResult?>(null));
    p.AddAfter((ctx, result, ct) => Task.FromResult(result));
});
```

## Core auth-style gates

In package **`Elsie.Core`** (no ASP.NET auth middleware):

| Gate | Behavior |
|------|----------|
| `ElsieAuth.RequireApiKey(key, headerName?, onlyMutatingMethods?)` | Default: **all methods**; opt into mutating-only |
| `ElsieAuth.RequireHeader(name, value?)` | Presence / exact value |
| `ElsieAuth.RequireBearer(predicate)` | `Authorization: Bearer …` |
| `ElsieAuth.RequireCookie(name, predicate?)` | Cookie presence / value |

Full principal auth: [auth.md](auth.md) (`Elsie.Auth`).

Rate limits: [rate-limiting.md](rate-limiting.md).

## Exception chain

1. **`ElsieOptions.MapException<TException>(…)`** — ordered, assignable match  
2. **Module `OnError`**  
3. **`ElsieOptions.ExceptionHandler`**  
4. **Rethrow** to the host

```csharp
builder.AddElsie(o =>
{
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.ExceptionHandler = (ctx, ex, ct) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error", ex.Message));
});

// module:
OnError((ctx, ex) => ElsieResult.BadRequest(ex.Message));
```

After-hook exceptions re-enter the same error chain.

## See also

- [auth.md](auth.md)
- [rate-limiting.md](rate-limiting.md)
- [cors.md](cors.md)
