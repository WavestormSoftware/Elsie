# Rate limiting

Built into **Elsie.Core** (`Elsie.RateLimiting`) as **before-hooks**.

```csharp
Before(ElsieRateLimit.FixedWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));
// or sliding window helpers
```

Partitioning defaults to remote IP when available (`ElsieRequest.RemoteIp`).

Returns **429** problem+json when exceeded.

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
