# Static files

Host helper on **`Elsie.Web`** (not a separate package).

## Map

```csharp
app.MapElsieStaticFiles(
    requestPath: "/assets",
    contentRoot: Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
```

Optional configure:

```csharp
app.MapElsieStaticFiles("/assets", root, o =>
{
    o.DefaultFileName = "index.html";
    o.ServeDefaultFile = true;
    o.ContentTypes[".webmanifest"] = "application/manifest+json";
});
```

## Behavior

| Feature | Support |
|---------|---------|
| GET / HEAD | Yes |
| Content-Type map | ~30 common extensions |
| Weak ETag + `Last-Modified` | Yes |
| Conditional **304** | `If-None-Match` / `If-Modified-Since` |
| Default document | Optional (`index.html`) |
| Path traversal | Rejected (404 problem+json under mount) |
| Missing file | **Fall through** to next middleware / Elsie |
| Range requests | **No** (explicit non-goal) |

Place **before** or **after** `MapElsie` depending on whether you want static files to take priority for overlapping paths. Typical: static middleware first for `/assets`, then `MapElsie`.

## See also

- [views.md](views.md)
- [getting-started.md](getting-started.md)
