# Routing

Routes are registered on modules and compiled into a **`RouteTable`** at startup. Matching is deterministic.

## Templates

| Form | Example |
|------|---------|
| Static | `/health` |
| Parameter | `/users/{id}` |
| Constrained | `/users/{id:guid}` |
| Optional | `/page/{num?}` |
| Default | `/page/{num=1}` |
| Catch-all | `/docs/{*path}` (must be final segment) |

`Path` / `Group` nest prefixes: `Path("/api")` + `Group("/todos")` + `Get("/{id}")` → `/api/todos/{id}`.

## Constraints

Built-in (case-insensitive names):

`int`, `long`, `guid`, `bool`, `alpha`, `datetime`, `decimal`, `double`,  
`minlength(n)`, `maxlength(n)`, `length(n)`, `length(min,max)`,  
`min(n)`, `max(n)`, `range(a,b)`, `regex(...)`

Custom:

```csharp
builder.AddElsie(o =>
{
    o.RouteConstraints["slug"] = v => v.Length > 0 && v.All(c => char.IsLetterOrDigit(c) || c == '-');
});
// /posts/{name:slug}
```

Unknown constraint names **throw at startup**.

## Precedence

Per path segment, rank is:

1. **static** (`users`)
2. **constrained param** (`{id:int}`)
3. **param** (`{id}`)
4. **catch-all** (`{*path}`)

Routes are ordered by the lexicographic comparison of these rank vectors.  
Equal method + equal ranks + equal statics → **ambiguity startup throw** (e.g. `/users/{id}` vs `/users/{name}`).

Registration order does **not** decide which route wins when ranks differ.

## Metadata & names

```csharp
Get("/todos/{id:guid}", handler)
    .Named("getTodo")
    .WithSummary("Get one todo")
    .WithDescription("...")
    .WithTags("todos")
    .Produces<Todo>()
    .Produces<ProblemDto>(404)
    .WithSecurity("ApiKey");
```

- Duplicate **names** → startup throw
- Names feed **`ctx.UrlFor("getTodo", new { id })`**
- Metadata feeds OpenAPI ([openapi.md](openapi.md))

## Method matching

- Exact method match → handler
- Path matches another method only → **405** + `Allow` + problem+json body
- No path match → not handled (`MapElsie` falls through unless `terminal: true`)
- **HEAD → GET** fallback when `ElsieOptions.ImplicitHead` is true (default)

## See also

- [modules.md](modules.md)
- [binding.md](binding.md)
- [openapi.md](openapi.md)
