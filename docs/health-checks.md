# Health checks

Ships in **`Elsie.Core`** (via meta package `Elsie`) — host-agnostic checks + auto-registered module.

## Setup

```csharp
using Elsie.HealthChecks;

builder.Services.AddElsieHealthChecks(o =>
{
    o.PathPrefix = "/healthz"; // default

    o.AddCheck("self", () => ElsieHealthCheckResult.Healthy("up"),
        ElsieHealthCheckTags.Live);

    o.AddCheck("db", async ct =>
    {
        // probe…
        return ElsieHealthCheckResult.Healthy();
    }, ElsieHealthCheckTags.Ready);

    o.AddCheck("cache", (sp, ct) =>
    {
        var cache = sp.GetService<ICache>();
        return Task.FromResult(
            cache is null
                ? ElsieHealthCheckResult.Degraded("no cache")
                : ElsieHealthCheckResult.Healthy());
    }, ElsieHealthCheckTags.Ready);
});
```

`AddElsieHealthChecks` registers **`ElsieHealthChecksModule`** for you.

## Endpoints

| Path | Filter |
|------|--------|
| `GET /healthz` | All checks |
| `GET /healthz/live` | Tag `live` |
| `GET /healthz/ready` | Tag `ready` |

Response JSON includes aggregate status, durations, and per-check entries.  
**Unhealthy** aggregate → **503**; healthy/degraded → **200**.

Statuses: `Healthy`, `Degraded`, `Unhealthy`.

## In-memory tests

Works on `ElsieInMemoryHost` the same as ASP.NET — no host-specific types in the package.

## See also

- [modules.md](modules.md)
- [testing.md](testing.md)
