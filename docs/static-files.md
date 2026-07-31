# Static files

Elsie host (`Elsie` package / `Elsie.Web` namespaces) serves static files via **`.StaticFiles(...)`**.

## Mount under a request path

```csharp
ElsieApp.Create(args)
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

## See also

- [views.md](views.md)
- [hosting-and-aot.md](hosting-and-aot.md)
- [getting-started.md](getting-started.md)


Streams file content (not fully buffered). Supports **ETag**, **If-None-Match**, **If-Modified-Since**, and single **Range** requests.
