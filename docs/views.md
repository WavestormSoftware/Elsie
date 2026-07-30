# Views

Package **`Elsie.Views`** — **Fluid** (Liquid) templates. No `HttpContext` in this package.

## Setup

```csharp
using Elsie.Views;

builder.Services.AddElsieViews(o =>
{
    o.ContentRoot = builder.Environment.ContentRootPath;
    o.RootPath = "Views";          // default
    o.Extension = ".liquid";       // default
    o.ReloadOnChange = true;       // default; set false in production
});
```

## Render

```csharp
Get("/", async (ctx, ct) =>
    await ctx.ViewAsync("home", new { Title = "Elsie", Name = "Ada" }, cancellationToken: ct));
```

Returns an **`Html`** `ElsieResult`.

## Templates

`Views/home.liquid`:

```liquid
{% layout '_Layout.liquid' %}
<h1>Hello {{ Name }}!</h1>
<p class="muted">{{ Request.Path }}</p>
```

`Views/_Layout.liquid`:

```liquid
<!DOCTYPE html>
<html>
<head><title>{{ Title }}</title></head>
<body>
  <main>{% renderbody %}</main>
</body>
</html>
```

- Output is HTML-encoded by default (Fluid)
- Ambient **`Request`** path/query exposed via view context
- Layouts / partials follow Liquid + Fluid conventions

## Seam

```csharp
public interface IElsieViewEngine
{
    Task<string> RenderAsync(string viewName, object? model, ElsieViewAmbient ambient, CancellationToken ct);
}
```

Default implementation: **`FluidElsieViewEngine`**. Cache key = path + mtime when `ReloadOnChange` is true.

## Sample

`samples/Elsie.Sample.Views`, `samples/Elsie.Sample.Full`.

## See also

- [results.md](results.md)
- [static-files.md](static-files.md)
