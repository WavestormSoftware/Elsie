# Views

Package **`Elsie.Views`** — **Fluid** (Liquid) templates. Host-agnostic (no ASP.NET types).

```csharp
var contentRoot = ResolveContentRoot(); // project dir when launched from bin/

ElsieApp.Create(args)
    .ContentRoot(contentRoot)
    .Services(s => s.AddElsieViews(o =>
    {
        o.ContentRoot = contentRoot;
        o.ReloadOnChange = true; // dev
    }))
    // ...
```

```csharp
Get("/", async (ctx, ct) =>
    await ctx.ViewAsync("home", new { Title = "Elsie", Name = "world" }, cancellationToken: ct));
```

Layouts/partials follow Fluid conventions (`_Layout.liquid`, etc.). Engine seam: `IElsieViewEngine`.

**Content root:** `dotnet run` from the project directory works with `Directory.GetCurrentDirectory()`. When the process cwd is `bin/...`, resolve the project directory (see [Dashboard](../samples/Elsie.Sample.Dashboard) / [Views](../samples/Elsie.Sample.Views) samples).

## See also

- [static-files.md](static-files.md)
- [getting-started.md](getting-started.md)
