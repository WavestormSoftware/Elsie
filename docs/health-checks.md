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

Registers an `ElsieHealthCheckMiddleware` (into the app middleware pipeline) serving:

- `GET /healthz`
- `GET /healthz/live`
- `GET /healthz/ready`

Probe paths short-circuit in the middleware; anything else continues down the pipeline.
Works on `ElsieInMemoryHost` and the real host the same way.

## See also

- [modules.md](modules.md)
