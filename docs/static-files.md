# Static files

Elsie does not ship custom static-file middleware. Use ASP.NET Core **`UseStaticFiles`** (and optionally **`UseDefaultFiles`**) on the host.

## Default `wwwroot`

```csharp
var app = builder.Build();

app.UseDefaultFiles(); // optional
app.UseStaticFiles();  // serves wwwroot at /

app.MapElsie();
app.Run();
```

Place static middleware **before** `MapElsie` so static paths win on overlap (normal ASP.NET pattern).

## Mount under a request path

```csharp
using Microsoft.Extensions.FileProviders;

app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/assets",
    FileProvider = new PhysicalFileProvider(
        Path.Combine(app.Environment.ContentRootPath, "wwwroot"))
});

app.MapElsie();
```

`GET /assets/app.css` → `wwwroot/app.css`.

## See also

- [views.md](views.md)
- [hosting-and-aot.md](hosting-and-aot.md)
- [getting-started.md](getting-started.md)
