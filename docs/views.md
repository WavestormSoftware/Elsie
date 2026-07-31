# Views

Package **`Elsie.Views`** — **Fluid** (Liquid) templates. Host-agnostic (no ASP.NET types).

```csharp
.Services(s => s.AddElsieViews(o =>
{
    o.ContentRoot = Directory.GetCurrentDirectory();
    o.ReloadOnChange = true;
}))
```

```csharp
Get("/", async (ctx, ct) =>
    await ctx.ViewAsync("home", new { Title = "Elsie", Name = "world" }, cancellationToken: ct));
```

Layouts/partials follow Fluid conventions (`_Layout.liquid`, etc.). Engine seam: `IElsieViewEngine`.

## See also

- [static-files.md](static-files.md)
- [getting-started.md](getting-started.md)
