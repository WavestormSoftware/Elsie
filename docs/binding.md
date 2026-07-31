# Binding

## Route / query

```csharp
ctx.Route<int>("id");
ctx.RouteOrDefault("name");
ctx.RequireRoute("id", out Guid id, out var error);
ctx.Query<bool>("shout");
ctx.QueryOrDefault("q");
ctx.TryQuery<bool>("done", out var done);
ctx.BindQuery<SearchQuery>();
```

## JSON

```csharp
var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
if (!bind.IsSuccess) return bind.Error!; // 400 / 415 problem+json
var body = bind.Value!;
```

Honors `ElsieOptions.MaxBindBodySize`. Prefer `ctx.Json(...)` when using app `JsonSerializerOptions` / source-gen.

## Form urlencoded + multipart

```csharp
var form = await ctx.BindFormAsync<LoginForm>(ct);
```

Supports:

- `application/x-www-form-urlencoded`
- `multipart/form-data` (field values; file parts skipped for POCO binding)

```csharp
public sealed class LoginForm
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}
```

## See also

- [results.md](results.md)
- [testing.md](testing.md) — multipart client helper
