# Routing

## Templates

`{name}`, optional `{name?}`, default `{name=5}`, constraints `{id:int}`, catch-all `{*path}` (last segment only).

**Built-in constraints:** `int`, `long`, `guid`, `bool`, `alpha`, `datetime`, `decimal`, `double`, `minlength(n)`, `maxlength(n)`, `length(n|min,max)`, `min(n)`, `max(n)`, `range(a,b)`, `regex(...)`.

## Matching

Per segment: **static > constrained > param > catch-all**.

Startup fails on unknown constraints, duplicate param names, bad catch-all placement, ambiguous routes, and duplicate route names.

Wrong verb on a known path → **405** + `Allow` + problem+json.

HEAD maps to GET when `ElsieOptions.ImplicitHead` is on (default).

```csharp
.Configure(o =>
{
    o.ImplicitHead = true;
})
```

No path match → host returns 404 problem+json (terminal host).

## Link generation

```csharp
Get("/todos/{id:guid}", …).Named("getTodo");

var relative = ctx.UrlFor("getTodo", new { id });
// /todos/…
var absolute = ctx.UrlFor("getTodo", new { id }, absolute: true);
// https://host/todos/…  (uses request scheme/host + PathBase)
```

## See also

- [modules.md](modules.md)
- [openapi.md](openapi.md)
