# Binding

Read route, query, form, and JSON input from **`ElsieContext`**.

## Typed accessors

```csharp
var id = ctx.Route<Guid>("id");           // default if missing/invalid
var shout = ctx.Query<bool>("shout");

if (!ctx.TryRoute<int>("id", out var n)) { /* ... */ }
if (!ctx.TryQuery<bool>("done", out var d)) { /* ... */ }

if (!ctx.RequireRoute("id", out Guid id, out var error))
    return error!; // 400 problem+json

if (!ctx.RequireQuery("page", out int page, out error))
    return error!;
```

Legacy helpers remain (`RequireRouteInt`, `TryGetQueryBool`, `RouteOrDefault`, …) and delegate to the typed path.

Conversion uses invariant culture: primitives, `Guid`, `DateTime` / `DateTimeOffset`, enums.

## Object binders

```csharp
var q = ctx.BindQuery<SearchQuery>();     // ElsieBindResult<T>
if (!q.IsSuccess) return q.Error!;

var r = ctx.BindRoute<RouteIds>();
var form = await ctx.BindFormAsync<LoginForm>(ct); // application/x-www-form-urlencoded
var json = await ctx.BindJsonAsync<CreateTodo>(ct);
```

- Reflection binders cache setters; nullable/default-aware
- Failures → **400** validation-style problem listing bad fields
- JSON: max body size `ElsieOptions.MaxBindBodySize` (default **4 MB**); path-rich errors

## FluentValidation

```csharp
// package Elsie.FluentValidation
services.AddSingleton<IValidator<CreateTodo>, CreateTodoValidator>();

var bind = await ctx.BindAndValidateJsonAsync<CreateTodo>(ct);
if (!bind.IsSuccess) return bind.Error!;
```

## Multipart

No multipart parser in core. Use the ASP.NET escape hatch:

```csharp
if (ctx.TryGetHttpContext(out var http))
{
    var form = await http.Request.ReadFormAsync(ct);
}
```

`Elsie.Testing` ships **`MultipartFormBuilder`** for tests.

## See also

- [results.md](results.md)
- [testing.md](testing.md)
