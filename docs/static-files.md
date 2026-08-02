# Static files

Elsie host (`Elsie` package / `Elsie.Web` namespaces) serves static files via **`.StaticFiles(...)`**.

## Mount under a request path

```csharp
ElsieApp.Create(args)
    .ContentRoot(Directory.GetCurrentDirectory()) // optional; resolves relative Root
    .Module<App>()
    .StaticFiles(s =>
    {
        s.Root = "wwwroot";
        s.RequestPath = "/assets";
        s.MaxAge = TimeSpan.FromHours(1);
    })
    .Run();
```

`GET /assets/app.css` → `wwwroot/app.css`.

Path traversal (`..`) is rejected. Content types are inferred from file extensions.

## Caching and ranges

Streams file content (not fully buffered). Supports:

- **ETag** + **If-None-Match** → **304**
- **If-Modified-Since** → **304**
- Single-range **Range** requests

Optional security headers on dynamic responses via `ElsieSecurityHeaders.DefaultAfter()` (static files short-circuit inside the pipeline, so register header middleware before `.StaticFiles(...)` or set cache/`MaxAge` deliberately).

## See also

- [views.md](views.md)
- [hosting-and-aot.md](hosting-and-aot.md)
- [getting-started.md](getting-started.md)
