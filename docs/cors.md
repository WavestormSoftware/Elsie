# CORS

Package **`Elsie.Cors`**. Preflight is handled by an **`IElsieRequestFilter`** before dispatch; actual responses get ACAO headers via an **after-hook**.

## Setup

```csharp
ElsieApp.Create(args)
    .Module<ApiModule>()
    .Services(s =>
    {
        s.AddElsieCors(o =>
        {
            o.AddDefaultPolicy(p => p
                .AllowOrigins("http://localhost:5173")
                .AllowMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .AllowHeaders("Content-Type", "Authorization")
                .AllowCredentials()
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
        });
    })
    .Run();
```

Fluent: `.Cors(o => …)` via `ElsieCorsAppExtensions`.

## Per-route policy

```csharp
Get("/admin", () => ElsieResult.Text("x")).WithCors("tight");
// options.AddPolicy("tight", p => p.AllowOrigin("https://admin.example"));
```

## Behavior

| Request | Behavior |
|---------|----------|
| Preflight `OPTIONS` + `Origin` + `Access-Control-Request-Method` | Filter short-circuits with 204 + CORS headers (or empty 204 if denied) |
| Actual request with `Origin` | After-hook adds ACAO on the matched route’s policy (or default) |

## See also

- [hosting-and-aot.md](hosting-and-aot.md)
- [modules.md](modules.md)
