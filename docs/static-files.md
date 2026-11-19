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

## Precompressed assets

With `EnablePrecompressedFiles` (default on), a sibling `app.js.br` / `app.js.gz` is served instead
of `app.js` when the client's `Accept-Encoding` allows it and the variant is not older than the
original:

- Brotli wins over gzip; both add `Content-Encoding` and `Vary: Accept-Encoding`.
- ETags include the encoding, so each variant revalidates independently.
- Range requests always receive identity bytes (ranges are not valid over encoded content).

Precompress at build time (`brotli app.js`, `gzip -9 app.js`) — Elsie never compresses on the fly
for static files (dynamic responses use the response-compression middleware instead).

## Immutable fingerprinted assets

When `ImmutableFingerprintedAssets` is on (default), names with a content hash segment
(`app.4f3a9c1d.js`, `styles.9c1d4f3a.css`) are served with
`Cache-Control: public, max-age=31536000, immutable`, overriding `MaxAge`. `min.js`-style names
without a hash keep the normal cache policy.

## SPA fallback

```csharp
.StaticFiles(s =>
{
    s.Root = "wwwroot";
    s.SpaFallback = "index.html";
})
```

Extension-less `GET`/`HEAD` requests that prefer HTML (`Accept: text/html` or `*/*`) and do not
match a file receive the fallback document with `Cache-Control: no-cache`. Missing assets (paths
with an extension, or requests sending `Accept: application/json`) still 404, so the fallback does
not hide broken bundles or API calls.

## See also

- [views.md](views.md)
- [hosting-and-aot.md](hosting-and-aot.md)
- [getting-started.md](getting-started.md)
