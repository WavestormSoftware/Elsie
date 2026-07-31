# Health checks

Built into **Elsie.Core** (`Elsie.HealthChecks`).

```csharp
.Services(s =>
{
    s.AddElsieHealthChecks(o =>
    {
        o.AddCheck("self", () => ElsieHealthCheckResult.Healthy("up"), ElsieHealthCheckTags.Live);
        o.AddCheck("db", () => ElsieHealthCheckResult.Healthy("ok"), ElsieHealthCheckTags.Ready);
    });
})
```

Registers an `ElsieHealthChecksModule` with routes such as:

- `GET /healthz`
- `GET /healthz/live`
- `GET /healthz/ready`

Works on `ElsieInMemoryHost` and the real host the same way.

## See also

- [modules.md](modules.md)
