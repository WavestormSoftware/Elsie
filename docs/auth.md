# Auth

Two layers:

1. **Core gates** in `Elsie` — header/API key/bearer/cookie checks (no principal)
2. **`Elsie.Auth`** — ASP.NET cookie/JWT wiring + principal gates

## Core gates (`ElsieAuth`)

```csharp
Before(ElsieAuth.RequireApiKey("dev-secret")); // all HTTP methods by default
Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("session"));
```

Failures return problem+json **401**.

## Package `Elsie.Auth`

### Setup

```csharp
builder.Services.AddElsieAuth(o =>
{
    o.Cookie = c =>
    {
        c.Cookie.Name = "elsie-auth";
        c.Cookie.HttpOnly = true;
        c.SlidingExpiration = true;
    };
    // optional:
    // o.JwtBearer = jwt => { jwt.Authority = "..."; jwt.Audience = "..."; };
    // o.Authorization = a => a.AddPolicy("AdminsOnly", p => p.RequireRole("admin"));
});

var app = builder.Build();
app.UseElsieAuth(); // UseAuthentication + UseAuthorization — before MapElsie
app.MapElsie();
```

`AddElsieAuth` also calls `AddRouting()` so `ValidateOnBuild` hosts resolve authorization services.

### Gates

```csharp
Before(ElsieAuthGates.RequireAuthenticated());
Before(ElsieAuthGates.RequireRole("admin", "owner"));
Before(ElsieAuthGates.RequireClaim(ClaimTypes.Name, "ada"));
Before(ElsieAuthGates.RequirePolicy("AdminsOnly")); // async before-hook
```

| Result | When |
|--------|------|
| 401 | Anonymous |
| 403 | Authenticated but role/claim/policy fails |

### Principal helpers

```csharp
var user = ctx.GetUser(); // HttpContext.User when hosted on ASP.NET

await ctx.SignInCookieAsync("ada", roles: ["user"]);
await ctx.SignInAsync(principal);
await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
```

These require the ASP.NET host adapter (HttpContext stash). On the pure in-memory host, `GetUser()` returns an empty principal.

## Sample

See `samples/Elsie.Sample.Full` and the `elsie-api` template (`templates/elsie-api`).

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
- [cors.md](cors.md)
