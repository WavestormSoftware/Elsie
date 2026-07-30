# CORS

Package **`Elsie.Cors`** implements CORS for Elsie-handled requests.

ASP.NET **`UseCors` is not enough**: Elsie matches routes (including OPTIONS) inside its own middleware, so the host CORS middleware never sees those preflights. Elsie answers **OPTIONS preflight** itself and stamps ACAO headers on actual responses via an **after-hook**.

## Setup

```csharp
builder.Services.AddElsieCors(o =>
{
    o.AddDefaultPolicy(p => p
        .AllowOrigins("http://localhost:5173")
        .AllowMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
        .AllowHeaders("Content-Type", "Authorization")
        .AllowCredentials()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));

    o.AddPolicy("public", p => p
        .AllowOrigin("*")
        .AllowMethods("GET")
        .AllowHeaders("*"));
});

var app = builder.Build();
app.UseElsieCors(); // before MapElsie
app.MapElsie();
```

`AllowAnyOrigin` + credentials → **startup throw** (invalid CORS combo).

## Per-route policy

```csharp
Get("/public/data", handler).WithCors("public");
```

Lookup: matched route's `.WithCors(name)` → else default policy name.

## Behavior

| Phase | Mechanism |
|-------|-----------|
| Preflight `OPTIONS` | `UseElsieCors` middleware — may complete without entering Elsie routes |
| Actual request | After-hook adds `Access-Control-*` when `Origin` present and policy allows |

## See also

- [auth.md](auth.md)
- [pipelines-and-errors.md](pipelines-and-errors.md)
