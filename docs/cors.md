# CORS

Package **`Elsie.Cors`**. CORS is a single middleware (`ElsieCorsMiddleware`): it handles OPTIONS
preflight (short-circuit 204 + allow headers) and applies `Access-Control-Allow-*` headers to
actual responses on the way back out. The legacy `IElsieRequestFilter` preflight + ACAO
after-hook wiring is removed.

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

`AddElsieCors` registers the middleware into the app pipeline. Fluent: `.Cors(o => …)` via
`ElsieCorsAppExtensions`.

## Per-route policy

```csharp
Get("/admin", () => ElsieResult.Text("x")).WithCors("tight");
// options.AddPolicy("tight", p => p.AllowOrigin("https://admin.example"));
```

## Behavior

| Request | Behavior |
|---------|----------|
| Preflight `OPTIONS` + `Origin` + `Access-Control-Request-Method` | Middleware short-circuits with 204 + CORS headers (or empty 204 if denied) |
| Actual request with `Origin` | Middleware adds ACAO on the matched route’s policy (or default) |

## See also

- [hosting-and-aot.md](hosting-and-aot.md)
- [modules.md](modules.md)
- [middleware.md](middleware.md)
