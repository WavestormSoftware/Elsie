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

Body reads go through **`Request.BufferBodyAsync`** once; later bind/antiforgery calls reuse the buffer (no double-read of the raw stream).

## Form urlencoded + multipart fields

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

## Multipart files

```csharp
var form = await ctx.ReadFormAsync(ct);
if (!form.IsSuccess) return form.Error!;

var title = form.Value!.Get("title");           // fields
var file = form.Value.GetFile("file");          // ElsieFormFile?
// or:
var files = await ctx.ReadFormFilesAsync(ct);   // all files
await using var stream = file!.OpenReadStream();
```

`ElsieFormFile` exposes `Name`, `FileName`, `ContentType`, `Length`, and stream access. Cap body size with `MaxRequestBodyBytes` / `MaxBindBodySize`.

## Validation (optional package)

```bash
dotnet add package Elsie.Validation
```

```csharp
s.AddElsieDataAnnotationsValidation();

var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
if (!bind.IsSuccess) return bind.Error!;
if (ctx.ValidateWithDataAnnotations(bind.Value!) is { } invalid)
    return invalid; // 400 validation problem
```

## See also

- [results.md](results.md)
- [auth.md](auth.md) — antiforgery shares the buffered body with form bind
- [testing.md](testing.md) — multipart client helper
