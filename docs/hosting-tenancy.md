# Hosting, tenancy and observability

Three small middlewares cover the operational edges most apps need. All three live in Core, so they
work identically over HTTP/1.1, HTTP/2, HTTP/3 and the in-memory test host.

## HTTPS redirection

```csharp
services.AddElsieHttpsRedirection(o =>
{
    o.HttpsPort = 443;            // omit the port for 443; set 8443 for custom setups
    o.Permanent = true;           // 308 (method-preserving) instead of 307
    o.ExcludedPaths.Add("/health");
});

// behind a proxy that terminates TLS
services.AddElsieHttpsRedirection(); // honours X-Forwarded-Proto: https
```

- `https` requests, and requests whose forwarded-proto header says `https`, pass through untouched.
- The redirect preserves path, path base and query string.
- Missing `Host` on a plain-HTTP request returns 400 (it cannot be redirected safely).

## Host routing / multi-tenancy

```csharp
services.AddElsieHostRouting(o =>
{
    o.AllowedHosts.Add("example.com");
    o.AllowedHosts.Add("*.example.org");   // any subdomain, not the apex
    o.RejectStatusCode = 421;              // default
});

app.MapGroup("/api").RequireHost("api.example.com").MapGet("/orders", () => "...");
```

- `ElsieHostFilterMiddleware` validates the `Host` header against the allow-list (ports honoured,
  IPv6 literals supported).
- Endpoints and groups can pin themselves to hosts with `RequireHost(...)`; endpoint metadata wins
  over the global list, so one listener can serve several tenants.
- `*.example.com` matches `api.example.com` and `a.b.example.com` but never `example.com` — use both
  entries when the apex is also valid.
- Rejections use `application/problem+json`; missing hosts are rejected only while a global
  allow-list is configured (`RequireHost` option).

## Server-Timing

```csharp
services.AddElsieServerTiming(o => o.TotalMetricName = "total");

await ElsieServerTiming.MeasureAsync(ctx, "db", () => repository.LoadAsync(id), "orders");
ElsieServerTiming.Add(ctx, "cache", 1.8, "hit");
```

The middleware measures the whole request and emits a single header such as:

```
Server-Timing: total;dur=12.4, db;dur=9.1;desc="orders", cache;dur=1.8;desc="hit"
```

Metrics are per-request and safe to call from any handler or filter. Durations use the invariant
`0.0#` format required by the W3C Server Timing spec.
